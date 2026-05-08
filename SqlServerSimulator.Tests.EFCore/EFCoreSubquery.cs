namespace SqlServerSimulator;

/// <summary>
/// End-to-end regression tests for EF Core's subquery emission patterns:
/// LINQ <c>Any</c> against another DbSet inside a WHERE predicate
/// translates to <c>EXISTS (SELECT 1 ...)</c>; LINQ <c>Contains</c> over a
/// subquery projection translates to <c>IN (SELECT ...)</c>. Validates the
/// simulator's correlated-subquery support against the SqlServer provider's
/// actual emit shapes.
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
        // Customer ids are 1, 2, 3 after the IDENTITY assignment.
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
        // EF Core translates `Any` against another DbSet to `WHERE EXISTS
        // (SELECT 1 FROM CustomerOrders o WHERE o.CustomerId = c.Id)`.
        // Customers 1 and 2 have orders; customer 3 doesn't.
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
        // The complement: customers without any order → only id 3.
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
        // EF Core translates `Contains` against a subquery to `IN (SELECT
        // ...)`. The subquery projects CustomerId from CustomerOrders;
        // customers with at least one order match.
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
        // Inner WHERE is also correlated: only customers with an order >= 25.
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
        // EF Core translates `Count` of a related set in projection to a
        // scalar subquery: `SELECT (SELECT COUNT(*) FROM CustomerOrders o
        // WHERE o.CustomerId = c.Id) FROM Customers c`. Customers 1, 2, 3
        // have order counts 2, 1, 0 respectively.
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
        // EF Core translates `o.Amount == context.CustomerOrders.Max(...)`
        // to `WHERE o.Amount = (SELECT MAX(o.Amount) FROM CustomerOrders)`.
        // Single highest-amount order: 30 (CustomerId=2).
        using var context = SeededContext();
        var customerIds = context.CustomerOrders
            .Where(o => o.Amount == context.CustomerOrders.Max(x => x.Amount))
            .Select(o => o.CustomerId)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 2 }, customerIds);
    }

    [TestMethod]
    public void Projection_DistinctCorrelatedCount_EmitsCorrelatedDerivedTable()
    {
        // EF Core 10 emits `(SELECT COUNT(*) FROM (SELECT DISTINCT col FROM t
        // WHERE t.k = outer.k) AS sub)` for `Distinct().Count()` over a
        // correlated subset — the inner derived table references the outer
        // scope via its WHERE. Before the always-defer derived-table fix
        // this raised "Invalid column name" because plain derived tables
        // didn't see outer scope.
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
        // c.Id=1 has orders [10, 20] → 2 distinct amounts.
        // c.Id=2 has order [30] → 1.
        // c.Id=3 has none → 0.
        Assert.AreEqual(2, rows[0].DistinctAmounts);
        Assert.AreEqual(1, rows[1].DistinctAmounts);
        Assert.AreEqual(0, rows[2].DistinctAmounts);
    }
}
