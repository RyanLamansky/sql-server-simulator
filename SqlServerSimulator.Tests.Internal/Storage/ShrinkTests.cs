using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Guards <c>DBCC SHRINKDATABASE</c> / <c>DBCC SHRINKFILE</c> trailing-trim: the
/// reclamation work bounds <see cref="Heap.Pages"/> / <see cref="Heap.LobPages"/>
/// by the peak working set but never lets the lists fall below their high-water
/// mark on their own (stable <c>(page, slot)</c> addresses forbid mid-list
/// removal). A shrink drops the fully-dead / freed pages off the *tail* of those
/// lists, lowering the high-water mark — but only the trailing run, so interior
/// dead pages and any version- or lock-pinned tail page stay put.
/// </summary>
[TestClass]
public sealed class ShrinkTests
{
    private static Heap HeapFor(SimulatedDbConnection conn, string table) =>
        conn.CurrentDatabase.Schemas[Database.DefaultSchemaName].HeapTables[table].Heap;

    private static void Exec(SimulatedDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    // Fill many pages, delete every row (each autocommit DELETE commits dead
    // bytes), then shrink. With the whole heap dead, the trailing run is the
    // entire list, so the page list collapses.
    [TestMethod]
    public void ShrinkDatabase_DropsFullyDeadTrailingDataPages()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");
        for (var id = 1; id <= 30; id++)
            Exec(conn, $"insert t values ({id}, replicate('a', 7000))");

        var grown = HeapFor(conn, "t").Pages.Count;
        IsGreaterThan(5, grown, "setup should have allocated many page-sized rows");

        Exec(conn, "delete from t");
        Exec(conn, "dbcc shrinkdatabase (simulated)");

        IsLessThanOrEqualTo(1, HeapFor(conn, "t").Pages.Count, "an all-dead heap should shrink its page list to the trailing-live remainder (here, empty).");
    }

    // Free a whole heap's worth of LOB chains, then shrink: the trailing free
    // run is every LOB page, so the list collapses to near zero.
    [TestMethod]
    public void ShrinkDatabase_DropsFreedTrailingLobPages()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        for (var id = 1; id <= 20; id++)
            Exec(conn, $"insert t values ({id}, replicate(N'a', 200))");

        IsGreaterThan(0, HeapFor(conn, "t").LobPages.Count, "MAX values should have pushed off-row LOB pages");

        Exec(conn, "delete from t");
        Exec(conn, "dbcc shrinkdatabase (simulated)");

        IsLessThanOrEqualTo(1, HeapFor(conn, "t").LobPages.Count, "freeing every chain then shrinking should drop the trailing free LOB pages.");
    }

    // Delete the *leading* rows but keep a live row on the last page: the tail
    // page isn't dead, so the trailing run is empty and nothing is dropped —
    // even though interior pages are reclaimable.
    [TestMethod]
    public void ShrinkDatabase_KeepsInteriorDeadPagesWhenTailIsLive()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v varchar(7000) not null)");
        for (var id = 1; id <= 10; id++)
            Exec(conn, $"insert t values ({id}, replicate('a', 7000))");

        var grown = HeapFor(conn, "t").Pages.Count;

        // Kill everything except the row on the last page.
        Exec(conn, "delete from t where id < 10");
        Exec(conn, "dbcc shrinkdatabase (simulated)");

        HasCount(grown, HeapFor(conn, "t").Pages, "a live row on the tail page blocks the trailing trim; interior dead pages can't be removed without renumbering.");
    }

    // A surviving row must read back intact after a shrink rearranges the lists
    // around it.
    [TestMethod]
    public void ShrinkDatabase_PreservesLiveValues()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        Exec(conn, "create table t (id int not null primary key, v nvarchar(max) not null)");
        Exec(conn, "insert t values (1, N'keep-me')");
        for (var id = 2; id <= 20; id++)
            Exec(conn, $"insert t values ({id}, replicate(N'a', 200))");
        Exec(conn, "delete from t where id >= 2");

        Exec(conn, "dbcc shrinkdatabase (simulated)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select v from t where id = 1";
        AreEqual("keep-me", (string)cmd.ExecuteScalar()!);
    }
}
