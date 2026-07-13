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
}
