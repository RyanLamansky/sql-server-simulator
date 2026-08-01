using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>REGEXP_*</c> result types over TDS. The four scalars project ordinary
/// <c>int</c> / <c>varchar</c> / <c>nvarchar</c> columns, but
/// <c>REGEXP_MATCHES</c> mixes a <c>bigint</c> ordinal, two <c>int</c>
/// positions, an input-typed match column and a <c>varchar(max)</c> JSON column
/// in one rowset — the one shape worth checking on the wire. Expected metadata
/// matches a live SQL Server 2025 (17.0.4065.4)
/// <c>sp_describe_first_result_set</c> probe.
/// </summary>
[TestClass]
public sealed class RegexpWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RegexpMatches_ColumnMetadataAndValues()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select * from regexp_matches('abcABC', 'a(b)(c)', 'i')", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("bigint", reader.GetDataTypeName(0));
        AreEqual("int", reader.GetDataTypeName(1));
        AreEqual("int", reader.GetDataTypeName(2));
        AreEqual("varchar", reader.GetDataTypeName(3));
        AreEqual("varchar", reader.GetDataTypeName(4));
        AreEqual(typeof(long), reader.GetFieldType(0));
        AreEqual(typeof(string), reader.GetFieldType(4));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1L, reader.GetInt64(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual(3, reader.GetInt32(2));
        AreEqual("abc", reader.GetString(3));
        AreEqual("""[{"value":"b","start":2,"length":1},{"value":"c","start":3,"length":1}]""", reader.GetString(4));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("ABC", reader.GetString(3));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// <c>substring_matches</c> is <c>varchar(max)</c> whatever the input's
    /// family, so an <c>nvarchar</c> input yields a mixed-family rowset.
    /// </summary>
    [TestMethod]
    public async Task RegexpMatches_NvarcharInput_KeepsVarcharMaxJson()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select * from regexp_matches(cast('ab' as nvarchar(20)), 'a')", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("nvarchar", reader.GetDataTypeName(3));
        AreEqual("varchar", reader.GetDataTypeName(4));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("a", reader.GetString(3));
    }

    [TestMethod]
    public async Task RegexpSplitToTable_ProjectsValueAndBigintOrdinal()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select * from regexp_split_to_table('a,b,c', ',')", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("value", reader.GetName(0));
        AreEqual("ordinal", reader.GetName(1));
        AreEqual("varchar", reader.GetDataTypeName(0));
        AreEqual("bigint", reader.GetDataTypeName(1));

        for (var expected = 1L; expected <= 3; expected++)
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(expected, reader.GetInt64(1));
        }
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// The family's 193xx diagnostics reach a real SqlClient with their number,
    /// class and state intact.
    /// </summary>
    [TestMethod]
    public async Task PatternError_ReachesClientWithNumberAndState()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(@"select regexp_count('a', '(ab)\1')", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(19300, ex.Number);
        AreEqual((byte)16, ex.Class);
        AreEqual((byte)1, ex.State);
        Assert.Contains(@"invalid escape sequence: \1", ex.Message);
    }
}
