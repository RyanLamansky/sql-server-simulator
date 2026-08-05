using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The reported type name for a <c>decimal</c>-family result column —
/// <c>decimal</c> vs <c>numeric</c> — probe-confirmed against SQL Server 2025
/// (<c>sys.dm_exec_describe_first_result_set</c> reports <c>numeric(2,1)</c>
/// for <c>SELECT 1.0</c> and <c>decimal(9,2)</c> for
/// <c>CAST(1.0 AS decimal(9,2))</c>). The two names share one storage type;
/// SQL Server reports <c>numeric</c> whenever the projected value traces back
/// to a numeric-named source: a decimal/numeric literal (all literals are
/// numeric-named), a <c>CAST</c>/<c>CONVERT … AS numeric</c>, or arithmetic /
/// a decimal-returning function carrying one. A bare <c>decimal</c> keyword,
/// integer operands, and a decimal-typed column all keep <c>decimal</c>.
/// </summary>
/// <remarks>
/// <para>
/// These live here because the distinction is <b>unreachable from the public
/// ADO.NET surface</b>: it rides the wire as the NUMERICN (0x6C) vs DECIMALN
/// (0x6A) COLMETADATA token, which mssql-jdbc's <c>getColumnTypeName</c> reads
/// but SqlClient collapses to <see cref="System.Data.SqlDbType.Decimal"/> —
/// real's own <c>GetDataTypeName</c>, <c>GetSchemaTable</c>'s
/// <c>DataTypeName</c> / <c>ProviderType</c> and
/// <c>GetProviderSpecificFieldType</c> all answer <c>decimal</c> for both
/// (probed over SqlClient 7.0.2, 2026-08-05). <c>SimulatedDbDataReader</c>
/// matches that collapse, so the plan-side flag is what the assertions read.
/// </para>
/// <para>
/// Deferred boundary: a decimal value read from a <b>column source</b> — a
/// declared column, or a derived-table / <c>VALUES</c> / set-op-subquery
/// column — still reports <c>decimal</c> (real reports <c>numeric</c>). Each
/// would need the source to remember its name, which risks the
/// storage-equality invariant the row encoder depends on. Every
/// direct-expression source (literal / CAST / arithmetic / function /
/// value-selecting form) is modeled here.
/// </para>
/// </remarks>
[TestClass]
public sealed class DecimalTypeNameTests
{
    /// <summary>The name the wire reports for a result column, off the plan's own decimal-family flag.</summary>
    private static string ColumnTypeName(string command, int ordinal)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var dbCommand = connection.CreateCommand();
        dbCommand.CommandText = command;
        var query = simulation.CreateResultSetsForCommand(dbCommand).OfType<SimulatedQueryResult>().First();
        var reportsNumeric = query.ColumnReportsNumeric is { } flags && flags[ordinal];
        return reportsNumeric ? "numeric" : query.Schema[ordinal].SqlServerName;
    }

    private static string TypeName(string expression) => ColumnTypeName($"select {expression} as v", 0);

    /// <summary>
    /// Ties the reported name to the byte the listener actually emits: the two
    /// names differ only in the COLMETADATA token, NUMERICN (0x6C) against
    /// DECIMALN (0x6A), with an identical body.
    /// </summary>
    [TestMethod]
    public void TheNameIsTheColMetadataToken()
    {
        AreEqual(0x6C, ColMetadataTypeToken("select 10.0 as v"));
        AreEqual(0x6A, ColMetadataTypeToken("select cast(1.5 as decimal(6,2)) as v"));
    }

    private static byte ColMetadataTypeToken(string command)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var dbCommand = connection.CreateCommand();
        dbCommand.CommandText = command;
        var query = simulation.CreateResultSetsForCommand(dbCommand).OfType<SimulatedQueryResult>().First();
        var stream = new MemoryStream();
        var transport = new TdsPacketTransport(stream) { PacketSize = Tds.DefaultPacketSize };
        var writer = new TdsTokenWriter(transport);
        TdsTypeCodec.WriteColMetadata(writer, query.Schema, query.ColumnNames, query.ColumnNullability, query.ColumnReportsNumeric);
        writer.FlushAsync(final: true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        // Past the packet header: TOKEN(1) COUNT(2) USERTYPE(4) FLAGS(2), then the TYPE_INFO token byte.
        return stream.ToArray()[Tds.HeaderSize..][9];
    }

    [TestMethod]
    [DataRow("10.0")]                       // decimal/numeric literal
    [DataRow("10.0 + 1")]                   // + integer literal
    [DataRow("10.0 * 2.0")]                 // two decimal literals
    [DataRow("cast(1.5 as numeric(6,2))")]  // CAST … AS numeric
    [DataRow("convert(numeric(6,2), 1.5)")] // CONVERT … , numeric
    [DataRow("round(1.55, 1)")]             // function preserves literal name
    [DataRow("ceiling(1.1)")]
    [DataRow("floor(1.1)")]
    [DataRow("abs(-1.1)")]
    [DataRow("-1.1")]                       // unary minus preserves it
    [DataRow("(10.0 + 1)")]                 // parentheses preserve it
    [DataRow("3000000000")]                 // integer literal past int is numeric(10, 0)
    [DataRow("-3000000000")]
    [DataRow("99999999999999999999")]
    [DataRow("3000000000 + 1")]
    public void NumericNamedSources_ReportNumeric(string expression) =>
        AreEqual("numeric", TypeName(expression));

    [TestMethod]
    [DataRow("power(2.0, 10)")]             // POWER takes the base's name
    [DataRow("sign(-3.2)")]                 // SIGN preserves the operand
    [DataRow("degrees(1.5)")]               // DEGREES / RADIANS preserve it
    [DataRow("radians(1.5)")]
    [DataRow("case when 1=0 then 1 else 2.5 end")]  // a numeric arm wins
    [DataRow("coalesce(null, 2.5)")]
    [DataRow("iif(1=1, 1, 2.5)")]
    [DataRow("isnull(null, 2.5)")]
    [DataRow("nullif(1.5, 2.5)")]           // result is arm a
    [DataRow("greatest(1.5, 2.5)")]
    [DataRow("least(1.5, 2.5)")]
    [DataRow("choose(1, 1.5, 2.5)")]
    public void NumericNamedDirectExpressions_ReportNumeric(string expression) =>
        AreEqual("numeric", TypeName(expression));

    [TestMethod]
    [DataRow("cast(1.5 as decimal(6,2))")]  // bare decimal keyword
    [DataRow("convert(decimal(6,2), 1.5)")]
    [DataRow("cast(1 as decimal(18,2))")]
    [DataRow("power(cast(1.5 as decimal(6,2)), 2)")]  // decimal-named base stays decimal
    [DataRow("case when 1=0 then cast(1 as decimal(6,2)) else cast(2 as decimal(6,2)) end")]
    public void DecimalNamedSources_ReportDecimal(string expression) =>
        AreEqual("decimal", TypeName(expression));

    [TestMethod]
    public void ColumnSourceName_StaysDecimal_Deferred()
    {
        // Deferred boundary: a decimal value read from a declared column, or
        // from a derived-table / VALUES / set-op subquery column, keeps
        // `decimal` because the column source doesn't remember its name.
        // Real reports `numeric` for these; documented as a known deferral.
        AreEqual("decimal", ColumnTypeName("select v from (values(1.0),(2.0)) t(v)", 0));
        AreEqual("decimal", ColumnTypeName("select avg(v) from (values(1.0),(2.0)) t(v)", 0));
        AreEqual("decimal", ColumnTypeName("select v from (select 1 as v union select 2.5) t", 0));
    }

    [TestMethod]
    public void DecimalColumn_And_ItsAggregates_ReportDecimal()
    {
        const string Command = "select d, d + 1 as d1, sum(d) as sd, avg(d) as ad from t group by d";
        AreEqual("decimal", ColumnTypeNameAfter("create table t (d decimal(6,2)); insert t values (1.50), (2.50)", Command, 0));  // bare decimal column
        AreEqual("decimal", ColumnTypeNameAfter("create table t (d decimal(6,2)); insert t values (1.50), (2.50)", Command, 1));  // + integer literal stays decimal
        AreEqual("decimal", ColumnTypeNameAfter("create table t (d decimal(6,2)); insert t values (1.50), (2.50)", Command, 2));  // SUM preserves the column's reported type name
        AreEqual("decimal", ColumnTypeNameAfter("create table t (d decimal(6,2)); insert t values (1.50), (2.50)", Command, 3));  // AVG likewise
    }

    [TestMethod]
    public void DecimalColumn_TimesNumericLiteral_ReportsNumeric()
    {
        // The numeric literal makes the arithmetic result numeric-named even
        // though the column itself is decimal (probe-confirmed).
        AreEqual("numeric", ColumnTypeNameAfter(
            "create table t (d decimal(6,2)); insert t values (1.50)", "select d * 100.0 as v from t", 0));
    }

    [TestMethod]
    public void SetOp_OfDecimalLiterals_ReportsNumeric()
    {
        AreEqual("numeric", ColumnTypeName("select 10.0 as v union all select 20.0", 0));
    }

    [TestMethod]
    public void NonDecimalResults_KeepTheirOwnName()
    {
        // The name annotation must not leak onto non-decimal columns.
        AreEqual("int", TypeName("1 + 2"));
        AreEqual("float", TypeName("cast(1.5 as float)"));
    }

    /// <summary>Runs <paramref name="setup"/>, then reports the name for one column of <paramref name="command"/>.</summary>
    private static string ColumnTypeNameAfter(string setup, string command, int ordinal)
    {
        var simulation = new Simulation();
        using (var setupConnection = simulation.CreateDbConnection())
        {
            setupConnection.Open();
            using var setupCommand = setupConnection.CreateCommand();
            setupCommand.CommandText = setup;
            _ = setupCommand.ExecuteNonQuery();
        }

        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var dbCommand = connection.CreateCommand();
        dbCommand.CommandText = command;
        var query = simulation.CreateResultSetsForCommand(dbCommand).OfType<SimulatedQueryResult>().First();
        return query.ColumnReportsNumeric is { } flags && flags[ordinal] ? "numeric" : query.Schema[ordinal].SqlServerName;
    }
}
