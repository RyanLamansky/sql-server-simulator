using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the UPDATE / DELETE shapes EF Core's SqlServer
/// provider emits — the change-tracker-driven <c>SaveChanges</c> path
/// (single-row UPDATE / DELETE per modified entity, batched as semicolon-
/// separated statements) and the EF7+ bulk <c>ExecuteUpdate</c> /
/// <c>ExecuteDelete</c> path (multi-table-syntax). Optimistic concurrency
/// via <c>[Timestamp]</c> rides through both.
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
    public void SaveChanges_ModifyExisting_EmitsUpdate()
    {
        using var context = SeededContext();
        var alice = context.People.Single(p => p.Name == "alice");
        alice.Name = "ALICE";
        _ = context.SaveChanges();

        // Read back through a fresh context to confirm persistence at the simulation level, not just change-tracker.
        using var fresh = new TestDbContext(context.Simulation);
        var reloaded = fresh.People.Single(p => p.Id == alice.Id);
        Assert.AreEqual("ALICE", reloaded.Name);
    }

    /// <summary>
    /// Async path smoke test — the simulator is fully synchronous, so this only
    /// confirms EF Core's default async-over-sync wrapper still works for UPDATE.
    /// </summary>
    [TestMethod]
    public async Task SaveChangesAsync_ModifyExisting_AsyncPathSmoke()
    {
        using var context = SeededContext();
        var alice = context.People.Single(p => p.Name == "alice");
        alice.Name = "ALICE";
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        using var fresh = new TestDbContext(context.Simulation);
        var reloaded = fresh.People.Single(p => p.Id == alice.Id);
        Assert.AreEqual("ALICE", reloaded.Name);
    }

    [TestMethod]
    public void SaveChanges_ModifyMultipleProperties_OneUpdate()
    {
        using var context = SeededContext();
        var bob = context.People.Single(p => p.Name == "bob");
        bob.Name = "BOB";
        bob.Code = "BB";
        _ = context.SaveChanges();

        using var fresh = new TestDbContext(context.Simulation);
        var reloaded = fresh.People.Single(p => p.Id == bob.Id);
        Assert.AreEqual("BOB", reloaded.Name);
        Assert.AreEqual("BB", reloaded.Code);
    }

    [TestMethod]
    public void SaveChanges_RemoveEntity_EmitsDelete()
    {
        using var context = SeededContext();
        var carol = context.People.Single(p => p.Name == "carol");
        _ = context.People.Remove(carol);
        _ = context.SaveChanges();

        using var fresh = new TestDbContext(context.Simulation);
        var names = fresh.People.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "alice", "bob", "dave" }, names);
    }

    [TestMethod]
    public void SaveChanges_ModifyAndRemove_BothApplied()
    {
        using var context = SeededContext();
        var alice = context.People.Single(p => p.Name == "alice");
        alice.Name = "AAA";
        var bob = context.People.Single(p => p.Name == "bob");
        _ = context.People.Remove(bob);
        _ = context.SaveChanges();

        using var fresh = new TestDbContext(context.Simulation);
        var rows = fresh.People.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "AAA", "carol", "dave" }, rows);
    }

    [TestMethod]
    public void SaveChanges_ModifyMultipleEntities_BatchedAsSemicolonSeparatedUpdates()
    {
        // EF Core 9 emits N semicolon-separated UPDATEs (not MERGE) on multi-entity SaveChanges.
        using var context = SeededContext();
        var people = context.People.OrderBy(p => p.Id).ToList();
        foreach (var p in people)
            p.Code += "x";
        _ = context.SaveChanges();

        using var fresh = new TestDbContext(context.Simulation);
        var rows = fresh.People.OrderBy(p => p.Id).Select(p => p.Code).ToArray();
        CollectionAssert.AreEqual(new[] { "Ax", "Bx", "Cx", "Dx" }, rows);
    }

    [TestMethod]
    public void SaveChanges_ModifyMultipleTimestampedEntities_BatchedUpdatesWithRowVersion()
    {
        // Regression: MERGE-WHEN-NOT-MATCHED-INSERT didn't auto-bump rowversion (INSERT did, but
        // the parallel MERGE path missed it). The seed phase below exercises the MERGE insert path.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(
            "create table Items (Id int primary key, Name nvarchar(50), RowVersion rowversion)");

        using var seed = new TimestampedDbContext(simulation);
        seed.Items.AddRange(
            new TimestampedItem { Id = 1, Name = "a" },
            new TimestampedItem { Id = 2, Name = "b" },
            new TimestampedItem { Id = 3, Name = "c" });
        _ = seed.SaveChanges();

        using var ctx = new TimestampedDbContext(simulation);
        var items = ctx.Items.OrderBy(i => i.Id).ToList();
        foreach (var i in items)
            i.Name += "!";
        _ = ctx.SaveChanges();

        using var fresh = new TimestampedDbContext(simulation);
        var names = fresh.Items.OrderBy(i => i.Id).Select(i => i.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "a!", "b!", "c!" }, names);
    }

    [TestMethod]
    public void ExecuteUpdate_BulkUpdate_EmitsMultiTableSyntax()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "alice", Code = "A" },
            new Person { Id = 2, Name = "bob", Code = "B" },
            new Person { Id = 3, Name = "carol", Code = "C" });
        _ = context.SaveChanges();

        var rows = context.People.Where(p => p.Code == "A" || p.Code == "B")
            .ExecuteUpdate(setters => setters.SetProperty(p => p.Name, p => p.Name.ToUpper()));

        Assert.AreEqual(2, rows);
        using var fresh = new TestDbContext(context.Simulation);
        var names = fresh.People.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "ALICE", "BOB", "carol" }, names);
    }

    [TestMethod]
    public void ExecuteDelete_BulkDelete_EmitsMultiTableSyntax()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "alice", Code = "A" },
            new Person { Id = 2, Name = "bob", Code = "B" },
            new Person { Id = 3, Name = "carol", Code = "C" });
        _ = context.SaveChanges();

        var rows = context.People.Where(p => p.Code == "B").ExecuteDelete();

        Assert.AreEqual(1, rows);
        using var fresh = new TestDbContext(context.Simulation);
        var names = fresh.People.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "alice", "carol" }, names);
    }

    [TestMethod]
    public void SaveChanges_TimestampEntity_ReadsBackAutoBumpedRowVersion()
    {
        // [Timestamp] entity → UPDATE OUTPUT INSERTED.[RowVersion] WHERE [Id]=@p AND [RowVersion]=@orig.
        // Verifies auto-bump on UPDATE, varbinary param in WHERE, and OUTPUT round-trip to the change tracker.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(
            "create table Items (Id int primary key, Name nvarchar(50), RowVersion rowversion)");

        using var context = new TimestampedDbContext(simulation);
        var item = new TimestampedItem { Id = 1, Name = "first" };
        _ = context.Items.Add(item);
        _ = context.SaveChanges();
        var initialRv = item.RowVersion!.ToArray();

        item.Name = "second";
        _ = context.SaveChanges();
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
