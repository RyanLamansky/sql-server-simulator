using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="CommandType.StoredProcedure"/> invocations arrive as name-form
/// RPC requests: result sets stream back, OUTPUT parameters and the RETURN code
/// surface in trailing tokens, RAISERROR becomes a <see cref="SqlException"/>,
/// PRINT reaches <see cref="SqlConnection.InfoMessage"/>, and a missing required
/// parameter raises the same Msg 201 the in-process engine does. Procedures are
/// created through the in-process DDL surface, then called over the wire.
/// </summary>
[TestClass]
public sealed class StoredProcedureRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    private static SqlCommand ProcCommand(string name, SqlConnection connection)
    {
        var command = new SqlCommand(name, connection) { CommandType = CommandType.StoredProcedure };
        return command;
    }

    [TestMethod]
    public async Task ResultSet_WithInputParameters()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p @a int, @b int as select @a as sum1, @a + @b as sum2");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = ProcCommand("dbo.p", connection);
        _ = command.Parameters.AddWithValue("@a", 10);
        _ = command.Parameters.AddWithValue("@b", 5);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(10, reader.GetInt32(0));
        AreEqual(15, reader.GetInt32(1));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task OutputParameter()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p @x int, @out int output as set @out = @x * 10");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = ProcCommand("dbo.p", connection);
        _ = command.Parameters.AddWithValue("@x", 7);
        var output = command.Parameters.Add("@out", SqlDbType.Int);
        output.Direction = ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(70, output.Value);
    }

    [TestMethod]
    public async Task ReturnCode_ReadViaReturnValueParameter()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p as return 7");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = ProcCommand("dbo.p", connection);
        var returnValue = command.Parameters.Add("@rc", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(7, returnValue.Value);
    }

    [TestMethod]
    public async Task OutputAndReturnCode_Together()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p @x int, @out int output as set @out = @x * 10; return @x + 1");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = ProcCommand("dbo.p", connection);
        _ = command.Parameters.AddWithValue("@x", 7);
        var output = command.Parameters.Add("@out", SqlDbType.Int);
        output.Direction = ParameterDirection.Output;
        var returnValue = command.Parameters.Add("@rc", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(70, output.Value);
        AreEqual(8, returnValue.Value);
    }

    [TestMethod]
    public async Task Raiserror_Severity16_ThrowsSqlException()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p as raiserror('boom from proc', 16, 1)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = ProcCommand("dbo.p", connection);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        });

        AreEqual(50000, ex.Number);
        AreEqual((byte)16, ex.Class);
        Contains("boom from proc", ex.Message);
    }

    // FAILING — documents an engine-level fidelity gap, not a wire-path one. A
    // PRINT inside a stored-procedure body is not surfaced on the connection's
    // InfoMessage (confirmed identical in-process: the proc body's child batch
    // never forwards the message to the outer connection). Real SQL Server
    // delivers proc-body PRINT to the client. Note the contrast with
    // TopLevelPrint_ViaSpExecuteSql_ReachesInfoMessage below, which passes: the
    // RPC info-message flush itself works; only proc-body PRINTs are dropped.
    [TestMethod]
    public async Task ProcBodyPrint_ReachesInfoMessage()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p as print 'hello from proc'");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                messages.Add(error.Message);
        };

        await using var command = ProcCommand("dbo.p", connection);
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        Contains("hello from proc", messages);
    }

    [TestMethod]
    public async Task TopLevelPrint_ViaSpExecuteSql_ReachesInfoMessage()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var messages = new List<string>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                messages.Add(error.Message);
        };

        // Parameterized => sp_executesql RPC path; the PRINT is top-level.
        await using var command = new SqlCommand("print @msg", connection);
        _ = command.Parameters.AddWithValue("@msg", "hello over rpc");
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        Contains("hello over rpc", messages);
    }

    [TestMethod]
    public async Task MissingRequiredParameter_RaisesMsg201()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p @x int as select @x");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = ProcCommand("dbo.p", connection);
            _ = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        });

        AreEqual(201, ex.Number);
    }
}
