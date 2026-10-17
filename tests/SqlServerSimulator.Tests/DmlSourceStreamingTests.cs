using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An <c>INSERT … SELECT</c> and a <c>MERGE</c> read and write as real's
/// pipelined plans do (probed 2026-10-17 against SQL Server 2025, 2,000 source
/// rows of 200 bytes): an <c>INSERT</c> whose source can't meet its target
/// writes each row as it reads it, a <c>MERGE</c> judges and writes each
/// source row's action as it reaches its target row, and a table source joined
/// on the leading columns of both clustered keys is read as the actions go —
/// so a row another session holds stops the statement with the rows before it
/// written, as a <c>NOLOCK</c> reader counts them.
/// </summary>
[TestClass]
public sealed class DmlSourceStreamingTests
{
    public TestContext TestContext { get; set; } = null!;

    private const int Rows = 2000;

    private static Simulation Tables(bool heapSource = false, int targetRows = 0)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table src (k int {(heapSource ? "not null" : "primary key")}, v char(200) not null);
            insert src select value, 'x' from generate_series(1, {Rows});
            create table tgt (k int primary key, v char(200) not null);
            insert tgt select value, 'x' from generate_series(1, {targetRows}) where {targetRows} > 0;
            """);
        return sim;
    }

    private static object? Scalar(DbConnection connection, string sql) => connection.CreateCommand(sql).ExecuteScalar();

    /// <summary>
    /// Runs <paramref name="statement"/> while another session holds the row
    /// <paramref name="held"/> names, and answers what a <c>NOLOCK</c> reader
    /// counted by <paramref name="count"/> while the statement waited on it.
    /// </summary>
    private async Task<int> WrittenWhileBlocked(Simulation sim, string held, string statement, string count)
    {
        using var holder = sim.CreateOpenConnection();
        using var runner = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        using var transaction = holder.BeginTransaction();
        using (var hold = holder.CreateCommand(held))
        {
            hold.Transaction = transaction;
            _ = hold.ExecuteNonQuery();
        }
        var running = await sim.StartBlocked(runner, statement, TestContext.CancellationToken);
        var written = (int)Scalar(observer, count)!;
        transaction.Rollback();
        _ = await running;
        return written;
    }

    [TestMethod]
    public async Task InsertSelect_WritesEachRowAsItReadsIt()
    {
        var sim = Tables();
        AreEqual(1499, await WrittenWhileBlocked(sim, "update src set v = 'y' where k = 1500", "insert tgt select k, v from src", "select count(*) from tgt with (nolock)"));
        AreEqual(Rows, Scalar(sim.CreateOpenConnection(), "select count(*) from tgt"));
    }

    [TestMethod]
    public async Task InsertSelect_FromItsOwnTarget_ReadsWholeFirst()
        => AreEqual(0, await WrittenWhileBlocked(Tables(), "update src set v = 'y' where k = 1500", "insert src select k + 10000, v from src", "select count(*) from src with (nolock) where k > 10000"));

    [TestMethod]
    public void InsertSelectOutput_SendsTheRowsBeforeAFailingSourceRow()
    {
        using var connection = Tables().CreateOpenConnection();
        using var reader = connection.CreateCommand("insert tgt output inserted.k select k, v from src where 1 / iif(k = 1500, 0, 1) = 1").ExecuteReader();
        var read = 0;
        var ex = Throws<SimulatedSqlException>(() =>
        {
            while (reader.Read())
                read++;
        });
        AreEqual((1499, 8134), (read, ex.Number));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Merge_WritesEachActionAsItReachesItsTargetRow(bool heapSource)
        => AreEqual(699, await WrittenWhileBlocked(
            Tables(heapSource, targetRows: 1000),
            "update tgt set v = 'y' where k = 700",
            "merge tgt using src on tgt.k = src.k when matched then update set v = 'z' when not matched then insert values (src.k, 'z');",
            "select count(*) from tgt with (nolock) where v = 'z'"));

    [TestMethod]
    [DataRow(false, 1499)]
    [DataRow(true, 0)]
    public async Task Merge_KeyedSourceIsReadAsTheActionsGo(bool heapSource, int written)
        => AreEqual(written, await WrittenWhileBlocked(
            Tables(heapSource, targetRows: 1000),
            "update src set v = 'y' where k = 1500",
            "merge tgt using src on tgt.k = src.k when matched then update set v = 'z' when not matched then insert values (src.k, 'z');",
            "select count(*) from tgt with (nolock) where v = 'z'"));

    [TestMethod]
    public void Merge_TargetRowMatchedTwice_Raises8672()
    {
        var sim = Tables(heapSource: true, targetRows: 1000);
        _ = sim.ExecuteNonQuery("insert src values (500, 'dup')");
        _ = sim.AssertSqlError("merge tgt using src on tgt.k = src.k when matched then update set v = src.v when not matched then insert values (src.k, src.v);", 8672);
        AreEqual(0, sim.ExecuteScalar("select count(*) from tgt where v <> 'x'"));
    }

    [TestMethod]
    public void Merge_DuplicateSourceKeysUnmatched_InsertTwice()
        => AreEqual(2627, new Simulation().AssertSqlError("""
            create table s (k int not null); insert s values (1), (1);
            create table t (k int primary key);
            merge t using s on t.k = s.k when matched then update set k = s.k when not matched then insert values (s.k);
            """, 2627).Number);
}
