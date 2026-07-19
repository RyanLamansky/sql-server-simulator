using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Guards heap data-page reclamation: a committed DELETE / forwarding-UPDATE
/// leaves dead row-payload bytes
/// in its page, and the allocator must reclaim that space — via intra-page
/// compaction plus reuse of the freed room by later inserts — rather than
/// appending fresh pages forever. Without it, <see cref="Heap.Pages"/> grows
/// with churn count even though the live row set is constant. These assert
/// <see cref="Heap.Pages"/>.Count stays bounded by the peak working set, not the
/// mutation count. (Reclamation reuses space in place and never removes a page
/// from the middle of the list, so the bound is peak concurrent pages, not the
/// current live count.)
/// </summary>
[TestClass]
public sealed class PageReclamationTests
{
    private static Heap HeapFor(SimulatedDbConnection conn, string table) =>
        conn.CurrentDatabase.Schemas[Database.DefaultSchemaName].HeapTables[table].Heap;

    private static void Exec(SimulatedDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    // One ~page-sized row, deleted and re-inserted many times. Each DELETE
    // commits dead bytes that fill a page; reuse must keep total pages near the
    // one-row working set instead of one page per insert.
    [TestMethod]
    public void DeleteInsertChurn_OfPageSizedRow_KeepsPagesBounded()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");

        for (var i = 0; i < 100; i++)
        {
            Exec(conn, "insert t values (1, replicate('a', 7000))");
            Exec(conn, "delete from t where id = 1");
        }

        IsLessThanOrEqualTo(4, HeapFor(conn, "t").Pages.Count, "DELETE should free its page-sized row's space for the next INSERT to reuse, not append a page per insert.");
    }

    // A growing UPDATE forwards (tombstones the old in-place row / old target);
    // those committed-dead images must be reclaimed across repeated grow/shrink
    // churn rather than accreting pages.
    [TestMethod]
    public void ForwardingUpdateChurn_KeepsPagesBounded()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");
        Exec(conn, "insert t values (1, 'x')");

        for (var i = 0; i < 100; i++)
        {
            Exec(conn, "update t set v = replicate('b', 7000) where id = 1");
            Exec(conn, "update t set v = 'x' where id = 1");
        }

        IsLessThanOrEqualTo(6, HeapFor(conn, "t").Pages.Count, "Forwarding-UPDATE churn should reclaim superseded row images, not grow the page list.");
    }

    // Survivors must read back correctly after heavy churn (and compaction)
    // around them.
    [TestMethod]
    public void ReclamationPreservesLiveValues()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");
        Exec(conn, "insert t values (1, 'survivor')");

        for (var i = 0; i < 100; i++)
        {
            Exec(conn, "insert t values (2, replicate('a', 7000))");
            Exec(conn, "delete from t where id = 2");
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select v from t where id = 1";
        AreEqual("survivor", (string)cmd.ExecuteScalar()!);
    }

    // Compaction relocates many interleaved survivors at once (the byte-moving
    // path): fill a page, delete alternating rows, then insert enough that the
    // no-fit path must compact and pack the survivors. Every survivor must read
    // back its exact original payload.
    [TestMethod]
    public void Compaction_PreservesManyInterleavedSurvivors()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(400) not null)");
        // ~406-byte rows pack ~18 to a page — enough to fill one and force the
        // reuse/compaction path on the follow-up inserts.
        for (var id = 1; id <= 18; id++)
            Exec(conn, $"insert t values ({id}, replicate(substring('0123456789', {id % 10} + 1, 1), 400))");

        Exec(conn, "delete from t where id % 2 = 0");

        for (var id = 19; id <= 30; id++)
            Exec(conn, $"insert t values ({id}, replicate(substring('0123456789', {id % 10} + 1, 1), 400))");

        using var cmd = conn.CreateCommand();
        for (var id = 1; id <= 17; id += 2)
        {
            cmd.CommandText = $"select v from t where id = {id}";
            AreEqual(new string((char)('0' + (id % 10)), 400), (string)cmd.ExecuteScalar()!, $"survivor id={id} payload corrupted by compaction");
        }
    }

    // Deleting a *forwarded* row must reclaim the relocated target's bytes, not
    // just the pointer slot. Each cycle grows a row (forwarding it to a fresh
    // target) then deletes it; without target reclamation the target pages
    // accrete one per cycle.
    [TestMethod]
    public void ForwardedRowDeleteChurn_KeepsPagesBounded()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");

        for (var i = 0; i < 100; i++)
        {
            Exec(conn, "insert t values (1, 'x')");
            Exec(conn, "update t set v = replicate('a', 7000) where id = 1");
            Exec(conn, "delete from t where id = 1");
        }

        IsLessThanOrEqualTo(6, HeapFor(conn, "t").Pages.Count, "Deleting a forwarded row should reclaim the relocated target's page space, not orphan a page per cycle.");
    }

    // Rolling back a forwarded-row DELETE must restore the row exactly once — the
    // pointer and target both un-tombstone and the target re-registers as a
    // forward target, so the row surfaces via the forwarder rather than also
    // appearing as a standalone live row.
    [TestMethod]
    public void RolledBackForwardedRowDelete_RestoresRowExactlyOnce()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");
        Exec(conn, "insert t values (1, 'x')");
        Exec(conn, "update t set v = replicate('a', 7000) where id = 1"); // grows → forwards
        Exec(conn, "begin tran; delete from t where id = 1; rollback");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from t";
        AreEqual(1, cmd.ExecuteScalar());
        cmd.CommandText = "select len(v) from t where id = 1";
        AreEqual(7000, cmd.ExecuteScalar());
    }

    // A rolled-back INSERT's page-payload bytes must be reclaimed: the row is
    // gone for good, so its slot is reclaimable and later inserts reuse the
    // space rather than appending a page per rolled-back cycle.
    [TestMethod]
    public void RolledBackInsertChurn_KeepsPagesBounded()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");

        for (var i = 0; i < 100; i++)
            Exec(conn, "begin tran; insert t values (1, replicate('a', 7000)); rollback");

        IsLessThanOrEqualTo(4, HeapFor(conn, "t").Pages.Count, "Rolled-back INSERT page bytes should be reclaimable and reused, not leak a page per cycle.");
    }

    // A rolled-back DELETE must not be reclaimed: compaction has to preserve an
    // uncommitted-tombstone's bytes so the row comes back intact.
    [TestMethod]
    public void RolledBackDelete_PreservesRow()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");
        Exec(conn, "insert t values (1, 'keep')");

        // Churn around it to provoke compaction, with the delete rolled back.
        for (var i = 0; i < 50; i++)
        {
            Exec(conn, "begin tran; delete from t where id = 1; rollback");
            Exec(conn, "insert t values (2, replicate('a', 7000))");
            Exec(conn, "delete from t where id = 2");
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select v from t where id = 1";
        AreEqual("keep", (string)cmd.ExecuteScalar()!);
    }
}
