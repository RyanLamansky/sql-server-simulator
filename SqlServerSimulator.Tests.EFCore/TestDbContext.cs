using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

internal class TestRow
{
    public int Id { get; set; }
}

/// <summary>
/// Exercises the simulator's variable-length string and binary columns
/// through EF Core: <see cref="Name"/> maps to <c>nvarchar(50)</c>,
/// <see cref="Code"/> to <c>varchar(10)</c>, and <see cref="Avatar"/> to
/// <c>varbinary(64)</c> — covering UTF-16, CP1252, and raw-bytes paths.
/// </summary>
internal class Person
{
    public int Id { get; set; }

    [Column(TypeName = "nvarchar(50)")]
    public string Name { get; set; } = null!;

    [Column(TypeName = "varchar(10)")]
    public string? Code { get; set; }

    [Column(TypeName = "varbinary(64)")]
    public byte[]? Avatar { get; set; }
}

/// <summary>
/// Exercises the simulator's <c>datetime</c>, <c>datetime2</c>, and
/// <c>datetimeoffset</c> column support through EF Core. CLR-side properties
/// are <see cref="DateTime"/> and <see cref="DateTimeOffset"/> only;
/// <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="TimeSpan"/>,
/// <c>DateTime → date</c>, and <c>DateTime → smalldatetime</c> remain
/// unreachable through EF Core's SqlServer provider because those mappings
/// downcast <see cref="System.Data.Common.DbParameter"/> to <c>SqlParameter</c>.
/// See <see cref="SimulatedDbParameter"/> for the full compatibility matrix.
/// </summary>
internal class Event
{
    public int Id { get; set; }

    [Column(TypeName = "datetime2(7)")]
    public DateTime CreatedAt { get; set; }

    [Column(TypeName = "datetime2(3)")]
    public DateTime? Updated { get; set; }

    [Column(TypeName = "datetimeoffset(7)")]
    public DateTimeOffset OccurredAt { get; set; }

    [Column(TypeName = "datetimeoffset(3)")]
    public DateTimeOffset? Cancelled { get; set; }

    /// <remarks>
    /// Nullable to keep <see cref="DateTime"/>'s default of <c>0001-01-01</c>
    /// from blowing up legacy <c>datetime</c>'s 1753-9999 range when the
    /// existing event tests omit it.
    /// </remarks>
    [Column(TypeName = "datetime")]
    public DateTime? Started { get; set; }

    [Column(TypeName = "datetime")]
    public DateTime? Ended { get; set; }

}

/// <summary>
/// Exercises the simulator's <c>uniqueidentifier</c> column support through
/// EF Core. <see cref="ExternalKey"/> is the natural-key identifier (a
/// freshly minted <see cref="Guid"/>); <see cref="OptionalKey"/> covers the
/// nullable column path. Pinning <see cref="Id"/> on a separate
/// <see cref="int"/> primary key keeps EF's tracker out of the
/// uniqueidentifier-comparison story so the tests focus on the type itself.
/// </summary>
internal class Document
{
    public int Id { get; set; }

    [Column(TypeName = "uniqueidentifier")]
    public Guid ExternalKey { get; set; }

    [Column(TypeName = "uniqueidentifier")]
    public Guid? OptionalKey { get; set; }
}

/// <summary>
/// Exercises the simulator's <c>decimal(p, s)</c> column support through
/// EF Core. <see cref="Price"/> uses <c>decimal(10, 2)</c> for a typical
/// price scenario; <see cref="Discount"/> covers the nullable path.
/// </summary>
internal class Product
{
    public int Id { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal Price { get; set; }

    [Column(TypeName = "decimal(5, 4)")]
    public decimal? Discount { get; set; }
}

internal class TestDbContext(Simulation simulation) : DbContext
{
    public Simulation Simulation { get; set; } = simulation;

    public TestDbContext(params ReadOnlySpan<int> values)
        : this(CreateDefaultSimulation(values))
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
    }

    public DbSet<TestRow> Rows => Set<TestRow>();

    public DbSet<Person> People => Set<Person>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<Product> Products => Set<Product>();

    public static Simulation CreateDefaultSimulation(params ReadOnlySpan<int> values)
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("create table Rows ( Id int )")
            .ExecuteNonQuery();

        if (values.Length != 0)
        {
            using var context = new TestDbContext(simulation);
            foreach (var value in values)
            {
                var row = new TestRow { Id = value };
                _ = context.Rows.Add(row);
            }
            _ = context.SaveChanges();
        }

        return simulation;
    }

    public static Simulation CreatePeopleSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table People (
                    Id int,
                    Name nvarchar(50) not null,
                    Code varchar(10) null,
                    Avatar varbinary(64) null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateDocumentsSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Documents (
                    Id int,
                    ExternalKey uniqueidentifier not null,
                    OptionalKey uniqueidentifier null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateProductsSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Products (
                    Id int,
                    Price decimal(10, 2) not null,
                    Discount decimal(5, 4) null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateEventsSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Events (
                    Id int,
                    CreatedAt datetime2(7) not null,
                    Updated datetime2(3) null,
                    OccurredAt datetimeoffset(7) not null,
                    Cancelled datetimeoffset(3) null,
                    Started datetime null,
                    Ended datetime null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }
}
