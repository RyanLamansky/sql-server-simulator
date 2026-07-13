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
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
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

    // A table-valued parameter (SqlDbType.Structured) is decoded to the 0xF3
    // wire token, which the RPC parser rejects. The unsupported-type error
    // surfaces as Msg 50000 naming the table-valued form. Asserts the observed
    // behavior; the message text documents the surface.
    [TestMethod]
    public async Task TableValuedParameter_SurfacesAsUnsupported()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var table = new DataTable();
        _ = table.Columns.Add("id", typeof(int));
        _ = table.Rows.Add(1);
        _ = table.Rows.Add(2);

        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("select count(*) from @t", connection);
            var parameter = command.Parameters.AddWithValue("@t", table);
            parameter.SqlDbType = SqlDbType.Structured;
            parameter.TypeName = "dbo.IntList";
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken);
        });

        AreEqual(50000, ex.Number);
        Contains("table-valued", ex.Message);
    }

    [TestMethod]
    public async Task OutputParameterWithErrorInSameStatement_ThrowsAndLeavesOutputUnwritten()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @out = 5; select @a / @b", connection);
        _ = command.Parameters.AddWithValue("@a", 1);
        _ = command.Parameters.AddWithValue("@b", 0);
        var output = command.Parameters.Add("@out", SqlDbType.Int);
        output.Direction = ParameterDirection.Output;

        var ex = await Assert.ThrowsAsync<SqlException>(async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(8134, ex.Number);

        // The statement faulted before the RETURNVALUE token was written, so the
        // output parameter is never assigned the server-side value.
        IsNull(output.Value);
    }
}
