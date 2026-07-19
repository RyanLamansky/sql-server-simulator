using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the analytic distribution windows
/// (<c>CUME_DIST</c> / <c>PERCENT_RANK</c>) and the ordered-set analytic
/// functions (<c>PERCENTILE_CONT</c> / <c>PERCENTILE_DISC</c>). The first two
/// ride the same ranking partition/peer-group path as RANK; the percentile
/// pair use a mandatory <c>WITHIN GROUP (ORDER BY ...)</c> for ordering and an
/// <c>OVER ([PARTITION BY ...])</c> with no ORDER BY, broadcasting the
/// per-partition percentile to every row. Expected values were probe-confirmed
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class AnalyticWindowTests
{
    // Single group with a NULL and a tied pair: {null, 10, 20, 20, 40}. NULLs
    // participate in CUME_DIST / PERCENT_RANK ordering but are excluded from
    // the percentile computation.
    private static DbConnection SeededValues()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (v int);
            insert t values (null), (10), (20), (20), (40)
            """).ExecuteNonQuery();
        return connection;
    }

    private static Dictionary<int, double> ReadValueToDouble(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var map = new Dictionary<int, double>();
        while (reader.Read())
            map[reader.IsDBNull(0) ? -1 : reader.GetInt32(0)] = reader.GetDouble(1);
        return map;
    }

    // values {null, 10, 20, 20, 40} fronted to a percentile query, so the
    // non-NULL working set is {10, 20, 20, 40}.
    private static string PercentileOver(string func) =>
        $"create table t (v int); insert t values (null),(10),(20),(20),(40); " +
        $"select {func} within group (order by v) over () from t";

    [TestMethod]
    public void CumeDist_IncludesNullsAndSharesPeerValue()
    {
        // 5 rows: null→1/5, 10→2/5, both 20s→4/5 (peers), 40→5/5.
        using var connection = SeededValues();
        var byValue = ReadValueToDouble(connection.CreateCommand(
            "select v, cume_dist() over (order by v) from t"));
        AreEqual(0.2, byValue[-1]);
        AreEqual(0.4, byValue[10]);
        AreEqual(0.8, byValue[20]);
        AreEqual(1.0, byValue[40]);
    }

    [TestMethod]
    public void PercentRank_UsesRankMinusOneOverCountMinusOne()
    {
        // RANK values null→1, 10→2, 20→3 (peers), 40→5; (rank-1)/(5-1).
        using var connection = SeededValues();
        var byValue = ReadValueToDouble(connection.CreateCommand(
            "select v, percent_rank() over (order by v) from t"));
        AreEqual(0.0, byValue[-1]);
        AreEqual(0.25, byValue[10]);
        AreEqual(0.5, byValue[20]);
        AreEqual(1.0, byValue[40]);
    }

    [TestMethod]
    public void PercentRank_SingleRowPartition_IsZero()
        => AreEqual(0.0, new Simulation().ExecuteScalar<double>(
            "create table t (n int); insert t values (42); select percent_rank() over (order by n) from t"));

    /// <summary>
    /// {10,20,30,40}: rank = 0.5*3 = 1.5 → 20 + 0.5*(30-20) = 25.
    /// </summary>
    [TestMethod]
    public void PercentileCont_Median_InterpolatesBetweenMiddleValues()
        => AreEqual(25.0, new Simulation().ExecuteScalar<double>(
            "create table t (n int); insert t values (10),(20),(30),(40); " +
            "select percentile_cont(0.5) within group (order by n) over () from t"));

    [TestMethod]
    public void PercentileCont_QuartilesIgnoreNull()
    {
        // Over non-NULL {10,20,20,40}: p25 → 17.5, p50 → 20, p75 → 25.
        AreEqual(17.5, new Simulation().ExecuteScalar<double>(PercentileOver("percentile_cont(0.25)")));
        AreEqual(20.0, new Simulation().ExecuteScalar<double>(PercentileOver("percentile_cont(0.5)")));
        AreEqual(25.0, new Simulation().ExecuteScalar<double>(PercentileOver("percentile_cont(0.75)")));
    }

    [TestMethod]
    public void PercentileDisc_PicksSmallestValueWithCumeDistAtLeastP()
    {
        // Over non-NULL {10,20,20,40}: p25 → 10, p50 → 20, p75 → 20, p100 → 40.
        AreEqual(10, new Simulation().ExecuteScalar<int>(PercentileOver("percentile_disc(0.25)")));
        AreEqual(20, new Simulation().ExecuteScalar<int>(PercentileOver("percentile_disc(0.5)")));
        AreEqual(20, new Simulation().ExecuteScalar<int>(PercentileOver("percentile_disc(0.75)")));
        AreEqual(40, new Simulation().ExecuteScalar<int>(PercentileOver("percentile_disc(1.0)")));
    }

    /// <summary>
    /// PERCENTILE_DISC returns the sort expr's type — decimal stays decimal.
    /// </summary>
    [TestMethod]
    public void PercentileDisc_ReturnsSortExpressionType()
        => AreEqual(20.00m, new Simulation().ExecuteScalar<decimal>(
            "create table t (n int); insert t values (10),(20),(30); " +
            "select percentile_disc(0.5) within group (order by cast(n as decimal(10,2))) over () from t"));

    /// <summary>
    /// {10,20,30,40} desc → {40,30,20,10}; rank 0.75 → 40 + 0.75*(30-40) = 32.5.
    /// </summary>
    [TestMethod]
    public void PercentileCont_DescReversesOrdering()
        => AreEqual(32.5, new Simulation().ExecuteScalar<double>(
            "create table t (n int); insert t values (10),(20),(30),(40); " +
            "select percentile_cont(0.25) within group (order by n desc) over () from t"));

    [TestMethod]
    public void Percentile_AllNullInput_ReturnsNull()
    {
        const string nulls = "create table t (n int); insert t values (null),(null); ";
        _ = IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar(nulls +
            "select percentile_cont(0.5) within group (order by n) over () from t"));
        _ = IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar(nulls +
            "select percentile_disc(0.5) within group (order by n) over () from t"));
    }

    [TestMethod]
    public void Percentile_PerPartition_BroadcastsGroupValue()
    {
        // g=1: {10,20,30} → median 20; g=2: {5,15} → cont 10, disc 5.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (g int, v int);
            insert p values (1,10),(1,20),(1,30),(2,5),(2,15)
            """);
        AreEqual(20.0, sim.ExecuteScalar<double>(
            "select percentile_cont(0.5) within group (order by v) over (partition by g) from p where g = 1"));
        AreEqual(10.0, sim.ExecuteScalar<double>(
            "select percentile_cont(0.5) within group (order by v) over (partition by g) from p where g = 2"));
        AreEqual(5, sim.ExecuteScalar<int>(
            "select percentile_disc(0.5) within group (order by v) over (partition by g) from p where g = 2"));
    }

    [TestMethod]
    public void PercentileArg_AcceptsVariable()
        => AreEqual(20.0, new Simulation().ExecuteScalar<double>("""
            create table t (n int);
            insert t values (10),(20),(30);
            declare @p float = 0.5;
            select percentile_cont(@p) within group (order by n) over () from t
            """));

    [TestMethod]
    public void PercentileCont_WithoutOver_RaisesMsg10753()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (n int); insert t values (10),(20); " +
            "select percentile_cont(0.5) within group (order by n) from t", 10753);
        Assert.Contains("must have an OVER clause", ex.Message);
    }

    [TestMethod]
    public void PercentileCont_OrderByInOver_RaisesMsg10758()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (n int); insert t values (10),(20); " +
            "select percentile_cont(0.5) within group (order by n) over (order by n) from t", 10758);
        Assert.Contains("may not have ORDER BY in OVER clause", ex.Message);
    }

    [TestMethod]
    public void Percentile_ArgOutOfRange_RaisesMsg8727()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (n int); insert t values (10),(20); " +
            "select percentile_cont(1.5) within group (order by n) over () from t", 8727);
        Assert.Contains("outside of range [0, 1]", ex.Message);
    }

    [TestMethod]
    public void CumeDist_WithoutOrderBy_IsRejected()
        => Throws<DbException>(() => new Simulation().ExecuteScalar(
            "create table t (n int); insert t values (10),(20); select cume_dist() over (partition by n) from t"));
}
