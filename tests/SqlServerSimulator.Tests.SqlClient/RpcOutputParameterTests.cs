using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Output-parameter writeback over the RPC path: SqlClient marks a parameter
/// ByRef, the simulator runs the sp_executesql statement, and the mutated value
/// returns in a RETURNVALUE token. Values whose coercion is nontrivial, and the
/// "statement never assigned it" case, are checked against the same simulation's
/// in-process ADO surface (the dual-read oracle).
/// </summary>
[TestClass]
public sealed class RpcOutputParameterTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task IntOutput_Assigned()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @out = @input * 3", connection);
        _ = command.Parameters.AddWithValue("@input", 14);
        var output = command.Parameters.Add("@out", SqlDbType.Int);
        output.Direction = ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(42, output.Value);
    }

    [TestMethod]
    public async Task NVarcharOutput_Assigned()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @out = N'café アイウ'", connection);
        var output = command.Parameters.Add("@out", SqlDbType.NVarChar, 50);
        output.Direction = ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual("café アイウ", output.Value);
    }

    [TestMethod]
    public async Task DecimalOutput_Assigned_DualRead()
    {
        var simulation = new Simulation();
        var oracle = Wire.OutputInProc(simulation, "set @out = 123.45", "@out", DbType.Decimal, ParameterDirection.Output, null);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @out = 123.45", connection);
        var output = command.Parameters.Add("@out", SqlDbType.Decimal);
        output.Precision = 10;
        output.Scale = 2;
        output.Direction = ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(oracle, output.Value);
    }

    [TestMethod]
    public async Task DateTime2Output_Assigned_DualRead()
    {
        var simulation = new Simulation();
        const string sql = "set @out = cast('2024-02-29T13:45:30.1234567' as datetime2(7))";
        var oracle = Wire.OutputInProc(simulation, sql, "@out", DbType.DateTime2, ParameterDirection.Output, null);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand(sql, connection);
        var output = command.Parameters.Add("@out", SqlDbType.DateTime2);
        output.Direction = ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(oracle, output.Value);
    }

    [TestMethod]
    public async Task UniqueIdentifierOutput_Assigned()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @out = cast('6F9619FF-8B86-D011-B42D-00C04FC964FF' as uniqueidentifier)", connection);
        var output = command.Parameters.Add("@out", SqlDbType.UniqueIdentifier);
        output.Direction = ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(new Guid("6F9619FF-8B86-D011-B42D-00C04FC964FF"), output.Value);
    }

    [TestMethod]
    public async Task InputOutput_ValueInChangedOut()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @out = @out * 2", connection);
        var output = command.Parameters.Add("@out", SqlDbType.Int);
        output.Direction = ParameterDirection.InputOutput;
        output.Value = 21;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(42, output.Value);
    }

    [TestMethod]
    public async Task OutputNeverAssigned_MatchesInProc_DualRead()
    {
        var simulation = new Simulation();
        // Statement does not touch @out; whatever the engine writes back (input
        // value vs NULL) is the contract, so trust the in-process surface.
        var oracle = Wire.OutputInProc(simulation, "select 1", "@out", DbType.Int32, ParameterDirection.Output, null);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select 1", connection);
        var output = command.Parameters.Add("@out", SqlDbType.Int);
        output.Direction = ParameterDirection.Output;
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.CancellationToken))
            {
            }
        }

        AreEqual(oracle ?? DBNull.Value, output.Value);
    }
}
