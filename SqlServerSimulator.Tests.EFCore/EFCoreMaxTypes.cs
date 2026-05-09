namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c> /
/// <c>varbinary(MAX)</c> support through EF Core. EF Core's default mapping
/// for an unannotated <c>string</c> property is <c>nvarchar(max)</c>, so any
/// model with such a property hits this path; the explicit MAX siblings
/// behave identically.
/// </summary>
[TestClass]
public class EFCoreMaxTypes
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Insert_NVarcharMax_DefaultStringMapping_RoundTrips()
    {
        // Body has no [Column] annotation; EF picks nvarchar(max) by default.
        using var context = new TestDbContext(TestDbContext.CreateArticlesSimulation()).WithSaved(new Article { Id = 1, Body = "hello world" });

        Assert.AreEqual("hello world", context.Articles.Select(a => a.Body).First());
    }

    [TestMethod]
    public void Insert_NVarcharMax_LargeValue_RoundTripsThroughLobChain()
    {
        using var context = new TestDbContext(TestDbContext.CreateArticlesSimulation());

        var big = new string('§', 12_000); // > inline 8060-byte cap, forces off-row LOB
        _ = context.Articles.Add(new Article { Id = 1, Body = big });
        _ = context.SaveChanges();

        Assert.AreEqual(big, context.Articles.Select(a => a.Body).First());
    }

    [TestMethod]
    public void Insert_VarcharMax_RoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreateArticlesSimulation());

        var big = new string('A', 20_000);
        _ = context.Articles.Add(new Article { Id = 1, Body = "irrelevant", Summary = big });
        _ = context.SaveChanges();

        Assert.AreEqual(big, context.Articles.Select(a => a.Summary).First());
    }

    [TestMethod]
    public void Insert_VarbinaryMax_LargeValue_RoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreateArticlesSimulation());

        var bytes = new byte[18_000];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i & 0xFF);

        _ = context.Articles.Add(new Article { Id = 1, Body = "irrelevant", Attachment = bytes });
        _ = context.SaveChanges();

        var roundtrip = context.Articles.Select(a => a.Attachment).First();
        Assert.IsNotNull(roundtrip);
        CollectionAssert.AreEqual(bytes, roundtrip);
    }

    [TestMethod]
    public async Task InsertAsync_NVarcharMax_RoundTrips()
    {
        await using var context = new TestDbContext(TestDbContext.CreateArticlesSimulation());
        const string body = "async path through nvarchar(max)";
        _ = context.Articles.Add(new Article { Id = 1, Body = body });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(body, context.Articles.Select(a => a.Body).First());
    }

    [TestMethod]
    public void Where_FiltersByNVarcharMaxEquality()
    {
        using var context = new TestDbContext(TestDbContext.CreateArticlesSimulation()).WithSaved(
            new Article { Id = 1, Body = "first" },
            new Article { Id = 2, Body = "second" },
            new Article { Id = 3, Body = "third" });

        var match = context.Articles.Where(a => a.Body == "second").Select(a => a.Id).Single();
        Assert.AreEqual(2, match);
    }

    [TestMethod]
    public void Insert_NullableVarcharMax_AcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreateArticlesSimulation()).WithSaved(new Article { Id = 1, Body = "body" });

        Assert.IsNull(context.Articles.Select(a => a.Summary).First());
    }
}
