using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Exercises ORDER BY and DISTINCT through EF Core's idiomatic LINQ surface:
/// <c>OrderBy</c> / <c>OrderByDescending</c> / <c>ThenBy</c>, the
/// determinism-by-sort form of <c>First</c> / <c>FirstOrDefault</c> /
/// <c>Take</c>, and <c>Distinct</c>. Together these confirm that EF Core's
/// generated SQL for these operators (TOP after ORDER BY, SELECT DISTINCT
/// across multi-column projections, ORDER BY by aliased projection) lands on
/// the simulator's pipeline correctly.
/// </summary>
[TestClass]
public class EFCoreOrderingAndDistinct
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeedPeople()
    {
        var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 3, Name = "Carol", Code = "B" },
            new Person { Id = 1, Name = "Alice", Code = "A" },
            new Person { Id = 2, Name = "Bob", Code = "B" },
            new Person { Id = 4, Name = "Dave", Code = "A" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void OrderBy_AscendingId_ReturnsRowsInOrder()
    {
        using var context = SeedPeople();
        var ids = context.People.OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, ids);
    }

    [TestMethod]
    public void OrderByDescending_Id_ReturnsRowsInReverseOrder()
    {
        using var context = SeedPeople();
        var ids = context.People.OrderByDescending(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 4, 3, 2, 1 }, ids);
    }

    [TestMethod]
    public void OrderBy_ThenBy_OrdersByPrimaryThenSecondary()
    {
        using var context = SeedPeople();
        var rows = context.People
            .OrderBy(p => p.Code)
            .ThenBy(p => p.Name)
            .Select(p => new { p.Code, p.Name })
            .ToArray();
        // Primary key Code: A,A,B,B; secondary Name within each group is alphabetical.
        CollectionAssert.AreEqual(
            new[] { ("A", "Alice"), ("A", "Dave"), ("B", "Bob"), ("B", "Carol") },
            rows.Select(r => (r.Code!, r.Name)).ToArray());
    }

    [TestMethod]
    public void OrderBy_String_RespectsCollationCaseInsensitive()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "B" },
            new Person { Id = 2, Name = "a" },
            new Person { Id = 3, Name = "C" });
        _ = context.SaveChanges();

        // Default collation case-insensitive: 'a' sorts with 'A', so order is a, B, C.
        var names = context.People.OrderBy(p => p.Name).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "a", "B", "C" }, names);
    }

    [TestMethod]
    public void OrderBy_FirstOrDefault_ReturnsLowestKey()
    {
        using var context = SeedPeople();
        var first = context.People.OrderBy(p => p.Id).Select(p => p.Name).FirstOrDefault();
        Assert.AreEqual("Alice", first);
    }

    [TestMethod]
    public void OrderByDescending_First_ReturnsHighestKey()
    {
        using var context = SeedPeople();
        var first = context.People.OrderByDescending(p => p.Id).Select(p => p.Name).First();
        Assert.AreEqual("Dave", first);
    }

    [TestMethod]
    public void OrderBy_Take_ReturnsFirstNRowsDeterministically()
    {
        using var context = SeedPeople();
        var top2 = context.People.OrderBy(p => p.Id).Select(p => p.Id).Take(2).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, top2);
    }

    [TestMethod]
    public void Distinct_OnSingleColumn_RemovesDuplicates()
    {
        using var context = SeedPeople();
        // Codes are A, A, B, B → distinct produces 2 rows. Order them so the
        // assertion isn't dependent on insertion order leaking through.
        var codes = context.People.Select(p => p.Code).Distinct().OrderBy(c => c).ToArray();
        CollectionAssert.AreEqual(new[] { "A", "B" }, codes);
    }

    [TestMethod]
    public void Distinct_OnMultiColumn_DedupesByTuple()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "Alice", Code = "A" },
            new Person { Id = 2, Name = "Alice", Code = "A" }, // duplicate (Name, Code)
            new Person { Id = 3, Name = "Alice", Code = "B" }, // different Code
            new Person { Id = 4, Name = "Bob", Code = "A" });
        _ = context.SaveChanges();

        var pairs = context.People
            .Select(p => new { p.Name, p.Code })
            .Distinct()
            .OrderBy(x => x.Name).ThenBy(x => x.Code)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { ("Alice", "A"), ("Alice", "B"), ("Bob", "A") },
            pairs.Select(p => (p.Name, p.Code!)).ToArray());
    }

    [TestMethod]
    public async Task OrderBy_FirstAsync_ReturnsLowestKey()
    {
        await using var context = SeedPeople();
        var first = await context.People
            .OrderBy(p => p.Id)
            .Select(p => p.Name)
            .FirstAsync(this.TestContext.CancellationToken);
        Assert.AreEqual("Alice", first);
    }

    [TestMethod]
    public void OrderBy_DateTime_ReturnsChronologicalOrder()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        context.Events.AddRange(
            new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4) },
            new Event { Id = 2, CreatedAt = new DateTime(2024, 1, 15) },
            new Event { Id = 3, CreatedAt = new DateTime(2025, 7, 22) });
        _ = context.SaveChanges();

        var times = context.Events
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.CreatedAt)
            .ToArray();
        CollectionAssert.AreEqual(new[]
        {
            new DateTime(2026, 5, 4),
            new DateTime(2025, 7, 22),
            new DateTime(2024, 1, 15)
        }, times);
    }
}
