using Microsoft.EntityFrameworkCore;
using SqlServerSimulator.EFCore;

namespace SqlServerSimulator;

/// <summary>
/// EF Core 10 oracle tests for the JSON pieces: <c>OwnsOne(...).ToJson()</c>
/// owned-types-as-JSON read/update paths (JSON_VALUE / JSON_MODIFY) and
/// primitive-collection read paths (OPENJSON). Each test takes the LINQ
/// shape EF Core would emit naturally and validates the simulator returns
/// the expected results, exercising the LINQ→SQL pipeline end-to-end.
/// </summary>
[TestClass]
public class EFCoreJson
{
    public TestContext TestContext { get; set; } = null!;

    private static JsonContext NewContext()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table Companies (
                Id int identity primary key,
                Name nvarchar(max) not null,
                Address nvarchar(max) null,
                Tags nvarchar(max) not null,
                Scores nvarchar(max) not null
            )
            """);
        return new JsonContext(simulation);
    }

    private static JsonContext SeededContext(params (string name, string? city, List<string> tags, List<int> scores)[] companies)
    {
        var ctx = NewContext();
        foreach (var (name, city, tags, scores) in companies)
        {
            _ = ctx.Companies.Add(new Company
            {
                Name = name,
                Address = city is null ? null : new Address { City = city, Street = "addr" },
                Tags = tags,
                Scores = scores,
            });
        }
        _ = ctx.SaveChanges();
        return ctx;
    }

    [TestMethod]
    public void OwnedAsJson_Insert_StoresJsonText()
    {
        using var ctx = SeededContext(("Acme", "Springfield", new() { "alpha" }, new() { 10 }));
        var company = ctx.Companies.AsNoTracking().Single();
        Assert.AreEqual("Springfield", company.Address!.City);
    }

    [TestMethod]
    public void OwnedAsJson_WhereByCity_FiltersWithJsonValue()
    {
        using var ctx = SeededContext(
            ("A", "Springfield", new(), new()),
            ("B", "Shelbyville", new(), new()),
            ("C", "Springfield", new(), new()));
        var matches = ctx.Companies.AsNoTracking().Where(c => c.Address!.City == "Springfield").Count();
        Assert.AreEqual(2, matches);
    }

    [TestMethod]
    public void OwnedAsJson_ProjectCity_UsesJsonValue()
    {
        using var ctx = SeededContext(("A", "Springfield", new(), new()));
        var city = ctx.Companies.AsNoTracking().Select(c => c.Address!.City).Single();
        Assert.AreEqual("Springfield", city);
    }

    [TestMethod]
    public void OwnedAsJson_PartialUpdate_UsesJsonModify()
    {
        using var ctx = SeededContext(("A", "Springfield", new(), new()));
        var company = ctx.Companies.Single();
        company.Address!.City = "Shelbyville";
        _ = ctx.SaveChanges();

        using var verifyCtx = new JsonContext(ctx.Simulation);
        Assert.AreEqual("Shelbyville", verifyCtx.Companies.AsNoTracking().Single().Address!.City);
    }

    [TestMethod]
    public void PrimitiveCollection_Contains_UsesOpenJson()
    {
        using var ctx = SeededContext(
            ("A", null, new() { "alpha", "beta" }, new()),
            ("B", null, new() { "gamma" }, new()),
            ("C", null, new() { "beta", "delta" }, new()));
        var ids = ctx.Companies.AsNoTracking()
            .Where(c => c.Tags.Contains("beta"))
            .OrderBy(c => c.Id)
            .Select(c => c.Name)
            .ToList();
        CollectionAssert.AreEqual(new[] { "A", "C" }, ids);
    }

    [TestMethod]
    public void PrimitiveCollection_Count_UsesOpenJson()
    {
        using var ctx = SeededContext(
            ("A", null, new(), new() { 10, 20, 30 }),
            ("B", null, new(), new()),
            ("C", null, new(), new() { 42 }));
        var counts = ctx.Companies.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new { c.Name, ScoreCount = c.Scores.Count })
            .ToList();
        Assert.HasCount(3, counts);
        Assert.AreEqual(3, counts[0].ScoreCount);
        Assert.AreEqual(0, counts[1].ScoreCount);
        Assert.AreEqual(1, counts[2].ScoreCount);
    }

    [TestMethod]
    public void PrimitiveCollection_AnyWithPredicate_UsesOpenJson()
    {
        using var ctx = SeededContext(
            ("A", null, new(), new() { 10, 20 }),
            ("B", null, new(), new() { 5, 8 }),
            ("C", null, new(), new() { 100 }));
        var names = ctx.Companies.AsNoTracking()
            .Where(c => c.Scores.Any(s => s > 15))
            .OrderBy(c => c.Id)
            .Select(c => c.Name)
            .ToList();
        CollectionAssert.AreEqual(new[] { "A", "C" }, names);
    }
}

internal sealed class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Address? Address { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<int> Scores { get; set; } = [];
}

internal sealed class Address
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
}

internal sealed class JsonContext(Simulation simulation) : DbContext
{
    public readonly Simulation Simulation = simulation;

    public DbSet<Company> Companies => Set<Company>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        _ = options.UseSqlServerSimulator(this.Simulation.CreateDbConnection());
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        _ = mb.Entity<Company>(b =>
        {
            _ = b.OwnsOne(c => c.Address, a =>
            {
                _ = a.ToJson();
            });
            _ = b.PrimitiveCollection(c => c.Tags);
            _ = b.PrimitiveCollection(c => c.Scores);
        });
    }
}
