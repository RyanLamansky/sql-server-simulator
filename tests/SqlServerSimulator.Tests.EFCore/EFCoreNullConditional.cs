namespace SqlServerSimulator;

/// <summary>
/// End-to-end coverage for the LINQ shapes EF Core 10 translates to
/// <c>ISNULL</c>. Probe-confirmed against EF Core 10 (2026-05-09):
/// <c>ISNULL</c> emission is gated on a CAST being involved in the
/// <c>??</c> operands — <c>(decimal?)nullableInt ?? 0m</c> translates to
/// <c>ISNULL(CAST([f].[NullableC] AS decimal(18,2)), 0.0)</c>, while
/// plain <c>nullableInt ?? 0</c> picks <c>COALESCE</c> instead. <c>IIF</c>
/// and <c>NULLIF</c> aren't emitted by any natural LINQ shape we found
/// (EF Core picks <c>CASE</c> for ternary projections and safe-divide),
/// so they don't have an EF Core integration story — direct simulator
/// coverage in <c>IsNullIifNullIfTests</c> is the right surface.
/// </summary>
[TestClass]
public class EFCoreNullConditional
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededFiltersContext()
    {
        var context = new TestDbContext(TestDbContext.CreateFiltersSimulation());
        context.Filters.AddRange(
            new Filter { A = 10, B = 2, NullableC = 5, IsActive = true, Status = "open" },
            new Filter { A = 10, B = 0, NullableC = null, IsActive = false, Status = null },
            new Filter { A = 20, B = 5, NullableC = 7, IsActive = true, Status = "open" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void IsNull_DecimalCastNullCoalesce_ReplacesNullWithFallback()
    {
        // (decimal?)f.NullableC ?? 0m → ISNULL(CAST(... AS decimal(18,2)), 0.0)
        using var context = SeededFiltersContext();
        var values = context.Filters
            .OrderBy(f => f.Id)
            .Select(f => (decimal?)f.NullableC ?? 0m)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 5m, 0m, 7m }, values);
    }

    [TestMethod]
    public void IsNull_LongCastNullCoalesce_ReplacesNullWithFallback()
    {
        // (long?)f.NullableC ?? 0L → ISNULL(CAST(... AS bigint), CAST(0 AS bigint))
        using var context = SeededFiltersContext();
        var values = context.Filters
            .OrderBy(f => f.Id)
            .Select(f => (long?)f.NullableC ?? 0L)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 5L, 0L, 7L }, values);
    }

    [TestMethod]
    public void IsNull_ChainedCoalesce_FallsThroughToInnerCast()
    {
        // (decimal?)f.NullableC ?? (decimal?)f.A ?? 0m → ISNULL(CAST(NullableC), CAST(A))
        using var context = SeededFiltersContext();
        var values = context.Filters
            .OrderBy(f => f.Id)
            .Select(f => (decimal?)f.NullableC ?? (decimal?)f.A ?? 0m)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 5m, 10m, 7m }, values);
    }
}
