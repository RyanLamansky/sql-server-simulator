using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the GROUP BY extension grammar — <c>ROLLUP</c>, <c>CUBE</c>,
/// <c>GROUPING SETS</c> — plus the <c>GROUPING()</c> / <c>GROUPING_ID()</c>
/// scalars that distinguish subtotal/total-row NULLs from data NULLs.
/// Probe-confirmed against SQL Server 2025 (2026-05-13).
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

    [TestMethod]
    public void GroupingOfNonReferenceExpression_RaisesMsg8161()
    {
        // Real SQL Server returns 0 for GROUPING(a+1) when GROUP BY a+1
        // matches exactly. The simulator doesn't do structural equality on
        // GROUP BY expressions yet, so non-Reference args always raise
        // Msg 8161 — the right Msg, the wrong row count. Documented as a
        // known divergence.
        using var conn = SeededSales();
        using var cmd = conn.CreateCommand(
            "select grouping(amount + 1) from sales group by amount + 1");
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
}
