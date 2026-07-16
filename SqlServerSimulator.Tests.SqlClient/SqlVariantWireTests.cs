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
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
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
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
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
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
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

    [TestMethod]
    public async Task CastWrappedInnerTypes_ReadOverWire()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
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
}
