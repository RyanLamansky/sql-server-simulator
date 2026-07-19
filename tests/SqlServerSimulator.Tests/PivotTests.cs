using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>PIVOT</c> / <c>UNPIVOT</c> table operators.
/// PIVOT desugars to grouped conditional aggregation (grouping key = every
/// inner column except the FOR column and the aggregate argument), each IN
/// value becoming <c>agg(CASE forCol WHEN value THEN argCol END)</c>; UNPIVOT
/// unfolds each row into one row per non-NULL IN column. Both attach as a
/// postfix to a FROM source and behave like a derived table downstream.
/// Behavior probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class PivotTests
{
    private static DbConnection SeededSales()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table sales (Region varchar(10), Yr int, Amount decimal(10,2), Note varchar(20))").ExecuteNonQuery();
        _ = connection.CreateCommand(
            "insert sales values " +
            "('East', 2020, 100.00, 'a'), ('East', 2020, 50.00, 'a'), " +
            "('East', 2021, 200.00, 'b'), ('West', 2020, 10.00, 'a'), " +
            "('West', 2021, 20.00, 'b'), ('West', 2022, 5.00, 'c')").ExecuteNonQuery();
        return connection;
    }

    // === PIVOT ===

    [TestMethod]
    public void Pivot_DerivedTable_GroupsByRemainingColumn()
    {
        // Inner projection drops Note, so the grouping key is Region alone:
        // one row per region, each year folded into its own column.
        using var connection = SeededSales();
        const string pivot =
            "(select Region, Yr, Amount from sales) src " +
            "pivot (sum(Amount) for Yr in ([2020],[2021],[2022])) as p";
        AreEqual(2, connection.CreateCommand($"select count(*) from {pivot}").ExecuteScalar());
        AreEqual(150.00m, connection.CreateCommand($"select [2020] from {pivot} where Region = 'East'").ExecuteScalar());
        AreEqual(200.00m, connection.CreateCommand($"select [2021] from {pivot} where Region = 'East'").ExecuteScalar());
        AreEqual(5.00m, connection.CreateCommand($"select [2022] from {pivot} where Region = 'West'").ExecuteScalar());
    }

    [TestMethod]
    public void Pivot_StrayColumn_SplitsGroups()
    {
        // SELECT * over the bare table keeps Note, so (Region, Note) is the
        // implicit grouping key — East splits into the 'a' and 'b' groups.
        using var connection = SeededSales();
        AreEqual(5, connection.CreateCommand(
            "select count(*) from " +
            "(select Region, Note, Yr, Amount from sales) src " +
            "pivot (sum(Amount) for Yr in ([2020],[2021],[2022])) as p").ExecuteScalar());
    }

    [TestMethod]
    public void Pivot_EmptyGroup_SumIsNullCountIsZero()
    {
        using var connection = SeededSales();
        // East has no 2022 row for SUM → NULL...
        AreEqual(DBNull.Value, connection.CreateCommand(
            "select [2022] from (select Region, Yr, Amount from sales) src " +
            "pivot (sum(Amount) for Yr in ([2022])) as p where Region = 'East'").ExecuteScalar());
        // ...but COUNT over an empty group is 0, not NULL.
        AreEqual(0, connection.CreateCommand(
            "select [2022] from (select Region, Yr from sales) src " +
            "pivot (count(Yr) for Yr in ([2022])) as p where Region = 'East'").ExecuteScalar());
    }

    [TestMethod]
    public void Pivot_ValueNotInSource_ColumnOfNulls()
    {
        using var connection = SeededSales();
        AreEqual(DBNull.Value, connection.CreateCommand(
            "select [2099] from (select Region, Yr, Amount from sales) src " +
            "pivot (sum(Amount) for Yr in ([2099])) as p where Region = 'East'").ExecuteScalar());
    }

    [TestMethod]
    public void Pivot_StringForColumn()
    {
        using var connection = SeededSales();
        AreEqual(150.00m, connection.CreateCommand(
            "select [East] from (select Yr, Region, Amount from sales) src " +
            "pivot (sum(Amount) for Region in ([East],[West])) as p where Yr = 2020").ExecuteScalar());
    }

    [TestMethod]
    public void Pivot_AvgAggregate_WidensScale()
    {
        // AVG(decimal(10,2)) → decimal(38,6): East's 2020 avg of 100 and 50.
        using var connection = SeededSales();
        AreEqual(75.000000m, connection.CreateCommand(
            "select [2020] from (select Region, Yr, Amount from sales) src " +
            "pivot (avg(Amount) for Yr in ([2020])) as p where Region = 'East'").ExecuteScalar());
    }

    [TestMethod]
    public void Pivot_WhereAndOrderByApplyToOutput()
    {
        using var connection = SeededSales();
        using var reader = connection.CreateCommand(
            "select Region from (select Region, Yr, Amount from sales) src " +
            "pivot (sum(Amount) for Yr in ([2020],[2021])) as p " +
            "where [2020] > 50 order by [2020] desc").ExecuteReader();
        var regions = new List<string>();
        while (reader.Read()) regions.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "East" }, regions);
    }

    [TestMethod]
    public void Pivot_ComputedForColumn_AdventureWorksShape()
    {
        // The shape of Sales.vSalesPersonSalesByFiscalYears: a derived table
        // projects YEAR(OrderDate) as the FOR column plus passthrough name
        // columns, pivoting SUM(SubTotal) across fiscal years.
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table orders (SalesPersonId int, FullName varchar(20), OrderDate date, SubTotal decimal(10,2))").ExecuteNonQuery();
        _ = connection.CreateCommand(
            "insert orders values " +
            "(1, 'Amy', '2002-03-01', 100.00), (1, 'Amy', '2002-09-01', 50.00), " +
            "(1, 'Amy', '2003-01-01', 200.00), (2, 'Bob', '2004-06-01', 7.00)").ExecuteNonQuery();
        const string pivot =
            "(select SalesPersonId, FullName, year(OrderDate) as FiscalYear, SubTotal from orders) soh " +
            "pivot (sum(SubTotal) for FiscalYear in ([2002],[2003],[2004])) as pvt";
        AreEqual(150.00m, connection.CreateCommand(
            $"select [2002] from {pivot} where SalesPersonId = 1").ExecuteScalar());
        AreEqual(7.00m, connection.CreateCommand(
            $"select [2004] from {pivot} where FullName = 'Bob'").ExecuteScalar());
        AreEqual(DBNull.Value, connection.CreateCommand(
            $"select [2003] from {pivot} where FullName = 'Bob'").ExecuteScalar());
        connection.Dispose();
    }

    // === PIVOT error paths ===

    [TestMethod]
    public void Pivot_CountStar_Rejected()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int, b int); " +
            "select * from (select a, b from t) s pivot (count(*) for b in ([1])) p", 102);

    [TestMethod]
    public void Pivot_TwoAggregates_Rejected()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int, b int, c int); " +
            "select * from (select a, b, c from t) s pivot (sum(c), count(c) for b in ([1])) p", 102);

    [TestMethod]
    public void Pivot_MissingAlias_Rejected()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int, b int, c int); " +
            "select * from (select a, b, c from t) s pivot (sum(c) for b in ([1]))", 102);

    [TestMethod]
    public void Pivot_UnknownForColumn_Msg207()
        => _ = new Simulation().AssertSqlError(
            "create table t (a int, b int, c int); " +
            "select * from (select a, b, c from t) s pivot (sum(c) for nope in ([1])) p", 207);

    [TestMethod]
    public void Pivot_DuplicateInValue_Msg8156()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (a int, b int, c int); " +
            "select * from (select a, b, c from t) s pivot (sum(c) for b in ([1],[1])) p", 8156);
        Contains("was specified multiple times for 'p'", ex.Message);
    }

    // === UNPIVOT ===

    private static DbConnection SeededQuarters()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand(
            "create table q (ProductId int, Q1 int, Q2 int, Q3 int, Q4 int)").ExecuteNonQuery();
        _ = connection.CreateCommand(
            "insert q values (1, 10, 20, null, 40), (2, null, null, null, null), (3, 5, 6, 7, 8)").ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void Unpivot_ExcludesNullsAndAllNullRows()
    {
        using var connection = SeededQuarters();
        using var reader = connection.CreateCommand(
            "select ProductId, Quarter, Sales from q " +
            "unpivot (Sales for Quarter in (Q1, Q2, Q3, Q4)) as u " +
            "order by ProductId, Quarter").ExecuteReader();
        var rows = new List<(int, string, int)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        // Product 2 is all-NULL (vanishes); Product 1 drops its Q3 NULL.
        CollectionAssert.AreEqual(
            new[]
            {
                (1, "Q1", 10), (1, "Q2", 20), (1, "Q4", 40),
                (3, "Q1", 5), (3, "Q2", 6), (3, "Q3", 7), (3, "Q4", 8),
            },
            rows);
    }

    [TestMethod]
    public void Unpivot_SelectStar_ValueThenNameColumn()
    {
        // SELECT * order: passthrough, then value column, then name column.
        using var connection = SeededQuarters();
        using var reader = connection.CreateCommand(
            "select * from q unpivot (Sales for Quarter in (Q1, Q2, Q3, Q4)) as u").ExecuteReader();
        AreEqual("ProductId", reader.GetName(0));
        AreEqual("Sales", reader.GetName(1));
        AreEqual("Quarter", reader.GetName(2));
    }

    [TestMethod]
    public void Unpivot_TypeConflict_Msg8167()
    {
        var ex = new Simulation().AssertSqlError(
            "create table m (Id int, A bigint, B int); insert m values (1, 100, 2); " +
            "select Id, Col, Val from m unpivot (Val for Col in (A, B)) as u", 8167);
        Contains("conflicts with the type of other columns", ex.Message);
    }

    [TestMethod]
    public void Unpivot_MissingAlias_Rejected()
        => _ = new Simulation().AssertSqlError(
            "create table q (ProductId int, Q1 int, Q2 int); " +
            "select * from q unpivot (Sales for Quarter in (Q1, Q2))", 102);

    [TestMethod]
    public void Unpivot_UnknownColumn_Msg207()
        => _ = new Simulation().AssertSqlError(
            "create table q (ProductId int, Q1 int, Q2 int); " +
            "select * from q unpivot (Sales for Quarter in (Q1, Nope)) as u", 207);
}
