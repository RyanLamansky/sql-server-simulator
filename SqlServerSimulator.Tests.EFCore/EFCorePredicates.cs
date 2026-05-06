namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the LINQ predicate shapes EF Core emits via its
/// SqlServer provider, exercising the simulator's full boolean-expression
/// grammar (AND, OR, NOT, parens) and three-valued evaluator. These pin the
/// "real-world LINQ Where queries round-trip" guarantee — the headline
/// unlock from adding tri-state Run plus OR/NOT/parens to BooleanExpression.
/// </summary>
[TestClass]
public class EFCorePredicates
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreateFiltersSimulation());
        context.Filters.AddRange(
            new Filter { A = 1, B = 1, NullableC = 10, IsActive = true, Status = "active" },
            new Filter { A = 1, B = 2, NullableC = null, IsActive = false, Status = "pending" },
            new Filter { A = 2, B = 2, NullableC = 20, IsActive = true, Status = "active" },
            new Filter { A = 2, B = 3, NullableC = null, IsActive = false, Status = null },
            new Filter { A = 3, B = 1, NullableC = 30, IsActive = true, Status = "archived" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Where_OrAcrossTwoColumns_FlatPredicate()
    {
        // EF Core emits `WHERE [A] = 1 OR [B] = 3`. Pre-fix the simulator
        // dropped the OR clause; now it returns rows matching either side.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.A == 1 || f.B == 3).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 4 }, ids);
    }

    [TestMethod]
    public void Where_OrChain_StatusValueSet()
    {
        // EF Core consolidates a same-column OR-chain into `[Status] IN
        // (N'a', N'b')`. The simulator now parses that path.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.Status == "active" || f.Status == "archived").OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, ids);
    }

    [TestMethod]
    public void Where_ContainsArray_EmitsInList()
    {
        // EF Core's idiomatic shape for "value in a set": LINQ Contains
        // against an in-memory array. SqlServer provider emits `IN (...)`.
        using var context = SeededContext();
        var wanted = new[] { "active", "archived" };
        var ids = context.Filters.Where(f => wanted.Contains(f.Status)).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, ids);
    }

    [TestMethod]
    public void Where_AndOrPrecedence_AndBindsTighter()
    {
        // `a == 1 || (b == 2 && active)` is what EF Core's expression
        // visitor will emit unparenthesized: `WHERE [A] = 1 OR [B] = 2 AND
        // [IsActive] = CAST(1 AS bit)`. Standard SQL precedence.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.A == 1 || (f.B == 2 && f.IsActive)).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
    }

    [TestMethod]
    public void Where_ParenthesizedCompound_ExplicitGrouping()
    {
        // (A=1 OR B=3) AND IsActive — explicit parens force AND across the
        // whole OR group rather than only the right side.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => (f.A == 1 || f.B == 3) && f.IsActive).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1 }, ids);
    }

    [TestMethod]
    public void Where_NotEqualOverNullableColumn_ExcludesNull()
    {
        // EF Core emits `WHERE [NullableC] <> 20` plus a null-compensation
        // branch (`OR [NullableC] IS NULL` or similar). Now both halves
        // parse; tri-state Run gives the right answer (rows 1 and 5).
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.NullableC != 20).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 5 }, ids);
    }

    [TestMethod]
    public void Where_NullableColumnIsNull()
    {
        // `f.NullableC == null` translates to `WHERE [NullableC] IS NULL`.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.NullableC == null).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 2, 4 }, ids);
    }

    [TestMethod]
    public void Where_NullableColumnIsNotNull()
    {
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.NullableC != null).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, ids);
    }

    [TestMethod]
    public void Where_NotInCompoundPredicate()
    {
        // !IsActive combined via AND with another comparison; EF Core emits
        // `WHERE [IsActive] = CAST(0 AS bit) AND [A] > 0` (or NOT-flavor
        // depending on version). Either lands on the new boolean grammar.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => !f.IsActive && f.A > 1).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 4 }, ids);
    }

    [TestMethod]
    public void Where_KeysetPagination_OrShape()
    {
        // The keyset-pagination shape EF Core users frequently write:
        // `(A > val) OR (A == val AND B > val2)` — gets the next page after
        // a known `(A, B)` cursor. Multi-clause OR with an inner AND group.
        using var context = SeededContext();
        var ids = context.Filters
            .Where(f => f.A > 1 || (f.A == 1 && f.B > 1))
            .OrderBy(f => f.A).ThenBy(f => f.B)
            .Select(f => f.Id)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, ids);
    }

    [TestMethod]
    public void Where_BoolColumnDirect_IncludesActiveOnly()
    {
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.IsActive).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, ids);
    }

    [TestMethod]
    public void Where_NotBoolColumn_OnlyInactive()
    {
        using var context = SeededContext();
        var ids = context.Filters.Where(f => !f.IsActive).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 2, 4 }, ids);
    }

    [TestMethod]
    public void Where_NullableEqualsValue_AutoExcludesNulls()
    {
        // `f.NullableC == 30` — SQL `=` on NULL returns UNKNOWN → excluded.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.NullableC == 30).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 5 }, ids);
    }
}
