using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end test for EF Core's <c>HasDbFunction</c> mapped to a
/// multi-statement TVF. The DbContext registers an
/// <see cref="IQueryable{T}"/>-returning method (<see cref="DocumentContext.OwnedBy"/>);
/// EF Core's SqlServer provider translates the LINQ call to
/// <c>SELECT ... FROM dbo.OwnedBy(@p)</c>, which hits the simulator's
/// MS-TVF dispatch path end-to-end. Validates the primary EF Core
/// integration surface for this feature.
/// </summary>
[TestClass]
public sealed class EFCoreMultiStatementTvf
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new DocumentContext(new Simulation()));

    private sealed class Doc
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public string Title { get; set; } = "";
    }

    private sealed class DocumentContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Doc> Docs => Set<Doc>();

        public IQueryable<Doc> OwnedBy(int ownerId) =>
            this.FromExpression(() => OwnedBy(ownerId));

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Doc>().ToTable("Docs");
            _ = modelBuilder.Entity<Doc>().Property(d => d.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Doc>().Property(d => d.Title).HasColumnType("nvarchar(50)");
            _ = modelBuilder.HasDbFunction(
                typeof(DocumentContext).GetMethod(nameof(OwnedBy), [typeof(int)])!)
                .HasName("OwnedBy");
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table Docs (
                Id int not null primary key,
                OwnerId int not null,
                Title nvarchar(50) not null
            )
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create function dbo.OwnedBy(@oid int)
            returns @r table (Id int not null, OwnerId int not null, Title nvarchar(50) not null)
            as
            begin
                insert into @r select Id, OwnerId, Title from dbo.Docs where OwnerId = @oid;
                return;
            end
            """).ExecuteNonQuery();
        return simulation;
    }

    [TestMethod]
    public void HasDbFunction_MapsToMultiStatementTvf()
    {
        using var context = new DocumentContext(CreateSimulation());
        context.Docs.AddRange(
            new Doc { Id = 1, OwnerId = 100, Title = "first" },
            new Doc { Id = 2, OwnerId = 100, Title = "second" },
            new Doc { Id = 3, OwnerId = 200, Title = "third" });
        _ = context.SaveChanges();

        var titles = context.OwnedBy(100)
            .OrderBy(d => d.Id)
            .Select(d => d.Title)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "first", "second" }, titles);
    }

    [TestMethod]
    public void HasDbFunction_FilterAndOrderTranslate()
    {
        // EF Core composes WHERE / ORDER BY over the TVF call client-side
        // (TVF body returns rows, the simulator's outer SELECT applies the
        // filter). This is the practical use case for HasDbFunction.
        using var context = new DocumentContext(CreateSimulation());
        context.Docs.AddRange(
            new Doc { Id = 1, OwnerId = 100, Title = "alpha" },
            new Doc { Id = 2, OwnerId = 100, Title = "beta" },
            new Doc { Id = 3, OwnerId = 100, Title = "gamma" });
        _ = context.SaveChanges();

        var afterBeta = context.OwnedBy(100)
            .Where(d => d.Title.CompareTo("beta") > 0)
            .OrderBy(d => d.Title)
            .Select(d => d.Title)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "gamma" }, afterBeta);
    }
}
