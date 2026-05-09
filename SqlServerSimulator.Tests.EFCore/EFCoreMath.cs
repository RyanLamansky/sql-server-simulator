namespace SqlServerSimulator;

/// <summary>
/// End-to-end coverage for EF Core 10's translation of <c>Math.X</c> LINQ
/// methods. Probe-confirmed (2026-05-09): EF emits the SQL function
/// directly for <c>Math.Round</c> / <c>Floor</c> / <c>Ceiling</c> /
/// <c>Pow</c> / <c>Sqrt</c> / <c>Sign</c> / <c>Log</c> / <c>Exp</c> /
/// <c>Log10</c>, and emits <c>ROUND(x, 0, 1)</c> for <c>Math.Truncate</c>
/// (the truncate-mode third-arg form). <c>Math.Abs</c> already routes
/// through the existing <c>AbsoluteValue</c> path.
/// </summary>
[TestClass]
public class EFCoreMath
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededProductsContext()
    {
        var context = new TestDbContext(TestDbContext.CreateProductsSimulation());
        context.Products.AddRange(
            new Product { Id = 1, Price = 12.345m },
            new Product { Id = 2, Price = 0.5m },
            new Product { Id = 3, Price = -5.5m });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Round_DecimalProperty()
    {
        // Math.Round(p.Price, 2) → ROUND([p].[Price], 2) — half-away-from-zero.
        using var context = SeededProductsContext();
        var rounded = context.Products
            .OrderBy(p => p.Id)
            .Select(p => Math.Round(p.Price, 2))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 12.35m, 0.50m, -5.50m }, rounded);
    }

    [TestMethod]
    public void RoundToInteger()
    {
        // Math.Round(p.Price) → ROUND([p].[Price], 0) — half-away-from-zero.
        using var context = SeededProductsContext();
        var rounded = context.Products
            .OrderBy(p => p.Id)
            .Select(p => Math.Round(p.Price))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 12m, 1m, -6m }, rounded);
    }

    [TestMethod]
    public void Floor_DecimalProperty()
    {
        // Math.Floor(p.Price) → FLOOR([p].[Price]).
        using var context = SeededProductsContext();
        var floors = context.Products
            .OrderBy(p => p.Id)
            .Select(p => Math.Floor(p.Price))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 12m, 0m, -6m }, floors);
    }

    [TestMethod]
    public void Ceiling_DecimalProperty()
    {
        using var context = SeededProductsContext();
        var ceilings = context.Products
            .OrderBy(p => p.Id)
            .Select(p => Math.Ceiling(p.Price))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 13m, 1m, -5m }, ceilings);
    }

    [TestMethod]
    public void Sign_IntFromOrderBy()
    {
        // EF Core emits SIGN([t].[Id]) for Math.Sign(int); the result is
        // an int column. Math.Sign(decimal) → int has a server-side type
        // mismatch (SQL Server's SIGN(decimal) returns decimal, but EF
        // reads the column as int) — that route isn't exercised here.
        using var context = SeededProductsContext();
        var signs = context.Products
            .OrderBy(p => p.Id)
            .Select(p => Math.Sign(p.Id))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 1, 1 }, signs);
    }

    [TestMethod]
    public void Abs_DecimalProperty()
    {
        // Math.Abs(p.Price) → ABS([p].[Price]) — preserves decimal(p,s).
        using var context = SeededProductsContext();
        var values = context.Products
            .OrderBy(p => p.Id)
            .Select(p => Math.Abs(p.Price))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 12.35m, 0.50m, 5.50m }, values);
    }

    [TestMethod]
    public void Truncate_DecimalProperty_EmitsRoundWithTruncateFlag()
    {
        // Math.Truncate(p.Price) → ROUND([p].[Price], 0, 1) — truncate mode.
        using var context = SeededProductsContext();
        var truncated = context.Products
            .OrderBy(p => p.Id)
            .Select(p => Math.Truncate(p.Price))
            .ToArray();
        CollectionAssert.AreEqual(new[] { 12m, 0m, -5m }, truncated);
    }

    [TestMethod]
    public void Power_AndSqrt_OverFloatProjection()
    {
        // Math.Pow / Math.Sqrt require float operands at the LINQ side
        // (EF Core's translator doesn't widen decimal to double
        // automatically), so route through a CAST to double.
        using var context = SeededProductsContext();
        var values = context.Products
            .Where(p => p.Price > 0)
            .OrderBy(p => p.Id)
            .Select(p => new { Pow = Math.Pow((double)p.Price, 2), Root = Math.Sqrt((double)p.Price) })
            .ToArray();
        // Price is decimal(10,2) so 12.345 stores as 12.35 → 12.35^2 = 152.5225.
        AreClose(152.5225m, (decimal)values[0].Pow, 0.001m);
        AreClose(3.5142m, (decimal)values[0].Root, 0.001m);
        AreClose(0.25m, (decimal)values[1].Pow, 0.001m);
    }

    [TestMethod]
    public void LogAndExp_FloatProjection()
    {
        using var context = SeededProductsContext();
        var values = context.Products
            .Where(p => p.Price > 0)
            .OrderBy(p => p.Id)
            .Select(p => new { Log = Math.Log((double)p.Price), Exp = Math.Exp(1.0) })
            .ToArray();
        Assert.HasCount(2, values);
        AreClose(2.5133m, (decimal)values[0].Log, 0.001m); // log(12.345)
        AreClose(2.7183m, (decimal)values[0].Exp, 0.001m); // e
    }

    [TestMethod]
    public void Log10_FloatProjection()
    {
        using var context = SeededProductsContext();
        var values = context.Products
            .Where(p => p.Price > 0)
            .OrderBy(p => p.Id)
            .Select(p => Math.Log10((double)p.Price))
            .ToArray();
        AreClose(1.0915m, (decimal)values[0], 0.001m); // log10(12.345)
    }

    private static void AreClose(decimal expected, decimal actual, decimal tolerance)
        => Assert.IsLessThanOrEqualTo(tolerance, Math.Abs(expected - actual), $"Expected {expected} ± {tolerance}, got {actual}");
}
