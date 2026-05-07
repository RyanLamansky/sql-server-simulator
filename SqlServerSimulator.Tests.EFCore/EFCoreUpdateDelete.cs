using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the UPDATE / DELETE shapes EF Core's SqlServer
/// provider emits — both the change-tracker-driven <c>SaveChanges</c>
/// path (single-row UPDATE / DELETE per modified entity, batched as
/// semicolon-separated statements) and the EF7+ bulk
/// <c>ExecuteUpdate</c> / <c>ExecuteDelete</c> path (multi-table-syntax
/// <c>UPDATE [a] SET ... FROM [t] AS [a]</c> / <c>DELETE [a] FROM [t] AS [a]</c>).
/// Optimistic concurrency via <c>[Timestamp]</c> rides through both paths.
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
        // The most basic real-app workflow: load an entity, change a
        // property, save. EF Core emits UPDATE [People] SET [Name] = @p0
        // WHERE [Id] = @p1. Pre-this-bundle, this would throw at
        // SaveChanges with "Incorrect syntax near 'update'".
        using var context = SeededContext();
        var alice = context.People.Single(p => p.Name == "alice");
        alice.Name = "ALICE";
        _ = context.SaveChanges();

        // Read back through a fresh context (different connection) to
        // confirm the update persisted at the simulation level, not
        // just in the change tracker.
        using var fresh = new TestDbContext(context.Simulation);
        var reloaded = fresh.People.Single(p => p.Id == alice.Id);
        Assert.AreEqual("ALICE", reloaded.Name);
    }

    /// <summary>
    /// Mirrors <see cref="SaveChanges_ModifyExisting_EmitsUpdate"/> through
    /// the async path. The simulator is fully synchronous, so this exists
    /// only to ensure EF Core's default async-over-sync wrapper still works
    /// for the UPDATE shape — same role <see cref="EFCoreBasics.InsertRowAsync"/>
    /// plays for INSERT.
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
        // EF Core 9 emits N semicolon-separated UPDATE statements (one per
        // modified entity) on SaveChanges of a multi-entity update — not a
        // single MERGE. Probed against real SQL Server 2025; the simulator's
        // multi-statement command path handles it without needing
        // MERGE WHEN MATCHED support.
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
        // EF7+ ExecuteUpdate emits a multi-table-syntax UPDATE
        //   UPDATE [a] SET [a].[col] = ... FROM [t] AS [a] WHERE ...
        // (probed against real SQL Server 2025, 2026-05-07). The simulator's
        // parser accepts the alias-form leading identifier and the trailing
        // `FROM <table> AS <alias>` clause; runtime column-resolvers use
        // name.Leaf so alias-qualified column refs resolve to the target.
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
        // EF7+ ExecuteDelete emits
        //   DELETE FROM [a] FROM [t] AS [a] WHERE ...
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
