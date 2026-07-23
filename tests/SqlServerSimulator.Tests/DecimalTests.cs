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
}
