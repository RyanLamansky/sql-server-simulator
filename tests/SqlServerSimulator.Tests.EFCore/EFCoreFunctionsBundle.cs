using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Coverage for the <see cref="EF.Functions"/> members added in the EF.Functions
/// scalar bundle: <c>IsNumeric</c>, <c>IsDate</c>, and <c>Random</c>. Each
/// emits the corresponding SQL Server built-in via the SqlServer provider,
/// so the LINQ→SQL pipeline is the actual coverage target — raw SQL paths
/// are validated in the *.Tests project.
/// </summary>
[TestClass]
public sealed class EFCoreFunctionsBundle
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "Alice", Code = "100" },
            new Person { Id = 2, Name = "Bob", Code = "abc" },
            new Person { Id = 3, Name = "Charlie", Code = "-7.5" },
            new Person { Id = 4, Name = "Dave", Code = null });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void EFFunctions_IsNumeric_FiltersNumericCodes()
    {
        using var context = SeededContext();
        var ids = context.People
            .Where(p => EF.Functions.IsNumeric(p.Code!))
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToArray();
        // '100' and '-7.5' are numeric per SQL Server's loose rules; 'abc'
        // and NULL are not. NULL through IsNumeric returns 0 (probe-confirmed
        // in the *.Tests suite). EF.Functions.IsNumeric surfaces this as
        // bool via comparison-to-1.
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    [TestMethod]
    public void EFFunctions_IsDate_FiltersDateStrings()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "X", Code = "2026-05-12" },
            new Person { Id = 2, Name = "Y", Code = "not-a-date" },
            new Person { Id = 3, Name = "Z", Code = "20260512" });
        _ = context.SaveChanges();
        var ids = context.People
            .Where(p => EF.Functions.IsDate(p.Code!))
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    /// <summary>
    /// <see cref="DbFunctionsExtensions.Random"/> emits <c>RAND()</c> (no
    /// seed). The defining behavior is that the value is unique per query
    /// invocation but reused across rows — assertion here just verifies
    /// the function executes and returns a value in [0, 1).
    /// </summary>
    [TestMethod]
    public void EFFunctions_Random_EvaluatesInRange()
    {
        using var context = SeededContext();
        var values = context.People.Select(p => new { p.Id, Roll = EF.Functions.Random() }).ToArray();
        Assert.HasCount(4, values);
        foreach (var v in values)
            Assert.IsTrue(v.Roll is >= 0.0 and < 1.0, $"Random produced {v.Roll}");
    }
}
