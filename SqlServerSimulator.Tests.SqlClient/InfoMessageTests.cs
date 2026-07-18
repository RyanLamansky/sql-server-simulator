using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Low-severity server messages (PRINT, RAISERROR at severity &lt;= 10) arrive
/// on <see cref="SqlConnection.InfoMessage"/> rather than as exceptions.
/// </summary>
[TestClass]
public sealed class InfoMessageTests
{
    public TestContext TestContext { get; set; } = null!;

    private static List<string> Subscribe(SqlConnection connection)
    {
        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                messages.Add(error.Message);
        };

        return messages;
    }

    [TestMethod]
    public async Task Print_RaisesInfoMessage()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var messages = Subscribe(connection);

        await using var command = new SqlCommand("print 'hello'", connection);
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        Contains("hello", messages);
    }

    [TestMethod]
    public async Task RaiseError_LowSeverity_ArrivesAsInfoMessage()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var messages = Subscribe(connection);

        await using var command = new SqlCommand("raiserror('warn', 5, 1)", connection);
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        Contains("warn", messages);
    }

    [TestMethod]
    public async Task Print_InfoToken_CarriesBatchRelativeLineNumber()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var lines = new List<int>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                lines.Add(error.LineNumber);
        };

        await using var command = new SqlCommand("select 1\nprint 'hello'", connection);
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            while (await reader.NextResultAsync(TestContext.CancellationToken))
            {
            }
        }

        // PRINT is the batch's second line; the INFO token carries that line.
        AreEqual(2, lines.Single());
    }

    // The go-sqlcmd shakedown (2026-07-14) exposed a token-stream desync when
    // an INFO token shares a SQLBatch response with a result set: SqlClient
    // hung until command timeout on every mixed shape below (go-mssqldb
    // instead silently dropped the message). Real SQL Server gives the
    // message-producing statement its own DONE and never emits INFO after
    // the final DONE. CommandTimeout is set so a regression fails the test
    // instead of stalling the suite.

    private async Task<(List<string> Messages, List<int> Values)> DrainBatch(string sql)
    {
        var simulation = new Simulation();
        _ = Wire.ReadAllInProc(simulation, "create table t (id int); insert t values (1)");
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var messages = Subscribe(connection);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        var values = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                    values.Add(reader.GetInt32(0));
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        }

        return (messages, values);
    }

    [TestMethod]
    public async Task Print_ThenSelect_DeliversInfoAndRows()
    {
        var (messages, values) = await DrainBatch("print 'before select'; select 42");
        Contains("before select", messages);
        AreEqual(42, values.Single());
    }

    [TestMethod]
    public async Task Select_ThenPrint_DeliversRowsAndInfo()
    {
        var (messages, values) = await DrainBatch("select 42; print 'after select'");
        Contains("after select", messages);
        AreEqual(42, values.Single());
    }

    [TestMethod]
    public async Task RaiseError_LowSeverity_ThenSelect_DeliversInfoAndRows()
    {
        var (messages, values) = await DrainBatch("raiserror('warn', 10, 1); select 42");
        Contains("warn", messages);
        AreEqual(42, values.Single());
    }

    [TestMethod]
    public async Task Print_BetweenOutcomeStatements_DeliversAll()
    {
        var (messages, values) = await DrainBatch("insert t values (2); print 'mid'; select id from t order by id");
        Contains("mid", messages);
        CollectionAssert.AreEqual(new[] { 1, 2 }, values);
    }
}
