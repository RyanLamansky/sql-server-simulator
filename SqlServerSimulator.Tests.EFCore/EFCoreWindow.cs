namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the LINQ shapes EF Core 10 translates to
/// <c>ROW_NUMBER() OVER(...)</c>: <c>SelectMany</c> + <c>OrderBy</c> +
/// <c>Take</c> over a collection navigation, the same with <c>Skip</c>,
/// and the per-group "latest record" pattern that emits ROW_NUMBER + 1.
/// EF Core 10 wraps every emission in a derived-table subquery filtered
/// by <c>WHERE row &lt;= N</c> (Take) or <c>WHERE 1 &lt; row AND row &lt;= K</c>
/// (Skip+Take); see CLAUDE.md for the full shape.
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
        // alice=1: 4 books with scores 10/20/30/15. bob=2: 2 books with scores 5/40.
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
        // EF Core 10 emits ROW_NUMBER() OVER(PARTITION BY AuthorId ORDER BY Score DESC)
        // wrapped in a derived table, then WHERE row <= 2.
        using var context = SeededContext();
        var titles = context.Authors
            .SelectMany(a => a.Books.OrderByDescending(b => b.Score).Take(2),
                        (a, b) => new { Author = a.Name, b.Title, b.Score })
            .OrderBy(x => x.Author).ThenByDescending(x => x.Score)
            .Select(x => x.Title)
            .ToArray();
        // alice: top-2 by score desc → B3 (30), B2 (20). bob: B6 (40), B5 (5).
        CollectionAssert.AreEqual(new[] { "B3", "B2", "B6", "B5" }, titles);
    }

    [TestMethod]
    public void SkipTake_PerGroup_EmitsRowNumberRange()
    {
        // Skip(1).Take(2) per group → WHERE 1 < row AND row <= 3 in the wrapped query.
        using var context = SeededContext();
        var titles = context.Authors
            .SelectMany(a => a.Books.OrderBy(b => b.Score).Skip(1).Take(2),
                        (a, b) => new { Author = a.Name, b.Title, b.Score })
            .OrderBy(x => x.Author).ThenBy(x => x.Score)
            .Select(x => x.Title)
            .ToArray();
        // alice: ranks 2-3 by score asc (10, 15, 20, 30) → B4 (15), B2 (20).
        // bob: ranks 2-3 by score asc (5, 40) → only rank 2: B6 (40).
        CollectionAssert.AreEqual(new[] { "B4", "B2", "B6" }, titles);
    }

    [TestMethod]
    public void LatestPerGroup_EmitsRowNumberLimitOne()
    {
        // Probe-confirmed shape for "latest record per group" via FirstOrDefault projection
        // on a navigation collection — translates to ROW_NUMBER + LEFT JOIN with row <= 1.
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
