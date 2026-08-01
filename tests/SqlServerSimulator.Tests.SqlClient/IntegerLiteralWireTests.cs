using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// COLMETADATA for integer literals as a real SqlClient reader observes it.
/// SQL Server types a bare integer literal <c>int</c> while it fits and
/// <c>numeric(digit_count, 0)</c> past that — never <c>bigint</c>, which only
/// a CAST reaches — so <c>SELECT 3000000000</c> advertises the NUMERICN wire
/// type at precision 10, scale 0 (probe-confirmed 2026-08-01 against SQL
/// Server 2025 via <c>sp_describe_first_result_set</c>, which reports
/// <c>numeric(10,0)</c> / <c>system_type_id</c> 108 / <c>max_length</c> 9).
/// </summary>
[TestClass]
public sealed class IntegerLiteralWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private async Task<(string TypeName, short Precision, short Scale, object? Value)> DescribeAsync(string sql)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var column = reader.GetColumnSchema()[0];
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        return (reader.GetDataTypeName(0), (short)(column.NumericPrecision ?? 0), (short)(column.NumericScale ?? 0), reader.GetValue(0));
    }

    [TestMethod]
    [DataRow("select 2147483647 as x", "int")]
    [DataRow("select -2147483648 as x", "int")]
    [DataRow("select 0 as x", "int")]
    public async Task WithinIntRange_AdvertisesInt(string sql, string typeName)
        => AreEqual(typeName, (await DescribeAsync(sql)).TypeName);

    /// <remarks>
    /// SqlClient maps the NUMERICN and DECIMALN wire tokens to one
    /// <c>SqlDbType.Decimal</c>, so <c>GetDataTypeName</c> reads
    /// <c>decimal</c> for both against a real server too; the precision is
    /// what separates this from the <c>bigint</c> the literal never becomes
    /// (which would advertise precision 19). The <c>numeric</c> spelling
    /// itself is asserted through the in-process reader in
    /// <c>DecimalTypeNameTests</c>.
    /// </remarks>
    [TestMethod]
    [DataRow("select 2147483648 as x", 10)]
    [DataRow("select 3000000000 as x", 10)]
    [DataRow("select 9999999999 as x", 10)]
    [DataRow("select 10000000000 as x", 11)]
    [DataRow("select 99999999999999999999 as x", 20)]
    public async Task PastIntRange_AdvertisesNumericAtDigitCount(string sql, int precision)
    {
        var (typeName, actualPrecision, scale, _) = await DescribeAsync(sql);
        AreEqual("decimal", typeName);
        AreEqual((short)precision, actualPrecision);
        AreEqual((short)0, scale);
    }

    [TestMethod]
    public async Task PastIntRange_ValueRoundTrips()
    {
        AreEqual(3000000000m, (await DescribeAsync("select 3000000000 as x")).Value);
        AreEqual(-3000000000m, (await DescribeAsync("select -3000000000 as x")).Value);
        AreEqual(int.MinValue, (await DescribeAsync("select -2147483648 as x")).Value);
    }

    /// <summary>
    /// Only a CAST reaches <c>bigint</c> on the wire — the literal alone never
    /// does, so the two shapes advertise different types for the same value.
    /// </summary>
    [TestMethod]
    public async Task CastReachesBigint()
        => AreEqual("bigint", (await DescribeAsync("select cast(3000000000 as bigint) as x")).TypeName);
}
