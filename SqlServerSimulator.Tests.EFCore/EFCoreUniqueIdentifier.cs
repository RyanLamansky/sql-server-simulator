namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's <c>uniqueidentifier</c> column support through
/// EF Core's idiomatic surface — Guid round-trips, nullable handling, WHERE
/// filtering by Guid, and ordering. EF maps <see cref="Guid"/> to
/// <c>uniqueidentifier</c> with <c>SqlDbType.UniqueIdentifier</c> as the
/// default, so unlike the <c>DateOnly</c> / <c>TimeOnly</c> mappings, this
/// path doesn't trigger the <c>SqlParameter</c>-downcast incompatibility
/// noted on <see cref="SimulatedDbParameter"/>.
/// </summary>
[TestClass]
public class EFCoreUniqueIdentifier
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Insert_Guid_RoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreateDocumentsSimulation());

        var key = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899");
        _ = context.Documents.Add(new Document { Id = 1, ExternalKey = key });
        _ = context.SaveChanges();

        Assert.AreEqual(key, context.Documents.Select(d => d.ExternalKey).First());
    }

    [TestMethod]
    public async Task InsertAsync_Guid_RoundTrips()
    {
        await using var context = new TestDbContext(TestDbContext.CreateDocumentsSimulation());

        var key = Guid.NewGuid();
        _ = context.Documents.Add(new Document { Id = 1, ExternalKey = key });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(key, context.Documents.Select(d => d.ExternalKey).First());
    }

    [TestMethod]
    public void Insert_NullableGuid_AcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreateDocumentsSimulation());
        _ = context.Documents.Add(new Document { Id = 1, ExternalKey = Guid.NewGuid() });
        _ = context.SaveChanges();

        Assert.IsNull(context.Documents.Select(d => d.OptionalKey).First());
    }

    [TestMethod]
    public void Insert_NullableGuid_AcceptsValue()
    {
        using var context = new TestDbContext(TestDbContext.CreateDocumentsSimulation());
        var optional = Guid.Parse("00000000-0000-0000-0000-000000000001");
        _ = context.Documents.Add(new Document { Id = 1, ExternalKey = Guid.NewGuid(), OptionalKey = optional });
        _ = context.SaveChanges();

        Assert.AreEqual(optional, context.Documents.Select(d => d.OptionalKey).First());
    }

    [TestMethod]
    public void Where_FiltersByGuidEquality()
    {
        using var context = new TestDbContext(TestDbContext.CreateDocumentsSimulation());

        var target = Guid.Parse("aabbccdd-eeff-0011-2233-445566778899");
        context.Documents.AddRange(
            new Document { Id = 1, ExternalKey = Guid.Parse("11111111-1111-1111-1111-111111111111") },
            new Document { Id = 2, ExternalKey = target },
            new Document { Id = 3, ExternalKey = Guid.Parse("22222222-2222-2222-2222-222222222222") });
        _ = context.SaveChanges();

        var match = context.Documents.Where(d => d.ExternalKey == target).Select(d => d.Id).Single();
        Assert.AreEqual(2, match);
    }

    [TestMethod]
    public void OrderBy_Guid_UsesSqlServerSortOrder()
    {
        // The Where filter on parameterized Guid above goes through EF's
        // SqlParameter machinery (no string promotion). This test pins the
        // server-side ORDER BY behavior — bytes 10..15 most significant,
        // matching real SQL Server's quirky uniqueidentifier sort.
        using var context = new TestDbContext(TestDbContext.CreateDocumentsSimulation());

        Guid[] ids =
        [
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-010000000000"),
            Guid.Parse("00000001-0000-0000-0000-000000000000"),
        ];
        for (var i = 0; i < ids.Length; i++)
            _ = context.Documents.Add(new Document { Id = i + 1, ExternalKey = ids[i] });
        _ = context.SaveChanges();

        var ordered = context.Documents.OrderBy(d => d.ExternalKey).Select(d => d.ExternalKey).ToArray();

        // Expected per SQL Server's uid sort: byte 10..15 dominates, so
        // 00000001-... (byte 0 = 0x01, all top bytes 0) ranks before
        // 00000000-...-000000000001 (byte 15 = 0x01) which ranks before
        // 00000000-...-010000000000 (byte 10 = 0x01).
        CollectionAssert.AreEqual(
            new[]
            {
                Guid.Parse("00000001-0000-0000-0000-000000000000"),
                Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Guid.Parse("00000000-0000-0000-0000-010000000000"),
            },
            ordered);
    }
}
