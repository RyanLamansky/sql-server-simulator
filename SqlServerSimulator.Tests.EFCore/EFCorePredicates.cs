namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for LINQ predicate shapes EF Core emits via its
/// SqlServer provider, exercising the simulator's full boolean-expression
/// grammar (AND, OR, NOT, parens) and three-valued evaluator.
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
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.A == 1 || f.B == 3).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 4 }, ids);
    }

    [TestMethod]
    public void Where_OrChain_StatusValueSet()
    {
        // EF Core consolidates a same-column OR-chain into IN (...).
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.Status == "active" || f.Status == "archived").OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, ids);
    }

    [TestMethod]
    public void Where_ContainsArray_EmitsInList()
    {
        using var context = SeededContext();
        var wanted = new[] { "active", "archived" };
        var ids = context.Filters.Where(f => wanted.Contains(f.Status)).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 5 }, ids);
    }

    [TestMethod]
    public void Where_AndOrPrecedence_AndBindsTighter()
    {
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.A == 1 || (f.B == 2 && f.IsActive)).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ids);
    }

    [TestMethod]
    public void Where_ParenthesizedCompound_ExplicitGrouping()
    {
        using var context = SeededContext();
        var ids = context.Filters.Where(f => (f.A == 1 || f.B == 3) && f.IsActive).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1 }, ids);
    }

    [TestMethod]
    public void Where_NotEqualOverNullableColumn_ExcludesNull()
    {
        // EF Core emits `[NullableC] <> 20` plus a null-compensation branch.
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.NullableC != 20).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 5 }, ids);
    }

    [TestMethod]
    public void Where_NullableColumnIsNull()
    {
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
        using var context = SeededContext();
        var ids = context.Filters.Where(f => !f.IsActive && f.A > 1).OrderBy(f => f.Id).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 4 }, ids);
    }

    [TestMethod]
    public void Where_KeysetPagination_OrShape()
    {
        // Keyset-pagination shape: `(A > val) OR (A == val AND B > val2)`.
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
        using var context = SeededContext();
        var ids = context.Filters.Where(f => f.NullableC == 30).Select(f => f.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 5 }, ids);
    }
}
