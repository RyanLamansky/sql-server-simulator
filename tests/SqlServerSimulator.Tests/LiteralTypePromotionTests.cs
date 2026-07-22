using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Type-inference fidelity for numeric literals and the untyped NULL constant,
/// probe-confirmed against SQL Server 2025:
/// <list type="bullet">
/// <item>An <b>integer literal</b> that meets a decimal/numeric partner (in
/// arithmetic, <c>CASE</c>, <c>COALESCE</c>, or a set op) is sized
/// <c>numeric(digit_count, 0)</c> — not <c>int</c>'s fixed <c>(10, 0)</c> — so
/// <c>10.0/3</c> is <c>numeric(8, 6)</c>, not <c>numeric(14, 12)</c>. A
/// non-literal <c>int</c> keeps <c>(10, 0)</c>.</item>
/// <item><b>Unary minus</b> preserves the operand's own precision/scale/family
/// (<c>-1.1</c> → <c>numeric(2, 1)</c>) instead of inflating it through a
/// synthetic <c>0 - x</c> subtraction.</item>
/// <item>An <b>untyped NULL</b> yields to any typed operand in
/// <c>COALESCE</c> / <c>ISNULL</c> / <c>CASE</c> / <c>IIF</c> promotion rather
/// than forcing its placeholder <c>int</c> type onto the result.</item>
/// </list>
/// The reported decimal type name stays <c>decimal</c> (real reports
/// <c>numeric</c>) — a deliberately separate follow-up — so these assert
/// precision/scale/value, not the name.
/// </summary>
[TestClass]
public sealed class LiteralTypePromotionTests
{
    // Materializes the expression via SELECT … INTO and reads the created
    // column's declared type from INFORMATION_SCHEMA — the projection type the
    // promotion sites compute (GetSqlType) and to which each value is coerced.
    // (The simulated reader doesn't implement GetColumnSchema, and a
    // CAST-to-sql_variant wrapper would report a nested CASE/COALESCE's
    // uncoerced branch type in the FROM-less run-before-GetSqlType path.)
    private static (string TypeName, int Precision, int Scale) ColumnType(string selectInto)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(selectInto);
        var typeName = (string)sim.ExecuteScalar("select data_type from information_schema.columns where table_name = 't'")!;
        var precision = sim.ExecuteScalar<int>("select convert(int, isnull(numeric_precision, 0)) from information_schema.columns where table_name = 't'");
        var scale = sim.ExecuteScalar<int>("select convert(int, isnull(numeric_scale, 0)) from information_schema.columns where table_name = 't'");
        return (typeName, precision, scale);
    }

    private static (string TypeName, int Precision, int Scale) TypeOf(string expr) =>
        ColumnType($"select {expr} as v into t");

    private static void AssertDecimal(string expr, int precision, int scale)
    {
        var (typeName, actualPrecision, actualScale) = TypeOf(expr);
        AreEqual("decimal", typeName, $"{expr} type name");
        AreEqual(precision, actualPrecision, $"{expr} precision");
        AreEqual(scale, actualScale, $"{expr} scale");
    }

    // ---- #2: integer literal sized by digit count in decimal arithmetic ----

    [TestMethod]
    [DataRow("10.0/3", 8, 6)]
    [DataRow("10.0/30", 8, 6)]
    [DataRow("10.0/12345", 9, 7)]
    [DataRow("10.0/1234567", 11, 9)]
    [DataRow("1.0/1234567890", 13, 12)]
    [DataRow("10.0*3", 5, 1)]
    [DataRow("1.5+1", 3, 1)]
    [DataRow("1.5*2", 4, 1)]
    [DataRow("1.5+1234567890", 12, 1)]
    [DataRow("10.0/-3", 8, 6)]        // negated literal stays a digit-count literal
    [DataRow("10.0/007", 8, 6)]       // leading zeros excluded from digit count
    public void IntegerLiteral_InDecimalArithmetic_SizedByDigitCount(string expr, int precision, int scale) =>
        AssertDecimal(expr, precision, scale);

    [TestMethod]
    public void IntegerLiteral_DecimalArithmetic_ValueMatchesScale()
    {
        // numeric(8,6) truncates the quotient to six fractional digits.
        AreEqual(3.333333m, ExecuteScalar("select 10.0/3"));
        AreEqual(30.0m, ExecuteScalar("select 10.0*3"));
        AreEqual(2.5m, ExecuteScalar("select 1.5+1"));
    }

    [TestMethod]
    public void NonLiteralInt_InDecimalArithmetic_KeepsPrecisionTen() =>
        AssertDecimal("10.0/CAST(3 AS int)", 14, 12);

    [TestMethod]
    [DataRow("3+4", "int", 7)]        // pure integer literals stay int
    [DataRow("7/2", "int", 3)]        // integer division unchanged
    public void PureIntegerArithmetic_Unchanged(string expr, string baseType, int value)
    {
        AreEqual(baseType, TypeOf(expr).TypeName, $"{expr} base type");
        AreEqual(value, ExecuteScalar<int>($"select {expr}"));
    }

    [TestMethod]
    public void IntegerLiteral_InCase_SizedByDigitCount()
    {
        AssertDecimal("CASE WHEN 1=0 THEN 1 ELSE 2.5 END", 2, 1);
        AssertDecimal("CASE WHEN 1=0 THEN 1 WHEN 1=1 THEN 100 ELSE 2.5 END", 4, 1);
        AreEqual(2.5m, ExecuteScalar("select CASE WHEN 1=0 THEN 1 ELSE 2.5 END"));
    }

    [TestMethod]
    public void IntegerLiteral_InCoalesceAndIif_SizedByDigitCount()
    {
        AssertDecimal("COALESCE(1, 2.5)", 2, 1);
        AssertDecimal("IIF(1=1, 1, 2.5)", 2, 1);
    }

    [TestMethod]
    public void IntegerLiteral_InSetOp_SizedByDigitCount()
    {
        AreEqual(("decimal", 2, 1), ColumnType("select 1 as v into t union select 2.5"));
        // Nested set-op: the folded 1/2 literals still size against 2.5.
        AreEqual(("decimal", 2, 1), ColumnType("select 1 as v into t union select 2 union select 2.5"));
        // All-integer union stays int.
        AreEqual(("int", 10, 0), ColumnType("select 1 as v into t union select 2 union select 250"));
    }

    // ---- #2b: unary minus preserves the operand's type ----

    [TestMethod]
    public void UnaryMinus_OnDecimal_PreservesPrecisionScale()
    {
        AssertDecimal("-1.1", 2, 1);
        AssertDecimal("-CAST(1.5 AS decimal(5,3))", 5, 3);
        AreEqual(-1.1m, ExecuteScalar("select -1.1"));
        AreEqual(-1.500m, ExecuteScalar("select -CAST(1.5 AS decimal(5,3))"));
    }

    [TestMethod]
    public void UnaryMinus_OnInteger_StaysInt() =>
        AreEqual("int", TypeOf("-1").TypeName);

    [TestMethod]
    public void UnaryMinus_Floor_PreservesInputPrecision()
    {
        // FLOOR faithfully preserves the (now un-inflated) input precision.
        AssertDecimal("FLOOR(-1.1)", 2, 0);
        AreEqual(-2m, ExecuteScalar("select FLOOR(-1.1)"));
    }

    [TestMethod]
    [DataRow("-CAST(1 AS bigint)", "bigint")]
    [DataRow("-CAST(1 AS smallint)", "smallint")]
    [DataRow("-CAST(1 AS tinyint)", "smallint")]   // tinyint is unsigned → widens to smallint
    [DataRow("-CAST(1 AS real)", "real")]           // stays real, not float
    [DataRow("-CAST(1 AS float)", "float")]
    [DataRow("-$1.00", "money")]
    [DataRow("-CAST(1.5 AS smallmoney)", "smallmoney")]
    public void UnaryMinus_PreservesFamily(string expr, string baseType) =>
        AreEqual(baseType, TypeOf(expr).TypeName, expr);

    [TestMethod]
    public void UnaryMinus_OnBit_RaisesMsg8117() =>
        AssertSqlMessage("select -CAST(1 AS bit)", "Operand data type bit is invalid for minus operator.");

    // ---- #4: untyped NULL yields to a typed operand ----

    [TestMethod]
    [DataRow("COALESCE(NULL, 'z')", "varchar")]
    [DataRow("COALESCE(NULL, NULL, 'z')", "varchar")]
    [DataRow("ISNULL(NULL, 'z')", "varchar")]
    [DataRow("COALESCE(NULL, 1)", "int")]
    [DataRow("COALESCE(NULL, CAST(1 AS bigint))", "bigint")]
    [DataRow("COALESCE(1, CAST(2 AS bigint))", "bigint")]
    public void UntypedNull_YieldsToTypedOperand(string expr, string baseType) =>
        AreEqual(baseType, TypeOf(expr).TypeName, expr);

    [TestMethod]
    public void UntypedNull_PreviouslyErroringCases_ReturnTypedValue()
    {
        // Before the fix these raised "Conversion failed when converting the
        // varchar value 'z' to data type int." because the bare NULL forced int.
        AreEqual("z", ExecuteScalar("select COALESCE(NULL, NULL, 'z')"));
        AreEqual("z", ExecuteScalar("select ISNULL(NULL, 'z')"));
        AreEqual("z", ExecuteScalar("select COALESCE(NULL, 'z')"));
        AreEqual(1, ExecuteScalar<int>("select COALESCE(NULL, 1)"));
    }

    [TestMethod]
    public void BareNull_WithNoTypedSibling_StaysInt() =>
        AreEqual("int", TypeOf("NULL").TypeName);
}
