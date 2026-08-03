using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="DbDataReader.RecordsAffected"/> reports rows the batch's
/// statements <em>changed</em>, never rows a SELECT <em>returned</em> — the
/// same number <c>ExecuteNonQuery</c> reports for the same batch, which is the
/// invariant every case below asserts on both surfaces at once.
/// <para>
/// Probed against SQL Server 2025 through real <c>Microsoft.Data.SqlClient</c>
/// (2026-08-03) across DDL, each DML verb, SELECT, SELECT INTO, MERGE,
/// <c>OUTPUT</c>-to-client DML, mixed batches, procedures and
/// <c>SET NOCOUNT</c>: the reader's value equals <c>ExecuteNonQuery</c>'s in
/// every shape. It accumulates as the reader is advanced (statements ahead of
/// the current result set have contributed, statements behind it have not
/// yet), and closing the reader runs the rest of the batch and folds in what it
/// counted. The wire half of the same contract is
/// <c>RecordsAffectedWireTests</c>, which asserts it through genuine SqlClient.
/// </para>
/// </summary>
[TestClass]
public sealed class RecordsAffectedTests
{
    /// <summary>
    /// Runs <paramref name="sql"/> twice against a freshly seeded simulation —
    /// once through <c>ExecuteNonQuery</c>, once through a fully drained and
    /// closed reader — and asserts both report <paramref name="expected"/>.
    /// </summary>
    private static void AssertRecordsAffected(int expected, string sql)
    {
        using (var connection = Seed().CreateOpenConnection())
        using (var command = connection.CreateCommand(sql))
            AreEqual(expected, command.ExecuteNonQuery(), $"ExecuteNonQuery: {sql}");

        using (var connection = Seed().CreateOpenConnection())
        using (var command = connection.CreateCommand(sql))
        {
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                }
            }
            while (reader.NextResult());
            reader.Close();
            AreEqual(expected, reader.RecordsAffected, $"reader: {sql}");
        }
    }

    private static Simulation Seed()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int, b varchar(10))",
            "insert into t values (1, 'a'), (2, 'b'), (3, 'c')");
        return simulation;
    }

    [TestMethod]
    public void DdlReportsNoCount() => AssertRecordsAffected(-1, "create table t9 (a int)");

    [TestMethod]
    public void InsertReportsRowsWritten() => AssertRecordsAffected(1, "insert into t values (9, 'z')");

    /// <summary>
    /// The shape the bug was: a three-row SELECT reported 3 because the reader
    /// counted its own <c>Read</c> calls. A SELECT changes nothing, so it
    /// contributes nothing and the batch reports no count at all.
    /// </summary>
    [TestMethod]
    public void SelectReportsNoCount() => AssertRecordsAffected(-1, "select a from t");

    [TestMethod]
    public void EmptySelectReportsNoCount() => AssertRecordsAffected(-1, "select a from t where a = 999");

    /// <summary>
    /// <c>SELECT @x = col FROM t</c> reads rows without returning them, and
    /// real still leaves it out of the count — it is a SELECT, not a change.
    /// </summary>
    [TestMethod]
    public void AssignmentOnlySelectReportsNoCount() =>
        AssertRecordsAffected(-1, "declare @x int; select @x = a from t");

    [TestMethod]
    public void UpdateMatchingNothingReportsZero() =>
        AssertRecordsAffected(0, "update t set b = 'x' where a = 999");

    [TestMethod]
    public void UpdateReportsRowsChanged() => AssertRecordsAffected(3, "update t set b = b");

    [TestMethod]
    public void DeleteReportsRowsRemoved() => AssertRecordsAffected(3, "delete from t");

    [TestMethod]
    public void MergeReportsRowsChanged() =>
        AssertRecordsAffected(2, "merge t as d using (values (1), (2)) as s (a) on d.a = s.a when matched then update set b = 'm';");

    /// <summary>SELECT INTO writes rows, so unlike a plain SELECT it counts.</summary>
    [TestMethod]
    public void SelectIntoReportsRowsWritten() =>
        AssertRecordsAffected(3, "select a into #s from t; drop table #s");

    /// <summary>
    /// A DML statement whose OUTPUT clause returns rows to the client is
    /// tabular, but its count is still a rows-affected count — the one result
    /// set that contributes.
    /// </summary>
    [TestMethod]
    public void OutputToClientReportsRowsChanged() =>
        AssertRecordsAffected(1, "insert into t output inserted.a values (9, 'z')");

    [TestMethod]
    public void OutputToClientMatchingNothingReportsZero() =>
        AssertRecordsAffected(0, "update t set b = 'x' output inserted.a where a = 999");

    /// <summary>Every counting statement in a batch adds in; the SELECT in the middle does not.</summary>
    [TestMethod]
    public void MixedBatchSumsOnlyTheChangingStatements() =>
        AssertRecordsAffected(4, """
            insert into t values (9, 'z');
            update t set b = 'q' where a < 3;
            select a from t;
            delete from t where a = 9;
            """);

    [TestMethod]
    public void SelectOnlyBatchReportsNoCount() =>
        AssertRecordsAffected(-1, "select a from t; select b from t; declare @x int; set @x = 5; select @x");

    [TestMethod]
    public void LoopSumsEachIteration() =>
        AssertRecordsAffected(3, "declare @i int = 0; while @i < 3 begin update t set b = b where a = 1; set @i += 1; end");

    [TestMethod]
    public void UntakenBranchReportsNoCount() => AssertRecordsAffected(-1, "if 1 = 0 update t set b = 'x'");

    [TestMethod]
    public void NoCountSuppressesTheCount() =>
        AssertRecordsAffected(-1, "set nocount on; insert into t values (9, 'z')");

    [TestMethod]
    public void NoCountSuppressesTheCountAheadOfAResultSet() =>
        AssertRecordsAffected(-1, "set nocount on; insert into t values (9, 'z'); select a from t");

    /// <summary>Only the statements that ran while NOCOUNT was on are suppressed.</summary>
    [TestMethod]
    public void NoCountTurnedOffMidBatchResumesCounting() =>
        AssertRecordsAffected(1, """
            set nocount on;
            insert into t values (9, 'z');
            set nocount off;
            insert into t values (8, 'y');
            """);

    /// <summary>
    /// The mirror case, which catches reading the session flag one statement
    /// too late: the first INSERT ran before NOCOUNT went on, so its count
    /// survives.
    /// </summary>
    [TestMethod]
    public void NoCountTurnedOnMidBatchKeepsTheEarlierCount() =>
        AssertRecordsAffected(1, """
            insert into t values (9, 'z');
            set nocount on;
            insert into t values (8, 'y');
            """);

    /// <summary>
    /// A procedure body's statements contribute their counts, and its
    /// SELECT does not.
    /// </summary>
    [TestMethod]
    public void ProcedureBodySumsItsDmlOnly()
    {
        var simulation = Seed();
        simulation.ExecuteBatches("""
            create procedure p as
            begin
                insert into t values (9, 'z');
                select a from t where a = 9;
                delete from t where a = 9;
            end
            """);
        using var connection = simulation.CreateOpenConnection();
        using (var command = connection.CreateCommand("exec p"))
            AreEqual(2, command.ExecuteNonQuery());

        using var reader = connection.CreateCommand("exec p").ExecuteReader();
        reader.Close();
        AreEqual(2, reader.RecordsAffected);
    }

    /// <summary>
    /// <c>SET NOCOUNT ON</c> inside a body reverts when the body exits, which
    /// is before the caller pulls what the body produced — so the suppression
    /// has to be recorded per statement rather than read at consumption time.
    /// </summary>
    [TestMethod]
    public void ProcedureBodyNoCountSuppressesItsCounts()
    {
        var simulation = Seed();
        simulation.ExecuteBatches("""
            create procedure p as
            begin
                set nocount on;
                insert into t values (9, 'z');
                delete from t where a = 9;
            end
            """);
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("exec p");
        AreEqual(-1, command.ExecuteNonQuery());
    }

    /// <summary>
    /// A plain batch's <c>SET NOCOUNT ON</c> is session state and outlives the
    /// batch, so the next command on the same connection is suppressed too —
    /// probe-confirmed, and the baseline the scoped cases below contrast with.
    /// </summary>
    [TestMethod]
    public void NoCountFromAPlainBatchOutlivesIt()
    {
        using var connection = Seed().CreateOpenConnection();
        using (var first = connection.CreateCommand("set nocount on; insert into t values (9, 'z')"))
            AreEqual(-1, first.ExecuteNonQuery());
        using var second = connection.CreateCommand("insert into t values (8, 'y')");
        AreEqual(-1, second.ExecuteNonQuery());
    }

    /// <summary>
    /// A command carrying parameters is an ad-hoc scope — SqlClient sends one
    /// as <c>sp_executesql</c> — so the SET options it changed revert when it
    /// returns and the next command counts normally (probe-confirmed: the same
    /// text with no parameters leaks, with one does not). EF Core's
    /// modification batches open with <c>SET NOCOUNT ON</c> and depend on this.
    /// </summary>
    [TestMethod]
    public void NoCountFromAParameterizedCommandIsScopedToIt()
    {
        using var connection = Seed().CreateOpenConnection();
        using (var first = connection.CreateCommand("set nocount on; insert into t values (@a, 'z')", ("@a", 9)))
            AreEqual(-1, first.ExecuteNonQuery());
        using var second = connection.CreateCommand("insert into t values (8, 'y')");
        AreEqual(1, second.ExecuteNonQuery());
    }

    [TestMethod]
    public void NoCountFromDynamicSqlIsScopedToIt()
    {
        using var connection = Seed().CreateOpenConnection();
        using (var first = connection.CreateCommand("exec('set nocount on; insert into t values (9, ''z'')')"))
            _ = first.ExecuteNonQuery();
        using var second = connection.CreateCommand("insert into t values (8, 'y')");
        AreEqual(1, second.ExecuteNonQuery());
    }

    [TestMethod]
    public void NoCountFromAProcedureBodyIsScopedToIt()
    {
        var simulation = Seed();
        simulation.ExecuteBatches("create procedure p as begin set nocount on; insert into t values (9, 'z'); end");
        using var connection = simulation.CreateOpenConnection();
        using (var first = connection.CreateCommand("exec p"))
            AreEqual(-1, first.ExecuteNonQuery());
        using var second = connection.CreateCommand("insert into t values (8, 'y')");
        AreEqual(1, second.ExecuteNonQuery());
    }

    /// <summary>
    /// The near-universal <c>set nocount on</c> opening a trigger applies
    /// inside the body only: the firing statement still reports its own count,
    /// and so does the next statement.
    /// </summary>
    [TestMethod]
    public void NoCountFromATriggerBodyIsScopedToIt()
    {
        var simulation = Seed();
        simulation.ExecuteBatches("create trigger tr on t after insert as begin set nocount on; end");
        using var connection = simulation.CreateOpenConnection();
        using (var firing = connection.CreateCommand("insert into t values (9, 'z')"))
            AreEqual(1, firing.ExecuteNonQuery());
        using var next = connection.CreateCommand("insert into t values (8, 'y')");
        AreEqual(1, next.ExecuteNonQuery());
    }

    /// <summary>
    /// The value is -1 until a statement contributes, grows as the reader is
    /// advanced past the counting statements, and is final once closed.
    /// </summary>
    [TestMethod]
    public void AccumulatesAsTheReaderAdvances()
    {
        using var connection = Seed().CreateOpenConnection();
        using var command = connection.CreateCommand("""
            select a from t;
            insert into t values (9, 'z');
            select a from t where a = 9;
            update t set b = b where a < 3;
            """);
        using var reader = command.ExecuteReader();

        // Parked on the leading SELECT: nothing has changed a row yet.
        AreEqual(-1, reader.RecordsAffected);
        while (reader.Read())
        {
        }

        // Advancing past the INSERT to the second result set folds it in.
        IsTrue(reader.NextResult());
        AreEqual(1, reader.RecordsAffected);
        while (reader.Read())
        {
        }

        // The trailing UPDATE is behind the reader until the batch ends.
        IsFalse(reader.NextResult());
        AreEqual(3, reader.RecordsAffected);
    }

    /// <summary>
    /// Closing a reader runs the batch's remaining statements, so their counts
    /// land even though the caller never advanced onto them — real reports the
    /// same total whether or not the reader was drained first.
    /// </summary>
    [TestMethod]
    public void ClosingWithoutDrainingStillCountsTheRest()
    {
        using var connection = Seed().CreateOpenConnection();
        using var command = connection.CreateCommand("""
            insert into t values (9, 'z');
            select a from t;
            delete from t where a = 9;
            """);
        using var reader = command.ExecuteReader();
        AreEqual(1, reader.RecordsAffected);
        reader.Close();
        AreEqual(2, reader.RecordsAffected);
        IsTrue(reader.IsClosed);
    }
}
