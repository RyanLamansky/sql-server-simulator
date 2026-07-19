using System.Data;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sql_variant</c> RPC parameters (TDS type token 0x62): the value is a
/// 4-byte total length (0 = NULL) then the MS-TDS §2.2.5.5.3 body — a base-type
/// token, a property-byte count, the property bytes, then the inner value —
/// decoded into the matching inner <see cref="Storage.SqlValue"/> and wrapped so
/// <c>SQL_VARIANT_PROPERTY(@p,'BaseType')</c> reports the base type SqlClient
/// chose. Per-base-type layouts + the base-type SqlClient assigns each CLR value
/// probe-confirmed against SQL Server 2025 + SqlClient 7.0.2 (2026-07-19):
/// strings (both <c>string</c> and <c>SqlString</c>) promote to <c>nvarchar</c>.
/// </summary>
[TestClass]
public sealed class SqlVariantRpcParameterTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task VariantParameter_PerBaseType_RoundTripsWithBaseType()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await RoundTrip(connection, 42, "int", 42);
        await RoundTrip(connection, 42L, "bigint", 42L);
        await RoundTrip(connection, (short)42, "smallint", (short)42);
        await RoundTrip(connection, (byte)42, "tinyint", (byte)42);
        await RoundTrip(connection, true, "bit", true);
        await RoundTrip(connection, 123.45m, "numeric", 123.45m);
        await RoundTrip(connection, 3.14d, "float", 3.14d);
        await RoundTrip(connection, 3.5f, "real", 3.5f);
        await RoundTrip(connection, "hello", "nvarchar", "hello");
        await RoundTrip(connection, new SqlString("ansi"), "nvarchar", "ansi");
        await RoundTrip(connection, new DateTime(2020, 1, 2, 3, 4, 5, 123), "datetime", new DateTime(2020, 1, 2, 3, 4, 5, 123));
        await RoundTrip(connection, Guid.Parse("11111111-2222-3333-4444-555555555555"), "uniqueidentifier", Guid.Parse("11111111-2222-3333-4444-555555555555"));
        await RoundTrip(connection, new SqlMoney(12.34m), "money", 12.34m);
        await RoundTrip(connection, new byte[] { 1, 2, 3, 4 }, "varbinary", new byte[] { 1, 2, 3, 4 });
        await RoundTrip(connection, new TimeSpan(1, 2, 3), "time", new TimeSpan(1, 2, 3));
    }

    [TestMethod]
    public async Task VariantParameter_Null_ReadsAsNull()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p, SQL_VARIANT_PROPERTY(@p, 'BaseType')", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Variant) { Value = DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(1, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task VariantParameter_DecimalPrecisionScale_SurfaceViaProperty()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select SQL_VARIANT_PROPERTY(@p, 'Precision'), SQL_VARIANT_PROPERTY(@p, 'Scale')", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Variant) { Value = 123.45m });

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        // SqlClient sends a variant decimal as numeric(38, 2).
        AreEqual(38, Convert.ToInt32(reader.GetValue(0)));
        AreEqual(2, Convert.ToInt32(reader.GetValue(1)));
    }

    [TestMethod]
    public async Task VariantParameter_DirectProcedureCall_Binds()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.EchoVariantType @v sql_variant as select SQL_VARIANT_PROPERTY(@v, 'BaseType')");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.EchoVariantType", connection) { CommandType = CommandType.StoredProcedure };
        _ = command.Parameters.Add(new SqlParameter("@v", SqlDbType.Variant) { Value = 99 });

        AreEqual("int", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// Output-direction UDT / sql_variant parameters are a documented residual:
    /// the RETURNVALUE writeback for these types is unmodeled and rejected up
    /// front with a clear error rather than a malformed token that would desync
    /// the client (real SQL Server supports them).
    /// </summary>
    [TestMethod]
    public async Task VariantParameter_OutputDirection_RaisesResidualError()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.SetVariant @v sql_variant output as set @v = cast(5 as int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.SetVariant", connection) { CommandType = CommandType.StoredProcedure };
        _ = command.Parameters.Add(new SqlParameter("@v", SqlDbType.Variant) { Direction = ParameterDirection.Output, Value = DBNull.Value });

        var ex = await ThrowsExactlyAsync<SqlException>(async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        Contains("output CLR UDT / sql_variant", ex.Message);
    }

    private async Task RoundTrip(SqlConnection connection, object value, string expectedBaseType, object expectedValue)
    {
        await using var command = new SqlCommand("select @p, SQL_VARIANT_PROPERTY(@p, 'BaseType')", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Variant) { Value = value });

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Wire.AssertValueEqual(expectedValue, reader.GetValue(0));
        AreEqual(expectedBaseType, reader.GetString(1));
    }
}
