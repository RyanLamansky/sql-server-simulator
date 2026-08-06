using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>decimal</c> / <c>numeric</c> value past a .NET <see cref="decimal"/>'s
/// range crossing the TDS endpoint. Real SqlClient reads it at full 38-digit
/// fidelity through <c>GetSqlDecimal</c> and sheds-or-raises through
/// <c>GetDecimal</c> — the same split it takes against a real server
/// (probe-confirmed, SqlClient 7.0.2 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class WideDecimalWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ThirtyEightDigits_ReadFullFidelityThroughGetSqlDecimal()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (v decimal(38, 0) not null);
            insert t values (12345678901234567890123456789012345678)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        var value = reader.GetSqlDecimal(0);
        AreEqual("12345678901234567890123456789012345678", value.ToString());
        AreEqual((byte)38, value.Precision);
        AreEqual((byte)0, value.Scale);
    }

    [TestMethod]
    public async Task DeclaredScaleThirty_KeepsEveryZeroOnTheWire()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (v decimal(38, 30) not null);
            insert t values (cast('0.123456789012345678901234567890' as decimal(38, 30)))
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("0.123456789012345678901234567890", reader.GetSqlDecimal(0).ToString());
    }

    /// <summary>
    /// The client accessor sheds trailing fractional zeros to fit, silently —
    /// SqlClient's own reader behavior, which the in-process reader mirrors.
    /// </summary>
    [TestMethod]
    public async Task GetDecimal_ShedsTrailingZerosToFit()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (v decimal(38, 30) not null);
            insert t values (1)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        var value = reader.GetDecimal(0);
        AreEqual(1m, value);
        AreEqual(28, value.Scale);
    }
}
