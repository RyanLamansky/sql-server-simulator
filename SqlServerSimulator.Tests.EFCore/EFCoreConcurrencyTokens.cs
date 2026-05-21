using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's optimistic-concurrency support via
/// <c>[Timestamp]</c> rowversion columns. On every UPDATE / DELETE EF
/// includes the rowversion in the WHERE clause; if the row's stored
/// rowversion has advanced since the entity was read, the affected-rows
/// count is 0 and EF throws <see cref="DbUpdateConcurrencyException"/>.
/// EF also OUTPUTs the new rowversion so the in-memory entity tracks
/// the latest version after a successful save. Exercises the simulator's
/// rowversion auto-advance + OUTPUT-INSERTED round-trip + WHERE-on-
/// rowversion filtering.
/// </summary>
[TestClass]
public class EFCoreConcurrencyTokens
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new InventoryContext(new Simulation()));

    private sealed class Item
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Name { get; set; } = "";

        public int Quantity { get; set; }

        [Timestamp]
        public byte[] RowVersion { get; set; } = [];
    }

    private sealed class InventoryContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Item> Items => Set<Item>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Item>().Property(i => i.Id).ValueGeneratedNever();
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Items (
                    Id int not null primary key,
                    Name nvarchar(30) not null,
                    Quantity int not null,
                    RowVersion rowversion not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static InventoryContext SeededContext()
    {
        var context = new InventoryContext(CreateSimulation());
        context.Items.AddRange(
            new Item { Id = 1, Name = "widget", Quantity = 10 },
            new Item { Id = 2, Name = "gadget", Quantity = 5 });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Insert_PopulatesRowVersion()
    {
        using var context = SeededContext();
        var widget = context.Items.Single(i => i.Id == 1);
        Assert.IsNotNull(widget.RowVersion);
        Assert.AreNotEqual(0, widget.RowVersion.Length);
    }

    [TestMethod]
    public void Update_AdvancesRowVersion()
    {
        using var context = SeededContext();
        var widget = context.Items.Single(i => i.Id == 1);
        var before = widget.RowVersion;
        widget.Quantity = 20;
        _ = context.SaveChanges();
        var after = widget.RowVersion;
        CollectionAssert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void StaleUpdate_ThrowsConcurrencyException()
    {
        var simulation = CreateSimulation();
        using (var first = new InventoryContext(simulation))
        {
            _ = first.Items.Add(new Item { Id = 1, Name = "widget", Quantity = 10 });
            _ = first.SaveChanges();
        }

        using var contextA = new InventoryContext(simulation);
        using var contextB = new InventoryContext(simulation);

        var widgetA = contextA.Items.Single(i => i.Id == 1);
        var widgetB = contextB.Items.Single(i => i.Id == 1);

        widgetA.Quantity = 20;
        _ = contextA.SaveChanges();

        widgetB.Quantity = 30;
        _ = Assert.Throws<DbUpdateConcurrencyException>(() => contextB.SaveChanges());
    }

    [TestMethod]
    public void StaleDelete_ThrowsConcurrencyException()
    {
        var simulation = CreateSimulation();
        using (var first = new InventoryContext(simulation))
        {
            _ = first.Items.Add(new Item { Id = 1, Name = "widget", Quantity = 10 });
            _ = first.SaveChanges();
        }

        using var contextA = new InventoryContext(simulation);
        using var contextB = new InventoryContext(simulation);

        var widgetA = contextA.Items.Single(i => i.Id == 1);
        var widgetB = contextB.Items.Single(i => i.Id == 1);

        widgetA.Quantity = 99;
        _ = contextA.SaveChanges();

        _ = contextB.Items.Remove(widgetB);
        _ = Assert.Throws<DbUpdateConcurrencyException>(() => contextB.SaveChanges());
    }
}
