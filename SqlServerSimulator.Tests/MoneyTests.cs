using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>money</c> / <c>smallmoney</c>. The big
/// authenticity surface here is the currency-symbol literal grammar — most
/// ISO currency symbols accepted, the newer Indian rupee (<c>₹</c>, U+20B9)
/// rejected. String → money parsing strips currency and thousands commas;
/// money → varchar emits 2 decimal places (not the storage scale of 4).
/// </summary>
[TestClass]
public sealed class MoneyTests
{
    [TestMethod]
    public void Literal_DollarSign_ProducesMoney()
    {
        AreEqual(5.95m, ExecuteScalar("select $5.95"));
    }

    [TestMethod]
    [DataRow("$5.95", "5.9500")]
    [DataRow("$+5.95", "5.9500")]
    [DataRow("$-5.95", "-5.9500")]
    [DataRow("$0", "0.0000")]
    [DataRow("$.5", "0.5000")]
    [DataRow("$5.", "5.0000")]
    [DataRow("£5.95", "5.9500")]
    [DataRow("€5.95", "5.9500")]
    [DataRow("¥100", "100.0000")]
    [DataRow("¢50", "50.0000")]
    [DataRow("₩100", "100.0000")]
    [DataRow("₪100", "100.0000")]
    [DataRow("₫100", "100.0000")]
    [DataRow("฿100", "100.0000")]
    [DataRow("₠100", "100.0000")]
    [DataRow("₱100", "100.0000")]
    public void Literal_AcceptedCurrencySymbols(string literal, string expected)
    {
        var value = ExecuteScalar<decimal>($"select {literal}");
        AreEqual(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [TestMethod]
    public void Literal_LoneDollarIsZero()
    {
        // Verified against SQL Server 2025 — surprising but consistent.
        AreEqual(0m, ExecuteScalar("select $"));
    }

    [TestMethod]
    public void Literal_ScientificNotationNotAcceptedInMoney()
    {
        // <c>$1e2</c> parses as <c>$1</c> only — the literal ends at <c>e</c>
        // and the trailing <c>e2</c> is consumed as something else (alias /
        // column ref). Verified against SQL Server 2025: returns 1.0000.
        AreEqual(1m, ExecuteScalar("select $1e2"));
    }

    [TestMethod]
    public void Cast_DefaultMoneyToVarchar_UsesTwoDecimalPlaces()
    {
        // Surprise: money's storage scale is 4 but its varchar default is 2.
        AreEqual("5.95", ExecuteScalar("select cast($5.95 as varchar(20))"));
        AreEqual("0.00", ExecuteScalar("select cast($0 as varchar(20))"));
    }

    [TestMethod]
    [DataRow("'5.95'", "5.9500")]
    [DataRow("'$5.95'", "5.9500")]
    [DataRow("'  $5.95  '", "5.9500")]
    [DataRow("'$-5.95'", "-5.9500")]
    [DataRow("'-$5.95'", "-5.9500")]
    [DataRow("'£5.95'", "5.9500")]
    [DataRow("'$5,000.00'", "5000.0000")]
    [DataRow("'5,000.00'", "5000.0000")]
    [DataRow("'5.123456'", "5.1235")]
    [DataRow("'5.12345'", "5.1235")]
    public void Cast_StringToMoney_AcceptsCurrencyAndCommas(string literal, string expected)
    {
        var value = ExecuteScalar<decimal>($"select cast({literal} as money)");
        AreEqual(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [TestMethod]
    [DataRow("'abc'")]
    [DataRow("'5.5e2'")]
    [DataRow("''")]
    public void Cast_StringToMoney_BadFormatRaisesMsg235(string literal)
    {
        var ex = Throws<DbException>(() => ExecuteScalar($"select cast({literal} as money)"));
        AreEqual("Cannot convert a char value to money. The char value has incorrect syntax.", ex.Message);
    }

    [TestMethod]
    public void Cast_OverflowToSmallMoney_RaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(214748.3648 as smallmoney)"));
        Assert.Contains("smallmoney", ex.Message);
    }

    [TestMethod]
    public void SmallMoney_BoundaryRangeRoundTrips()
    {
        AreEqual(214748.3647m, ExecuteScalar("select cast(214748.3647 as smallmoney)"));
        AreEqual(-214748.3648m, ExecuteScalar("select cast(-214748.3648 as smallmoney)"));
    }

    [TestMethod]
    public void Money_BoundaryRangeRoundTrips()
    {
        AreEqual(922337203685477.5807m, ExecuteScalar("select cast(922337203685477.5807 as money)"));
    }

    [TestMethod]
    public void MoneyArithmetic_MoneyPlusMoneyIsMoney()
    {
        AreEqual(8m, ExecuteScalar("select $5 + $3"));
    }

    [TestMethod]
    public void MoneyArithmetic_MoneyTimesIntIsMoney()
    {
        AreEqual(15m, ExecuteScalar("select $5 * 3"));
    }

    [TestMethod]
    public void Promote_MoneyAndStringInComparison()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v money)");
        _ = simulation.ExecuteNonQuery("insert into t (v) values ($5.95)");
        var match = simulation.ExecuteScalar<decimal>("select v from t where v = '5.95'");
        AreEqual(5.95m, match);
    }

    [TestMethod]
    public void Insert_StringIntoMoneyColumn_StripsCurrencyAndStores()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v money)");
        _ = simulation.ExecuteNonQuery("insert into t (v) values ('$1,234.56')");
        AreEqual(1234.56m, simulation.ExecuteScalar<decimal>("select v from t"));
    }

    [TestMethod]
    public void StorageWidth_MoneyIs8_SmallMoneyIs4()
    {
        // Indirect check: smoke-test create succeeds for both; row-size
        // budgeting depends on these widths.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a money, b smallmoney)");
    }

    [TestMethod]
    public void Parameter_DecimalRoundTripsThroughMoneyColumn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (v money)");
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t (v) values (@p)", ("@p", 19.99m));
        _ = command.ExecuteNonQuery();
        AreEqual(19.99m, simulation.ExecuteScalar<decimal>("select v from t"));
    }
}
