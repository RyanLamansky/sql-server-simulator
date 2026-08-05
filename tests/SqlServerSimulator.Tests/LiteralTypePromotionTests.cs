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
/// <item>A <b>decimal literal</b>'s precision counts significant digits with
/// an integer part of exactly <c>0</c> contributing nothing, floored at 1
/// (<c>0.1</c> → <c>(1, 1)</c>, <c>0.05</c> → <c>(2, 2)</c>), and a literal
/// may omit the leading integer digit (<c>.5</c> = <c>0.5</c> →
/// <c>(1, 1)</c>).</item>
/// </list>
/// These assert precision/scale/value via <c>INFORMATION_SCHEMA</c> (the
/// storage type name is always <c>decimal</c> there, regardless of the
/// reported numeric-vs-decimal name, which rides the wire only and is asserted
/// separately in <c>Tests.Internal</c>'s <c>DecimalTypeNameTests</c>).
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

    // ---- decimal-literal precision: leading-zero + leading-dot forms ----

    [TestMethod]
    [DataRow("0.1", 1, 1)]      // integer part 0 contributes nothing
    [DataRow("0.5", 1, 1)]
    [DataRow("0.05", 2, 2)]
    [DataRow("0.00", 2, 2)]
    [DataRow("0.10", 2, 2)]     // written trailing zero still counts
    [DataRow("1.5", 2, 1)]      // significant leading digit counts
    [DataRow("12.5", 3, 1)]
    [DataRow("10.05", 4, 2)]
    [DataRow("100.0", 4, 1)]
    public void DecimalLiteral_Precision_LeadingZeroNotCounted(string expr, int precision, int scale) =>
        AssertDecimal(expr, precision, scale);

    [TestMethod]
    [DataRow(".5", 1, 1)]
    [DataRow(".05", 2, 2)]
    [DataRow(".123", 3, 3)]
    public void DecimalLiteral_LeadingDot_ParsesAsFractional(string expr, int precision, int scale) =>
        AssertDecimal(expr, precision, scale);

    [TestMethod]
    public void DecimalLiteral_LeadingDot_ValueMatches()
    {
        AreEqual(0.5m, ExecuteScalar("select .5"));
        AreEqual(0.05m, ExecuteScalar("select .05"));
        AreEqual(0.123m, ExecuteScalar("select .123"));
    }

    [TestMethod]
    [DataRow("select .")]        // bare dot is not a numeric literal
    [DataRow("select 1..2")]     // a second dot doesn't extend the literal
    public void MalformedDotNumeric_RaisesSyntaxError(string command) =>
        _ = AssertSqlError(command, 102);

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

    // ---- integer literals past int's range type numeric(digit_count, 0) ----

    [TestMethod]
    [DataRow("2147483647", 10, 0)]            // last int
    [DataRow("-2147483647", 10, 0)]
    [DataRow("0", 10, 0)]
    [DataRow("5", 10, 0)]
    public void IntegerLiteral_WithinIntRange_StaysInt(string expr, int precision, int scale)
    {
        AreEqual("int", TypeOf(expr).TypeName, $"{expr} type name");
        // int's fixed precision, not the literal's digit count.
        AreEqual(precision, TypeOf(expr).Precision, $"{expr} precision");
        AreEqual(scale, TypeOf(expr).Scale, $"{expr} scale");
    }

    /// <summary>
    /// SQL Server never types a bare integer literal <c>bigint</c> — past
    /// <c>int</c> it goes straight to <c>numeric(digit_count, 0)</c>, scaling
    /// with the written digit count, and only a CAST reaches <c>bigint</c>.
    /// Probe-confirmed 2026-08-01 via <c>sql_variant_property</c>.
    /// </summary>
    [TestMethod]
    [DataRow("2147483648", 10)]               // first past int
    [DataRow("3000000000", 10)]
    [DataRow("9999999999", 10)]
    [DataRow("10000000000", 11)]
    [DataRow("-3000000000", 10)]              // sign doesn't change the count
    [DataRow("-9999999999", 10)]
    [DataRow("+2147483648", 10)]
    [DataRow("0000000003000000000", 10)]      // leading zeros excluded
    [DataRow("99999999999999999999", 20)]
    public void IntegerLiteral_PastIntRange_IsNumericAtDigitCount(string expr, int precision) =>
        AssertDecimal(expr, precision, 0);

    [TestMethod]
    public void IntegerLiteral_PastIntRange_KeepsItsValue()
    {
        AreEqual(3000000000m, ExecuteScalar("select 3000000000"));
        AreEqual(-3000000000m, ExecuteScalar("select -3000000000"));
        AreEqual(99999999999999999999m, ExecuteScalar("select 99999999999999999999"));
    }

    /// <summary>
    /// The one magnitude where the negated constant lands back inside
    /// <c>int</c>: real folds <c>- &lt;integer constant&gt;</c> and types the
    /// resulting value, so <c>-2147483648</c> — parenthesized or not — is
    /// <c>int</c> even though <c>2147483648</c> alone is
    /// <c>numeric(10, 0)</c>. The fold is literal-only; the same value in a
    /// <c>numeric(10, 0)</c> variable stays numeric under unary minus.
    /// </summary>
    [TestMethod]
    [DataRow("-2147483648")]
    [DataRow("- 2147483648")]
    [DataRow("-(2147483648)")]
    public void NegatedIntMinLiteral_FoldsToInt(string expr)
    {
        AreEqual("int", TypeOf(expr).TypeName, expr);
        AreEqual(int.MinValue, ExecuteScalar<int>($"select {expr}"));
    }

    [TestMethod]
    public void NegatedIntMinVariable_StaysNumeric()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("declare @d numeric(10, 0) = 2147483648; select -@d as v into t");
        AreEqual("decimal", (string)sim.ExecuteScalar("select data_type from information_schema.columns where table_name = 't'")!);
    }

    /// <summary>
    /// Arithmetic over a past-int literal follows the ordinary decimal
    /// formulas, so it widens by one for <c>+</c> and sums the operand
    /// precisions for <c>*</c> — probe-confirmed <c>3000000000 + 1</c> →
    /// <c>numeric(11, 0)</c>, <c>3000000000 * 2</c> → <c>numeric(12, 0)</c>,
    /// <c>3000000000 / 2</c> → <c>numeric(16, 6)</c>.
    /// </summary>
    [TestMethod]
    [DataRow("3000000000 + 1", 11, 0)]
    [DataRow("3000000000 * 2", 12, 0)]
    [DataRow("3000000000 / 2", 16, 6)]
    public void PastIntLiteral_Arithmetic_FollowsDecimalFormulas(string expr, int precision, int scale) =>
        AssertDecimal(expr, precision, scale);

    [TestMethod]
    public void PastIntLiteral_Arithmetic_KeepsItsValue() =>
        AreEqual(3000000001m, ExecuteScalar("select 3000000000 + 1"));

    /// <summary>
    /// Past 38 digits real reports Msg 1007 rather than letting the literal
    /// reach the type factory — the same gate the decimal-literal branch uses.
    /// </summary>
    [TestMethod]
    public void IntegerLiteral_Past38Digits_RaisesMsg1007() =>
        new Simulation().AssertSqlError(
            "select 999999999999999999999999999999999999999",
            1007,
            "The number '999999999999999999999999999999999999999' is out of the range for numeric representation (maximum precision 38).");

    /// <summary>
    /// A past-int literal still narrows into an <c>int</c> target through the
    /// ordinary arithmetic-overflow surface — the expression-worded Msg 8115,
    /// not a decimal-specific family.
    /// </summary>
    [TestMethod]
    [DataRow("cast(3000000000 as int)")]
    [DataRow("convert(int, 3000000000)")]
    public void PastIntLiteral_NarrowedToInt_RaisesMsg8115(string expr) =>
        new Simulation().AssertSqlError(
            $"select {expr}",
            8115,
            "Arithmetic overflow error converting expression to data type int.");

    /// <summary>
    /// A past-int literal inserts into a <c>bigint</c> column unchanged —
    /// numeric(10, 0) converts implicitly on the way in.
    /// </summary>
    [TestMethod]
    public void PastIntLiteral_InsertsIntoBigintColumn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (b bigint, d decimal(20, 0))",
            "insert t values (3000000000, 99999999999999999999)");
        AreEqual(3000000000L, sim.ExecuteScalar<long>("select b from t"));
        AreEqual(99999999999999999999m, sim.ExecuteScalar("select d from t"));
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from t where b = 3000000000"));
    }

    /// <summary>
    /// <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> accept any scale-0 exact
    /// numeric, so a past-int row count is an ordinary accepted value rather
    /// than the grammar's Msg 1060 (which a fractional scale still gets).
    /// </summary>
    [TestMethod]
    public void PastIntLiteral_AsRowCount_IsAccepted()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (v int)",
            "insert t values (1), (2), (3)");
        AreEqual(3, sim.ExecuteNonQuery("update top (9999999999) t set v = v + 100"));
        AreEqual(3, sim.ExecuteScalar<int>("select count(*) from (select top (3000000000) * from t) u"));
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from (select * from t order by v offset 3000000000 rows) u"));
        AreEqual(3, sim.ExecuteScalar<int>("select count(*) from (select * from t order by v offset 0 rows fetch next 3000000000 rows only) u"));
    }

    [TestMethod]
    public void FractionalRowCount_StillRaisesMsg1060()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (v int)");
        sim.AssertSqlError(
            "select top (2.5) * from t",
            1060,
            "The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.");
    }
}
