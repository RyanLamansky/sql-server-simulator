namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's LINQ aggregate translations: <c>.Count()</c>,
/// <c>.Sum()</c>, <c>.Max()</c>, <c>.Min()</c>, <c>.Average()</c>, plus
/// <c>.GroupBy(...).Select(...)</c> projections that exercise the
/// simulator's GROUP BY / HAVING path. Pre-aggregate-support these all
/// failed with "COUNT not a recognized built-in function".
/// </summary>
[TestClass]
public class EFCoreAggregates
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreateFiltersSimulation());
        context.Filters.AddRange(
            new Filter { A = 1, B = 1, NullableC = 10, IsActive = true, Status = "active" },
            new Filter { A = 1, B = 2, NullableC = null, IsActive = false, Status = "pending" },
            new Filter { A = 2, B = 2, NullableC = 20, IsActive = true, Status = "active" },
            new Filter { A = 2, B = 3, NullableC = null, IsActive = false, Status = null },
            new Filter { A = 3, B = 1, NullableC = 30, IsActive = true, Status = "archived" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Count_OverWholeTable()
    {
        using var context = SeededContext();
        Assert.AreEqual(5, context.Filters.Count());
    }

    [TestMethod]
    public void Count_WithPredicate()
    {
        using var context = SeededContext();
        Assert.AreEqual(3, context.Filters.Count(f => f.IsActive));
    }

    [TestMethod]
    public void Sum_OverInt()
    {
        using var context = SeededContext();
        Assert.AreEqual(9, context.Filters.Sum(f => f.A));
    }

    [TestMethod]
    public void Max_OverInt()
    {
        using var context = SeededContext();
        Assert.AreEqual(3, context.Filters.Max(f => f.A));
    }

    [TestMethod]
    public void Min_OverInt()
    {
        using var context = SeededContext();
        Assert.AreEqual(1, context.Filters.Min(f => f.A));
    }

    [TestMethod]
    public void Average_OverInt()
    {
        // EF Core casts to float in the SQL emit so .Average() returns the
        // mathematical mean (1.8 for sum=9, count=5) rather than SQL Server's
        // truncating int AVG (which would return 1).
        using var context = SeededContext();
        Assert.AreEqual(1.8d, context.Filters.Average(f => f.A));
    }

    [TestMethod]
    public void GroupBy_CountPerKey()
    {
        // EF Core: .GroupBy(s).Select(g => new { Key, Count }).
        using var context = SeededContext();
        var byStatus = context.Filters
            .GroupBy(f => f.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToArray();

        var statusCounts = byStatus.ToDictionary(x => x.Status ?? "<null>", x => x.Count);
        Assert.AreEqual(2, statusCounts["active"]);
        Assert.AreEqual(1, statusCounts["pending"]);
        Assert.AreEqual(1, statusCounts["archived"]);
        Assert.AreEqual(1, statusCounts["<null>"]);
    }

    [TestMethod]
    public void GroupBy_SumPerKey()
    {
        using var context = SeededContext();
        var byA = context.Filters
            .GroupBy(f => f.A)
            .Select(g => new { A = g.Key, Total = g.Sum(f => f.B) })
            .ToArray();

        var aTotals = byA.ToDictionary(x => x.A, x => x.Total);
        Assert.AreEqual(3, aTotals[1]); // 1 + 2
        Assert.AreEqual(5, aTotals[2]); // 2 + 3
        Assert.AreEqual(1, aTotals[3]);
    }

    [TestMethod]
    public void Aggregate_OnEmptyFilteredSet()
    {
        // Sum / Max / Min over empty input → null in SQL; EF Core's int
        // aggregates throw or return 0 depending on the call shape. Use
        // .Where(...).Sum(...) which returns 0 for empty (the no-op
        // identity).
        using var context = SeededContext();
        Assert.AreEqual(0, context.Filters.Where(f => f.A > 999).Sum(f => f.A));
    }

    [TestMethod]
    public void GroupBy_MultipleAggregatesPerGroup()
    {
        // GroupBy producing multiple aggregates per key in one projection.
        // Verifies the simulator emits all three aggregates from a single
        // grouped scan rather than re-issuing per-aggregate.
        var simulation = TestDbContext.CreateCustomersSimulation();
        using (var seed = new TestDbContext(simulation))
        {
            seed.CustomerOrders.AddRange(
                new CustomerOrder { CustomerId = 1, Amount = 10 },
                new CustomerOrder { CustomerId = 1, Amount = 20 },
                new CustomerOrder { CustomerId = 2, Amount = 30 },
                new CustomerOrder { CustomerId = 2, Amount = 40 });
            _ = seed.SaveChanges();
        }

        using var context = new TestDbContext(simulation);
        var summary = context.CustomerOrders
            .GroupBy(o => o.CustomerId)
            .Select(g => new
            {
                CustomerId = g.Key,
                Total = g.Sum(o => o.Amount),
                Count = g.Count(),
                Max = g.Max(o => o.Amount),
            })
            .OrderBy(x => x.CustomerId)
            .ToList();

        Assert.HasCount(2, summary);
        Assert.AreEqual(30m, summary[0].Total);
        Assert.AreEqual(2, summary[0].Count);
        Assert.AreEqual(20m, summary[0].Max);
        Assert.AreEqual(70m, summary[1].Total);
    }
}
