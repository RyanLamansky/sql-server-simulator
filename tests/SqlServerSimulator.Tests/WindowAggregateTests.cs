using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for aggregate window functions
/// (<c>SUM/AVG/COUNT/COUNT_BIG/MIN/MAX/STDEV/STDEVP/VAR/VARP/CHECKSUM_AGG/APPROX_COUNT_DISTINCT
/// (...) OVER (PARTITION BY ...)</c>). The simulator supports the implicit-frame
/// whole-partition default; ORDER BY inside OVER for aggregates raises
/// <see cref="NotSupportedException"/>.
/// </summary>
[TestClass]
public sealed class WindowAggregateTests
{
    private static DbConnection SeededSales()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table sales (region varchar(10), salesperson varchar(20), amount decimal(10,2));
            insert sales values
                ('east', 'alice', 100.00),
                ('east', 'alice', 200.00),
                ('east', 'bob',   150.00),
                ('east', 'bob',   null),
                ('west', 'carol', 300.00),
                ('west', 'carol', 500.00),
                ('west', 'dan',   null)
            """).ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void Sum_OverPartitionBy_BroadcastsTotalAcrossDetailRows()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, amount, sum(amount) over(partition by region) from sales order by region, amount").ExecuteReader();
        var pairs = new List<(string Region, decimal? Amount, decimal? Sum)>();
        while (reader.Read())
            pairs.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2)));
        // east: 100+200+150 = 450 (all 4 rows). west: 300+500 = 800 (all 3 rows).
        foreach (var (region, _, sum) in pairs)
            AreEqual(region == "east" ? 450.00m : 800.00m, sum);
    }

    [TestMethod]
    public void Sum_OverEmptyPartition_BroadcastsGrandTotal()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand("select sum(amount) over() from sales").ExecuteReader();
        var values = new List<decimal>();
        while (reader.Read())
            values.Add(reader.GetDecimal(0));
        HasCount(7, values);
        // 100+200+150+300+500 = 1250.
        foreach (var v in values)
            AreEqual(1250.00m, v);
    }

    [TestMethod]
    public void Sum_Int_StaysInt_OverflowsThrowsMsg8115()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table big (v int);
            insert big values (2000000000), (2000000000)
            """).ExecuteNonQuery();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("select sum(v) over() from big").ExecuteScalar());
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Avg_DecimalScale_WidensToScale6OrMoreLikePlainAvg()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table d (v decimal(5,2));
            insert d values (1.23), (4.56)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select avg(v) over() from d").ExecuteReader();
        IsTrue(reader.Read());
        // (1.23+4.56)/2 = 2.895; AVG decimal(5,2) → decimal(38, max(2,6)) = decimal(38,6).
        AreEqual(2.895000m, reader.GetDecimal(0));
    }

    [TestMethod]
    public void Avg_Int_TruncatesPerPartition()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (g varchar(1), v int);
            insert t values ('a', 1), ('a', 2), ('a', 2)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select avg(v) over(partition by g) from t").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        HasCount(3, values);
        // (1+2+2)/3 = 5/3 = 1 (integer truncation); broadcast.
        foreach (var v in values)
            AreEqual(1, v);
    }

    [TestMethod]
    public void Count_Star_OverPartitionBy_CountsAllRowsIncludingNull()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, count(*) over(partition by region) from sales").ExecuteReader();
        var byRegion = new Dictionary<string, int>();
        while (reader.Read())
            byRegion[reader.GetString(0)] = reader.GetInt32(1);
        AreEqual(4, byRegion["east"]);
        AreEqual(3, byRegion["west"]);
    }

    [TestMethod]
    public void Count_Column_OverPartitionBy_SkipsNulls()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, count(amount) over(partition by region) from sales").ExecuteReader();
        var byRegion = new Dictionary<string, int>();
        while (reader.Read())
            byRegion[reader.GetString(0)] = reader.GetInt32(1);
        // east: 4 rows, 1 NULL → 3 non-null. west: 3 rows, 1 NULL → 2.
        AreEqual(3, byRegion["east"]);
        AreEqual(2, byRegion["west"]);
    }

    [TestMethod]
    public void CountBig_Star_ReturnsBigInt()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select count_big(*) over(partition by region) from sales where region='east'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(4L, reader.GetInt64(0));
    }

    [TestMethod]
    public void MinMax_OverPartitionBy_PerPartition()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, min(amount) over(partition by region), max(amount) over(partition by region) from sales").ExecuteReader();
        var seen = new HashSet<(string, decimal, decimal)>();
        while (reader.Read())
            _ = seen.Add((reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2)));
        Contains(("east", 100.00m, 200.00m), seen);
        Contains(("west", 300.00m, 500.00m), seen);
    }

    [TestMethod]
    public void Sum_AllNullPartition_ReturnsNull()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select sum(amount) over(partition by salesperson) from sales where salesperson='dan'").ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void Avg_AllNullPartition_ReturnsNull()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select avg(amount) over(partition by salesperson) from sales where salesperson='dan'").ExecuteReader();
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void Count_AllNullPartition_ReturnsZero()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select count(amount) over(partition by salesperson) from sales where salesperson='dan'").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(0, reader.GetInt32(0));
    }

    [TestMethod]
    public void Stdev_OverPartitionBy_Float()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, stdev(amount) over(partition by region) from sales where region='east'").ExecuteReader();
        IsTrue(reader.Read());
        // east non-null: 100, 200, 150 → sample stdev = 50.
        AreEqual(50.0, reader.GetDouble(1), 1e-9);
    }

    [TestMethod]
    public void PartitionBy_MultiKey_SubdividesPartitions()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, salesperson, sum(amount) over(partition by region, salesperson) from sales order by region, salesperson, amount").ExecuteReader();
        var triples = new List<(string Region, string Person, decimal? Sum)>();
        while (reader.Read())
            triples.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2)));
        var alice = triples.Where(t => t.Region == "east" && t.Person == "alice").ToList();
        IsTrue(alice.All(t => t.Sum == 300.00m));
        var bob = triples.Where(t => t.Region == "east" && t.Person == "bob").ToList();
        IsTrue(bob.All(t => t.Sum == 150.00m));
        var (_, _, danSum) = triples.Single(t => t.Region == "west" && t.Person == "dan");
        IsNull(danSum);
    }

    [TestMethod]
    public void TwoIndependentWindows_BothEvaluated()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, sum(amount) over(partition by region), sum(amount) over() from sales").ExecuteReader();
        var triples = new List<(string Region, decimal Sum, decimal Total)>();
        while (reader.Read())
            triples.Add((reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2)));
        HasCount(7, triples);
        foreach (var (region, partSum, grand) in triples)
        {
            AreEqual(region == "east" ? 450.00m : 800.00m, partSum);
            AreEqual(1250.00m, grand);
        }
    }

    [TestMethod]
    public void RowNumberAndAggregateWindow_CoexistInSameSelect()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (g int, v int);
            insert t values (1,10), (1,20), (2,30)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select g, v, row_number() over(partition by g order by v) rn, sum(v) over(partition by g) sm from t order by g, v").ExecuteReader();
        var rows = new List<(int G, int V, long Rn, int Sm)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt32(3)));
        HasCount(3, rows);
        AreEqual((1, 10, 1L, 30), rows[0]);
        AreEqual((1, 20, 2L, 30), rows[1]);
        AreEqual((2, 30, 1L, 30), rows[2]);
    }

    [TestMethod]
    public void EmptyResult_ReturnsZeroRows()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, sum(amount) over(partition by region) from sales where 1 = 0").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void OrderBy_ByWindowOrdinal_Works()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, sum(amount) over(partition by region) from sales order by 2 desc, region").ExecuteReader();
        var firstSums = new List<decimal>();
        while (reader.Read())
            firstSums.Add(reader.GetDecimal(1));
        // First three: west (sum 800). Last four: east (sum 450).
        AreEqual(800.00m, firstSums[0]);
        AreEqual(800.00m, firstSums[2]);
        AreEqual(450.00m, firstSums[3]);
        AreEqual(450.00m, firstSums[6]);
    }

    [TestMethod]
    public void Sum_BigInt_StaysBigInt()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table b (g int, v bigint);
            insert b values (1, 1000000000000), (1, 1000000000000)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select sum(v) over() from b").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(2000000000000L, reader.GetInt64(0));
    }

    [TestMethod]
    [DataRow("select sum(distinct amount) over(partition by region) from sales", 10759)]
    [DataRow("select count(distinct salesperson) over(partition by region) from sales", 10759)]
    [DataRow("select string_agg(salesperson, ',') over(partition by region) from sales", 4113)]
    [DataRow("select region from sales where sum(amount) over(partition by region) > 100", 4108)]
    [DataRow("select region from sales group by region having sum(amount) over(partition by region) > 100", 4108)]
    [DataRow("select region from sales group by sum(amount) over(partition by region)", 4108)]
    // Frame without ORDER BY → Msg 10756 (probe-confirmed). Applies to both
    // ROWS and RANGE.
    [DataRow("select sum(amount) over(partition by region rows between unbounded preceding and current row) from sales", 10756)]
    [DataRow("select sum(amount) over(partition by region range between unbounded preceding and current row) from sales", 10756)]
    public void WindowParserRejections(string sql, int errorNumber)
    {
        using var connection = SeededSales();
        var ex = Throws<DbException>(() => _ = connection.CreateCommand(sql).ExecuteScalar());
        AreEqual(errorNumber.ToString(), ex.Data["HelpLink.EvtID"]);
    }

    /// <summary>
    /// A window in a grouped query runs over the groups, so its operand has to
    /// satisfy GROUP BY containment like any other group-level expression:
    /// bare <c>amount</c> is Msg 8120 even though the identical text is legal
    /// in an ungrouped window query (probe-confirmed against SQL Server 2025).
    /// The nested form that <em>is</em> legal — <c>sum(sum(amount)) over()</c>
    /// — is covered by <see cref="AggregateOverAggregate_ProjectsGrandTotal"/>.
    /// </summary>
    [TestMethod]
    public void WindowOperandMustSatisfyGroupByContainment()
    {
        using var connection = SeededSales();
        var ex = Throws<DbException>(() => _ = connection
            .CreateCommand("select region, sum(amount), sum(amount) over() from sales group by region")
            .ExecuteScalar());
        AreEqual("8120", ex.Data["HelpLink.EvtID"]);
    }

    /// <summary>
    /// Same rule through a PARTITION BY key rather than an operand.
    /// </summary>
    [TestMethod]
    public void WindowPartitionKeyMustSatisfyGroupByContainment()
    {
        using var connection = SeededSales();
        var ex = Throws<DbException>(() => _ = connection
            .CreateCommand("select region, sum(amount), row_number() over(partition by amount order by region) from sales group by region")
            .ExecuteScalar());
        AreEqual("8120", ex.Data["HelpLink.EvtID"]);
    }

    /// <summary>
    /// <c>sum(sum(x)) over()</c> — the aggregate-over-aggregate shape reporting
    /// SQL uses for "group subtotal against the overall total". The inner
    /// aggregate is the group's value, the outer window spans every group, so
    /// the grand total repeats on each row (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void AggregateOverAggregate_ProjectsGrandTotal()
    {
        using var connection = SeededSales();
        using var reader = connection
            .CreateCommand("select region, sum(amount) as total, sum(sum(amount)) over() as grand from sales group by region order by region")
            .ExecuteReader();

        var grandTotals = new List<decimal>();
        var subtotals = new List<decimal>();
        while (reader.Read())
        {
            subtotals.Add(Convert.ToDecimal(reader.GetValue(1)));
            grandTotals.Add(Convert.ToDecimal(reader.GetValue(2)));
        }

        IsNotEmpty(grandTotals);
        var expectedGrand = subtotals.Sum();
        foreach (var grand in grandTotals)
            AreEqual(expectedGrand, grand);
    }

    /// <summary>
    /// The bare (non-window) nesting stays Msg 130, and so does a second level
    /// of nesting under an OVER — real accepts exactly one (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("select region, sum(sum(amount)) from sales group by region")]
    [DataRow("select region, sum(sum(sum(amount))) over() from sales group by region")]
    public void NestedAggregateWithoutOverIsRejected(string sql)
    {
        using var connection = SeededSales();
        var ex = Throws<DbException>(() => _ = connection.CreateCommand(sql).ExecuteScalar());
        AreEqual("130", ex.Data["HelpLink.EvtID"]);
    }

    /// <summary>
    /// The window sees only groups that survived HAVING (probe-confirmed): the
    /// count-over-all-groups reflects the filtered set, not every group.
    /// </summary>
    [TestMethod]
    public void WindowOverGroups_SeesPostHavingRowsOnly()
    {
        using var connection = SeededSales();
        using var command = connection.CreateCommand(
            "select region, count(*) over() as visible from sales group by region having sum(amount) > 0 order by region");
        using var reader = command.ExecuteReader();

        var visible = new List<int>();
        while (reader.Read())
            visible.Add(Convert.ToInt32(reader.GetValue(1)));

        IsNotEmpty(visible);
        foreach (var count in visible)
            AreEqual(visible.Count, count);
    }

    /// <summary>
    /// Over the multi-stream grouping shapes the window spans every grouping
    /// set's groups as a single row set — <c>ROLLUP(region)</c> emits two
    /// region rows plus the grand total, so <c>count(*) over()</c> reads 3.
    /// The semantics matrix lives in <c>GroupingSetTests</c>.
    /// </summary>
    [TestMethod]
    [DataRow("select region, sum(amount), count(*) over() from sales group by rollup(region)")]
    [DataRow("select region, sum(amount), count(*) over() from sales group by cube(region)")]
    public void WindowWithMultipleGroupingSets_CountsEverySetsGroups(string sql)
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        var counts = new List<int>();
        while (reader.Read())
            counts.Add(Convert.ToInt32(reader.GetValue(2)));
        HasCount(3, counts);
        foreach (var count in counts)
            AreEqual(3, count);
    }
}
