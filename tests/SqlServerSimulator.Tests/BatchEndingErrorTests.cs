using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Errors that end the whole batch rather than their statement: those a trigger
/// body leaves unhandled, since the body starts under <c>XACT_ABORT ON</c>, and
/// the run-time errors that behave as under that option whatever it says
/// (probed 2026-09-24 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class BatchEndingErrorTests
{
    private static DbConnection WithLog(params ReadOnlySpan<string> batches)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table log (step int)");
        simulation.ExecuteBatches(batches);
        return simulation.CreateOpenConnection();
    }

    /// <summary>Runs a batch whose statement errors are incidental; it has run as far as it goes by the time they surface.</summary>
    private static void RunPastErrors(DbConnection connection, string commandText)
    {
        try
        {
            _ = connection.CreateCommand(commandText).ExecuteNonQuery();
        }
        catch (SimulatedSqlException)
        {
        }
    }

    private static int[] Steps(DbConnection connection)
    {
        using var reader = connection.CreateCommand("select step from log order by step").ExecuteReader();
        return [.. reader.EnumerateRecords().Select(record => record.GetInt32(0))];
    }

    private const string FiringTable = "create table t (a int)";

    [TestMethod]
    public void TriggerBody_StartsUnderXactAbort()
    {
        using var connection = WithLog(FiringTable, "create trigger tr on t after insert as insert log values (@@options & 16384)");
        _ = connection.CreateCommand("set xact_abort off; insert t values (1)").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 16384 }, Steps(connection));
    }

    [TestMethod]
    public void UnhandledTriggerError_EndsTheBatchAndRollsBack()
    {
        using var connection = WithLog(FiringTable, "create trigger tr on t after insert as select 1/0");
        RunPastErrors(connection, "begin tran; insert log values (1); insert t values (1); insert log values (2)");
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        IsEmpty(Steps(connection));
    }

    [TestMethod]
    public void ErrorFromAProcedureTheTriggerCalls_EndsTheBatch()
    {
        using var connection = WithLog(
            FiringTable,
            "create procedure p as select 1/0; insert log values (1)",
            "create trigger tr on t after insert as exec p");
        RunPastErrors(connection, "insert t values (1); insert log values (2)");
        IsEmpty(Steps(connection));
    }

    [TestMethod]
    public void RaiserrorInATriggerBody_RunsOnAndKeepsTheRow()
    {
        using var connection = WithLog(FiringTable, "create trigger tr on t after insert as begin raiserror('r', 16, 1); insert log values (1) end");
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t values (1); insert log values (2)").ExecuteNonQuery());
        AreEqual(50000, ex.Number);
        CollectionAssert.AreEqual(new[] { 1, 2 }, Steps(connection));
        AreEqual(1, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void TriggerBodyTurningXactAbortOff_RunsOnPastItsError()
    {
        using var connection = WithLog(FiringTable, "create trigger tr on t after insert as begin set xact_abort off; select 1/0; insert log values (1) end");
        RunPastErrors(connection, "insert t values (1); insert log values (2)");
        CollectionAssert.AreEqual(new[] { 1, 2 }, Steps(connection));
    }

    [TestMethod]
    public void UnhandledTriggerError_IsFollowedByStatementTerminated()
    {
        using var connection = WithLog(FiringTable, "create trigger tr on t after insert as insert log values (1/0)");
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t values (1)").ExecuteNonQuery());
        CollectionAssert.AreEqual(new[] { 8134, 3621 }, ex.Errors.Cast<SimulatedError>().Select(error => error.Number).ToArray());
    }

    [TestMethod]
    [DataRow("declare @s varchar(9) = 'a'; declare @m money = cast(@s as money)", 235)]
    [DataRow("declare @i int = 13; declare @d date = datefromparts(2024, @i, 1)", 289)]
    [DataRow("declare @i int = 13; declare @d smalldatetime = smalldatetimefromparts(2024, @i, 1, 0, 0)", 289)]
    [DataRow("declare @p nvarchar(9) = N'x'; declare @v nvarchar(9) = json_value(N'{}', @p)", 13607)]
    [DataRow("declare @p nvarchar(20) = N'strict $.a'; declare @v nvarchar(9) = json_value(N'{}', @p)", 13608)]
    [DataRow("declare @j nvarchar(9) = N'x'; declare @v nvarchar(9) = json_value(@j, '$')", 13609)]
    [DataRow("declare @p nvarchar(20) = N'append strict $.a'; declare @v nvarchar(99) = json_modify(N'{\"a\":1}', @p, 1)", 13621)]
    [DataRow("declare @p nvarchar(20) = N'strict $.a'; declare @v nvarchar(99) = json_value(N'{\"a\":{}}', @p)", 13623)]
    [DataRow("declare @p nvarchar(20) = N'strict $.a'; declare @v nvarchar(99) = json_query(N'{\"a\":1}', @p)", 13624)]
    [DataRow("exec ('create table existing (a int)')", 2714)]
    [DataRow("select 1 as a into existing", 2714)]
    [DataRow("exec ('create type existing_type from int')", 219)]
    [DataRow("exec ('create unique index ix on duplicates (a)')", 1505)]
    public void RunTimeError_EndsTheBatchAndRollsBack_OrDoomsWhenCaught(string statement, int number)
    {
        using var connection = WithLog(
            "create table existing (a int)",
            "create type existing_type from int",
            "create table duplicates (a int); insert duplicates values (1), (1)");

        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(
            $"begin tran; insert log values (1); {statement}; insert log values (2)").ExecuteNonQuery());
        AreEqual(number, ex.Number);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        IsEmpty(Steps(connection));

        RunPastErrors(connection, $"""
            begin tran;
            begin try {statement} end try begin catch end catch;
            declare @state int = xact_state();
            rollback;
            insert log values (@state)
            """);
        CollectionAssert.AreEqual(new[] { -1 }, Steps(connection));
    }

    [TestMethod]
    public void DuplicateSynonym_EndsOnlyItsStatement()
    {
        using var connection = WithLog("create synonym s for log");
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand(
            "begin tran; insert log values (1); exec ('create synonym s for log'); insert log values (2); commit").ExecuteNonQuery());
        AreEqual(2714, ex.Number);
        CollectionAssert.AreEqual(new[] { 1, 2 }, Steps(connection));
    }
}
