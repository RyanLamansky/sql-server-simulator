using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>EXEC … WITH RESULT SETS</c> projection as it reaches the wire: the
/// declared column names, types and NULL / NOT NULL flags are what
/// COLMETADATA carries, so a real SqlClient reader sees the override rather
/// than the module's own shape. The contract violations arrive as ordinary
/// error tokens.
/// </summary>
[TestClass]
public sealed class WithResultSetsWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DeclaredTypesAndNullability_ReachColMetadata()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p as select 1 as a, 'hello' as b");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "exec dbo.p with result sets ((x varchar(30) not null, y nvarchar(40) null))", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        AreEqual("x", columns[0].ColumnName);
        AreEqual("varchar", columns[0].DataTypeName);
        AreEqual(30, columns[0].ColumnSize);
        IsFalse(columns[0].AllowDBNull);
        AreEqual("y", columns[1].ColumnName);
        AreEqual("nvarchar", columns[1].DataTypeName);
        AreEqual(40, columns[1].ColumnSize);
        IsTrue(columns[1].AllowDBNull);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("1", reader.GetString(0));
        AreEqual("hello", reader.GetString(1));
    }

    [TestMethod]
    public async Task UnspecifiedNullability_IsNullableOnTheWire()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p as select 1 as a");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("exec dbo.p with result sets ((x bigint))", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        AreEqual("bigint", columns[0].DataTypeName);
        IsTrue(columns[0].AllowDBNull);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1L, reader.GetInt64(0));
    }

    [TestMethod]
    public async Task ContractViolation_SurfacesAsASqlError()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.p as select 1 as a, 'hello' as b");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("exec dbo.p with result sets ((x int))", connection);
        var exception = await ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync(TestContext.CancellationToken));
        AreEqual(11537, exception.Number);
        AreEqual("dbo.p", exception.Procedure);
    }
}
