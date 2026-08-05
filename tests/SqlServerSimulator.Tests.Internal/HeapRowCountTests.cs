using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="Heap.RowCount"/> is maintained rather than walked — the join
/// planner reads it once per join level per execution — so the safety net is
/// that the maintained value equals the walk after every seam that can move it.
/// <see cref="Heap.RecomputeRowCount"/> is that walk, and it is also what the
/// two structural seams (TRUNCATE and its rollback) call.
/// </summary>
[TestClass]
public sealed class HeapRowCountTests
{
    private static SimulatedDbConnection OpenWithTable()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, "create table t (id int not null primary key, pad varchar(400) not null)");
        return connection;
    }

    private static void Exec(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static Heap HeapOf(SimulatedDbConnection connection) =>
        connection.CurrentDatabase.Schemas["dbo"].HeapTables["t"].Heap;

    /// <summary>The maintained count and the walk agree; asserted after every step below.</summary>
    private static void AssertConsistent(Heap heap)
    {
        var maintained = heap.RowCount;
        AreEqual(maintained, heap.RecomputeRowCount());
    }

    /// <summary>Enough rows to span several pages, so page allocation is exercised rather than a single-page fast path.</summary>
    private static void Fill(SimulatedDbConnection connection, int rows)
    {
        Exec(connection, $"""
            declare @i int = 1;
            while @i <= {rows} begin insert t values (@i, replicate('x', 100)); set @i = @i + 1; end
            """);
    }

    [TestMethod]
    public void InsertsMaintainTheCount()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        AreEqual(0, heap.RowCount);
        Fill(connection, 200);
        AreEqual(200, heap.RowCount);
        AssertConsistent(heap);
    }

    /// <summary>
    /// A DELETE tombstones its slot in place, so the slot total — which is what
    /// this count has always reported — doesn't move. The point is that the
    /// maintained value still matches the walk.
    /// </summary>
    [TestMethod]
    public void DeletesLeaveTheSlotTotalAndStayConsistent()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 100);
        Exec(connection, "delete t where id <= 50");
        AssertConsistent(heap);
    }

    /// <summary>An UPDATE that relocates a row (a wider payload) allocates a slot and must be counted.</summary>
    [TestMethod]
    public void RelocatingUpdatesStayConsistent()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 100);
        Exec(connection, "update t set pad = replicate('y', 400) where id <= 30");
        AssertConsistent(heap);
    }

    [TestMethod]
    public void RolledBackInsertsStayConsistent()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 50);
        Exec(connection, "begin tran; insert t values (9001, 'a'), (9002, 'b'); rollback");
        AssertConsistent(heap);
    }

    [TestMethod]
    public void RolledBackDeletesStayConsistent()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 50);
        Exec(connection, "begin tran; delete t where id <= 20; rollback");
        AssertConsistent(heap);
    }

    /// <summary>TRUNCATE clears the page list wholesale, which re-derives the count.</summary>
    [TestMethod]
    public void TruncateResetsTheCount()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 100);
        Exec(connection, "truncate table t");
        AreEqual(0, heap.RowCount);
        AssertConsistent(heap);
    }

    /// <summary>A rolled-back TRUNCATE re-attaches the old pages, and the count follows them back.</summary>
    [TestMethod]
    public void RolledBackTruncateRestoresTheCount()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 100);
        var before = heap.RowCount;
        Exec(connection, "begin tran; truncate table t; rollback");
        AreEqual(before, heap.RowCount);
        AssertConsistent(heap);
    }

    /// <summary>
    /// <c>DBCC SHRINKDATABASE</c> drops the trailing run of fully-dead pages,
    /// which is the one seam that lowers the count without a TRUNCATE.
    /// </summary>
    [TestMethod]
    public void TrimmingDeadTailPagesLowersTheCount()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 300);
        var before = heap.RowCount;
        Exec(connection, "delete t where id > 100");
        Exec(connection, "dbcc shrinkdatabase(simulated)");
        IsLessThanOrEqualTo(before, heap.RowCount);
        AssertConsistent(heap);
    }

    /// <summary>Insert-after-truncate resumes counting from zero rather than from the pre-truncate total.</summary>
    [TestMethod]
    public void InsertsAfterTruncateResumeFromZero()
    {
        using var connection = OpenWithTable();
        var heap = HeapOf(connection);
        Fill(connection, 100);
        Exec(connection, "truncate table t");
        Fill(connection, 10);
        AreEqual(10, heap.RowCount);
        AssertConsistent(heap);
    }
}
