using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the UPDATE / DELETE shapes EF Core's SqlServer
/// provider emits during <c>SaveChanges</c> on modified or removed
/// entities. Without this bundle the simulator couldn't faithfully stand
/// in for any real EF Core app — the canonical "load, modify, save"
/// flow ended at SaveChanges. Concurrency tokens (rowversion + UPDATE
/// OUTPUT) and the EF7+ bulk operations (ExecuteUpdate / ExecuteDelete)
/// are both deferred to follow-up bundles; see CLAUDE.md.
/// </summary>
[TestClass]
public class EFCoreUpdateDelete
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "alice", Code = "A" },
            new Person { Id = 2, Name = "bob", Code = "B" },
            new Person { Id = 3, Name = "carol", Code = "C" },
            new Person { Id = 4, Name = "dave", Code = "D" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public async Task SaveChanges_ModifyExisting_EmitsUpdate()
    {
        // The most basic real-app workflow: load an entity, change a
        // property, save. EF Core emits UPDATE [People] SET [Name] = @p0
        // WHERE [Id] = @p1. Pre-this-bundle, this would throw at
        // SaveChanges with "Incorrect syntax near 'update'".
        using var context = SeededContext();
        var alice = context.People.Single(p => p.Name == "alice");
        alice.Name = "ALICE";
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        // Read back through a fresh context (different connection) to
        // confirm the update persisted at the simulation level, not
        // just in the change tracker.
        using var fresh = new TestDbContext(context.Simulation);
        var reloaded = fresh.People.Single(p => p.Id == alice.Id);
        Assert.AreEqual("ALICE", reloaded.Name);
    }

    [TestMethod]
    public async Task SaveChanges_ModifyMultipleProperties_OneUpdate()
    {
        using var context = SeededContext();
        var bob = context.People.Single(p => p.Name == "bob");
        bob.Name = "BOB";
        bob.Code = "BB";
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        using var fresh = new TestDbContext(context.Simulation);
        var reloaded = fresh.People.Single(p => p.Id == bob.Id);
        Assert.AreEqual("BOB", reloaded.Name);
        Assert.AreEqual("BB", reloaded.Code);
    }

    [TestMethod]
    public async Task SaveChanges_RemoveEntity_EmitsDelete()
    {
        using var context = SeededContext();
        var carol = context.People.Single(p => p.Name == "carol");
        _ = context.People.Remove(carol);
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        using var fresh = new TestDbContext(context.Simulation);
        var names = fresh.People.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "alice", "bob", "dave" }, names);
    }

    [TestMethod]
    public async Task SaveChanges_ModifyAndRemove_BothApplied()
    {
        using var context = SeededContext();
        var alice = context.People.Single(p => p.Name == "alice");
        alice.Name = "AAA";
        var bob = context.People.Single(p => p.Name == "bob");
        _ = context.People.Remove(bob);
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        using var fresh = new TestDbContext(context.Simulation);
        var rows = fresh.People.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "AAA", "carol", "dave" }, rows);
    }

    [TestMethod]
    public async Task SaveChanges_ModifyMultipleEntities_BatchedAsSemicolonSeparatedUpdates()
    {
        // EF Core 9 emits N semicolon-separated UPDATE statements (one per
        // modified entity) on SaveChanges of a multi-entity update — not a
        // single MERGE. Probed against real SQL Server 2025; the simulator's
        // multi-statement command path handles it without needing
        // MERGE WHEN MATCHED support.
        using var context = SeededContext();
        var people = context.People.OrderBy(p => p.Id).ToList();
        foreach (var p in people)
            p.Code += "x";
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        using var fresh = new TestDbContext(context.Simulation);
        var rows = fresh.People.OrderBy(p => p.Id).Select(p => p.Code).ToArray();
        CollectionAssert.AreEqual(new[] { "Ax", "Bx", "Cx", "Dx" }, rows);
    }

    [TestMethod]
    public async Task SaveChanges_ModifyMultipleTimestampedEntities_BatchedUpdatesWithRowVersion()
    {
        // EF Core 9 with [Timestamp] emits N semicolon-separated
        //   UPDATE [T] SET [c] = @p OUTPUT INSERTED.[RowVersion]
        //     WHERE [Id] = @p AND [RowVersion] = @p
        // statements on a multi-entity SaveChanges. The seed phase exercises
        // MERGE WHEN NOT MATCHED INSERT for a rowversion table — the bug
        // fixed alongside this test was that MERGE didn't auto-bump
        // rowversion at insert time (INSERT did but MERGE was a parallel
        // path that missed it).
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(
            "create table Items (Id int primary key, Name nvarchar(50), RowVersion rowversion)");

        using var seed = new TimestampedDbContext(simulation);
        seed.Items.AddRange(
            new TimestampedItem { Id = 1, Name = "a" },
            new TimestampedItem { Id = 2, Name = "b" },
            new TimestampedItem { Id = 3, Name = "c" });
        _ = await seed.SaveChangesAsync(this.TestContext.CancellationToken);

        using var ctx = new TimestampedDbContext(simulation);
        var items = ctx.Items.OrderBy(i => i.Id).ToList();
        foreach (var i in items)
            i.Name += "!";
        _ = await ctx.SaveChangesAsync(this.TestContext.CancellationToken);

        using var fresh = new TimestampedDbContext(simulation);
        var names = fresh.Items.OrderBy(i => i.Id).Select(i => i.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "a!", "b!", "c!" }, names);
    }

    [TestMethod]
    public async Task SaveChanges_TimestampEntity_ReadsBackAutoBumpedRowVersion()
    {
        // EF Core's [Timestamp]-tracked entity emits
        //   UPDATE [T] SET [Name] = @p0 OUTPUT INSERTED.[RowVersion] WHERE [Id] = @p1 AND [RowVersion] = @p2;
        // on SaveChanges. The simulator must auto-bump rv on UPDATE, accept
        // the varbinary parameter in the WHERE comparison, and surface the
        // new value via OUTPUT. After SaveChanges, EF Core compares the
        // tracked entity's RowVersion to detect concurrency failures.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(
            "create table Items (Id int primary key, Name nvarchar(50), RowVersion rowversion)");

        using var context = new TimestampedDbContext(simulation);
        var item = new TimestampedItem { Id = 1, Name = "first" };
        _ = context.Items.Add(item);
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);
        var initialRv = item.RowVersion!.ToArray();

        item.Name = "second";
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);
        var bumpedRv = item.RowVersion!.ToArray();

        Assert.IsFalse(initialRv.SequenceEqual(bumpedRv), "rowversion must change after SaveChanges of a modified entity");
    }
}

internal class TimestampedItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[]? RowVersion { get; set; }
}

internal class TimestampedDbContext(Simulation simulation) : DbContext
{
    public Simulation Simulation { get; set; } = simulation;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
    }

    public DbSet<TimestampedItem> Items => Set<TimestampedItem>();
}
