using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The reported type name for a <c>decimal</c>-family result column —
/// <c>decimal</c> vs <c>numeric</c> — probe-confirmed against SQL Server 2025
/// (<c>sp_describe_first_result_set</c> / JDBC <c>getColumnTypeName</c>). The
/// two names share one storage type; SQL Server reports <c>numeric</c>
/// whenever the projected value traces back to a numeric-named source: a
/// decimal/numeric literal (all literals are numeric-named), a
/// <c>CAST</c>/<c>CONVERT … AS numeric</c>, or arithmetic / a decimal-returning
/// function carrying one. A bare <c>decimal</c> keyword, integer operands, and
/// a decimal-typed column all keep <c>decimal</c>. The name is surfaced by the
/// in-process reader's <c>GetDataTypeName</c> (mirrored on the wire by the TDS
/// NUMERICN-vs-DECIMALN COLMETADATA token).
/// </summary>
/// <remarks>
/// Deferred boundary: a decimal value read from a <b>column source</b> — a
/// declared column, or a derived-table / <c>VALUES</c> / set-op-subquery
/// column — still reports <c>decimal</c> (real reports <c>numeric</c>). Each
/// would need the source to remember its name, which risks the
/// storage-equality invariant the row encoder depends on. Every
/// direct-expression source (literal / CAST / arithmetic / function /
/// value-selecting form) is modeled here.
/// </remarks>
[TestClass]
public sealed class DecimalTypeNameTests
{
    private static string TypeName(string expression)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand($"select {expression} as v").ExecuteReader();
        return reader.GetDataTypeName(0);
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

    private static string ColumnTypeName(string command, int ordinal)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(command).ExecuteReader();
        return reader.GetDataTypeName(ordinal);
    }

    [TestMethod]
    public void DecimalColumn_And_ItsAggregates_ReportDecimal()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (d decimal(6,2))");
        _ = simulation.ExecuteNonQuery("insert t values (1.50), (2.50)");
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection
            .CreateCommand("select d, d + 1 as d1, sum(d) as sd, avg(d) as ad from t group by d")
            .ExecuteReader();
        AreEqual("decimal", reader.GetDataTypeName(0));  // bare decimal column
        AreEqual("decimal", reader.GetDataTypeName(1));  // + integer literal stays decimal
        AreEqual("decimal", reader.GetDataTypeName(2));  // SUM preserves the column's reported type name
        AreEqual("decimal", reader.GetDataTypeName(3));  // AVG likewise
    }

    [TestMethod]
    public void DecimalColumn_TimesNumericLiteral_ReportsNumeric()
    {
        // The numeric literal makes the arithmetic result numeric-named even
        // though the column itself is decimal (probe-confirmed).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (d decimal(6,2))");
        _ = simulation.ExecuteNonQuery("insert t values (1.50)");
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select d * 100.0 as v from t").ExecuteReader();
        AreEqual("numeric", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void SetOp_OfDecimalLiterals_ReportsNumeric()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select 10.0 as v union all select 20.0").ExecuteReader();
        AreEqual("numeric", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void NonDecimalResults_KeepTheirOwnName()
    {
        // The name annotation must not leak onto non-decimal columns.
        AreEqual("int", TypeName("1 + 2"));
        AreEqual("float", TypeName("cast(1.5 as float)"));
    }
}
