using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// EF Core's HiLo identity strategy (<c>.UseHiLo("seqname")</c>) issues
/// SaveChanges-time <c>SELECT NEXT VALUE FOR &lt;seq&gt;</c> calls to allocate
/// IDs in client-side batches. Coverage here boots a sequence object manually
/// (since EF migrations / EnsureCreated isn't exercised by other tests) and
/// then exercises the LINQ→SQL pipeline through SaveChanges — confirms that
/// the simulator's NEXT VALUE FOR shape works against the EF SqlServer
/// provider's emit pattern.
/// </summary>
[TestClass]
public sealed class EFCoreHiLo
{
    public TestContext TestContext { get; set; } = null!;

    private class HiLoEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
    }

    private class HiLoDbContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<HiLoEntity>()
                .Property(e => e.Id)
                .UseHiLo("HiLoSeq");
        }

        public DbSet<HiLoEntity> Items => Set<HiLoEntity>();
    }

    [TestMethod]
    public void HiLo_InsertedRows_GetSequentialIds()
    {
        var simulation = new Simulation();
        // Bootstrap the sequence + table manually (no EnsureCreated path).
        // EF's HiLo default range is 10 per allocation; the simulator allocates
        // one ID at a time, so start at 1 and let the provider's client-side
        // allocator handle batching.
        _ = simulation.ExecuteNonQuery("""
            create sequence HiLoSeq as bigint start with 1 increment by 10;
            create table Items (
                Id int not null primary key,
                Name nvarchar(50) not null
            )
            """);
        using var context = new HiLoDbContext(simulation);
        _ = context.Items.Add(new HiLoEntity { Name = "Alice" });
        _ = context.Items.Add(new HiLoEntity { Name = "Bob" });
        _ = context.Items.Add(new HiLoEntity { Name = "Charlie" });
        _ = context.SaveChanges();

        var rows = context.Items.OrderBy(e => e.Id).Select(e => new { e.Id, e.Name }).ToArray();
        Assert.HasCount(3, rows);
        // EF's HiLo allocator pulls the first sequence value (1), then assigns
        // Ids 1..N from a client-side range until N hits the increment-by
        // (10). The exact IDs depend on EF's internal allocator — assert
        // monotonicity and unique-positive instead of pinning values.
        Assert.IsGreaterThan(0, rows[0].Id);
        Assert.IsGreaterThan(rows[0].Id, rows[1].Id);
        Assert.IsGreaterThan(rows[1].Id, rows[2].Id);
        Assert.AreEqual("Alice", rows.First(r => r.Id == rows.Min(x => x.Id)).Name);
    }
}
