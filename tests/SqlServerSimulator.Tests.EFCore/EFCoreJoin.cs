namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the JOIN shapes EF Core's SqlServer provider
/// emits — explicit LINQ <c>Join</c> (translates to <c>INNER JOIN</c>),
/// the navigation / projection patterns that translate to <c>LEFT JOIN</c>,
/// and the .NET 10 / EF Core 10 <c>LeftJoin</c> / <c>RightJoin</c> LINQ
/// operators that translate to <c>LEFT JOIN</c> / <c>RIGHT JOIN</c>.
/// Validates that the simulator's multi-source row pipeline matches EF
/// Core's expectations end-to-end. <c>FULL OUTER JOIN</c> isn't covered
/// here because .NET 10 LINQ doesn't expose a <c>FullJoin</c> operator
/// (the only way to reach it is raw SQL, which belongs in the parser
/// tests, not <c>*.Tests.EFCore</c>).
/// </summary>
[TestClass]
public class EFCoreJoin
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
        // alpha=1 has 2 orders, beta=2 has 1, gamma=3 has none.
        context.CustomerOrders.AddRange(
            new CustomerOrder { CustomerId = 1, Amount = 10m },
            new CustomerOrder { CustomerId = 1, Amount = 20m },
            new CustomerOrder { CustomerId = 2, Amount = 30m });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Join_ExplicitInnerJoin_PairsMatching()
    {
        // EF Core's `Join` LINQ method emits `INNER JOIN`.
        using var context = SeededContext();
        var pairs = context.Customers
            .Join(context.CustomerOrders,
                c => c.Id,
                o => o.CustomerId,
                (c, o) => new { c.Name, o.Amount })
            .OrderBy(x => x.Name).ThenBy(x => x.Amount)
            .ToArray();
        Assert.HasCount(3, pairs);
        Assert.AreEqual("alpha", pairs[0].Name); Assert.AreEqual(10m, pairs[0].Amount);
        Assert.AreEqual("alpha", pairs[1].Name); Assert.AreEqual(20m, pairs[1].Amount);
        Assert.AreEqual("beta", pairs[2].Name); Assert.AreEqual(30m, pairs[2].Amount);
    }

    [TestMethod]
    public void RightJoin_EmitsRightJoinAndIncludesUnmatchedRight()
    {
        // EF Core 10's RightJoin LINQ operator translates to RIGHT JOIN.
        // Seed an orphan order (CustomerId = 99 references no Customer) so the
        // unmatched-right path emits with a NULL customer name.
        using var context = SeededContext();
        _ = context.CustomerOrders.Add(new CustomerOrder { CustomerId = 99, Amount = 99m });
        _ = context.SaveChanges();

        var pairs = context.Customers
            .RightJoin(context.CustomerOrders,
                c => c.Id,
                o => o.CustomerId,
                (c, o) => new { CustomerName = c == null ? null : c.Name, o.Amount })
            .OrderBy(x => x.Amount)
            .ToArray();
        Assert.HasCount(4, pairs);
        Assert.AreEqual("alpha", pairs[0].CustomerName); Assert.AreEqual(10m, pairs[0].Amount);
        Assert.AreEqual("alpha", pairs[1].CustomerName); Assert.AreEqual(20m, pairs[1].Amount);
        Assert.AreEqual("beta", pairs[2].CustomerName); Assert.AreEqual(30m, pairs[2].Amount);
        Assert.IsNull(pairs[3].CustomerName); Assert.AreEqual(99m, pairs[3].Amount);
    }

    [TestMethod]
    public void LeftJoin_EmitsLeftJoinAndIncludesUnmatchedLeft()
    {
        // EF Core 10's LeftJoin LINQ operator translates to LEFT JOIN. The
        // GroupJoin/DefaultIfEmpty pattern below covers the same shape; this
        // exercises the explicit operator path through the join pipeline.
        using var context = SeededContext();
        var pairs = context.Customers
            .LeftJoin(context.CustomerOrders,
                c => c.Id,
                o => o.CustomerId,
                (c, o) => new { c.Name, Amount = o == null ? (decimal?)null : o.Amount })
            .OrderBy(x => x.Name).ThenBy(x => x.Amount)
            .ToArray();
        // gamma has no orders → null Amount.
        Assert.HasCount(4, pairs);
        Assert.AreEqual("gamma", pairs[3].Name);
        Assert.IsNull(pairs[3].Amount);
    }

    [TestMethod]
    public void GroupJoin_DefaultIfEmpty_EmitsLeftJoin()
    {
        // The classic EF Core pattern for "left outer join via LINQ":
        // GroupJoin + SelectMany + DefaultIfEmpty. Translates to LEFT JOIN.
        using var context = SeededContext();
        var pairs = context.Customers
            .GroupJoin(context.CustomerOrders,
                c => c.Id,
                o => o.CustomerId,
                (c, orders) => new { c.Name, Orders = orders })
            .SelectMany(x => x.Orders.DefaultIfEmpty(),
                (c, o) => new { c.Name, Amount = o == null ? (decimal?)null : o.Amount })
            .OrderBy(x => x.Name).ThenBy(x => x.Amount)
            .ToArray();
        // Expected: alpha (10), alpha (20), beta (30), gamma (NULL).
        Assert.HasCount(4, pairs);
        Assert.AreEqual("gamma", pairs[3].Name);
        Assert.IsNull(pairs[3].Amount);
    }
}
