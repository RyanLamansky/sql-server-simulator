using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for SQL aggregate functions: COUNT/COUNT_BIG/SUM/AVG/MAX/MIN,
/// the statistical family (STDEV, STDEVP, VAR, VARP), STRING_AGG, CHECKSUM_AGG,
/// APPROX_COUNT_DISTINCT — both standalone and with GROUP BY / HAVING.
/// </summary>
[TestClass]
public sealed class AggregateTests
{
    private static DbConnection Seeded(string schema, string values)
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand($"create table t ({schema})").ExecuteNonQuery();
        if (!string.IsNullOrEmpty(values))
            _ = connection.CreateCommand($"insert into t values {values}").ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void Count_Star_CountsRowsIncludingNullColumns()
    {
        using var connection = Seeded("a int", "(1), (2), (null), (3)");
        AreEqual(4, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Count_Column_SkipsNulls()
    {
        using var connection = Seeded("a int", "(1), (2), (null), (3)");
        AreEqual(3, connection.CreateCommand("select count(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Count_Distinct_DedupsAndSkipsNulls()
    {
        using var connection = Seeded("a int", "(1), (2), (1), (null), (2)");
        AreEqual(2, connection.CreateCommand("select count(distinct a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Count_EmptyInput_ReturnsZero()
    {
        // Only aggregate that doesn't return NULL on empty input.
        using var connection = Seeded("a int", "");
        AreEqual(0, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void CountBig_StarAlias_ReturnsBigInt()
    {
        using var connection = Seeded("a int", "(1), (2), (3)");
        AreEqual(3L, connection.CreateCommand("select count_big(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_Int_TracksTotalSkippingNulls()
    {
        using var connection = Seeded("a int", "(10), (20), (null), (30)");
        AreEqual(60, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_Decimal_PreservesScale()
    {
        using var connection = Seeded("p decimal(10, 2)", "(1.50), (2.50), (3.00)");
        AreEqual(7.00m, connection.CreateCommand("select sum(p) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_EmptyInput_ReturnsNull()
    {
        using var connection = Seeded("a int", "");
        AreEqual(DBNull.Value, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_AllNullInput_ReturnsNull()
    {
        using var connection = Seeded("a int", "(null), (null)");
        AreEqual(DBNull.Value, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_IntOverflow_RaisesMsg8115()
    {
        using var connection = Seeded("a int", "(2147483647), (1)");
        var ex = Throws<DbException>(() => connection.CreateCommand("select sum(a) from t").ExecuteScalar());
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Sum_Float_AccumulatesViaDouble()
    {
        using var connection = Seeded("a float", "(1.5), (2.25), (0.25)");
        AreEqual(4.0, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Sum_Real_AccumulatesViaDoubleAndNarrowsBack()
    {
        using var connection = Seeded("a real", "(1.5), (2.25), (0.25)");
        AreEqual(4.0f, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Avg_Int_TruncatesByIntegerDivision()
    {
        using var connection = Seeded("a int", "(1), (2), (2)");
        AreEqual(1, connection.CreateCommand("select avg(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Avg_Decimal_WidensToDecimal38_6()
    {
        using var connection = Seeded("p decimal(10, 2)", "(1.50), (2.50), (3.00)");
        AreEqual(2.333333m, connection.CreateCommand("select avg(p) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Max_OnInt()
    {
        using var connection = Seeded("a int", "(10), (5), (20), (null)");
        AreEqual(20, connection.CreateCommand("select max(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Min_OnInt()
    {
        using var connection = Seeded("a int", "(10), (5), (20), (null)");
        AreEqual(5, connection.CreateCommand("select min(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void MaxMin_OnString_ByCollationOrder()
    {
        using var connection = Seeded("s nvarchar(20)", "('alpha'), ('gamma'), ('beta')");
        AreEqual("gamma", connection.CreateCommand("select max(s) from t").ExecuteScalar());
        AreEqual("alpha", connection.CreateCommand("select min(s) from t").ExecuteScalar());
    }

    [TestMethod]
    public void MaxMin_EmptyInput_ReturnsNull()
    {
        using var connection = Seeded("a int", "");
        AreEqual(DBNull.Value, connection.CreateCommand("select max(a) from t").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand("select min(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Max_OnText_RaisesMsg8117()
    {
        using var connection = Seeded("t text", "('x')");
        var ex = Throws<DbException>(() => connection.CreateCommand("select max(t) from t").ExecuteScalar());
        AreEqual("8117", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Stdev_SingleRow_ReturnsNull()
    {
        // Sample stddev needs n > 1.
        using var connection = Seeded("a int", "(5)");
        AreEqual(DBNull.Value, connection.CreateCommand("select stdev(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void StdevP_SingleRow_ReturnsZero()
    {
        using var connection = Seeded("a int", "(5)");
        AreEqual(0d, connection.CreateCommand("select stdevp(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void Var_VarP_OverIntegerColumn()
    {
        // 10, 20, 30: mean=20, sample var = ((10-20)² + 0 + (30-20)²) / 2 = 100. Population var = 200/3 ≈ 66.67.
        using var connection = Seeded("a int", "(10), (20), (30)");
        AreEqual(100d, connection.CreateCommand("select var(a) from t").ExecuteScalar());
        var pop = (double)connection.CreateCommand("select varp(a) from t").ExecuteScalar()!;
        IsLessThan(1e-5, Math.Abs(pop - 66.6666666));
    }

    [TestMethod]
    public void StringAgg_ConcatsWithSeparator()
    {
        using var connection = Seeded("s nvarchar(20)", "('a'), ('b'), ('c')");
        AreEqual("a,b,c", connection.CreateCommand("select string_agg(s, ',') from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_SkipsNulls()
    {
        using var connection = Seeded("s nvarchar(20)", "('a'), (null), ('b')");
        AreEqual("a,b", connection.CreateCommand("select string_agg(s, ',') from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringAgg_EmptyInput_ReturnsNull()
    {
        using var connection = Seeded("s nvarchar(20)", "");
        AreEqual(DBNull.Value, connection.CreateCommand("select string_agg(s, ',') from t").ExecuteScalar());
    }

    [TestMethod]
    public void ChecksumAgg_OrderIndependentFold()
    {
        // Semantic guarantee: same multiset → same checksum (exact bit pattern not pinned).
        using var ascending = Seeded("a int", "(1), (2), (3)");
        using var reversed = Seeded("a int", "(3), (2), (1)");
        AreEqual(
            ascending.CreateCommand("select checksum_agg(a) from t").ExecuteScalar(),
            reversed.CreateCommand("select checksum_agg(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void ApproxCountDistinct_BehavesLikeCountDistinct()
    {
        // Simulator implements as exact COUNT(DISTINCT). Returns bigint.
        using var connection = Seeded("a int", "(1), (2), (1), (null), (3)");
        AreEqual(3L, connection.CreateCommand("select approx_count_distinct(a) from t").ExecuteScalar());
    }

    [TestMethod]
    public void GroupBy_PartitionsByKey()
    {
        using var connection = Seeded("s nvarchar(20), a int", "('alpha', 1), ('alpha', 2), ('beta', 5), ('beta', 7)");
        using var reader = connection.CreateCommand("select s, sum(a) from t group by s").ExecuteReader();
        var totals = new Dictionary<string, int>();
        while (reader.Read())
            totals[(string)reader[0]] = (int)reader[1];
        AreEqual(3, totals["alpha"]);
        AreEqual(12, totals["beta"]);
    }

    [TestMethod]
    public void GroupBy_NullKey_OneBucketForNulls()
    {
        // SQL Server: NULL is a valid group key with exactly one bucket.
        using var connection = Seeded("a int, b int", "(null, 1), (null, 2), (1, 5)");
        using var reader = connection.CreateCommand("select a, sum(b) from t group by a").ExecuteReader();
        var seen = new List<(object key, int sum)>();
        while (reader.Read())
            seen.Add((reader[0], (int)reader[1]));
        HasCount(2, seen);
    }

    [TestMethod]
    public void GroupBy_Having_FiltersByAggregatePredicate()
    {
        using var connection = Seeded("s nvarchar(20)", "('alpha'), ('alpha'), ('beta'), ('gamma')");
        using var reader = connection.CreateCommand("select s, count(*) from t group by s having count(*) > 1").ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            AreEqual("alpha", reader[0]);
            AreEqual(2, reader[1]);
            rows++;
        }
        AreEqual(1, rows);
    }
}
