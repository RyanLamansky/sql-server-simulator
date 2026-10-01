using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The Msg 208 a compile sends for a scalar function call it couldn't inline
/// goes out ahead of everything the batch runs and carries no DONE of its own:
/// the next DONE carries its error bit and drops its count flag, so that
/// statement raises no <c>StatementCompleted</c> and adds nothing to
/// <c>RecordsAffected</c> (probed 2026-09-30 against SQL Server 2025 through
/// SqlClient 7).
/// </summary>
[TestClass]
public sealed class ScalarUdfInliningWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation WithBrokenFunction()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        foreach (var batch in (string[])["create table t (a int); insert t values (1), (2), (3)", "create function dbo.f() returns int as\nbegin\n  declare @x int;\n  select @x = count(*) from nosuch;\n  return @x;\nend"])
        {
            using var command = connection.CreateCommand();
            command.CommandText = batch;
            _ = command.ExecuteNonQuery();
        }
        return simulation;
    }

    [TestMethod]
    public async Task TheFailure_TakesTheNextStatementsCount()
    {
        await using var listener = await WithBrokenFunction().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        connection.FireInfoMessageEventOnUserErrors = true;
        List<string> sent = [];
        connection.InfoMessage += (_, e) => sent.AddRange(e.Errors.Cast<SqlError>().Select(static error => $"{error.Number} L{error.LineNumber} {error.Procedure}"));
        await using var command = new SqlCommand("update t set a = a; select 1 where 1 = 0 and dbo.f() = 1; update t set a = a", connection);
        command.StatementCompleted += (_, e) => sent.Add($"completed {e.RecordCount}");

        AreEqual(3, await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual("208 L4 f / completed 0 / completed 3", string.Join(" / ", sent));
    }

    [TestMethod]
    public async Task TheFailure_PrecedesAnEarlierStatementsRows()
    {
        await using var listener = await WithBrokenFunction().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        connection.FireInfoMessageEventOnUserErrors = true;
        List<string> sent = [];
        connection.InfoMessage += (_, e) => sent.AddRange(e.Errors.Cast<SqlError>().Select(static error => $"{error.Number} L{error.LineNumber} {error.Procedure}"));
        await using var command = new SqlCommand("select 'a'; select dbo.f()", connection);
        command.StatementCompleted += (_, e) => sent.Add($"completed {e.RecordCount}");

        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                    sent.Add($"row {reader.GetValue(0)}");
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        }
        AreEqual("208 L4 f / row a / 208 L1 ", string.Join(" / ", sent));
    }

    /// <summary>
    /// Without the event, <c>ExecuteReader</c> raises the failure with
    /// everything the rest of the batch raised, as SqlClient drains a
    /// response it hands no reader for.
    /// </summary>
    [TestMethod]
    public async Task ExecuteReader_RaisesTheFailureWithTheRestOfTheBatch()
    {
        await using var listener = await WithBrokenFunction().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 'a'; select dbo.f()", connection);
        var error = await ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync(TestContext.CancellationToken));
        AreEqual("208 f, 208 ", string.Join(", ", error.Errors.Cast<SqlError>().Select(static e => $"{e.Number} {e.Procedure}")));
    }
}
