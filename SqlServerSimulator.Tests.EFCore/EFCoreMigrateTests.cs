using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// Drives EF Core 10's <c>Database.Migrate()</c>
/// end-to-end against the simulator. <c>Migrate()</c> internally wraps its
/// work in <c>sp_getapplock '__EFMigrationsLock', 'Session', 'Exclusive'</c>
/// / <c>sp_releaseapplock</c>, so a green run is the oracle that the
/// application-lock surface satisfies EF Core's migration-locking contract —
/// creating the history table, applying the pending migration, and recording
/// it. Uses a hand-written <see cref="Migration"/> + <c>ModelSnapshot</c> pair
/// (no dotnet-ef tooling).
/// </summary>
[TestClass]
public class EFCoreMigrateTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Migrate_CreatesTable_AndSecondMigrateIsNoOp()
    {
        var simulation = new Simulation();

        using (var context = new MigrateDbContext(simulation))
        {
            // First Migrate: acquires the __EFMigrationsLock applock, creates
            // the history table + the migration's Gadgets table, records the
            // applied migration, releases the applock.
            context.Database.Migrate();
        }

        // The migrated table is real: insert and read a row back through EF.
        using (var context = new MigrateDbContext(simulation))
        {
            _ = context.Gadgets.Add(new Gadget { Name = "widget" });
            _ = context.SaveChanges();
        }

        using (var context = new MigrateDbContext(simulation))
        {
            var name = context.Gadgets.Select(g => g.Name).Single();
            Assert.AreEqual("widget", name);
        }

        // Second Migrate is a clean no-op: the migration id is already in the
        // history table, so nothing is re-applied and the row survives.
        using (var context = new MigrateDbContext(simulation))
        {
            context.Database.Migrate();
        }

        using (var context = new MigrateDbContext(simulation))
        {
            Assert.AreEqual(1, context.Gadgets.Count());
        }
    }
}

internal class Gadget
{
    public int Id { get; set; }

    [Column(TypeName = "nvarchar(50)")]
    public string Name { get; set; } = null!;
}

internal class MigrateDbContext(Simulation simulation) : DbContext
{
    private readonly Simulation simulation = simulation;

    public DbSet<Gadget> Gadgets => Set<Gadget>();

    // The hand-written ModelSnapshot isn't a byte-exact match for the
    // runtime model (EF's design-time tooling would normally keep them in
    // sync); the snapshot-vs-model diff is orthogonal to the applock path
    // Migrate() exercises, so suppress the resulting pending-changes gate.
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder
            .UseSqlServer(this.simulation.CreateDbConnection())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Gadget>();
}

[DbContext(typeof(MigrateDbContext))]
[Migration("20240101000000_InitialCreate")]
internal partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: "Gadgets",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(50)", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Gadgets", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "Gadgets");
}

[DbContext(typeof(MigrateDbContext))]
internal partial class MigrateDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.HasAnnotation("ProductVersion", "10.0.2");

        _ = modelBuilder.Entity("SqlServerSimulator.Gadget", b =>
        {
            _ = b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("int");

            _ = b.HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn);

            _ = b.Property<string>("Name")
                .IsRequired()
                .HasColumnType("nvarchar(50)");

            _ = b.HasKey("Id");

            _ = b.ToTable("Gadgets");
        });
    }
}
