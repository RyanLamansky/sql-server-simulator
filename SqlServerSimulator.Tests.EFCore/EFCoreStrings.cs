using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's <c>varchar</c> / <c>nvarchar</c> column support
/// through EF Core's idiomatic surface: typed entities mapped via attributes,
/// LINQ projections, and SaveChanges for the write path. Truncation surfaces
/// as DbUpdateException with the simulator's SimulatedSqlException as the
/// inner exception, matching real SQL Server's failure shape under EF Core.
/// </summary>
[TestClass]
public class EFCoreStrings
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Insert_NameRoundTripsViaProjection()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Alice" });
        _ = context.SaveChanges();

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
        // Code is varchar(10) — the UTF-8 storage path. ASCII fits 1:1 with bytes.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Bob", Code = "ABC123" });
        _ = context.SaveChanges();

        Assert.AreEqual("ABC123", context.People.Select(p => p.Code).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_NullableCodeAcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Carol", Code = null });
        _ = context.SaveChanges();

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
        // EF Core wraps the simulator's SimulatedSqlException in DbUpdateException,
        // matching real SQL Server's failure shape — the Msg 2628 truncation error
        // surfaces on InnerException.
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
        // varchar uses Windows-1252; characters outside CP1252 (CJK, emoji,
        // non-Latin scripts) silently round-trip as '?', matching SQL Server's
        // default collation. EF Core surfaces this without intervention.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Eve", Code = "日本" });
        _ = context.SaveChanges();

        Assert.AreEqual("??", context.People.Select(p => p.Code).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_NVarcharAcceptsSupplementaryCharacter()
    {
        // 🎉 is one Unicode code point but two UTF-16 code units (surrogate pair).
        // It fits in nvarchar(50) — the simulator's check is on UTF-16 code units,
        // matching SQL Server's nvarchar semantics.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "🎉 party 🎉" });
        _ = context.SaveChanges();

        Assert.AreEqual("🎉 party 🎉", context.People.Select(p => p.Name).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_VarbinaryAvatarRoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0xFF };
        _ = context.People.Add(new Person { Id = 1, Name = "Frank", Avatar = bytes });
        _ = context.SaveChanges();

        var read = context.People.Select(p => p.Avatar).FirstOrDefault();
        CollectionAssert.AreEqual(bytes, read);
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
        Assert.Contains("0x", ex.InnerException.Message); // hex prefix, not string
    }

    [TestMethod]
    public void Insert_NullableAvatarAcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Hank", Avatar = null });
        _ = context.SaveChanges();

        Assert.IsNull(context.People.Select(p => p.Avatar).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Length()
    {
        // EF Core translates string.Length to CAST(LEN(x) AS int) — exercises
        // LEN's trailing-space exclusion and CAST in one query.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "Alice   " });
        _ = context.SaveChanges();

        var len = context.People.Select(p => p.Name.Length).FirstOrDefault();
        Assert.AreEqual(5, len);
    }

    [TestMethod]
    public void StringFunction_ToUpper()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "alice" });
        _ = context.SaveChanges();

        Assert.AreEqual("ALICE", context.People.Select(p => p.Name.ToUpper()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_ToLower()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "ALICE" });
        _ = context.SaveChanges();

        Assert.AreEqual("alice", context.People.Select(p => p.Name.ToLower()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Trim()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "  bob  " });
        _ = context.SaveChanges();

        Assert.AreEqual("bob", context.People.Select(p => p.Name.Trim()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_TrimStart()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "  bob" });
        _ = context.SaveChanges();

        Assert.AreEqual("bob", context.People.Select(p => p.Name.TrimStart()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_TrimEnd()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "bob  " });
        _ = context.SaveChanges();

        Assert.AreEqual("bob", context.People.Select(p => p.Name.TrimEnd()).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Substring()
    {
        // C# Substring(start, length) is 0-indexed; EF Core's translation to
        // T-SQL SUBSTRING (1-indexed) handles the off-by-one.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "alphabet" });
        _ = context.SaveChanges();

        Assert.AreEqual("lpha", context.People.Select(p => p.Name.Substring(1, 4)).FirstOrDefault());
    }

    [TestMethod]
    public void StringFunction_Replace()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "hello" });
        _ = context.SaveChanges();

        Assert.AreEqual("heLLo", context.People.Select(p => p.Name.Replace("l", "L")).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_FixedLengthChar_RoundTripsWithPadding()
    {
        // char(5) is the simulator's fixed-length CP1252 type. EF Core maps
        // strings to it via Column(TypeName="char(5)"); SaveChanges goes through
        // SqlServerStringTypeMapping which sets SqlDbType.Char on the parameter.
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Iris", Tag = "hi" });
        _ = context.SaveChanges();

        Assert.AreEqual("hi   ", context.People.Select(p => p.Tag).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_FixedLengthNChar_RoundTripsWithPadding()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Jane", Initials = "JD" });
        _ = context.SaveChanges();

        Assert.AreEqual("JD ", context.People.Select(p => p.Initials).FirstOrDefault());
    }

    [TestMethod]
    public void Insert_FixedLengthBinary_RoundTripsWithZeroPadding()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        _ = context.People.Add(new Person { Id = 1, Name = "Karl", Stamp = [0xCA, 0xFE] });
        _ = context.SaveChanges();

        var read = context.People.Select(p => p.Stamp).FirstOrDefault();
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE, 0, 0 }, read);
    }

    [TestMethod]
    public void Insert_MultipleRows_RoundTripsBothColumns()
    {
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());

        context.People.AddRange(
            new Person { Id = 1, Name = "Alice", Code = "A" },
            new Person { Id = 2, Name = "Bob", Code = "B" },
            new Person { Id = 3, Name = "Carol", Code = null });
        _ = context.SaveChanges();

        var names = context.People.Select(p => p.Name).ToArray();
        var codes = context.People.Select(p => p.Code).ToArray();

        CollectionAssert.AreEquivalent(new[] { "Alice", "Bob", "Carol" }, names);
        CollectionAssert.AreEquivalent(new[] { "A", "B", null }, codes);
    }

    [TestMethod]
    public void StringFunction_IndexOf()
    {
        // EF Core translates .IndexOf to CHARINDEX-1 (CHARINDEX is 1-based,
        // .NET's IndexOf is 0-based; -1 conversion happens server-side).
        using var context = new TestDbContext(TestDbContext.CreatePeopleSimulation());
        _ = context.People.Add(new Person { Id = 1, Name = "hello world" });
        _ = context.SaveChanges();

        using var fresh = new TestDbContext(context.Simulation);
        var index = fresh.People.Select(p => p.Name.IndexOf("world", StringComparison.Ordinal)).Single();
        Assert.AreEqual(6, index);
    }
}
