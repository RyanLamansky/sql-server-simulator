using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's PRIMARY KEY constraint enforcement through EF
/// Core. The duplicate-key path surfaces as <see cref="DbUpdateException"/>
/// whose inner is the simulator's Msg 2627 — the same shape EF Core's
/// SqlServer provider produces against a real database.
/// </summary>
[TestClass]
public class EFCorePrimaryKey
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SaveChanges_FirstRow_RoundTripsThroughPrimaryKey()
    {
        using var context = new TestDbContext(TestDbContext.CreateInventorySimulation()).WithSaved(new Inventory { Sku = "WIDGET-A", Quantity = 7 });

        var fetched = context.Inventory.AsNoTracking().Single();
        Assert.AreEqual("WIDGET-A", fetched.Sku);
        Assert.AreEqual(7, fetched.Quantity);
    }

    [TestMethod]
    public void SaveChanges_DuplicateKey_RaisesDbUpdateException()
    {
        using var context = new TestDbContext(TestDbContext.CreateInventorySimulation()).WithSaved(new Inventory { Sku = "WIDGET-A", Quantity = 1 });

        using var context2 = new TestDbContext(context.Simulation);
        _ = context2.Inventory.Add(new Inventory { Sku = "WIDGET-A", Quantity = 99 });
        var ex = Assert.Throws<DbUpdateException>(() => context2.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("PRIMARY KEY constraint 'pk_inventory'", ex.InnerException.Message);
        Assert.Contains("WIDGET-A", ex.InnerException.Message);
    }

    [TestMethod]
    public void SaveChanges_BatchWithMidBatchPkViolation_RollsBackEntireBatch()
    {
        // EF Core 10 batches multiple Add()s of the same entity type into a
        // single multi-row INSERT statement. SQL Server's auto-commit-mode
        // statement atomicity (Bundle 1) means a mid-batch PK collision rolls
        // back the entire INSERT — neither the valid rows before nor after
        // the collision land in the table.
        using var context = new TestDbContext(TestDbContext.CreateInventorySimulation()).WithSaved(new Inventory { Sku = "EXISTING", Quantity = 1 });

        using var context2 = new TestDbContext(context.Simulation);
        _ = context2.Inventory.Add(new Inventory { Sku = "NEW-1", Quantity = 10 });
        _ = context2.Inventory.Add(new Inventory { Sku = "EXISTING", Quantity = 99 });
        _ = context2.Inventory.Add(new Inventory { Sku = "NEW-2", Quantity = 30 });

        _ = Assert.Throws<DbUpdateException>(() => context2.SaveChanges());

        using var context3 = new TestDbContext(context.Simulation);
        var skus = context3.Inventory.AsNoTracking().Select(i => i.Sku).OrderBy(s => s).ToArray();
        CollectionAssert.AreEqual(new[] { "EXISTING" }, skus);
    }

    [TestMethod]
    public async Task SaveChangesAsync_DuplicateKey_RaisesDbUpdateException()
    {
        await using var context = new TestDbContext(TestDbContext.CreateInventorySimulation());

        _ = context.Inventory.Add(new Inventory { Sku = "BOLT-7", Quantity = 1 });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        await using var context2 = new TestDbContext(context.Simulation);
        _ = context2.Inventory.Add(new Inventory { Sku = "BOLT-7", Quantity = 2 });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context2.SaveChangesAsync(this.TestContext.CancellationToken));
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("PRIMARY KEY constraint 'pk_inventory'", ex.InnerException.Message);
    }
}
