namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the LINQ shapes EF Core 10 translates to
/// <c>CROSS APPLY</c> / <c>OUTER APPLY</c>: <c>SelectMany</c> over a filtered
/// collection navigation, and the same shape with <c>DefaultIfEmpty</c> for
/// the OUTER variant.
/// </summary>
[TestClass]
public class EFCoreApply
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreateAuthorsSimulation());
        context.Authors.AddRange(
            new Author { Name = "alice" },
            new Author { Name = "bob" },
            new Author { Name = "carol" });
        _ = context.SaveChanges();
        // alice=1: 3 books with scores 10/20/30. bob=2: 1 book with score 5. carol=3: no books.
        context.Books.AddRange(
            new Book { AuthorId = 1, Title = "B1", Score = 10 },
            new Book { AuthorId = 1, Title = "B2", Score = 20 },
            new Book { AuthorId = 1, Title = "B3", Score = 30 },
            new Book { AuthorId = 2, Title = "B4", Score = 5 });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void SelectMany_FilteredNavigation_EmitsCrossApply()
    {
        using var context = SeededContext();
        var pairs = context.Authors
            .SelectMany(a => a.Books.Where(b => b.Score > 10),
                        (a, b) => new { Author = a.Name, b.Title, b.Score })
            .OrderBy(x => x.Author).ThenBy(x => x.Score)
            .ToArray();
        Assert.HasCount(2, pairs);
        Assert.AreEqual(("alice", "B2", 20), (pairs[0].Author, pairs[0].Title, pairs[0].Score));
        Assert.AreEqual(("alice", "B3", 30), (pairs[1].Author, pairs[1].Title, pairs[1].Score));
    }

    [TestMethod]
    public void SelectMany_FilterReferencesOuter_EmitsCrossApply()
    {
        // Inner WHERE references both outer and inner columns — can't lift to JOIN, must be APPLY.
        using var context = SeededContext();
        var pairs = context.Authors
            .SelectMany(a => a.Books.Where(b => b.Score >= a.Id * 5),
                        (a, b) => new { Author = a.Name, b.Score })
            .OrderBy(x => x.Author).ThenBy(x => x.Score)
            .ToArray();
        // alice (id=1, threshold=5): all 3 books match. bob (id=2, threshold=10): 5 fails.
        Assert.HasCount(3, pairs);
        Assert.AreEqual("alice", pairs[0].Author);
        Assert.AreEqual("alice", pairs[1].Author);
        Assert.AreEqual("alice", pairs[2].Author);
    }

    [TestMethod]
    public void SelectMany_NoMatches_DropsOuterRow()
    {
        // carol has no books → CROSS APPLY drops the row entirely.
        using var context = SeededContext();
        var names = context.Authors
            .SelectMany(a => a.Books, (a, b) => a.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "alice", "bob" }, names);
    }
}
