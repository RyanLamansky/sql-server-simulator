using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// EF Core's trigger-aware emit surface (<c>ToTable(b =&gt;
/// b.HasTrigger("name"))</c>, introduced in EF Core 7). The annotation
/// tells EF a trigger exists on the entity's table so the provider
/// switches from the default <c>OUTPUT INSERTED</c> SaveChanges shape
/// to a trigger-compatible round-trip (in SQL Server triggers can
/// interfere with direct <c>OUTPUT</c> from INSERT/UPDATE/DELETE). The
/// fixture locks down compatibility with that annotation across EF Core
/// upgrades — if a future EF version changes the trigger-safe emit
/// shape to something the simulator doesn't support yet, these tests
/// catch it. Database-defined trigger behavior itself is covered by
/// <c>TriggerTests</c> in the main test project.
/// </summary>
[TestClass]
public sealed class EFCoreTriggers
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new TriggerDbContext(new Simulation()));

    private class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = null!;
        public int Price { get; set; }
    }

    private class TriggerDbContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Product>().ToTable("Products", t => t.HasTrigger("tr_product_audit"));
        }

        public DbSet<Product> Products => Set<Product>();
    }

    [TestMethod]
    public void HasTrigger_Insert_FiresAndAuditPopulated()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table Products (
                Id int identity(1,1) primary key,
                Name nvarchar(50) not null,
                Price int not null
            );
            create table ProductAudit (
                ProductId int not null,
                NewName nvarchar(50) not null,
                NewPrice int not null
            );
            """,
            """
            create trigger tr_product_audit on Products after insert
            as
                insert ProductAudit(ProductId, NewName, NewPrice)
                select Id, Name, Price from inserted
            """);
        using var context = new TriggerDbContext(simulation);
        _ = context.Products.Add(new Product { Name = "Widget", Price = 100 });
        _ = context.Products.Add(new Product { Name = "Gadget", Price = 200 });
        _ = context.SaveChanges();

        // The HasTrigger annotation switches EF's emit shape; whatever
        // shape it produces must still flow through the simulator's
        // trigger dispatch and populate the audit table.
        using var auditConn = simulation.CreateOpenConnection();
        using var reader = auditConn
            .CreateCommand("select ProductId, NewName, NewPrice from ProductAudit order by ProductId")
            .ExecuteReader();
        var audit = new List<(int Id, string Name, int Price)>();
        while (reader.Read())
            audit.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        Assert.HasCount(2, audit);
        Assert.AreEqual("Widget", audit[0].Name);
        Assert.AreEqual(100, audit[0].Price);
        Assert.AreEqual("Gadget", audit[1].Name);
        Assert.AreEqual(200, audit[1].Price);
    }

    [TestMethod]
    public void HasTrigger_SaveChanges_RetrievesGeneratedIdentity()
    {
        // The trigger-compatible SaveChanges shape still has to return
        // generated identity values to the EF entity. Verify the
        // round-trip — EF reads Id back from the database after the
        // trigger fires, and the in-memory entity reflects it.
        //
        // The body must not SELECT. A trigger body's result sets are the
        // firing statement's on real SQL Server, so a body of `select 1`
        // interleaves an extra result set with EF's identity reads and
        // breaks SaveChanges there too — verified against SQL Server 2025,
        // which returns four result sets for this two-entity batch. The
        // fixture used to do exactly that and passed only because the
        // simulator dropped body result sets.
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table Products (
                Id int identity(1,1) primary key,
                Name nvarchar(50) not null,
                Price int not null
            )
            """,
            """
            create trigger tr_product_audit on Products after insert
            as
                set nocount on
            """);
        using var context = new TriggerDbContext(simulation);
        var p1 = new Product { Name = "First", Price = 10 };
        var p2 = new Product { Name = "Second", Price = 20 };
        _ = context.Products.Add(p1);
        _ = context.Products.Add(p2);
        _ = context.SaveChanges();

        Assert.IsGreaterThan(0, p1.Id);
        Assert.IsGreaterThan(p1.Id, p2.Id);
    }
}
