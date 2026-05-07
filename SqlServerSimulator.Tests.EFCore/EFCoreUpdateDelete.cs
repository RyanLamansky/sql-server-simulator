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
}
