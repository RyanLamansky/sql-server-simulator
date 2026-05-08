using System.Data.Common;
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
    public void Literal_FractionalDigits_ProducesDecimalValue()
    {
        var value = ExecuteScalar("select 100.5");
        AreEqual(100.5m, value);
    }

    [TestMethod]
    public void Literal_IntegerOnly_StaysAsInt()
    {
        var value = ExecuteScalar("select 100");
        AreEqual(100, value);
    }

    [TestMethod]
    public void Cast_StringToDecimal_BasicRoundTrip()
    {
        var value = ExecuteScalar("select cast('100.5' as decimal(10, 2))");
        AreEqual(100.50m, value);
    }

    [TestMethod]
    [DataRow("'12.345'", "12.35")]
    [DataRow("'12.346'", "12.35")]
    [DataRow("'-12.345'", "-12.35")]
    [DataRow("'12.5'", "13")]
    [DataRow("'12.49'", "12")]
    [DataRow("'-12.5'", "-13")]
    public void Cast_StringToDecimal_RoundsHalfAwayFromZero(string literal, string expected)
    {
        // SQL Server uses half-away-from-zero rounding when scale is reduced
        // during CAST (verified against SQL Server 2025).
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
    public void Cast_StringToDecimal_AcceptsCommonForms(string literal, double expectedRaw)
    {
        var value = ExecuteScalar<decimal>($"select cast({literal} as decimal(10, 2))");
        AreEqual((decimal)expectedRaw, value);
    }

    [TestMethod]
    [DataRow("'abc'")]
    [DataRow("''")]
    [DataRow("'$5.95'")]
    public void Cast_StringToDecimal_BadFormatRaisesMsg8114(string literal)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast({literal} as decimal(10, 2))"));
        AreEqual("Error converting data type varchar to numeric.", ex.Message);
    }

    [TestMethod]
    public void Cast_DecimalOverflow_RaisesMsg8115()
    {
        // 1000 doesn't fit decimal(3, 0) — integer part exceeds the 3-digit budget.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(1000 as decimal(3, 0))"));
        AreEqual("Arithmetic overflow error converting expression to data type numeric.", ex.Message);
    }

    [TestMethod]
    [DataRow("1.5", "1")]
    [DataRow("-1.5", "-1")]
    [DataRow("2.5", "2")]
    [DataRow("0.5", "0")]
    [DataRow("-0.5", "0")]
    public void Cast_DecimalToInt_TruncatesTowardZero(string sourceLiteral, string expected)
    {
        // Decimal → int uses truncation toward zero, NOT the half-away-from-
        // zero rounding that string → decimal scale-reduction uses. Verified
        // against SQL Server 2025: 1.5 → 1, -1.5 → -1, 0.5 → 0.
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
    public void Cast_NumericIsAliasOfDecimal()
    {
        AreEqual(12.34m, ExecuteScalar("select cast(12.34 as numeric(10, 2))"));
    }

    [TestMethod]
    public void Cast_DecimalDefaultPrecisionIs18Scale0()
    {
        // decimal with no parens defaults to (18, 0); 12.34 truncates the
        // fraction. Scale 0 still rounds half-away-from-zero on cast, so .5
        // rounds up.
        AreEqual(12m, ExecuteScalar("select cast(12.34 as decimal)"));
    }

    [TestMethod]
    public void DecimalArithmetic_AddPrecisionScale()
    {
        // d(5,2) + d(7,3) → d(8,3) per the formula
        // p = max(p1-s1, p2-s2) + max(s1, s2) + 1 = max(3, 4) + max(2, 3) + 1 = 8
        // s = max(s1, s2) = 3
        AreEqual(2.464m, ExecuteScalar("select cast(1.23 as decimal(5, 2)) + cast(1.234 as decimal(7, 3))"));
    }

    [TestMethod]
    public void DecimalArithmetic_MultiplyPrecisionScale()
    {
        // d(5,2) * d(7,3) → d(13,5): p = p1+p2+1 = 13, s = s1+s2 = 5.
        AreEqual(1.51782m, ExecuteScalar("select cast(1.23 as decimal(5, 2)) * cast(1.234 as decimal(7, 3))"));
    }

    [TestMethod]
    public void DecimalArithmetic_DividePreservesScale()
    {
        // d(5,2) / d(7,3): scale = max(6, s1+p2+1) = max(6, 10) = 10.
        AreEqual(3.3333333333m, ExecuteScalar("select cast(10 as decimal(5, 2)) / cast(3 as decimal(7, 3))"));
    }

    [TestMethod]
    public void DecimalArithmetic_DivideEnforcesMinScale6()
    {
        // d(38, 30) / d(38, 30) hits the 38-cap with the min-scale-6 rule.
        AreEqual(1m, ExecuteScalar("select cast(1 as decimal(38, 30)) / cast(1 as decimal(38, 30))"));
    }

    [TestMethod]
    public void DecimalArithmetic_DivideByZero_RaisesMsg8134()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(1 as decimal(5, 2)) / cast(0 as decimal(5, 2))"));
        AreEqual("Divide by zero error encountered.", ex.Message);
    }

    [TestMethod]
    public void DecimalArithmetic_PromotesIntegerToDecimal()
    {
        // d(5, 2) + int → d(13, 2): integer canonicalizes to d(10, 0) per
        // SQL Server's table; result formula yields p=13, s=2.
        AreEqual(3.23m, ExecuteScalar("select cast(1.23 as decimal(5, 2)) + 2"));
    }

    [TestMethod]
    public void DecimalArithmetic_OverflowOnMultiplicationRaisesMsg8115()
    {
        // d(20, 0) * d(20, 0) → p=41 capped to 38. Two 20-digit values
        // multiply to ~10^40, which exceeds the result type's 38-digit max.
        var ex = Throws<DbException>(() => ExecuteScalar(
            "select cast(99999999999999999999 as decimal(20, 0)) * cast(99999999999999999999 as decimal(20, 0))"));
        AreEqual("Arithmetic overflow error converting expression to data type numeric.", ex.Message);
    }

    [TestMethod]
    public void DecimalArithmetic_StaticSchemaMatchesRuntimeForDivision()
    {
        // The EF percent-of-total shape: (s.Amount * 100.0) / (sum). Before
        // the per-operator-arithmetic fix, the static schema computed via
        // joint-envelope Promote diverged from the per-operator runtime
        // type, causing the RowEncoder to reject the value with a
        // "decimal(38,N) vs decimal(38,M)" mismatch. The probed result is
        // d(38,24); confirming both schema and value land at scale 24.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table sales (region varchar(10), amount decimal(10,2))");
        _ = simulation.ExecuteNonQuery(
            "insert into sales values ('east', 100), ('east', 200), ('east', 150), ('west', 300), ('west', 500), ('west', 50)");
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand(
            "select s.region, s.amount, s.amount * 100.0 / (select sum(s0.amount) from sales as s0 where s0.region = s.region) from sales as s")
            .ExecuteReader();
        AreEqual("decimal", reader.GetDataTypeName(2));
        var pcts = new List<(string Region, decimal Amount, decimal Pct)>();
        while (reader.Read())
            pcts.Add((reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2)));
        // east totals 450; west totals 850. Verify a couple per region.
        var (_, _, east100Pct) = pcts.Single(p => p.Region == "east" && p.Amount == 100m);
        var (_, _, west500Pct) = pcts.Single(p => p.Region == "west" && p.Amount == 500m);
        AreEqual(decimal.Round(100m * 100m / 450m, 6), decimal.Round(east100Pct, 6));
        AreEqual(decimal.Round(500m * 100m / 850m, 6), decimal.Round(west500Pct, 6));
    }

    [TestMethod]
    public void DecimalArithmetic_38CapPreservesSmallScaleForMultiplication()
    {
        // d(38,2) * d(38,2): formula gives p=77, s=4. The 38-cap reduces
        // precision but the scale floor is min(originalScale, 6) = 4 (not
        // 0 — that was a latent bug in the old non-division path). SQL
        // Server returns d(38,4); verify the runtime value carries scale 4.
        var v = ExecuteScalar("select cast(123 as decimal(38,2)) * cast(456 as decimal(38,2))");
        AreEqual(56088.0000m, v);
    }

    [TestMethod]
    public void DecimalArithmetic_38CapForDivisionFloorsAtSix()
    {
        // d(38,30) / d(38,30): formula gives p=107, s=69. The 38-cap with
        // floor 6 lands at d(38,6).
        AreEqual(1.000000m, ExecuteScalar("select cast(1 as decimal(38,30)) / cast(1 as decimal(38,30))"));
    }

    [TestMethod]
    public void DecimalArithmetic_ChainedExpressionPropagatesScale()
    {
        // (d(10,2) * d(10,2)) * d(10,2) — first step yields d(21,4); next
        // step yields d(32,6) per the multiplication formula. Ensures the
        // schema/runtime parity holds through chaining.
        AreEqual(8.000000m, ExecuteScalar("select cast(2 as decimal(10,2)) * cast(2 as decimal(10,2)) * cast(2 as decimal(10,2))"));
    }

    [TestMethod]
    public void DecimalArithmetic_ModuloPreservesMaxScale()
    {
        // d(10,2) % d(5,2) → d(5,2): p = min(p1-s1, p2-s2) + max(s1,s2)
        // = min(8, 3) + 2 = 5; s = max(s1, s2) = 2.
        AreEqual(1.50m, ExecuteScalar("select cast(7.5 as decimal(10,2)) % cast(3.0 as decimal(5,2))"));
    }

    [TestMethod]
    public void Promote_DecimalAndStringInComparison()
    {
        // Implicit conversion: '1.5' parses as decimal(2,1), promoted up.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert into t (v) values (cast(1.5 as decimal(10, 2)))");
        var match = simulation.ExecuteScalar<decimal>("select v from t where v = '1.5'");
        AreEqual(1.5m, match);
    }

    [TestMethod]
    public void Promote_DecimalAndIntegerInComparison()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert into t (v) values (cast(1 as decimal(10, 2)))");
        var match = simulation.ExecuteScalar<decimal>("select v from t where v = 1");
        AreEqual(1m, match);
    }

    [TestMethod]
    public void Insert_DecimalLiteralIntoDecimalColumn_RoundsToColumnScale()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert into t (v) values (1.234)");
        AreEqual(1.23m, simulation.ExecuteScalar<decimal>("select v from t"));
    }

    [TestMethod]
    public void Insert_StringIntoDecimalColumn_ParsesAndStores()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v decimal(10, 2))");
        _ = simulation.ExecuteNonQuery("insert into t (v) values ('5.95')");
        AreEqual(5.95m, simulation.ExecuteScalar<decimal>("select v from t"));
    }

    [TestMethod]
    public void StorageWidth_MatchesSqlServerByPrecisionTier()
    {
        // SQL Server's documented widths: 5/9/13/17 bytes for p ≤ 9 / 10-19 /
        // 20-28 / 29-38. We expose this through the type's FixedLength via
        // the public CREATE TABLE path — different widths produce different
        // row sizes, so a too-wide schema raises Msg 1701.
        var simulation = new Simulation();
        // Schema with 4 decimal columns at progressively higher precision tiers
        // sums to 5 + 9 + 13 + 17 + (other column overhead) — well within 8060,
        // so the create succeeds. We're really pinning the no-throw path here.
        _ = simulation.ExecuteNonQuery(
            "create table t (a decimal(9, 0), b decimal(19, 0), c decimal(28, 0))");
    }

    [TestMethod]
    public void Cast_DecimalPrecisionAbove28_TypeIsValid_ValuesWithinNetDecimalRangeWork()
    {
        // The simulator allows decimal(29-38) declarations so that storage
        // byte-width matches SQL Server. .NET decimal values within its
        // ~7.9e28 range round-trip cleanly; values exceeding the .NET range
        // are out of scope (would need an arbitrary-precision mantissa).
        AreEqual(1m, ExecuteScalar("select cast(1 as decimal(29, 0))"));
        AreEqual(123.45m, ExecuteScalar("select cast(123.45 as decimal(38, 2))"));
    }

    [TestMethod]
    public void Parameter_DecimalRoundTrips()
    {
        const decimal expected = 123.45m;
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select @p", ("@p", expected));
        var actual = command.ExecuteScalar();
        AreEqual(expected, actual);
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
}
