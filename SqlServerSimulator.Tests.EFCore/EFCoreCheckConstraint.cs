using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's CHECK constraint enforcement through EF
/// Core's SaveChanges path. Violations surface as
/// <see cref="DbUpdateException"/> with the simulator's Msg 547 in the
/// inner exception — the same shape EF Core's SqlServer provider produces
/// against a real database, so calling code that catches
/// <see cref="DbUpdateException"/> works against either.
/// </summary>
[TestClass]
public class EFCoreCheckConstraint
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SaveChanges_StockItemPositiveQuantity_RoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreateStockItemsSimulation());
        _ = context.StockItems.Add(new StockItem { Sku = "BOLT-7", Quantity = 12 });
        _ = context.SaveChanges();

        var fetched = context.StockItems.AsNoTracking().Single();
        Assert.AreEqual("BOLT-7", fetched.Sku);
        Assert.AreEqual(12, fetched.Quantity);
    }

    [TestMethod]
    public void SaveChanges_StockItemNegativeQuantity_RaisesDbUpdateException()
    {
        // The CHECK constraint `Quantity > 0` rejects the row at the
        // simulator level; EF Core wraps Msg 547 in DbUpdateException.
        using var context = new TestDbContext(TestDbContext.CreateStockItemsSimulation());
        _ = context.StockItems.Add(new StockItem { Sku = "BOLT-7", Quantity = -5 });

        var ex = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("CHECK constraint", ex.InnerException.Message);
        Assert.Contains("ck_stockitem_qty", ex.InnerException.Message);
    }

    [TestMethod]
    public async Task SaveChangesAsync_StockItemNegativeQuantity_RaisesDbUpdateException()
    {
        await using var context = new TestDbContext(TestDbContext.CreateStockItemsSimulation());
        _ = context.StockItems.Add(new StockItem { Sku = "WIDGET-2", Quantity = -1 });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(this.TestContext.CancellationToken));
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("CHECK constraint", ex.InnerException.Message);
    }

    [TestMethod]
    public void SaveChanges_StockItemZeroQuantity_AlsoRejected()
    {
        // Predicate is strict `> 0`; zero falls outside.
        using var context = new TestDbContext(TestDbContext.CreateStockItemsSimulation());
        _ = context.StockItems.Add(new StockItem { Sku = "ZERO", Quantity = 0 });

        var ex = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.AreEqual("547", ex.InnerException.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void SaveChanges_StockItemMultiRowBatch_FailingRowSurfacesAsDbUpdateException()
    {
        // EF Core's SqlServer provider sends multi-row INSERTs through a
        // single MERGE statement; the simulator's MERGE implementation
        // enforces CHECK per-row at insert time. The first failing row
        // raises Msg 547, EF Core wraps it in DbUpdateException. Whether
        // earlier rows in the batch are visible depends on transaction
        // semantics — the simulator doesn't model transactions, so we only
        // assert the exception, not roll-back atomicity.
        using var context = new TestDbContext(TestDbContext.CreateStockItemsSimulation());
        context.StockItems.AddRange(
            new StockItem { Sku = "OK-1", Quantity = 5 },
            new StockItem { Sku = "BAD", Quantity = -1 },
            new StockItem { Sku = "OK-2", Quantity = 7 });

        var ex = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.AreEqual("547", ex.InnerException.Data["HelpLink.EvtID"]);
    }
}
