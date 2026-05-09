using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Exercises <c>varchar</c> / <c>nvarchar</c> / <c>varbinary</c> / fixed-length
/// string and binary support through EF Core. Truncation surfaces as
/// <c>DbUpdateException</c> wrapping <c>SimulatedSqlException</c>.
/// </summary>
[TestClass]
public class EFCoreStrings
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Insert_NameRoundTripsViaProjection()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Alice" });

        Assert.AreEqual("Alice", context.People.Select(p => p.Name).FirstOrDefault());
    }

    [TestMethod]
    public async Task InsertAsync_NameRoundTrips()
    {
        await using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "Alice" });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual("Alice", await context.People.Select(p => p.Name).FirstOrDefaultAsync(this.TestContext.CancellationToken));
    }

    [TestMethod]
    public void Insert_VarcharCodeRoundTrips()
    {
        // Code is varchar(10); ASCII fits 1:1 with bytes.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Bob", Code = "ABC123" });

        Assert.AreEqual("ABC123", context.People.Select(p => p.Code).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_NullableCodeAcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Carol", Code = null });

        Assert.IsNull(context.People.Select(p => p.Code).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_NameAtMaxLengthSucceeds()
    {
        // nvarchar(50) — 50 UCS-2 code units exactly fits.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        var atLimit = new string('x', 50);
        _ = context.People.Add(new Person { Id = 1, Name = atLimit });
        _ = context.SaveChanges();

        Assert.AreEqual(atLimit, context.People.Select(p => p.Name).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_NameOverMaxLengthRaisesUpdateException()
    {
        // EF Core wraps SimulatedSqlException in DbUpdateException; Msg 2628 surfaces on InnerException.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = new string('x', 51) });

        var ex = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("would be truncated", ex.InnerException.Message);
        Assert.Contains("People", ex.InnerException.Message);
        Assert.Contains("Name", ex.InnerException.Message);
    }

    [TestMethod]
    public void Insert_VarcharCodeOutOfCp1252_RoundTripsAsReplacement()
    {
        // varchar uses Windows-1252; non-CP1252 chars silently round-trip as '?'.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Eve", Code = "日本" });

        Assert.AreEqual("??", context.People.Select(p => p.Code).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_NVarcharAcceptsSupplementaryCharacter()
    {
        // 🎉 is one code point but two UTF-16 code units (surrogate pair); fits nvarchar(50).
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "🎉 party 🎉" });

        Assert.AreEqual("🎉 party 🎉", context.People.Select(p => p.Name).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_VarbinaryAvatarRoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF };
        _ = context.People.Add(new Person { Id = 1, Name = "Frank", Avatar = bytes });
        _ = context.SaveChanges();

        CollectionAssert.AreEqual(bytes, context.People.Select(p => p.Avatar).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_VarbinaryAvatarOverMax_RaisesTruncationWithHexValue()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        var oversize = new byte[65]; // Avatar is varbinary(64)
        for (var i = 0; i < oversize.Length; i++)
            oversize[i] = (byte)i;

        _ = context.People.Add(new Person { Id = 1, Name = "Greta", Avatar = oversize });

        var ex = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.IsNotNull(ex.InnerException);
        Assert.Contains("would be truncated", ex.InnerException.Message);
        Assert.Contains("Avatar", ex.InnerException.Message);
        Assert.Contains("0x", ex.InnerException.Message);
    }

    [TestMethod]
    public void Insert_NullableAvatarAcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Hank", Avatar = null });

        Assert.IsNull(context.People.Select(p => p.Avatar).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Length()
    {
        // EF Core translates string.Length to CAST(LEN(x) AS int); LEN excludes trailing spaces.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Alice   " });

        Assert.AreEqual(5, context.People.Select(p => p.Name.Length).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_ToUpper()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "alice" });

        Assert.AreEqual("ALICE", context.People.Select(p => p.Name.ToUpper()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_ToLower()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "ALICE" });

        Assert.AreEqual("alice", context.People.Select(p => p.Name.ToLower()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Trim()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "  bob  " });

        Assert.AreEqual("bob", context.People.Select(p => p.Name.Trim()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_TrimStart()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "  bob" });

        Assert.AreEqual("bob", context.People.Select(p => p.Name.TrimStart()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_TrimEnd()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "bob  " });

        Assert.AreEqual("bob", context.People.Select(p => p.Name.TrimEnd()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Substring()
    {
        // C# Substring is 0-indexed; T-SQL SUBSTRING is 1-indexed; EF handles the off-by-one.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "alphabet" });

        Assert.AreEqual("lpha", context.People.Select(p => p.Name.Substring(1, 4)).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Replace()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "hello" });

        Assert.AreEqual("heLLo", context.People.Select(p => p.Name.Replace("l", "L")).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_FixedLengthChar_RoundTripsWithPadding()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Iris", Tag = "hi" });

        Assert.AreEqual("hi   ", context.People.Select(p => p.Tag).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_FixedLengthNChar_RoundTripsWithPadding()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Jane", Initials = "JD" });

        Assert.AreEqual("JD ", context.People.Select(p => p.Initials).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_FixedLengthBinary_RoundTripsWithZeroPadding()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "Karl", Stamp = [0xCA, 0xFE] });

        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0 }, context.People.Select(p => p.Stamp).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_MultipleRows_RoundTripsBothColumns()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(
            new Person { Id = 1, Name = "Alice", Code = "A" },
            new Person { Id = 2, Name = "Bob", Code = "B" },
            new Person { Id = 3, Name = "Carol", Code = null });

        CollectionAssert.AreEquivalent(new[] { "Alice", "Bob", "Carol" }, context.People.Select(p => p.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "A", "B", null }, context.People.Select(p => p.Code).ToArray());
    }

    [TestMethod]
    public void StringFunction_IndexOf()
    {
        // EF Core translates .IndexOf to CHARINDEX-1 (CHARINDEX is 1-based, IndexOf 0-based).
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation()).WithSaved(new Person { Id = 1, Name = "hello world" });

        using var fresh = new TestDbContext(context.Simulation);
        Assert.AreEqual(6, fresh.People.Select(p => p.Name.IndexOf("world", StringComparison.Ordinal)).Single());
    }
}
