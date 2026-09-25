using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A procedure or dynamic-SQL body runs on past a statement-terminating error
/// the way a batch does, what it sent before an error that ends it still
/// reaches the client, and its return status reports the errors its own
/// statements raised (probed 2026-09-24 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ProcedureBodyContinuationTests
{
    private static Simulation WithLog(params ReadOnlySpan<string> batches)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table log (step int)");
        simulation.ExecuteBatches(batches);
        return simulation;
    }

    private static int[] Steps(Simulation simulation)
    {
        using var reader = simulation.ExecuteReader("select step from log order by step");
        return [.. reader.EnumerateRecords().Select(record => record.GetInt32(0))];
    }

    private static SimulatedSqlException Fails(Simulation simulation, string commandText)
        => Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery(commandText));

    /// <summary>Runs a batch whose statement errors are incidental; it has run to its end by the time they surface.</summary>
    private static void RunPastErrors(Simulation simulation, string commandText)
    {
        try
        {
            _ = simulation.ExecuteNonQuery(commandText);
        }
        catch (SimulatedSqlException)
        {
        }
    }

    [TestMethod]
    public void ProcedureBody_RunsOnPastAStatementError_AndSoDoesTheCaller()
    {
        var simulation = WithLog("create procedure p as insert log values (1); select 1/0; insert log values (2)");
        AreEqual(8134, Fails(simulation, "exec p; insert log values (3)").Number);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, Steps(simulation));
    }

    [TestMethod]
    public void DynamicSql_RunsOnPastAStatementError()
    {
        var simulation = WithLog();
        AreEqual(8134, Fails(simulation, "exec ('insert log values (1); select 1/0; insert log values (2)'); insert log values (3)").Number);
        AreEqual(8134, Fails(simulation, "exec sp_executesql N'insert log values (4); select 1/0; insert log values (5)'; insert log values (6)").Number);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, Steps(simulation));
    }

    [TestMethod]
    public void NestedProcedure_RunsOnAndSoDoesItsCaller()
    {
        var simulation = WithLog(
            "create procedure inner_p as insert log values (1); select 1/0; insert log values (2)",
            "create procedure outer_p as exec inner_p; insert log values (3)");
        AreEqual(8134, Fails(simulation, "exec outer_p").Number);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, Steps(simulation));
    }

    [TestMethod]
    public void CallerTry_CatchesTheFirstErrorAndAbandonsTheRestOfTheBody()
    {
        var simulation = WithLog("create procedure p as insert log values (1); select 1/0; insert log values (2)");
        simulation.ExecuteBatches("begin try exec p end try begin catch insert log values (9) end catch");
        CollectionAssert.AreEqual(new[] { 1, 9 }, Steps(simulation));
    }

    [TestMethod]
    public void ThrowInABody_EndsTheCallersBatch()
    {
        var simulation = WithLog("create procedure p as insert log values (1); throw 50001, 'x', 1; insert log values (2)");
        AreEqual(50001, Fails(simulation, "exec p; insert log values (3)").Number);
        CollectionAssert.AreEqual(new[] { 1 }, Steps(simulation));
    }

    [TestMethod]
    public void MessagesSentBeforeABodyEndingError_StillReachTheClient()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create procedure p as print 'before'; throw 50001, 'thrown', 1");
        var ex = Fails(simulation, "exec p");
        CollectionAssert.Contains(ex.Errors.Cast<SimulatedError>().Select(error => error.Message).ToArray(), "before");
    }

    [TestMethod]
    public void ResultSetsSentBeforeACaughtError_PrecedeTheCatch()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create procedure p as select 1 as a; select 1/0");
        using var reader = simulation.ExecuteReader("begin try exec p end try begin catch select error_number() end catch");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        // The failing SELECT's own result set — its metadata went out before
        // its first row failed — is empty, then the CATCH's.
        IsTrue(reader.NextResult());
        IsFalse(reader.Read());
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(8134, reader.GetInt32(0));
    }

    [TestMethod]
    public void TriggerBodyResultSet_PrecedesTheFiringStatementsError()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int)",
            "create trigger tr on t after insert as begin select 7 as r; select 1/0 end");
        using var reader = simulation.ExecuteReader("insert t values (1)");
        IsTrue(reader.Read());
        AreEqual(7, reader.GetInt32(0));
        // The failing SELECT sent its metadata, so its error surfaces from the
        // Read on its empty result set (probed 2026-09-25 against SQL Server 2025).
        IsTrue(reader.NextResult());
        AreEqual(8134, Throws<SimulatedSqlException>(() => reader.Read()).Number);
    }

    [TestMethod]
    [DataRow("select 1/0", -6)]
    [DataRow("raiserror('x', 11, 1)", -1)]
    [DataRow("raiserror('x', 14, 1)", -4)]
    [DataRow("raiserror('x', 11, 1); raiserror('y', 16, 1)", -6)]
    [DataRow("raiserror('y', 16, 1); raiserror('x', 11, 1)", -6)]
    [DataRow("select 1/0; return", -6)]
    [DataRow("select 1/0; return 0", 0)]
    [DataRow("select 1/0; return 5", 5)]
    [DataRow("begin try select 1/0 end try begin catch end catch", -6)]
    [DataRow("raiserror('note', 10, 1)", 0)]
    [DataRow("exec inner_p", 0)]
    [DataRow("exec ('select 1/0')", 0)]
    public void ReturnStatus_WithoutAReturnValue_ReportsTheBodysOwnWorstError(string body, int expected)
    {
        var simulation = WithLog("create procedure inner_p as select 1/0", $"create procedure p as {body}");
        RunPastErrors(simulation, "declare @rc int = -99; exec @rc = p; insert log values (@rc)");
        CollectionAssert.AreEqual(new[] { expected }, Steps(simulation));
    }

    [TestMethod]
    public void ReturnNull_ReturnsZeroAndSaysSo()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create procedure p as return null");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<(int Number, string Message)>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SimulatedError>().Select(error => (error.Number, error.Message)));
        AreEqual(0, connection.CreateCommand("declare @rc int = -99; exec @rc = [P]; select @rc").ExecuteScalar());
        CollectionAssert.AreEqual(
            new[] { (282, "The 'p' procedure attempted to return a status of NULL, which is not allowed. A status of 0 will be returned instead.") },
            messages);
    }

    [TestMethod]
    [DataRow("select 1", 0)]
    [DataRow("select 1/0; raiserror(''x'', 11, 1)", 50000)]
    [DataRow("select 1/0; declare @x int", 8134)]
    [DataRow("begin try select 1/0 end try begin catch end catch", 0)]
    [DataRow("select * from nosuch", 208)]
    [DataRow("select 1 +", 102)]
    public void SpExecuteSql_ReturnsTheBatchsFinalErrorNumber(string body, int expected)
    {
        var simulation = WithLog();
        RunPastErrors(simulation, $"declare @rc int = -99; exec @rc = sp_executesql N'{body}'; insert log values (@rc)");
        CollectionAssert.AreEqual(new[] { expected }, Steps(simulation));
    }

    [TestMethod]
    [DataRow("exec with_return", 8134)]
    [DataRow("exec with_success", 0)]
    [DataRow("exec ('select 1/0')", 8134)]
    [DataRow("select 1/0; declare @x int", 8134)]
    [DataRow("select 1/0; declare @t table (a int)", 8134)]
    [DataRow("select 1/0; declare @x int = 5", 0)]
    [DataRow("select 1/0; declare c cursor for select 1", 0)]
    public void AtAtError_AfterAStatement(string statement, int expected)
    {
        var simulation = WithLog(
            "create procedure with_return as select 1/0; return",
            "create procedure with_success as select 1/0; select 1");
        RunPastErrors(simulation, $"{statement}; insert log values (@@error)");
        CollectionAssert.AreEqual(new[] { expected }, Steps(simulation));
    }

    [TestMethod]
    public void AtAtError_ReadsZeroInsideAnIfOrWhileBody()
    {
        var simulation = WithLog();
        RunPastErrors(simulation, "select 1/0; if 1 = 1 insert log values (@@error)");
        RunPastErrors(simulation, "declare @i int = 0; select 1/0; while @i < 1 begin insert log values (@@error); set @i += 1 end");
        CollectionAssert.AreEqual(new[] { 0, 0 }, Steps(simulation));
    }

    [TestMethod]
    public void DynamicSql_RefusesAReturnValue()
        => new Simulation().AssertSqlError("exec ('return 3')", 178);

    [TestMethod]
    public void ExecString_TakesNoReturnCodeVariable()
        => new Simulation().ValidateSyntaxError("declare @rc int; exec @rc = ('select 1')", "(");
}
