namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's computed-column support through EF Core's
/// <c>HasComputedColumnSql</c> mapping. EF Core treats the mapped property
/// as <see cref="System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Computed"/>:
/// it omits the column from INSERTs and recovers the server-assigned value
/// through <c>OUTPUT INSERTED.&lt;col&gt;</c>.
/// </summary>
[TestClass]
public class EFCoreComputedColumn
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SaveChanges_PopulatesComputedDecimal()
    {
        using var context = new TestDbContext(TestDbContext.CreateReceiptsSimulation());

        var receipt = new Receipt { Subtotal = 100.00m, Tax = 8.25m };
        _ = context.Receipts.Add(receipt);
        _ = context.SaveChanges();

        Assert.AreEqual(108.25m, receipt.Total);
    }

    [TestMethod]
    public void SaveChanges_AcrossRows_PopulatesEachComputedValue()
    {
        using var context = new TestDbContext(TestDbContext.CreateReceiptsSimulation());

        var receipts = new[]
        {
            new Receipt { Subtotal = 10m, Tax = 1m },
            new Receipt { Subtotal = 20m, Tax = 2m },
            new Receipt { Subtotal = 30m, Tax = 3m },
        };
        foreach (var r in receipts)
            _ = context.Receipts.Add(r);
        _ = context.SaveChanges();

        // EF Core's MERGE batch doesn't promise to preserve Add-order when
        // matching OUTPUT rows back to entities, so check by Subtotal+Tax
        // rather than position.
        foreach (var r in receipts)
            Assert.AreEqual(r.Subtotal + r.Tax, r.Total);
    }

    [TestMethod]
    public async Task SaveChangesAsync_PopulatesComputedValue()
    {
        await using var context = new TestDbContext(TestDbContext.CreateReceiptsSimulation());

        var receipt = new Receipt { Subtotal = 50m, Tax = 4m };
        _ = context.Receipts.Add(receipt);
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(54m, receipt.Total);
    }
}
