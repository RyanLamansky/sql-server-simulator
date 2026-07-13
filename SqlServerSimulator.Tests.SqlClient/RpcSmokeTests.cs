using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// First-light checks for the RPC path: parameterized commands arrive as
/// sp_executesql RPC requests, not batches.
/// </summary>
[TestClass]
public sealed class RpcSmokeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ParameterizedSelect_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select @a + @b", connection);
        _ = command.Parameters.AddWithValue("@a", 40);
        _ = command.Parameters.AddWithValue("@b", 2);
        AreEqual(42, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ParameterizedInsertAndReadBack_StringParameter()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int, name nvarchar(50))");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var insert = new SqlCommand("insert t values (@id, @name)", connection))
        {
            _ = insert.Parameters.AddWithValue("@id", 7);
            _ = insert.Parameters.AddWithValue("@name", "café アイウ");
            AreEqual(1, await insert.ExecuteNonQueryAsync(TestContext.CancellationToken));
        }

        await using var select = new SqlCommand("select name from t where id = @id", connection);
        _ = select.Parameters.AddWithValue("@id", 7);
        AreEqual("café アイウ", await select.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task OutputParameter_ThroughSpExecuteSql()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @result = @input * 3", connection);
        _ = command.Parameters.AddWithValue("@input", 14);
        var result = command.Parameters.Add("@result", System.Data.SqlDbType.Int);
        result.Direction = System.Data.ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        AreEqual(42, result.Value);
    }

    [TestMethod]
    public async Task PreparedCommand_ExecutesRepeatedly()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("insert t values (@v)", connection);
        var parameter = command.Parameters.Add("@v", System.Data.SqlDbType.Int);
        await command.PrepareAsync(TestContext.CancellationToken);
        for (var i = 1; i <= 3; i++)
        {
            parameter.Value = i;
            AreEqual(1, await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        }

        await using var sum = new SqlCommand("select sum(id) from t", connection);
        AreEqual(6, await sum.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
