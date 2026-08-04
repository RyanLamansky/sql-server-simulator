using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>decimal(p, s)</c> / <c>numeric(p, s)</c>
/// type — variable storage width, literal type inference, arithmetic
/// precision/scale rules, CAST round-trips through string and integers, and
/// the rounding-vs-truncation distinction (<see cref="decimal"/> → integer
/// truncates toward zero; string → decimal rounds half-away-from-zero).
/// </summary>
[TestClass]
public sealed class DecimalTests
{
    [TestMethod]
    public void Literal_FractionalDigits_ProducesDecimalValue() => AreEqual(100.5m, ExecuteScalar("select 100.5"));

    [TestMethod]
    public void Literal_IntegerOnly_StaysAsInt() => AreEqual(100, ExecuteScalar("select 100"));

    [TestMethod]
    public void Cast_StringToDecimal_BasicRoundTrip() => AreEqual(100.50m, ExecuteScalar("select cast('100.5' as decimal(10, 2))"));

    [TestMethod]
    [DataRow("'12.345'", "12.35")]
    [DataRow("'12.346'", "12.35")]
    [DataRow("'-12.345'", "-12.35")]
    [DataRow("'12.5'", "13")]
    [DataRow("'12.49'", "12")]
    [DataRow("'-12.5'", "-13")]
    public void Cast_StringToDecimal_RoundsHalfAwayFromZero(string literal, string expected)
    {
        var scale = expected.Contains('.') ? 2 : 0;
        var value = ExecuteScalar<decimal>($"select cast({literal} as decimal(10, {scale}))");
        AreEqual(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [TestMethod]
    [DataRow("'+12.34'", 12.34)]
    [DataRow("' 12.34 '", 12.34)]
    [DataRow("'.5'", 0.5)]
    [DataRow("'5.'", 5.0)]
    [DataRow("'-.5'", -0.5)]
    public void Cast_StringToDecimal_AcceptsCommonForms(string literal, double expectedRaw) =>
        AreEqual((decimal)expectedRaw, ExecuteScalar<decimal>($"select cast({literal} as decimal(10, 2))"));

    [TestMethod]
    [DataRow("'abc'")]
    [DataRow("''")]
    [DataRow("'$5.95'")]
    public void Cast_StringToDecimal_BadFormatRaisesMsg8114(string literal) =>
        AssertSqlMessage($"select cast({literal} as decimal(10, 2))", "Error converting data type varchar to numeric.");

    [TestMethod]
    public void Cast_DecimalOverflow_RaisesMsg8115() =>
        AssertSqlMessage("select cast(1000 as decimal(3, 0))", "Arithmetic overflow error converting expression to data type numeric.");

    [TestMethod]
    [DataRow("1.5", "1")]
    [DataRow("-1.5", "-1")]
    [DataRow("2.5", "2")]
    [DataRow("0.5", "0")]
    [DataRow("-0.5", "0")]
    public void Cast_DecimalToInt_TruncatesTowardZero(string sourceLiteral, string expected)
    {
        // Decimal → int truncates toward zero, NOT half-away-from-zero (that applies to string → decimal).
        var value = ExecuteScalar<int>($"select cast(cast({sourceLiteral} as decimal(10, 2)) as int)");
        AreEqual(int.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [TestMethod]
    public void Cast_DecimalToVarchar_EmitsTrailingZerosToScale()
    {
        AreEqual("100.50", ExecuteScalar("select cast(cast(100.5 as decimal(10, 2)) as varchar(50))"));
        AreEqual("0.00000", ExecuteScalar("select cast(cast(0 as decimal(10, 5)) as varchar(50))"));
        AreEqual("100", ExecuteScalar("select cast(cast(100 as decimal(10, 0)) as varchar(50))"));
        AreEqual("-100.50", ExecuteScalar("select cast(cast(-100.5 as decimal(10, 2)) as varchar(50))"));
    }

    [TestMethod]
    public void Cast_NumericIsAliasOfDecimal() => AreEqual(12.34m, ExecuteScalar("select cast(12.34 as numeric(10, 2))"));

    [TestMethod]
    public void Cast_DecimalDefaultPrecisionIs18Scale0()
    {
        // decimal with no parens defaults to (18, 0); 12.34 truncates the fraction.
        AreEqual(12m, ExecuteScalar("select cast(12.34 as decimal)"));
    }

    [TestMethod]
    public void DecimalArithmetic_AddPrecisionScale() =>
        AreEqual(2.464m, ExecuteScalar("select cast(1.23 as decimal(5, 2)) + cast(1.234 as decimal(7, 3))"));

    [TestMethod]
    public void DecimalArithmetic_MultiplyPrecisionScale() =>
        AreEqual(1.51782m, ExecuteScalar("select cast(1.23 as decimal(5, 2)) * cast(1.234 as decimal(7, 3))"));

    [TestMethod]
    public void DecimalArithmetic_DividePreservesScale() =>
        AreEqual(3.3333333333m, ExecuteScalar("select cast(10 as decimal(5, 2)) / cast(3 as decimal(7, 3))"));

    [TestMethod]
    public void DecimalArithmetic_DivideEnforcesMinScale6() =>
        AreEqual(1m, ExecuteScalar("select cast(1 as decimal(38, 30)) / cast(1 as decimal(38, 30))"));

    [TestMethod]
    public void DecimalArithmetic_DivideByZero_RaisesMsg8134() =>
        AssertSqlMessage("select cast(1 as decimal(5, 2)) / cast(0 as decimal(5, 2))", "Divide by zero error encountered.");

    [TestMethod]
    public void DecimalArithmetic_PromotesIntegerToDecimal() =>
        AreEqual(3.23m, ExecuteScalar("select cast(1.23 as decimal(5, 2)) + 2"));

    [TestMethod]
    public void DecimalArithmetic_OverflowOnMultiplicationRaisesMsg8115() =>
        AssertSqlMessage(
            "select cast(99999999999999999999 as decimal(20, 0)) * cast(99999999999999999999 as decimal(20, 0))",
            "Arithmetic overflow error converting expression to data type numeric.");

    [TestMethod]
    public void DecimalArithmetic_StaticSchemaMatchesRuntimeForDivision()
    {
        // EF percent-of-total shape: (s.Amount * 100.0) / (sum). Before the per-operator-arithmetic fix,
        // the static schema (joint-envelope Promote) diverged from the runtime type, causing RowEncoder
        // to reject the value. Probed result: d(38,24).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table sales (region varchar(10), amount decimal(10,2))");
        _ = simulation.ExecuteNonQuery(
            "insert sales values ('east', 100), ('east', 200), ('east', 150), ('west', 300), ('west', 500), ('west', 50)");
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select s.region, s.amount, s.amount * 100.0 / (select sum(s0.amount) from sales as s0 where s0.region = s.region) from sales as s")
            .ExecuteReader();
        // The `* 100.0` numeric literal makes the result numeric-named, so
        // SQL Server reports the numeric type name (probe-confirmed:
        // numeric(38, 24)); only the name differs from decimal, storage is
        // identical.
        AreEqual("numeric", reader.GetDataTypeName(2));
        var pcts = new List<(string Region, decimal Amount, decimal Pct)>();
        while (reader.Read())
            pcts.Add((reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2)));
        var (_, _, east100Pct) = pcts.Single(p => p.Region == "east" && p.Amount == 100m);
        var (_, _, west500Pct) = pcts.Single(p => p.Region == "west" && p.Amount == 500m);
        AreEqual(decimal.Round(100m * 100m / 450m, 6), decimal.Round(east100Pct, 6));
        AreEqual(decimal.Round(500m * 100m / 850m, 6), decimal.Round(west500Pct, 6));
    }

    [TestMethod]
    public void DecimalArithmetic_38CapPreservesSmallScaleForMultiplication()
    {
        // Regression: the 38-cap floor is min(originalScale, 6), not 0. d(38,2) * d(38,2) → d(38,4).
        AreEqual(56088.0000m, ExecuteScalar("select cast(123 as decimal(38,2)) * cast(456 as decimal(38,2))"));
    }

    [TestMethod]
    public void DecimalArithmetic_38CapForDivisionFloorsAtSix() =>
        AreEqual(1.000000m, ExecuteScalar("select cast(1 as decimal(38,30)) / cast(1 as decimal(38,30))"));

    /// <summary>
    /// Division truncates its quotient toward zero at the result scale where
    /// every other operator rounds — and it does so at every cap depth, not
    /// only where the 38-precision cap moved the scale. Expected values are
    /// SQL Server 2025's own (probed 2026-08-04).
    /// </summary>
    [TestMethod]
    // Uncapped (p = 9), capped by 4 (p = 42), capped by 1 (p = 39) and exactly
    // at the boundary (p = 38) all drop the same 571428|571… tail.
    [DataRow("cast(4.00 as decimal(5, 2)) / cast(7 as decimal(1, 0))", "0.571428")]
    [DataRow("cast(4.00 as decimal(38, 2)) / cast(7 as decimal(1, 0))", "0.571428")]
    [DataRow("cast(4.00 as decimal(35, 2)) / cast(7 as decimal(1, 0))", "0.571428")]
    [DataRow("cast(4.00 as decimal(34, 2)) / cast(7 as decimal(1, 0))", "0.571428")]
    // Truncation is toward zero, so a sign on either operand only moves the sign.
    [DataRow("cast(-4.00 as decimal(38, 2)) / cast(7 as decimal(1, 0))", "-0.571428")]
    [DataRow("cast(4.00 as decimal(38, 2)) / cast(-7 as decimal(1, 0))", "-0.571428")]
    [DataRow("cast(-4.00 as decimal(38, 2)) / cast(-7 as decimal(1, 0))", "0.571428")]
    [DataRow("cast(-4.00 as decimal(5, 2)) / cast(7 as decimal(1, 0))", "-0.571428")]
    // An exact half at the cut drops too — this is truncation, not a rounding mode.
    [DataRow("cast(1 as decimal(5, 0)) / cast(1600000 as decimal(7, 0))", "0.00000062")]
    [DataRow("cast(3.00 as decimal(38, 2)) / cast(2000000 as decimal(7, 0))", "0.000001")]
    // Above the 6-floor: scale 13 (capped from 21), 15 (uncapped) and 22.
    [DataRow("cast(4.0000000000 as decimal(30, 10)) / cast(7.00000 as decimal(10, 5))", "0.5714285714285")]
    [DataRow("cast(2.0000 as decimal(10, 4)) / cast(7.00000 as decimal(10, 5))", "0.285714285714285")]
    [DataRow("cast(1.00 as decimal(5, 2)) / cast(7.00 as money)", "0.1428571428571428571428")]
    // Literal operands take the same rule.
    [DataRow("2.0/3.0", "0.666666")]
    [DataRow("2/3.0", "0.666666")]
    public void DecimalDivision_TruncatesTowardZeroAtTheResultScale(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar<decimal>($"select {expression}")
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// A result scale at .NET <see cref="decimal"/>'s own 28-digit ceiling
    /// still reports real's digits: dividing first and truncating after would
    /// hand .NET's rounding of the quotient (…6667) into the kept digits, so
    /// the dividend is scaled up front instead.
    /// </summary>
    [TestMethod]
    public void DecimalDivision_ResultScaleAtDecimalsCeiling_KeepsRealsDigits() =>
        AreEqual(
            "0.6666666666666666666666666666",
            ExecuteScalar<decimal>("select cast(2 as decimal(38, 28)) / cast(3 as decimal(1, 0))")
                .ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The contrast case for the rule above: every operator but division
    /// rounds half away from zero at the result scale, including on the capped
    /// paths where the scale reduction is what forces digits away.
    /// </summary>
    [TestMethod]
    // Multiply, capped from scale 36 to the 6-floor: an exact half rounds up,
    // and a longer tail rounds to nearest, in both signs.
    [DataRow("cast(1.500000000000000000 as decimal(38, 18)) * cast(1.000001000000000000 as decimal(38, 18))", "1.500002")]
    [DataRow("cast(-1.500000000000000000 as decimal(38, 18)) * cast(1.000001000000000000 as decimal(38, 18))", "-1.500002")]
    [DataRow("cast(1.000000900000000000 as decimal(38, 18)) * cast(1.000000900000000000 as decimal(38, 18))", "1.000002")]
    [DataRow("cast(1.500000000000000000 as decimal(38, 18)) * cast(1.000000500000000000 as decimal(38, 18))", "1.500001")]
    // Add / subtract, capped from scale 30 to 10.
    [DataRow("cast(1.0000000000 as decimal(38, 10)) + cast(0.000000000050000000000000000000 as decimal(38, 30))", "1.0000000001")]
    [DataRow("cast(-1.0000000000 as decimal(38, 10)) - cast(0.000000000090000000000000000000 as decimal(38, 30))", "-1.0000000001")]
    public void DecimalArithmetic_EveryOperatorButDivisionRoundsHalfAwayFromZero(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar<decimal>($"select {expression}")
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The 38-precision cap splits by operator family. Multiply and divide
    /// reduce the scale by the whole excess down to a floor of
    /// <c>min(scale, 6)</c>; add and subtract instead drop the carry digit
    /// first and keep whatever scale fits beside the widest operand's integral
    /// part, with no floor — so <c>decimal(38, 38) + decimal(38, 0)</c> is
    /// scale 0 where the excess rule would say 6. Rendered strings, since the
    /// declared scale is the point.
    /// </summary>
    [TestMethod]
    [DataRow("cast(1.00000000000000000005 as decimal(38, 20)) + cast(1.00000000000000000005 as decimal(38, 20))", "2.00000000000000000010")]
    [DataRow("cast(1.0000000005 as decimal(38, 10)) + cast(0.000000000000000000000000000000 as decimal(38, 30))", "1.0000000005")]
    [DataRow("cast(1.0000005 as decimal(38, 7)) + cast(1.0000005 as decimal(38, 7))", "2.0000010")]
    [DataRow("cast(1.0005 as decimal(38, 4)) + cast(1.0005 as money)", "2.0010")]
    [DataRow("cast(1.00005 as decimal(38, 5)) + cast(1.00000000000000000000000000000000005 as decimal(38, 35))", "2.00005")]
    [DataRow("cast(0.5 as decimal(38, 38)) + cast(1 as decimal(38, 0))", "2")]
    [DataRow("cast(1.0000000005 as decimal(38, 10)) - cast(0.0000000000 as decimal(38, 10))", "1.0000000005")]
    // Multiply keeps the 6-floor whatever the excess would give (4 and 3 here).
    [DataRow("cast(1.23456 as decimal(20, 5)) * cast(1.23456 as decimal(23, 5))", "1.524138")]
    [DataRow("cast(1.23456789 as decimal(25, 8)) * cast(1.23456789 as decimal(25, 8))", "1.524158")]
    // Divide above the floor keeps its reduced scale (14 → 7).
    [DataRow("cast(1.234 as decimal(30, 3)) / cast(1.2345 as decimal(10, 4))", "0.9995949")]
    public void DecimalArithmetic_38Cap_ReducesScalePerOperatorFamily(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar<decimal>($"select {expression}")
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    public void DecimalArithmetic_ChainedExpressionPropagatesScale() =>
        AreEqual(8.000000m, ExecuteScalar("select cast(2 as decimal(10,2)) * cast(2 as decimal(10,2)) * cast(2 as decimal(10,2))"));

    [TestMethod]
    public void DecimalArithmetic_ModuloPreservesMaxScale() =>
        AreEqual(1.50m, ExecuteScalar("select cast(7.5 as decimal(10,2)) % cast(3.0 as decimal(5,2))"));

    [TestMethod]
    public void Promote_DecimalAndStringInComparison()
    {
        AreEqual(1.5m, new Simulation().ExecuteScalar<decimal>("""
            create table t (v decimal(10, 2));
            insert t (v) values (cast(1.5 as decimal(10, 2)));
            select v from t where v = '1.5'
            """));
    }

    [TestMethod]
    public void Promote_DecimalAndIntegerInComparison()
    {
        AreEqual(1m, new Simulation().ExecuteScalar<decimal>("""
            create table t (v decimal(10, 2));
            insert t (v) values (cast(1 as decimal(10, 2)));
            select v from t where v = 1
            """));
    }

    [TestMethod]
    public void Insert_DecimalLiteralIntoDecimalColumn_RoundsToColumnScale()
    {
        AreEqual(1.23m, new Simulation().ExecuteScalar<decimal>("""
            create table t (v decimal(10, 2));
            insert t (v) values (1.234);
            select v from t
            """));
    }

    [TestMethod]
    public void Insert_StringIntoDecimalColumn_ParsesAndStores()
    {
        AreEqual(5.95m, new Simulation().ExecuteScalar<decimal>("""
            create table t (v decimal(10, 2));
            insert t (v) values ('5.95');
            select v from t
            """));
    }

    [TestMethod]
    public void StorageWidth_MatchesSqlServerByPrecisionTier()
    {
        // SQL Server widths: 5/9/13/17 bytes for p ≤ 9 / 10-19 / 20-28 / 29-38. Pinning the no-throw path:
        // the schema below sums well within 8060, so create succeeds.
        _ = new Simulation().ExecuteNonQuery(
            "create table t (a decimal(9, 0), b decimal(19, 0), c decimal(28, 0))");
    }

    [TestMethod]
    public void Cast_DecimalPrecisionAbove28_TypeIsValid_ValuesWithinNetDecimalRangeWork()
    {
        // Simulator allows decimal(29-38) declarations so storage byte-width matches SQL Server.
        // Values within .NET decimal's ~7.9e28 range round-trip; values above are out of scope.
        AreEqual(1m, ExecuteScalar("select cast(1 as decimal(29, 0))"));
        AreEqual(123.45m, ExecuteScalar("select cast(123.45 as decimal(38, 2))"));
    }

    [TestMethod]
    public void Parameter_DecimalRoundTrips()
    {
        const decimal expected = 123.45m;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        AreEqual(expected, command.ExecuteScalar());
    }

    [TestMethod]
    public void GetDecimal_ReturnsTheValue()
    {
        const decimal expected = 123.45m;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(expected, reader.GetDecimal(0));
    }

    /// <summary>
    /// The .NET <see cref="decimal"/> a conversion or a computation produces
    /// carries the *declared* scale of its result type, not whatever scale the
    /// source value happened to have — <c>cast(1 as numeric(10, 2))</c> is
    /// <c>1.00m</c>. Scale is invisible to decimal equality, so the assertion
    /// compares the rendered text; the expectations are the strings SQL Server
    /// 2025 emits for the same expression through <c>JSON_ARRAY</c>, which
    /// writes the raw value rather than formatting from the declared type.
    /// </summary>
    [TestMethod]
    [DataRow("cast(1 as numeric(10, 2))", "1.00")]
    [DataRow("cast(1 as numeric(10, 0))", "1")]
    [DataRow("cast(1.5 as numeric(10, 4))", "1.5000")]
    [DataRow("cast(1 as decimal(28, 7))", "1.0000000")]
    [DataRow("convert(numeric(10, 2), 1)", "1.00")]
    [DataRow("try_cast('1' as numeric(10, 2))", "1.00")]
    [DataRow("try_convert(numeric(10, 2), 1)", "1.00")]
    [DataRow("cast(cast(1 as numeric(10, 2)) as numeric(10, 5))", "1.00000")]
    [DataRow("cast(cast(1.999 as numeric(10, 3)) as numeric(10, 1))", "2.0")]
    // Widening a value rounded down to a signed zero drops the sign, as real does.
    [DataRow("cast(cast(-0.001 as numeric(10, 2)) as numeric(10, 4))", "0.0000")]
    [DataRow("case when 1 = 1 then cast(1 as numeric(10, 2)) else cast(1 as numeric(10, 4)) end", "1.0000")]
    [DataRow("(select cast(1 as numeric(10, 2)))", "1.00")]
    public void DeclaredScale_Conversions_CarryTheTargetScale(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar<decimal>($"select {expression}")
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Arithmetic results carry the scale the per-operator promotion rules
    /// declare — <c>max(s1, s2)</c> for <c>+ - %</c>, <c>s1 + s2</c> for
    /// <c>*</c>, and division's <c>max(6, s1 + p2 + 1)</c>. Expectations
    /// probed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("cast(1 as numeric(10, 2)) + cast(1 as numeric(5, 1))", "2.00")]
    [DataRow("cast(1 as numeric(10, 2)) + 0", "1.00")]
    [DataRow("cast(1 as numeric(10, 2)) + cast(1 as int)", "2.00")]
    [DataRow("cast(1 as numeric(10, 2)) - cast(1 as numeric(10, 2))", "0.00")]
    [DataRow("cast(1 as numeric(10, 2)) * cast(2 as numeric(5, 1))", "2.000")]
    [DataRow("cast(1 as numeric(10, 2)) / cast(2 as numeric(5, 1))", "0.50000000")]
    [DataRow("cast(10 as numeric(5, 0)) / cast(4 as numeric(5, 0))", "2.500000")]
    [DataRow("cast(1 as numeric(10, 2)) % cast(0.7 as numeric(5, 1))", "0.30")]
    [DataRow("-cast(1 as numeric(10, 2))", "-1.00")]
    [DataRow("round(cast(1.239 as numeric(10, 3)), 1)", "1.200")]
    [DataRow("abs(cast(-1 as numeric(10, 2)))", "1.00")]
    [DataRow("sign(cast(-1 as numeric(10, 2)))", "-1.00")]
    [DataRow("power(cast(2 as numeric(10, 2)), 2)", "4.00")]
    // CEILING / FLOOR return numeric(p, 0), so the fractional digits go away.
    [DataRow("ceiling(cast(1.2 as numeric(10, 2)))", "2")]
    [DataRow("floor(cast(1.8 as numeric(10, 2)))", "1")]
    public void DeclaredScale_Arithmetic_CarriesTheResultTypeScale(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar<decimal>($"select {expression}")
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// A value read back from storage, and an aggregate over one, carry their
    /// declared scale too — <c>SUM</c> keeps the column's, <c>AVG</c> promotes
    /// to <c>numeric(38, max(s, 6))</c>.
    /// </summary>
    [TestMethod]
    [DataRow("v", "1.00")]
    [DataRow("sum(v)", "1.00")]
    [DataRow("avg(v)", "1.000000")]
    [DataRow("min(v)", "1.00")]
    [DataRow("max(v)", "1.00")]
    public void DeclaredScale_StorageAndAggregates_CarryTheDeclaredScale(string expression, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<decimal>($"""
            create table t (v numeric(10, 2));
            insert t (v) values (1);
            select {expression} from t
            """).ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// <c>CHECKSUM</c> keys off the numeric value rather than the declared
    /// scale, so two spellings of the same number agree the way real's do.
    /// </summary>
    [TestMethod]
    public void Checksum_IgnoresDeclaredScale() =>
        AreEqual(1, ExecuteScalar("""
            select case when checksum(cast(1 as numeric(10, 2))) = checksum(cast(1 as numeric(10, 0)))
                then 1 else 0 end
            """));
}
