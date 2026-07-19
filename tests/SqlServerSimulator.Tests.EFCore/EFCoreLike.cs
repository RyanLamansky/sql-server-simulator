using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's <c>LIKE</c> support through EF Core's idiomatic
/// surfaces: <see cref="string.StartsWith(string)"/>, <see cref="string.EndsWith(string)"/>,
/// <see cref="string.Contains(string)"/>, and <see cref="EF.Functions"/>'s
/// <c>Like</c> family. Different LINQ shapes hit different SQL emit paths in
/// the SqlServer provider; coverage here pins which shapes round-trip
/// end-to-end.
/// </summary>
[TestClass]
public class EFCoreLike
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        context.People.AddRange(
            new Person { Id = 1, Name = "Alice" },
            new Person { Id = 2, Name = "Bob" },
            new Person { Id = 3, Name = "Alicia" },
            new Person { Id = 4, Name = "Charlie" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void StartsWith_FiltersByPrefix()
    {
        using var context = SeededContext();
        var ids = context.People.Where(p => p.Name.StartsWith("Ali")).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    [TestMethod]
    public void EndsWith_FiltersBySuffix()
    {
        using var context = SeededContext();
        var ids = context.People.Where(p => p.Name.EndsWith("ie")).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 4 }, ids);
    }

    [TestMethod]
    public void Contains_FiltersBySubstring()
    {
        // EF Core's SqlServer provider emits CHARINDEX for parameterized
        // Contains rather than LIKE, but the result must agree either way.
        using var context = SeededContext();
        var ids = context.People.Where(p => p.Name.Contains("li")).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 4 }, ids);
    }

    [TestMethod]
    public void EFFunctionsLike_ConstantPattern()
    {
        using var context = SeededContext();
        var ids = context.People.Where(p => EF.Functions.Like(p.Name, "Ali%")).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    [TestMethod]
    public void EFFunctionsLike_ParameterizedPattern()
    {
        using var context = SeededContext();
        var pattern = "%li%";
        var ids = context.People.Where(p => EF.Functions.Like(p.Name, pattern)).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3, 4 }, ids);
    }

    [TestMethod]
    public void EFFunctionsLike_WildcardSingleChar()
    {
        // "_" in EF's LIKE binding is the SQL one-char wildcard, not the C#
        // identifier convention.
        using var context = SeededContext();
        var ids = context.People.Where(p => EF.Functions.Like(p.Name, "_ob")).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 2 }, ids);
    }

    [TestMethod]
    public void EFFunctionsLike_BracketCharacterClass()
    {
        using var context = SeededContext();
        var ids = context.People.Where(p => EF.Functions.Like(p.Name, "[ABC]%")).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, ids);
    }

    [TestMethod]
    public void EFFunctionsLike_WithEscape()
    {
        // The 3-arg EF.Functions.Like overload threads an ESCAPE clause through
        // to the simulator. Insert a row whose name contains a literal '%' and
        // verify the escaped pattern matches it without treating '%' as wild.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(
            new Person { Id = 1, Name = "100% Pure" },
            new Person { Id = 2, Name = "Mostly Pure" });

        var ids = context.People.Where(p => EF.Functions.Like(p.Name, "%!%%", "!")).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1 }, ids);
    }

    [TestMethod]
    public void StartsWith_OnVarcharColumn_AlsoWorks()
    {
        // Code is varchar(10) — exercises the CP1252 path rather than nvarchar.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(
            new Person { Id = 1, Name = "x", Code = "ABC-1" },
            new Person { Id = 2, Name = "y", Code = "ABC-2" },
            new Person { Id = 3, Name = "z", Code = "XYZ-1" });

        var ids = context.People.Where(p => p.Code!.StartsWith("ABC")).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2 }, ids);
    }

    [TestMethod]
    public void NotStartsWith_NegatesPredicate()
    {
        using var context = SeededContext();
        var ids = context.People.Where(p => !p.Name.StartsWith("Ali")).OrderBy(p => p.Id).Select(p => p.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 2, 4 }, ids);
    }
}
