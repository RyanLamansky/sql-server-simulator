using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for derived tables in FROM that reference outer-scope columns —
/// SQL Server's "any FROM derived table can correlate" rule, not just the
/// CROSS APPLY / OUTER APPLY shape. The simulator threads the outer-type
/// resolver into the inner Parse and defers execution per Selection
/// instance so the inner plan re-runs with each call's outer resolver.
/// </summary>
[TestClass]
public sealed class CorrelatedDerivedTableTests
{
    private static DbConnection SeededSales()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table sales (region varchar(10), salesperson varchar(20), amount decimal(10,2))").ExecuteNonQuery();
        _ = connection.CreateCommand(
            "insert into sales values " +
            "('east', 'alice',   100), " +
            "('east', 'alice',   200), " +
            "('east', 'bob',     150), " +
            "('west', 'carol',   300), " +
            "('west', 'carol',   500), " +
            "('west', 'dan',      50)").ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void DistinctCorrelatedCount_TheEFShape_Works()
    {
        // The exact shape EF Core 10 emits for `db.X.Select(s => new {
        // DistinctCount = db.Y.Where(...).Distinct().Count() })`. Before
        // this fix, the simulator threw "Invalid column name 's.Region'"
        // because the inner derived table couldn't see the outer scope.
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select s.region, (select count(*) from (select distinct s0.salesperson from sales as s0 where s0.region = s.region) as s1) from sales as s").ExecuteReader();
        var counts = new Dictionary<string, int>();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        AreEqual(2, counts["east"]);  // alice, bob
        AreEqual(2, counts["west"]);  // carol, dan
    }

    [TestMethod]
    public void CorrelatedDerivedTableInWhereExists()
    {
        // `where exists (select 1 from (correlated derived) as sub where ...)`
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select distinct s.region from sales as s where exists (" +
            "  select 1 from (select distinct x.salesperson from sales as x where x.region = s.region) as sub" +
            "  where sub.salesperson = 'alice')").ExecuteReader();
        var regions = new List<string>();
        while (reader.Read())
            regions.Add(reader.GetString(0));
        // Only east has 'alice'; west doesn't.
        HasCount(1, regions);
        AreEqual("east", regions[0]);
    }

    [TestMethod]
    public void NonCorrelatedDerivedTable_StillWorks()
    {
        // Regression — an uncorrelated derived table (no outer reference)
        // should still produce its own row stream. Always-defer changes
        // the runtime path but the result must be identical.
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select region, total from (select region, sum(amount) as total from sales group by region) as g order by region").ExecuteReader();
        var pairs = new List<(string Region, decimal Total)>();
        while (reader.Read())
            pairs.Add((reader.GetString(0), reader.GetDecimal(1)));
        HasCount(2, pairs);
        AreEqual(("east", 450.00m), pairs[0]);
        AreEqual(("west", 850.00m), pairs[1]);
    }

    [TestMethod]
    public void NonCorrelatedDerivedTable_JoinedToHeapTable()
    {
        // Pre-existing JOIN-with-derived-table shape. After the always-defer
        // change the JoinDriver lateral branch handles the ON predicate
        // and INNER semantics for these too.
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select s.region, s.salesperson, t.total " +
            "from sales as s inner join (select region, sum(amount) as total from sales group by region) as t " +
            "  on s.region = t.region " +
            "order by s.region, s.salesperson, s.amount").ExecuteReader();
        var rows = new List<(string Region, string Person, decimal Total)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetDecimal(2)));
        HasCount(6, rows);
        // Each detail row carries its region's broadcast total.
        IsTrue(rows.Where(r => r.Region == "east").All(r => r.Total == 450.00m));
        IsTrue(rows.Where(r => r.Region == "west").All(r => r.Total == 850.00m));
    }

    [TestMethod]
    public void LeftJoin_DerivedTable_NullFillsNoMatch()
    {
        // LEFT JOIN to a derived table with an ON predicate that fails
        // for some outer rows must null-fill, matching real SQL Server.
        using var connection = SeededSales();
        _ = connection.CreateCommand(
            "create table regions (region varchar(10), label varchar(20))").ExecuteNonQuery();
        _ = connection.CreateCommand(
            "insert into regions values ('east', 'East'), ('north', 'North')").ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select r.region, r.label, t.total " +
            "from regions as r left join (select region, sum(amount) as total from sales group by region) as t " +
            "  on r.region = t.region " +
            "order by r.region").ExecuteReader();
        var rows = new List<(string Region, string Label, decimal? Total)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDecimal(2)));
        HasCount(2, rows);
        AreEqual("east", rows[0].Region);
        AreEqual(450.00m, rows[0].Total);
        // 'north' has no matching sales rows → null-filled total.
        AreEqual("north", rows[1].Region);
        IsNull(rows[1].Total);
    }

    [TestMethod]
    public void CorrelatedDerivedTable_AsScalarSubqueryFromSource()
    {
        // Scalar subquery whose own FROM is a correlated derived table —
        // the EF distinct-count shape with TOP rather than COUNT.
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select s.region, (select top 1 sub.salesperson from (select distinct x.salesperson from sales as x where x.region = s.region) as sub order by sub.salesperson) " +
            "from sales as s where s.salesperson = 'alice'").ExecuteReader();
        // Both alice rows are in 'east'; the inner returns the first
        // distinct salesperson alphabetically in 'east' = 'alice'.
        var rows = new List<(string Region, string FirstSp)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        HasCount(2, rows);
        IsTrue(rows.All(r => r.Region == "east" && r.FirstSp == "alice"));
    }
}
