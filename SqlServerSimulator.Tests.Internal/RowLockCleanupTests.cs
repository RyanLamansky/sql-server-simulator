using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Internal-only: the row-lock dict on <see cref="HeapTable.RowLocks"/> is
/// populated lazily per row touched, and entries for tombstoned slots are
/// purged at DELETE commit time. Validates the dict size collapses to zero
/// after the rows are gone — slot ids never get reused (per the
/// <c>Heap.DeleteAt</c> quirk noted in CLAUDE.md), so the entries would
/// otherwise accumulate over the table's lifetime.
/// </summary>
[TestClass]
public sealed class RowLockCleanupTests
{
    private static (Simulation Sim, SimulatedDbConnection Conn) Open()
    {
        var sim = new Simulation();
        var conn = (SimulatedDbConnection)sim.CreateDbConnection();
        conn.Open();
        return (sim, conn);
    }

    private static void Run(SimulatedDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    private static HeapTable HeapFor(SimulatedDbConnection conn, string leafName) =>
        conn.CurrentDatabase.Schemas[Database.DefaultSchemaName].HeapTables[leafName];

    [TestMethod]
    public void Delete_AutoCommit_PurgesRowLockEntries()
    {
        var (_, conn) = Open();
        Run(conn, """
            create table t (id int primary key);
            insert t values (1), (2), (3);
            delete from t
            """);
        IsEmpty(HeapFor(conn, "t").RowLocks);
    }

    [TestMethod]
    public void Delete_WhereClause_DropsOnlyMatchingSlotEntries()
    {
        var (_, conn) = Open();
        Run(conn, """
            create table t (id int primary key);
            insert t values (1), (2), (3), (4), (5);
            delete from t where id in (2, 4)
            """);
        // DELETE's WHERE-eval probes every candidate row (populates RowLocks
        // for all 5 slots), then tombstones 2 of them. The 3 surviving
        // slots keep their entries; deleted slots are gone.
        HasCount(3, HeapFor(conn, "t").RowLocks);
    }

    [TestMethod]
    public void Delete_InsideExplicitTx_PurgesAtStatementCommit()
    {
        var (_, conn) = Open();
        Run(conn, """
            create table t (id int primary key);
            insert t values (1), (2), (3)
            """);
        Run(conn, """
            begin tran;
            delete from t where id = 1;
            commit tran
            """);
        // Same shape: id=1 tombstoned and dropped; id=2 and id=3 were
        // WHERE-probed and keep their entries.
        HasCount(2, HeapFor(conn, "t").RowLocks);
    }

    [TestMethod]
    public void Delete_ThenInsert_DictStaysClean()
    {
        var (_, conn) = Open();
        Run(conn, """
            create table t (id int primary key);
            insert t values (1);
            delete from t where id = 1;
            insert t values (2);
            delete from t where id = 2
            """);
        IsEmpty(HeapFor(conn, "t").RowLocks);
    }
}
