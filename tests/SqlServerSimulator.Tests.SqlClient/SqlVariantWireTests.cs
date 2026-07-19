using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sql_variant</c> over the wire, read by real Microsoft.Data.SqlClient:
/// the per-row inner types on <c>sys.database_scoped_configurations</c>
/// (bool / int / DBNull), the exact DacFx bacpac-export LEFT-JOIN shape whose
/// <c>(bool)reader[…]</c> unbox drove the whole bundle, and CAST-wrapped
/// smallint / nvarchar / bit / NULL inner values.
/// </summary>
[TestClass]
public sealed class SqlVariantWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DscValues_ReadOverWire_WithPerRowInnerTypes()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "SELECT name, value FROM sys.database_scoped_configurations ORDER BY configuration_id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("sql_variant", reader.GetDataTypeName(1));

        var byName = new Dictionary<string, object?>();
        while (await reader.ReadAsync(TestContext.CancellationToken))
            byName[(string)reader.GetValue(0)] = reader.GetValue(1);

        _ = IsInstanceOfType<int>(byName["MAXDOP"]!);
        AreEqual(0, byName["MAXDOP"]);
        _ = IsInstanceOfType<bool>(byName["LEGACY_CARDINALITY_ESTIMATION"]!);
        IsFalse((bool)byName["LEGACY_CARDINALITY_ESTIMATION"]!);
        IsTrue((bool)byName["PARAMETER_SNIFFING"]!);
        IsFalse((bool)byName["QUERY_OPTIMIZER_HOTFIXES"]!);
    }

    [TestMethod]
    public async Task DscValueForSecondary_ReadOverWire_IsDbNull()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "SELECT value_for_secondary FROM sys.database_scoped_configurations WHERE name = 'MAXDOP'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
    }

    // The DacFx SqlDatabaseOptions reverse-engineering shape: a bit-valued
    // scoped-configuration variant read as [value] and unboxed with
    // (bool)reader[...]. This is the exact cast that threw
    // "Unable to cast object of type 'System.String' to type 'System.Boolean'"
    // before sql_variant was modeled.
    [TestMethod]
    public async Task DacFxLegacyCardinalityEstimation_UnboxesToBool()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("""
            SELECT [dbscl].[value] AS [LegacyCardinalityEstimation]
            FROM [sys].[database_scoped_configurations] AS [anchor]
            LEFT JOIN [sys].[database_scoped_configurations] AS [dbscl] WITH (NOLOCK)
                ON [dbscl].[name] = N'LEGACY_CARDINALITY_ESTIMATION'
            WHERE [anchor].[name] = N'MAXDOP'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsFalse((bool)reader["LegacyCardinalityEstimation"]);
    }

    // SERVERPROPERTY projects sql_variant over the wire (0x62 COLMETADATA) like
    // real; each cell carries its probed inner base type — EngineEdition an int,
    // Edition an nvarchar string.
    [TestMethod]
    public async Task ServerProperty_ReadOverWire_ReportsVariantWithInnerTypes()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "SELECT SERVERPROPERTY('EngineEdition') AS engine, SERVERPROPERTY('Edition') AS edition", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("sql_variant", reader.GetDataTypeName(0));
        AreEqual("sql_variant", reader.GetDataTypeName(1));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        _ = IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(3, reader.GetValue(0));
        AreEqual("Developer Edition (64-bit)", reader.GetValue(1));
    }

    // SESSION_CONTEXT round-trips the stored value's base type through the
    // sql_variant wire form: an int stored reads back as a boxed int.
    [TestMethod]
    public async Task SessionContext_ReadOverWire_PreservesIntInner()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var set = new SqlCommand("exec sp_set_session_context N'tenant', 42", connection))
            _ = await set.ExecuteNonQueryAsync(TestContext.CancellationToken);
        await using var command = new SqlCommand("SELECT SESSION_CONTEXT(N'tenant')", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("sql_variant", reader.GetDataTypeName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        _ = IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(42, reader.GetValue(0));
    }

    [TestMethod]
    public async Task CastWrappedInnerTypes_ReadOverWire()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT CAST(CAST(5 AS smallint) AS sql_variant) AS s,
                   CAST(N'OFF' AS sql_variant) AS n,
                   CAST(CAST(1 AS bit) AS sql_variant) AS b,
                   CAST(CAST(NULL AS int) AS sql_variant) AS z
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        _ = IsInstanceOfType<short>(reader.GetValue(0));
        AreEqual((short)5, reader.GetValue(0));
        AreEqual("OFF", reader.GetValue(1));
        IsTrue((bool)reader.GetValue(2));
        IsTrue(await reader.IsDBNullAsync(3, TestContext.CancellationToken));
    }

    // ---- Output direction ----

    // RETURNVALUE for an output sql_variant parameter: TYPE_INFO 0x62 + ULONG
    // max length, value = ULONG total length (0 = NULL) + the self-describing
    // variant body — probe-captured against SQL Server 2025 + SqlClient 7.0.2
    // (2026-07-19), the same forms a variant result column carries.

    private static async Task<object> RunVariantOutputProc(Simulation simulation, int which, CancellationToken token)
    {
        await using var listener = await simulation.ListenLocalAsync(0, token);
        await using var connection = await Wire.OpenAsync(listener, token);
        await using var command = new SqlCommand("dbo.get_v", connection) { CommandType = CommandType.StoredProcedure };
        _ = command.Parameters.AddWithValue("@which", which);
        var output = command.Parameters.Add(new SqlParameter("@v", SqlDbType.Variant) { Direction = ParameterDirection.Output });
        _ = await command.ExecuteNonQueryAsync(token);
        return output.Value;
    }

    private static Simulation CreateVariantOutputSim()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create proc dbo.get_v @which int, @v sql_variant output as
            if @which = 1 set @v = 4242
            else if @which = 2 set @v = cast(N'hello' as nvarchar(20))
            else if @which = 3 set @v = cast(1.5 as decimal(5,2))
            """);
        return simulation;
    }

    [TestMethod]
    public async Task VariantOutput_IntInner_ReadsAsInt()
        => AreEqual(4242, await RunVariantOutputProc(CreateVariantOutputSim(), 1, TestContext.CancellationToken));

    [TestMethod]
    public async Task VariantOutput_NvarcharInner_ReadsAsString()
        => AreEqual("hello", await RunVariantOutputProc(CreateVariantOutputSim(), 2, TestContext.CancellationToken));

    [TestMethod]
    public async Task VariantOutput_DecimalInner_ReadsAsDecimal()
        => AreEqual(1.50m, await RunVariantOutputProc(CreateVariantOutputSim(), 3, TestContext.CancellationToken));

    [TestMethod]
    public async Task VariantOutput_NullValue_ReadsAsDbNull()
        => _ = IsInstanceOfType<DBNull>(await RunVariantOutputProc(CreateVariantOutputSim(), 0, TestContext.CancellationToken));

    [TestMethod]
    public async Task VariantOutput_ThroughTextCommand_PreservesFloatInner()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("set @v = cast(3.25 as float)", connection);
        var output = command.Parameters.Add(new SqlParameter("@v", SqlDbType.Variant) { Direction = ParameterDirection.Output });
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(3.25d, output.Value);
    }
}
