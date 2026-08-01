using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the GROUP BY extension grammar — <c>ROLLUP</c>, <c>CUBE</c>,
/// <c>GROUPING SETS</c> — plus the <c>GROUPING()</c> / <c>GROUPING_ID()</c>
/// scalars that distinguish subtotal/total-row NULLs from data NULLs, and the
/// window functions that span every set's groups as one row set.
/// Probe-confirmed against SQL Server 2025 (2026-05-13, windows 2026-07-31).
/// </summary>
[TestClass]
public sealed class GroupingSetTests
{
    private static DbConnection SeededSales()
    {
        var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table sales (region varchar(10), product varchar(10), amount int);
            insert sales values
                ('east', 'widget', 100), ('east', 'widget', 150),
                ('east', 'gadget', 50), ('east', null, 25),
                ('west', 'widget', 200), ('west', 'gadget', 300)
            """).ExecuteNonQuery();
        return conn;
    }

    private static List<(string? Region, string? Product, int Total)> ReadRegionProductTotal(DbConnection conn, string sql)
    {
        using var reader = conn.CreateCommand(sql).ExecuteReader();
        var rows = new List<(string?, string?, int)>();
        while (reader.Read())
        {
            var region = reader.IsDBNull(0) ? null : reader.GetString(0);
            var product = reader.IsDBNull(1) ? null : reader.GetString(1);
            var total = reader.GetInt32(2);
            rows.Add((region, product, total));
        }
        return rows;
    }

    [TestMethod]
    public void Rollup_TwoColumns_NPlus1Sets()
    {
        // ROLLUP(region, product) generates sets: (region, product), (region), ().
        // Per-region subtotals + grand total in addition to the leaf rows.
        using var conn = SeededSales();
        var rows = ReadRegionProductTotal(conn,
            "select region, product, sum(amount) from sales group by rollup(region, product) order by region, product");
        // Order: region NULLS FIRST (varchar NULLs sort first under default).
        Assert.HasCount(8, rows);
        CollectionAssert.Contains(rows, ((string?)null, (string?)null, 825));   // Grand total
        CollectionAssert.Contains(rows, ((string?)"east", (string?)null, 25));  // east + null product (real data)
        CollectionAssert.Contains(rows, ((string?)"east", (string?)null, 325)); // east subtotal
        CollectionAssert.Contains(rows, ((string?)"east", (string?)"gadget", 50));
        CollectionAssert.Contains(rows, ((string?)"east", (string?)"widget", 250));
        CollectionAssert.Contains(rows, ((string?)"west", (string?)null, 500)); // west subtotal
        CollectionAssert.Contains(rows, ((string?)"west", (string?)"gadget", 300));
        CollectionAssert.Contains(rows, ((string?)"west", (string?)"widget", 200));
    }

    [TestMethod]
    public void Cube_TwoColumns_AllSubsets()
    {
        // CUBE(region, product) generates 2^2 = 4 sets: (region, product),
        // (region), (product), (). Total rows = distinct (r, p) + distinct r
        // + distinct p + 1 grand total.
        using var conn = SeededSales();
        var rows = ReadRegionProductTotal(conn,
            "select region, product, sum(amount) from sales group by cube(region, product) order by region, product");
        Assert.HasCount(11, rows);
        // Includes per-product subtotals across all regions:
        CollectionAssert.Contains(rows, ((string?)null, (string?)"gadget", 350));
        CollectionAssert.Contains(rows, ((string?)null, (string?)"widget", 450));
        // The null-product across all regions (one real row in 'east'):
        CollectionAssert.Contains(rows, ((string?)null, (string?)null, 25));
        // Plus the grand total:
        CollectionAssert.Contains(rows, ((string?)null, (string?)null, 825));
    }

    [TestMethod]
    public void GroupingSets_ExplicitList()
    {
        // GROUPING SETS((region, product), (region), ()) should match the
        // ROLLUP shape (which is exactly this set list).
        using var conn = SeededSales();
        var rows = ReadRegionProductTotal(conn,
            "select region, product, sum(amount) from sales group by grouping sets((region, product), (region), ()) order by region, product");
        Assert.HasCount(8, rows);
    }

    [TestMethod]
    public void GroupingSetsEmpty_SingleGrandTotalRow()
    {
        // GROUPING SETS(()) is the grand-total-only form.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand(
            "select sum(amount) from sales group by grouping sets(())").ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(825, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Grouping_DistinguishesSubtotalFromRealNull()
    {
        // Order by grouping(product), region — subtotals (grouping=1) come
        // after detail rows (grouping=0) for the product column.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select region, product, grouping(region) as gr, grouping(product) as gp, sum(amount) as total
            from sales group by rollup(region, product)
            order by 3, 4, 1, 2
            """).ExecuteReader();
        var rows = new List<(string? Region, string? Product, byte GR, byte GP, int Total)>();
        while (reader.Read())
        {
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetByte(2),
                reader.GetByte(3),
                reader.GetInt32(4)));
        }
        Assert.HasCount(8, rows);
        // First five rows are detail rows (gr=0, gp=0). The (east, null, 25)
        // is a real-NULL data row, NOT a subtotal — its grouping() flags are
        // both 0 (probe-confirmed: GROUPING distinguishes them).
        Assert.HasCount(5, rows.FindAll(r => r.GR == 0 && r.GP == 0));
        // Per-region subtotals: gp=1.
        Assert.HasCount(2, rows.FindAll(r => r.GR == 0 && r.GP == 1));
        // Grand total: gr=1, gp=1.
        Assert.HasCount(1, rows.FindAll(r => r.GR == 1 && r.GP == 1));
    }

    [TestMethod]
    public void GroupingId_BitmapLeftmostIsMostSignificantBit()
    {
        // GROUPING_ID(region, product): region grouped → bit 1; product
        // grouped → bit 0. So bitmap values: detail rows = 0, per-region
        // (product grouped) = 1, per-product across regions = 2, grand
        // total = 3.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select grouping_id(region, product) as gid, sum(amount) as total
            from sales group by cube(region, product)
            order by 1
            """).ExecuteReader();
        var bitmaps = new List<int>();
        while (reader.Read())
            bitmaps.Add(reader.GetInt32(0));
        Assert.HasCount(11, bitmaps);
        // Probe-confirmed row counts: 5 detail rows (only 5 distinct
        // (region, product) combos exist in the data — west has no null-
        // product row), 2 per-region subtotals (east, west), 3 per-product
        // values across regions (null/gadget/widget), 1 grand total.
        Assert.HasCount(5, bitmaps.FindAll(b => b == 0));
        Assert.HasCount(2, bitmaps.FindAll(b => b == 1));
        Assert.HasCount(3, bitmaps.FindAll(b => b == 2));
        Assert.HasCount(1, bitmaps.FindAll(b => b == 3));
    }

    [TestMethod]
    public void MixedGroupBy_RegularColumnPlusRollup_CartesianProduct()
    {
        // `GROUP BY region, ROLLUP(product)` — region is always grouped,
        // ROLLUP(product) contributes (product) and (). So sets:
        // (region, product), (region). 5 detail + 2 per-region = 7 rows.
        using var conn = SeededSales();
        var rows = ReadRegionProductTotal(conn,
            "select region, product, sum(amount) from sales group by region, rollup(product) order by region, product");
        Assert.HasCount(7, rows);
        CollectionAssert.Contains(rows, ((string?)"east", (string?)null, 25));   // real data row (east, null product)
        CollectionAssert.Contains(rows, ((string?)"east", (string?)null, 325));  // east subtotal across products
        CollectionAssert.Contains(rows, ((string?)"west", (string?)null, 500));  // west subtotal
    }

    [TestMethod]
    public void HavingWithGroupingFilter()
    {
        // GROUPING(region) = 0 filters to detail (non-grand-total) rows.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select region, sum(amount) as total
            from sales group by rollup(region)
            having grouping(region) = 0
            order by region
            """).ExecuteReader();
        var rows = new List<(string Region, int Total)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        Assert.HasCount(2, rows);
        AreEqual(("east", 325), rows[0]);
        AreEqual(("west", 500), rows[1]);
    }

    [TestMethod]
    public void SingleColumnRollup_TwoGroupingSets()
    {
        // ROLLUP(region) → (region), (). Per-region totals + grand total.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand(
            "select region, sum(amount) from sales group by rollup(region) order by region").ExecuteReader();
        var rows = new List<(string? Region, int Total)>();
        while (reader.Read())
            rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1)));
        Assert.HasCount(3, rows);
        CollectionAssert.Contains(rows, ((string?)null, 825));
        CollectionAssert.Contains(rows, ((string?)"east", 325));
        CollectionAssert.Contains(rows, ((string?)"west", 500));
    }

    [TestMethod]
    public void GroupingOutsideGroupBy_RaisesMsg8161()
    {
        // Probe-confirmed: GROUPING() outside any GROUP BY context raises
        // Msg 8161 (same as when arg isn't in GROUP BY).
        using var conn = SeededSales();
        using var cmd = conn.CreateCommand("select grouping(region) from sales");
        var ex = Throws<DbException>(() => cmd.ExecuteReader().Read());
        AreEqual("8161", ex.Data["HelpLink.EvtID"]);
        AreEqual(
            "Argument 1 of the GROUPING function does not match any of the expressions in the GROUP BY clause.",
            ex.Message);
    }

    [TestMethod]
    public void GroupingOfNonGroupedColumn_RaisesMsg8161()
    {
        // Probe-confirmed: GROUPING(product) when GROUP BY only lists region
        // raises Msg 8161 — the arg must match a GROUP BY expression.
        using var conn = SeededSales();
        using var cmd = conn.CreateCommand(
            "select region, grouping(product) from sales group by region");
        var ex = Throws<DbException>(() => cmd.ExecuteReader().Read());
        AreEqual("8161", ex.Data["HelpLink.EvtID"]);
    }

    private static DbConnection SeededExpr()
    {
        var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table t (a int, b int, v int);
            insert t values (1, 10, 100), (1, 20, 200), (2, 10, 50)
            """).ExecuteNonQuery();
        return conn;
    }

    [TestMethod]
    public void GroupingOfExpression_MatchesGroupByExpression()
    {
        // Probe-confirmed 2026-07-10: GROUPING(a+1) with GROUP BY ROLLUP(a+1)
        // returns 0 for the detail rows and 1 for the rolled-up grand total.
        using var conn = SeededExpr();
        using var reader = conn.CreateCommand(
            "select sum(v) s, grouping(a + 1) g from t group by rollup(a + 1) order by g, s").ExecuteReader();
        var rows = new List<(int Total, int Grouping)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetByte(1)));
        // Two detail rows (a+1 = 2 → 300, a+1 = 3 → 50), each grouping 0;
        // plus the grand total (350) at grouping 1.
        Assert.HasCount(3, rows);
        AreEqual((50, 0), rows[0]);
        AreEqual((300, 0), rows[1]);
        AreEqual((350, 1), rows[2]);
    }

    [TestMethod]
    public void GroupingOfExpression_RedundantParensStillMatch()
    {
        // Probe-confirmed 2026-07-10: GROUPING((a+1)) — extra parentheses —
        // still matches GROUP BY a+1 (parens are normalized away).
        using var conn = SeededExpr();
        using var reader = conn.CreateCommand(
            "select grouping((a + 1)) g from t group by rollup(a + 1) order by g").ExecuteReader();
        var groupings = new List<byte>();
        while (reader.Read())
            groupings.Add(reader.GetByte(0));
        // Two detail rows at 0, one grand total at 1.
        Assert.HasCount(3, groupings);
        AreEqual((byte)0, groupings[0]);
        AreEqual((byte)0, groupings[1]);
        AreEqual((byte)1, groupings[2]);
    }

    [TestMethod]
    public void GroupingIdOfExpression_MatchesGroupingSets()
    {
        // Probe-confirmed 2026-07-10: GROUPING_ID(a+1, b) with GROUPING SETS
        // ((a+1,b),(a+1),()) yields 0 (both present), 1 (b grouped away), and
        // 3 (both grouped away).
        using var conn = SeededExpr();
        using var reader = conn.CreateCommand("""
            select grouping_id(a + 1, b) gid, sum(v) s from t
            group by grouping sets ((a + 1, b), (a + 1), ())
            order by gid
            """).ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        // Three detail (gid 0), two subtotal (gid 1), one grand total (gid 3).
        CollectionAssert.Contains(ids, 0);
        CollectionAssert.Contains(ids, 1);
        CollectionAssert.Contains(ids, 3);
    }

    [TestMethod]
    public void GroupingOfMismatchedExpression_RaisesMsg8161()
    {
        // Probe-confirmed 2026-07-10: GROUPING(1+a) is structurally distinct
        // from GROUP BY a+1 (operand order differs, no commutative
        // normalization), so it raises Msg 8161.
        using var conn = SeededExpr();
        using var cmd = conn.CreateCommand(
            "select grouping(1 + a) g from t group by rollup(a + 1)");
        var ex = Throws<DbException>(() => cmd.ExecuteReader().Read());
        AreEqual("8161", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void GroupingOfDifferentConstantExpression_RaisesMsg8161()
    {
        // Probe-confirmed 2026-07-10: GROUPING(a+2) against GROUP BY a+1 is a
        // value mismatch → Msg 8161.
        using var conn = SeededExpr();
        using var cmd = conn.CreateCommand(
            "select grouping(a + 2) g from t group by rollup(a + 1)");
        var ex = Throws<DbException>(() => cmd.ExecuteReader().Read());
        AreEqual("8161", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void SimpleGroupBy_StillWorksAfterRefactor()
    {
        // Regression: the FromClause.GroupBy → GroupingSets refactor must
        // preserve plain GROUP BY semantics. Smoke test.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand(
            "select region, sum(amount) from sales group by region order by region").ExecuteReader();
        var rows = new List<(string Region, int Total)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        Assert.HasCount(2, rows);
        AreEqual(("east", 325), rows[0]);
        AreEqual(("west", 500), rows[1]);
    }

    [TestMethod]
    public void GroupByEmptyParens_ProducesSingleGrandTotalRow()
    {
        // `GROUP BY ()` is the empty grouping set — one group over all rows,
        // the bare-parenthesis equivalent of `GROUPING SETS(())`.
        using var conn = SeededSales();
        AreEqual(825, conn.CreateCommand("select sum(amount) from sales group by ()").ExecuteScalar());
    }

    [TestMethod]
    public void GroupByEmptyParens_CountsAllRows()
    {
        using var conn = SeededSales();
        AreEqual(6, conn.CreateCommand("select count(*) from sales group by ()").ExecuteScalar());
    }

    [TestMethod]
    public void GroupByParenthesizedColumn_StillGroupsByThatColumn()
    {
        // `GROUP BY (region)` is a parenthesized grouping key, not the empty
        // set — must still fold rows per region.
        using var conn = SeededSales();
        AreEqual(2, conn.CreateCommand("select count(*) from (select region from sales group by (region)) g").ExecuteScalar());
    }

    [TestMethod]
    public void Rollup_Window_SpansEveryGroupingSet()
    {
        // Probe-confirmed 2026-07-31: a window in a ROLLUP-grouped SELECT runs
        // over the *complete* grouped result, subtotal and grand-total rows
        // included — `sum(sum(amount)) over ()` totals 325 + 500 + 825 = 1650
        // and `count(*) over ()` counts all three output rows.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select region, sum(amount) s, sum(sum(amount)) over () w, count(*) over () c
            from sales group by rollup(region) order by region
            """).ExecuteReader();
        var rows = new List<(int Total, int Window, int Count)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        Assert.HasCount(3, rows);
        AreEqual(825 + 325 + 500, rows.Sum(r => r.Total));
        foreach (var (_, window, count) in rows)
        {
            AreEqual(1650, window);
            AreEqual(3, count);
        }
    }

    [TestMethod]
    public void Rollup_PartitionByGroupedColumn_FoldsSubtotalNullsWithDataNulls()
    {
        // Probe-confirmed 2026-07-31: a subtotal row's grouped-away key reads
        // as NULL and PARTITION BY can't tell it from a data NULL, so the
        // east/NULL leaf (25), both region subtotals (325, 500) and the grand
        // total (825) share one 4-row partition summing 1675.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select product,
                   sum(sum(amount)) over (partition by product) wp,
                   count(*) over (partition by product) cp
            from sales group by rollup(region, product)
            """).ExecuteReader();
        var rows = new List<(string? Product, int Window, int Count)>();
        while (reader.Read())
            rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        Assert.HasCount(8, rows);
        foreach (var (product, window, count) in rows)
        {
            AreEqual(product switch { null => 1675, "gadget" => 350, _ => 450 }, window);
            AreEqual(product is null ? 4 : 2, count);
        }
    }

    [TestMethod]
    public void Rollup_PartitionByGroupingFlag_SeparatesSubtotalsFromDataNulls()
    {
        // GROUPING() is legal inside a window's PARTITION BY and is the only
        // way to keep subtotal rows out of the data-NULL partition: adding it
        // splits the 1675 partition above into the east/NULL leaf (25) and the
        // three grouped-away rows (325 + 500 + 825 = 1650).
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select product, grouping(product) gp,
                   sum(sum(amount)) over (partition by product, grouping(product)) w
            from sales group by rollup(region, product)
            """).ExecuteReader();
        var rows = new List<(string? Product, byte Grouping, int Window)>();
        while (reader.Read())
            rows.Add((reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetByte(1), reader.GetInt32(2)));
        Assert.HasCount(8, rows);
        foreach (var (product, grouping, window) in rows)
        {
            AreEqual(
                grouping == 1 ? 1650 : product switch { null => 25, "gadget" => 350, _ => 450 },
                window);
        }
    }

    [TestMethod]
    public void Cube_RankTiesAcrossGroupingSetBoundaries()
    {
        // Ranking treats the concatenated stream as one row set: the east/NULL
        // leaf (25) and the CUBE's all-region NULL-product subtotal (also 25)
        // come from different grouping sets yet tie — ROW_NUMBER hands out 10
        // and 11 while RANK gives both 10 (probe-confirmed 2026-07-31).
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select sum(amount) s,
                   row_number() over (order by sum(amount) desc) rn,
                   rank() over (order by sum(amount) desc) rk
            from sales group by cube(region, product) order by rn
            """).ExecuteReader();
        var rows = new List<(int Total, long RowNumber, long Rank)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)));
        Assert.HasCount(11, rows);
        AreEqual((825, 1L, 1L), rows[0]);
        AreEqual((25, 10L, 10L), rows[9]);
        AreEqual((25, 11L, 10L), rows[10]);
    }

    [TestMethod]
    public void GroupingSets_LagReadsThePreviousGroupAcrossSets()
    {
        // LAG steps through the concatenated stream, so a row's predecessor
        // can belong to a different grouping set.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select sum(amount) s, lag(sum(amount)) over (order by sum(amount)) prev
            from sales group by grouping sets ((region), (product), ()) order by s
            """).ExecuteReader();
        var rows = new List<(int Total, int? Previous)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt32(1)));
        Assert.HasCount(6, rows);
        AreEqual((25, null), rows[0]);
        AreEqual((325, 25), rows[1]);
        AreEqual((350, 325), rows[2]);
        AreEqual((450, 350), rows[3]);
        AreEqual((500, 450), rows[4]);
        AreEqual((825, 500), rows[5]);
    }

    [TestMethod]
    public void Rollup_Window_SeesPostHavingGroupsOnly()
    {
        // HAVING runs before the window pass, and it filters across every
        // grouping set: east's 325 subtotal drops, leaving west (500) and the
        // grand total (825) — so the window counts 2 and totals 1325.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select sum(amount) s, count(*) over () c, sum(sum(amount)) over () w
            from sales group by rollup(region) having sum(amount) > 400 order by s
            """).ExecuteReader();
        var rows = new List<(int Total, int Count, int Window)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        Assert.HasCount(2, rows);
        AreEqual((500, 2, 1325), rows[0]);
        AreEqual((825, 2, 1325), rows[1]);
    }

    [TestMethod]
    public void Rollup_Window_FrameRunsOverTheConcatenatedStream()
    {
        // A running total over the ROLLUP result accumulates the grand-total
        // row alongside the leaves: region sorts NULLs first, so the frame
        // reads 825, then 1150, then 1650.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select sum(sum(amount)) over (order by region rows unbounded preceding) rt
            from sales group by rollup(region) order by region
            """).ExecuteReader();
        var running = new List<int>();
        while (reader.Read())
            running.Add(reader.GetInt32(0));
        Assert.HasCount(3, running);
        AreEqual(825, running[0]);
        AreEqual(1150, running[1]);
        AreEqual(1650, running[2]);
    }

    [TestMethod]
    [DataRow("select top 2 sum(amount) s, count(*) over () c from sales group by rollup(region) order by s desc")]
    [DataRow("select sum(amount) s, count(*) over () c from sales group by rollup(region) order by s desc offset 0 rows fetch next 2 rows only")]
    public void Rollup_RowLimitingAppliesAfterTheWindow(string sql)
    {
        // TOP / OFFSET-FETCH trim the already-windowed stream, so the count
        // stays 3 even though only two rows come back.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand(sql).ExecuteReader();
        var rows = new List<(int Total, int Count)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1)));
        Assert.HasCount(2, rows);
        AreEqual((825, 3), rows[0]);
        AreEqual((500, 3), rows[1]);
    }

    [TestMethod]
    public void Cube_DistinctDedupesAfterTheWindow()
    {
        // DISTINCT collapses the windowed projection, not the group stream:
        // the two grouped-away-vs-not partitions each total 825.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select distinct grouping(region) gr, sum(sum(amount)) over (partition by grouping(region)) w
            from sales group by cube(region) order by gr
            """).ExecuteReader();
        var rows = new List<(byte Grouping, int Window)>();
        while (reader.Read())
            rows.Add((reader.GetByte(0), reader.GetInt32(1)));
        Assert.HasCount(2, rows);
        AreEqual(((byte)0, 825), rows[0]);
        AreEqual(((byte)1, 825), rows[1]);
    }

    [TestMethod]
    public void GroupingSets_WindowInOrderBy()
    {
        // A window is legal in the grouped query's ORDER BY, spanning every
        // set the same way a select-list window does.
        using var conn = SeededSales();
        using var reader = conn.CreateCommand("""
            select sum(amount) s from sales group by grouping sets ((region), (product), ())
            order by row_number() over (order by sum(amount) desc)
            """).ExecuteReader();
        var totals = new List<int>();
        while (reader.Read())
            totals.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new List<int> { 825, 500, 450, 350, 325, 25 }, totals);
    }

    [TestMethod]
    // A bare column in a window operand / PARTITION BY carries the same
    // containment obligation as the select list (Msg 8120), a second nesting
    // level under OVER stays Msg 130, and a window in HAVING stays Msg 4108 —
    // identical to the plain-GROUP-BY path.
    [DataRow("select region, sum(amount), sum(amount) over () from sales group by rollup(region)", 8120)]
    [DataRow("select region, sum(amount), sum(sum(amount)) over (partition by product) from sales group by rollup(region)", 8120)]
    [DataRow("select region, sum(sum(sum(amount))) over () from sales group by cube(region)", 130)]
    [DataRow("select region, sum(amount) from sales group by rollup(region) having sum(sum(amount)) over () > 1", 4108)]
    public void WindowOverGroupingSets_BindingRejections(string sql, int errorNumber)
    {
        using var conn = SeededSales();
        var ex = Throws<DbException>(() => _ = conn.CreateCommand(sql).ExecuteScalar());
        AreEqual(errorNumber.ToString(), ex.Data["HelpLink.EvtID"]);
    }
}
