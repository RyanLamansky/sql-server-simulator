using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Error surfaces specific to the RPC path: a parameterized statement that
/// faults mid-execution, an unsupported table-valued parameter, and an error
/// raised in the same statement that writes an output parameter.
/// </summary>
[TestClass]
public sealed class RpcErrorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ParameterizedDivideByZero_Number8134()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select @a / @b", connection);
            _ = command.Parameters.AddWithValue("@a", 1);
            _ = command.Parameters.AddWithValue("@b", 0);
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken);
        });

        AreEqual(8134, ex.Number);
    }

    [TestMethod]
    public async Task OutputParameterBeforeErrorInBatch_ThrowsButWritesOutput()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @out = 5; select @a / @b", connection);
        _ = command.Parameters.AddWithValue("@a", 1);
        _ = command.Parameters.AddWithValue("@b", 0);
        var output = command.Parameters.Add("@out", SqlDbType.Int);
        output.Direction = ParameterDirection.Output;

        var ex = await Assert.ThrowsAsync<SqlException>(async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(8134, ex.Number);

        // The `set @out = 5` ran, then the divide-by-zero (severity 16) ended
        // its own statement but let the RPC continue — so the RETURNVALUE token
        // still carries the assigned value. Probed against SQL Server 2025
        // (2026-07-14): real SqlClient reports output.Value == 5 here, matching
        // this. (Before wire error-continuation the simulator left it unwritten,
        // which diverged from the reference.)
        AreEqual(5, output.Value);
    }

    [TestMethod]
    public async Task CSharpNullValue_IsNotSupplied_8178()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select @a, @b", connection);
        _ = command.Parameters.Add(new SqlParameter("@a", SqlDbType.Int) { Value = 1 });
        _ = command.Parameters.Add(new SqlParameter("@b", SqlDbType.NVarChar, -1) { Value = null });

        var ex = await Assert.ThrowsAsync<SqlException>(async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(8178, ex.Number);
        AreEqual(0, ex.LineNumber);
        AreEqual("The parameterized query '(@a int,@b nvarchar(max) )select @a, @b' expects the parameter '@b', which was not supplied.", ex.Message);

        // The session runs on, and DBNull.Value is a supplied NULL.
        command.Parameters["@b"].Value = DBNull.Value;
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CSharpNullValue_StoredProcedure_TakesItsDefault()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure zzd @x int = 7 output as begin select @x; set @x = 9 end");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("zzd", connection) { CommandType = CommandType.StoredProcedure };
        var x = command.Parameters.Add(new SqlParameter("@x", SqlDbType.Int) { Direction = ParameterDirection.InputOutput, Value = null });

        AreEqual(7, await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(9, x.Value);
    }
}
