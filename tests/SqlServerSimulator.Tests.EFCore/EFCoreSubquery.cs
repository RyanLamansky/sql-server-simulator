namespace SqlServerSimulator;

/// <summary>
/// End-to-end regression tests for EF Core's subquery emission patterns:
/// <c>Any</c> against another DbSet → <c>EXISTS (SELECT 1 ...)</c>;
/// <c>Contains</c> over a subquery projection → <c>IN (SELECT ...)</c>;
/// aggregates / scalar comparisons against subqueries; correlated
/// derived tables.
/// </summary>
[TestClass]
public class EFCoreSubquery
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreateCustomersSimulation());
        context.Customers.AddRange(
            new Customer { Name = "alpha" },
            new Customer { Name = "beta" },
            new Customer { Name = "gamma" });
        _ = context.SaveChanges();
        // Customer ids are 1, 2, 3 after IDENTITY assignment.
        context.CustomerOrders.AddRange(
            new CustomerOrder { CustomerId = 1, Amount = 10m },
            new CustomerOrder { CustomerId = 1, Amount = 20m },
            new CustomerOrder { CustomerId = 2, Amount = 30m });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Where_AnyCorrelated_EmitsExistsSubquery()
    {
        using var context = SeededContext();
        var ids = context.Customers
            .Where(c => context.CustomerOrders.Any(o => o.CustomerId == c.Id))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void Where_NotAnyCorrelated_EmitsNotExistsSubquery()
    {
        using var context = SeededContext();
        var ids = context.Customers
            .Where(c => !context.CustomerOrders.Any(o => o.CustomerId == c.Id))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 3 }, ids);
    }

    [TestMethod]
    public void Where_ContainsSubquery_EmitsInSelect()
    {
        using var context = SeededContext();
        var ids = context.Customers
            .Where(c => context.CustomerOrders.Select(o => o.CustomerId).Contains(c.Id))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void Where_AnyWithAdditionalPredicate_FiltersInsideSubquery()
    {
        using var context = SeededContext();
        var ids = context.Customers
            .Where(c => context.CustomerOrders.Any(o => o.CustomerId == c.Id && o.Amount >= 25m))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 2 }, ids);
    }

    [TestMethod]
    public void Projection_CountCorrelated_EmitsScalarSubquery()
    {
        using var context = SeededContext();
        var rows = context.Customers
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                OrderCount = context.CustomerOrders.Count(o => o.CustomerId == c.Id)
            })
            .ToArray();
        Assert.HasCount(3, rows);
        Assert.AreEqual(2, rows[0].OrderCount);
        Assert.AreEqual(1, rows[1].OrderCount);
        Assert.AreEqual(0, rows[2].OrderCount);
    }

    [TestMethod]
    public void Where_ScalarComparisonAgainstSubquery_FiltersByMaxAmount()
    {
        using var context = SeededContext();
        var customerIds = context.CustomerOrders
            .Where(o => o.Amount == context.CustomerOrders.Max(x => x.Amount))
            .Select(o => o.CustomerId)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 2 }, customerIds);
    }

    [TestMethod]
    public void Projection_PercentOfTotal_DecimalDivisionScalePreserved()
    {
        // Regression: chained multiply+divide produces d(38,24) at runtime; static schema must agree.
        using var context = SeededContext();
        var rows = context.CustomerOrders
            .OrderBy(o => o.Id)
            .Select(o => new
            {
                o.Id,
                Pct = o.Amount * 100m / context.CustomerOrders.Where(x => x.CustomerId == o.CustomerId).Sum(x => x.Amount),
            })
            .ToArray();
        Assert.HasCount(3, rows);
        // Customer 1: 10+20=30. Pct(10)=33.33; Pct(20)=66.66. Customer 2: 30 → 100%.
        Assert.AreEqual(decimal.Round(10m * 100m / 30m, 6), decimal.Round(rows[0].Pct, 6));
        Assert.AreEqual(decimal.Round(20m * 100m / 30m, 6), decimal.Round(rows[1].Pct, 6));
        Assert.AreEqual(100m, rows[2].Pct);
    }

    [TestMethod]
    public void Projection_DistinctCorrelatedCount_EmitsCorrelatedDerivedTable()
    {
        // Regression: pre-fix, correlated derived tables raised "Invalid column name" because
        // plain derived tables didn't see outer scope. Always-defer fix routes the inner plan
        // through outerResolver per row.
        using var context = SeededContext();
        var rows = context.Customers
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                DistinctAmounts = context.CustomerOrders
                    .Where(o => o.CustomerId == c.Id)
                    .Select(o => o.Amount)
                    .Distinct()
                    .Count(),
            })
            .ToArray();
        Assert.HasCount(3, rows);
        // c.Id=1: amounts [10, 20] → 2 distinct. c.Id=2: [30] → 1. c.Id=3: none → 0.
        Assert.AreEqual(2, rows[0].DistinctAmounts);
        Assert.AreEqual(1, rows[1].DistinctAmounts);
        Assert.AreEqual(0, rows[2].DistinctAmounts);
    }
}
