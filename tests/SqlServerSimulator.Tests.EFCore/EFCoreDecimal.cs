namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's <c>decimal(p, s)</c> column support through
/// EF Core. <see cref="decimal"/> is a base-ADO.NET type and EF maps it
/// without overriding <c>SqlDbType</c>, so this path doesn't trigger the
/// <c>SqlParameter</c>-downcast incompatibility that the newer date/time
/// mappings hit.
/// </summary>
[TestClass]
public class EFCoreDecimal
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Insert_Decimal_RoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreateProductsSimulation());

        const decimal price = 19.99m;
        _ = context.Products.Add(new Product { Id = 1, Price = price });
        _ = context.SaveChanges();

        Assert.AreEqual(price, context.Products.Select(p => p.Price).First());
    }

    [TestMethod]
    public async Task InsertAsync_Decimal_RoundTrips()
    {
        await using var context = new TestDbContext(TestDbContext.CreateProductsSimulation());

        const decimal price = 1234.56m;
        _ = context.Products.Add(new Product { Id = 1, Price = price });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(price, context.Products.Select(p => p.Price).First());
    }

    [TestMethod]
    public void Insert_NullableDecimal_AcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreateProductsSimulation()).WithSaved(new Product { Id = 1, Price = 9.99m });

        Assert.IsNull(context.Products.Select(p => p.Discount).First());
    }

    [TestMethod]
    public void Insert_NullableDecimal_AcceptsValue()
    {
        using var context = new TestDbContext(TestDbContext.CreateProductsSimulation());
        const decimal discount = 0.1234m;
        _ = context.Products.Add(new Product { Id = 1, Price = 9.99m, Discount = discount });
        _ = context.SaveChanges();

        Assert.AreEqual(discount, context.Products.Select(p => p.Discount).First());
    }

    [TestMethod]
    public void Where_FiltersByDecimalEquality()
    {
        using var context = new TestDbContext(TestDbContext.CreateProductsSimulation());

        const decimal target = 19.99m;
        context.Products.AddRange(
            new Product { Id = 1, Price = 9.99m },
            new Product { Id = 2, Price = target },
            new Product { Id = 3, Price = 29.99m });
        _ = context.SaveChanges();

        var match = context.Products.Where(p => p.Price == target).Select(p => p.Id).Single();
        Assert.AreEqual(2, match);
    }

    [TestMethod]
    public void OrderBy_DecimalAscending()
    {
        using var context = new TestDbContext(TestDbContext.CreateProductsSimulation());

        decimal[] prices = [29.99m, 9.99m, 19.99m];
        for (var i = 0; i < prices.Length; i++)
            _ = context.Products.Add(new Product { Id = i + 1, Price = prices[i] });
        _ = context.SaveChanges();

        var ordered = context.Products.OrderBy(p => p.Price).Select(p => p.Price).ToArray();
        CollectionAssert.AreEqual(new[] { 9.99m, 19.99m, 29.99m }, ordered);
    }

    [TestMethod]
    public void Cast_DecimalToDouble_ProjectsAsFloat()
    {
        using var context = new TestDbContext(TestDbContext.CreateProductsSimulation()).WithSaved(new Product { Id = 1, Price = 19.99m });

        var asDouble = context.Products.Select(p => (double)p.Price).Single();
        Assert.AreEqual(19.99, asDouble, 0.001);
    }
}
