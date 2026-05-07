namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the LINQ projection / WHERE shapes EF Core's
/// SqlServer provider emits as <c>CASE</c> expressions: ternary
/// projections, <c>Any()</c> in projection (wraps EXISTS in
/// <c>CASE WHEN ... THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END</c>), and
/// boolean conditional projections.
/// </summary>
[TestClass]
public class EFCoreCase
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
    public void Projection_Ternary_EmitsCase()
    {
        // EF Core translates `c.Id > 1 ? "big" : "small"` to
        // `SELECT CASE WHEN [c].[Id] > 1 THEN N'big' ELSE N'small' END`.
        using var context = SeededContext();
        var labels = context.Customers
            .OrderBy(c => c.Id)
            .Select(c => c.Id > 1 ? "big" : "small")
            .ToArray();
        CollectionAssert.AreEqual(new[] { "small", "big", "big" }, labels);
    }

    [TestMethod]
    public void Projection_AnyCorrelated_EmitsCaseWithExists()
    {
        // EF Core translates `Any` in a projection slot to
        // `SELECT CASE WHEN EXISTS (...) THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END`.
        // Customers with at least one order: 1 and 2.
        using var context = SeededContext();
        var rows = context.Customers
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                HasOrder = context.CustomerOrders.Any(o => o.CustomerId == c.Id)
            })
            .ToArray();
        Assert.HasCount(3, rows);
        Assert.IsTrue(rows[0].HasOrder);
        Assert.IsTrue(rows[1].HasOrder);
        Assert.IsFalse(rows[2].HasOrder);
    }
}
