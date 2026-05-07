namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the set-operation shapes EF Core's SqlServer
/// provider emits — LINQ <c>Union</c>/<c>Concat</c>/<c>Intersect</c>/
/// <c>Except</c> against another query translate to <c>UNION</c>/
/// <c>UNION ALL</c>/<c>INTERSECT</c>/<c>EXCEPT</c>. Validates that the
/// simulator's set-op pipeline handles EF Core's concrete emit shapes
/// end-to-end.
/// </summary>
[TestClass]
public class EFCoreSetOperations
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
        context.CustomerOrders.AddRange(
            new CustomerOrder { CustomerId = 1, Amount = 10m },
            new CustomerOrder { CustomerId = 2, Amount = 30m });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Concat_EmitsUnionAll()
    {
        // LINQ Concat preserves duplicates → UNION ALL.
        using var context = SeededContext();
        var ids = context.Customers.Select(c => c.Id)
            .Concat(context.Customers.Select(c => c.Id))
            .OrderBy(x => x)
            .ToArray();
        // Each id appears twice.
        CollectionAssert.AreEqual(new[] { 1, 1, 2, 2, 3, 3 }, ids);
    }

    [TestMethod]
    public void Union_EmitsUnionWithDedup()
    {
        // LINQ Union dedupes → UNION.
        using var context = SeededContext();
        var ids = context.Customers.Select(c => c.Id)
            .Union(context.Customers.Select(c => c.Id))
            .OrderBy(x => x)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
    }

    [TestMethod]
    public void Intersect_EmitsIntersect()
    {
        // Customers ids 1,2,3 INTERSECT Customer ids of those with orders (1,2) → {1,2}.
        using var context = SeededContext();
        var ids = context.Customers.Select(c => c.Id)
            .Intersect(context.CustomerOrders.Select(o => o.CustomerId))
            .OrderBy(x => x)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void Except_EmitsExcept()
    {
        // Customer ids minus customer ids with orders → {3}.
        using var context = SeededContext();
        var ids = context.Customers.Select(c => c.Id)
            .Except(context.CustomerOrders.Select(o => o.CustomerId))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 3 }, ids);
    }
}
