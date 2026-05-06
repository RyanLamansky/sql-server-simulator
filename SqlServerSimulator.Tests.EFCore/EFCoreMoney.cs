namespace SqlServerSimulator;

/// <summary>
/// Exercises the SqlServerSimulator EF Core adapter's
/// <c>decimal → money</c> and <c>decimal → smallmoney</c> mappings. Both
/// CLR/store pairs would throw at SaveChanges under vanilla
/// <c>UseSqlServer</c> because <c>SqlServerDecimalTypeMapping</c>
/// downcasts the parameter to <c>SqlParameter</c> to override
/// <c>SqlDbType</c>; the substitute
/// <see cref="Microsoft.EntityFrameworkCore.Storage.DecimalTypeMapping"/>
/// sets <see cref="System.Data.DbType.Currency"/> directly.
/// </summary>
[TestClass]
public class EFCoreMoney
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Insert_Money_RoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        _ = context.Invoices.Add(new Invoice { Id = 1, Amount = 1234.5678m, Surcharge = 9.99m });
        _ = context.SaveChanges();

        Assert.AreEqual(1234.5678m, context.Invoices.Select(i => i.Amount).First());
    }

    [TestMethod]
    public async Task InsertAsync_Money_RoundTrips()
    {
        await using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        _ = context.Invoices.Add(new Invoice { Id = 1, Amount = 42m, Surcharge = 1m });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(42m, context.Invoices.Select(i => i.Amount).First());
    }

    [TestMethod]
    public void Insert_Money_RoundsHalfUpAtScale4()
    {
        // money stores 4 fractional digits; .23456 rounds up to .2346.
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        _ = context.Invoices.Add(new Invoice { Id = 1, Amount = 1.23456m, Surcharge = 0m });
        _ = context.SaveChanges();

        Assert.AreEqual(1.2346m, context.Invoices.Select(i => i.Amount).First());
    }

    [TestMethod]
    public void Insert_SmallMoney_RoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        _ = context.Invoices.Add(new Invoice { Id = 1, Amount = 0m, Surcharge = 12.3456m });
        _ = context.SaveChanges();

        Assert.AreEqual(12.3456m, context.Invoices.Select(i => i.Surcharge).First());
    }

    [TestMethod]
    public void Insert_NullableMoney_AcceptsNullAndValue()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        context.Invoices.AddRange(
            new Invoice { Id = 1, Amount = 100m, Surcharge = 0m },
            new Invoice { Id = 2, Amount = 200m, Surcharge = 0m, Tip = 15.50m });
        _ = context.SaveChanges();

        var rows = context.Invoices.OrderBy(i => i.Id).Select(i => i.Tip).ToArray();
        Assert.IsNull(rows[0]);
        Assert.AreEqual(15.50m, rows[1]);
    }

    [TestMethod]
    public void Insert_NullableSmallMoney_AcceptsNullAndValue()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        context.Invoices.AddRange(
            new Invoice { Id = 1, Amount = 100m, Surcharge = 0m },
            new Invoice { Id = 2, Amount = 200m, Surcharge = 0m, Discount = -5.25m });
        _ = context.SaveChanges();

        var rows = context.Invoices.OrderBy(i => i.Id).Select(i => i.Discount).ToArray();
        Assert.IsNull(rows[0]);
        Assert.AreEqual(-5.25m, rows[1]);
    }

    [TestMethod]
    public void Where_FiltersByMoneyEquality()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        var target = 99.99m;
        context.Invoices.AddRange(
            new Invoice { Id = 1, Amount = 50m, Surcharge = 0m },
            new Invoice { Id = 2, Amount = target, Surcharge = 0m },
            new Invoice { Id = 3, Amount = 500m, Surcharge = 0m });
        _ = context.SaveChanges();

        var match = context.Invoices.Where(i => i.Amount == target).Select(i => i.Id).Single();
        Assert.AreEqual(2, match);
    }

    [TestMethod]
    public void Insert_Money_NegativeValue_RoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateInvoicesSimulation());
        _ = context.Invoices.Add(new Invoice { Id = 1, Amount = -1234.5678m, Surcharge = 0m });
        _ = context.SaveChanges();

        Assert.AreEqual(-1234.5678m, context.Invoices.Select(i => i.Amount).First());
    }
}
