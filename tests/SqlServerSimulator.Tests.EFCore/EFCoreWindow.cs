namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the LINQ shapes EF Core 10 translates to
/// <c>ROW_NUMBER() OVER(...)</c>: <c>SelectMany</c> + <c>OrderBy</c> +
/// <c>Take</c> over a collection navigation, the same with <c>Skip</c>,
/// and the per-group "latest record" pattern. EF Core 10 always wraps the
/// emission in a derived-table subquery — see CLAUDE.md for the shape.
/// </summary>
[TestClass]
public class EFCoreWindow
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreateAuthorsSimulation());
        context.Authors.AddRange(
            new Author { Name = "alice" },
            new Author { Name = "bob" });
        _ = context.SaveChanges();
        // alice=1: 4 books (10/20/30/15). bob=2: 2 books (5/40).
        context.Books.AddRange(
            new Book { AuthorId = 1, Title = "B1", Score = 10 },
            new Book { AuthorId = 1, Title = "B2", Score = 20 },
            new Book { AuthorId = 1, Title = "B3", Score = 30 },
            new Book { AuthorId = 1, Title = "B4", Score = 15 },
            new Book { AuthorId = 2, Title = "B5", Score = 5 },
            new Book { AuthorId = 2, Title = "B6", Score = 40 });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Take_PerGroup_EmitsRowNumberFilter()
    {
        using var context = SeededContext();
        var titles = context.Authors
            .SelectMany(a => a.Books.OrderByDescending(b => b.Score).Take(2),
                        (a, b) => new { Author = a.Name, b.Title, b.Score })
            .OrderBy(x => x.Author).ThenByDescending(x => x.Score)
            .Select(x => x.Title)
            .ToArray();
        // alice top-2 desc: B3 (30), B2 (20). bob: B6 (40), B5 (5).
        CollectionAssert.AreEqual(new[] { "B3", "B2", "B6", "B5" }, titles);
    }

    [TestMethod]
    public void SkipTake_PerGroup_EmitsRowNumberRange()
    {
        using var context = SeededContext();
        var titles = context.Authors
            .SelectMany(a => a.Books.OrderBy(b => b.Score).Skip(1).Take(2),
                        (a, b) => new { Author = a.Name, b.Title, b.Score })
            .OrderBy(x => x.Author).ThenBy(x => x.Score)
            .Select(x => x.Title)
            .ToArray();
        // alice ranks 2-3 asc (10, 15, 20, 30): B4 (15), B2 (20). bob ranks 2-3 asc (5, 40): only rank 2 exists, B6 (40).
        CollectionAssert.AreEqual(new[] { "B4", "B2", "B6" }, titles);
    }

    [TestMethod]
    public void LatestPerGroup_EmitsRowNumberLimitOne()
    {
        using var context = SeededContext();
        var pairs = context.Authors
            .Select(a => new { a.Name, Latest = a.Books.OrderByDescending(b => b.Score).FirstOrDefault() })
            .OrderBy(x => x.Name)
            .ToArray();
        Assert.HasCount(2, pairs);
        Assert.AreEqual("alice", pairs[0].Name);
        Assert.AreEqual("B3", pairs[0].Latest!.Title);
        Assert.AreEqual("bob", pairs[1].Name);
        Assert.AreEqual("B6", pairs[1].Latest!.Title);
    }
}
