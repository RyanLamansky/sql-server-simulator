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
        using var context = new TestDbContext(TestDbContext.CreateInventorySimulation());

        _ = context.Inventory.Add(new Inventory { Sku = "WIDGET-A", Quantity = 7 });
        _ = context.SaveChanges();

        var fetched = context.Inventory.AsNoTracking().Single();
        Assert.AreEqual("WIDGET-A", fetched.Sku);
        Assert.AreEqual(7, fetched.Quantity);
    }

    [TestMethod]
    public void SaveChanges_DuplicateKey_RaisesDbUpdateException()
    {
        using var context = new TestDbContext(TestDbContext.CreateInventorySimulation());

        _ = context.Inventory.Add(new Inventory { Sku = "WIDGET-A", Quantity = 1 });
        _ = context.SaveChanges();

        using var context2 = new TestDbContext(context.Simulation);
        _ = context2.Inventory.Add(new Inventory { Sku = "WIDGET-A", Quantity = 99 });
        var ex = Assert.Throws<DbUpdateException>(() => context2.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("PRIMARY KEY constraint 'pk_inventory'", ex.InnerException.Message);
        Assert.Contains("WIDGET-A", ex.InnerException.Message);
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
