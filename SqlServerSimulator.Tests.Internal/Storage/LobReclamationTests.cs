using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Guards off-row LOB-chain reclamation: an UPDATE / DELETE that supersedes
/// an off-row <c>nvarchar(MAX)</c> value
/// must return the old chain's pages to the heap's free-list rather than
/// orphaning them in <see cref="Heap.LobPages"/>. Without reclamation
/// a logical dataset of constant size grows <see cref="Heap.LobPages"/>
/// without bound under mutation churn — the leak that made long-running
/// simulations a memory problem. These tests assert the page count stays a
/// small multiple of the live working set, not a function of the churn count.
/// </summary>
[TestClass]
public sealed class LobReclamationTests
{
    private static Heap HeapFor(SimulatedDbConnection conn, string table) =>
        conn.CurrentDatabase.Schemas[Database.DefaultSchemaName].HeapTables[table].Heap;

    private static void Exec(SimulatedDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    // One logical row, repeatedly rewritten with a fresh off-row value. The
    // working set is a single chain (one page for the ~200-byte payload); the
    // free-list must keep total page count bounded regardless of update count.
    [TestMethod]
    public void RepeatedUpdate_OfMaxValue_KeepsLobPagesBounded()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        Exec(conn, "insert t values (1, replicate(N'a', 100))");

        for (var i = 0; i < 100; i++)
            Exec(conn, $"update t set v = replicate(N'b', 100) where id = 1");

        // Live set is one chain (one page). Allow generous slack for the
        // in-flight old+new overlap during a single statement.
        IsLessThanOrEqualTo(4, HeapFor(conn, "t").LobPages.Count, "LobPages should stay near the one-chain working set; the free-list bounds repeated-UPDATE churn.");
    }

    // Delete then re-insert the same logical row many times. Each DELETE
    // supersedes a chain; the free-list must reclaim it for the next INSERT.
    [TestMethod]
    public void DeleteInsertChurn_KeepsLobPagesBounded()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");

        for (var i = 0; i < 100; i++)
        {
            Exec(conn, "insert t values (1, replicate(N'a', 100))");
            Exec(conn, "delete from t where id = 1");
        }

        IsLessThanOrEqualTo(4, HeapFor(conn, "t").LobPages.Count, "DELETE should free the chain for the next INSERT to reuse.");
    }

    // A rolled-back INSERT's chain must not leak (closes the legacy
    // "orphaned LOB chains for rolled-back inserts also leak" quirk).
    [TestMethod]
    public void RolledBackInsert_FreesItsChain()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");

        for (var i = 0; i < 100; i++)
        {
            Exec(conn, "begin tran; insert t values (1, replicate(N'a', 100)); rollback");
        }

        IsLessThanOrEqualTo(4, HeapFor(conn, "t").LobPages.Count, "Rolled-back INSERT chains should be reclaimed on rollback.");
    }

    // A rolled-back UPDATE's freshly-allocated new chain must be reclaimed on
    // rollback, while the restored old value's chain stays live.
    [TestMethod]
    public void RolledBackUpdate_FreesNewChain()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        Exec(conn, "insert t values (1, replicate(N'a', 100))");

        for (var i = 0; i < 100; i++)
            Exec(conn, "begin tran; update t set v = replicate(N'b', 100) where id = 1; rollback");

        IsLessThanOrEqualTo(4, HeapFor(conn, "t").LobPages.Count, "A rolled-back UPDATE should reclaim its new chain and keep only the restored original.");
    }

    // Inside one explicit transaction the superseded chains can't be reclaimed
    // mid-flight (a rollback might still need them), so LobPages grows to the
    // tx's peak. The win is reuse: after the tx commits those chains are free,
    // so a second equally-long transaction draws from the free-list instead of
    // growing the page list further.
    [TestMethod]
    public void ReclaimedChains_AreReusedAcrossTransactions()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        Exec(conn, "insert t values (1, replicate(N'a', 100))");

        const string churnTx = "begin tran; declare @i int = 0; while @i < 100 begin update t set v = replicate(N'b', 100) where id = 1; set @i += 1; end; commit";
        Exec(conn, churnTx);
        var afterFirst = HeapFor(conn, "t").LobPages.Count;
        Exec(conn, churnTx);
        var afterSecond = HeapFor(conn, "t").LobPages.Count;

        IsLessThanOrEqualTo(afterFirst, afterSecond, $"Second churn tx (peak {afterSecond}) should reuse the first's freed pages (peak {afterFirst}), not grow the list.");
    }

    // Under READ_COMMITTED_SNAPSHOT a superseded chain is pinned by its history
    // entry until version-store GC (which runs at each tx commit) trims it; with
    // no concurrent snapshot reader that happens immediately, so churn stays
    // bounded across committed transactions.
    [TestMethod]
    public void VersionedUpdate_ReclaimsViaGarbageCollection()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "alter database simulated set read_committed_snapshot on");
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        Exec(conn, "insert t values (1, replicate(N'a', 100))");

        for (var i = 0; i < 100; i++)
            Exec(conn, "begin tran; update t set v = replicate(N'b', 100) where id = 1; commit");

        IsLessThanOrEqualTo(8, HeapFor(conn, "t").LobPages.Count, "Version-store GC should trim each committed tx's history entry and free its chain when no snapshot needs it.");
    }

    // Under RCSI with no concurrent snapshot reader, even plain autocommit
    // UPDATEs (no explicit transaction) must reclaim their superseded chains:
    // version-store GC runs at the end of a versioned autocommit statement when
    // no snapshot is open, so churn stays bounded without an explicit COMMIT to
    // trigger collection.
    [TestMethod]
    public void VersionedAutocommitUpdate_ReclaimsWithoutExplicitTransaction()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "alter database simulated set read_committed_snapshot on");
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        Exec(conn, "insert t values (1, replicate(N'a', 100))");

        for (var i = 0; i < 100; i++)
            Exec(conn, "update t set v = replicate(N'b', 100) where id = 1");

        IsLessThanOrEqualTo(8, HeapFor(conn, "t").LobPages.Count, "A versioned autocommit UPDATE with no open snapshot should GC its superseded chain immediately, not wait for an explicit-tx commit.");
    }

    // Reclamation must not corrupt a surviving value: the live row reads back
    // correctly after extensive churn around it.
    [TestMethod]
    public void ReclamationPreservesLiveValue()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        Exec(conn, "insert t values (1, N'keep-me')");
        Exec(conn, "insert t values (2, replicate(N'x', 200))");

        for (var i = 0; i < 50; i++)
            Exec(conn, $"update t set v = replicate(N'y', 200) where id = 2");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select v from t where id = 1";
        AreEqual("keep-me", (string)cmd.ExecuteScalar()!);
    }
}
