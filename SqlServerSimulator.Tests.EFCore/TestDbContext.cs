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

    [Column(TypeName = "char(5)")]
    public string? Tag { get; set; }

    [Column(TypeName = "nchar(3)")]
    public string? Initials { get; set; }

    [Column(TypeName = "binary(4)")]
    public byte[]? Stamp { get; set; }
}

/// <summary>
/// Exercises the simulator's <c>datetime</c>, <c>datetime2</c>, and
/// <c>datetimeoffset</c> column support through EF Core. The narrowed
/// date/time pairs (<c>date</c>, <c>smalldatetime</c>, <c>time</c>,
/// <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="TimeSpan"/>)
/// require the SqlServerSimulator EF Core adapter and are covered on a
/// separate entity by <see cref="AdapterTestDbContext"/>.
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

/// <summary>
/// Exercises the simulator's <c>nvarchar(MAX)</c> / <c>varchar(MAX)</c> /
/// <c>varbinary(MAX)</c> support through EF Core — the LOB-eligible MAX
/// siblings of the bounded var types. EF Core's default mapping for an
/// unannotated <c>string</c> property is <c>nvarchar(max)</c>, so this
/// entity is also a regression check for the simulator handling that
/// default without a length annotation.
/// </summary>
internal class Article
{
    public int Id { get; set; }

    // Default EF mapping for string is nvarchar(max).
    public string Body { get; set; } = null!;

    [Column(TypeName = "varchar(max)")]
    public string? Summary { get; set; }

    [Column(TypeName = "varbinary(max)")]
    public byte[]? Attachment { get; set; }
}

/// <summary>
/// Exercises the simulator's <c>IDENTITY</c> column support through EF Core.
/// EF Core defaults int primary keys to <c>ValueGeneratedOnAdd</c>; with the
/// SqlServer provider this means the column is created with <c>IDENTITY(1, 1)</c>
/// and SaveChanges expects the database to generate the key on insert.
/// </summary>
internal class Widget
{
    public int Id { get; set; }

    [Column(TypeName = "nvarchar(50)")]
    public string Name { get; set; } = null!;
}

/// <summary>
/// Exercises the simulator's <c>NEWSEQUENTIALID()</c> default-clause support
/// through EF Core. <see cref="Id"/> is wired in <c>OnModelCreating</c> with
/// <c>HasDefaultValueSql("newsequentialid()")</c> so EF Core defers GUID
/// generation to the server (otherwise the SqlServer convention installs
/// <c>SequentialGuidValueGenerator</c> client-side and the simulator's
/// default expression never fires).
/// </summary>
internal class Token
{
    [Column(TypeName = "uniqueidentifier")]
    public Guid Id { get; set; }

    [Column(TypeName = "nvarchar(50)")]
    public string Label { get; set; } = null!;
}

/// <summary>
/// Exercises the simulator's computed-column support through EF Core. The
/// <see cref="Total"/> property is wired in <c>OnModelCreating</c> with
/// <c>HasComputedColumnSql</c>, which sets <see cref="DatabaseGeneratedOption.Computed"/>:
/// EF Core omits the column from INSERTs and recovers the server-assigned
/// value through <c>OUTPUT INSERTED.Total</c> on SaveChanges.
/// </summary>
internal class Receipt
{
    public int Id { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal Tax { get; set; }

    [Column(TypeName = "decimal(10, 2)")]
    public decimal Total { get; set; }
}

/// <summary>
/// Exercises the simulator's PRIMARY KEY constraint through EF Core.
/// <see cref="Sku"/> is the caller-supplied PK; SaveChanges round-trips
/// through a normal INSERT. A duplicate-key insert surfaces as a wrapped
/// <see cref="DbUpdateException"/> whose
/// inner exception is the simulator's Msg 2627.
/// </summary>
internal class Inventory
{
    [Column(TypeName = "nvarchar(50)")]
    public string Sku { get; set; } = null!;

    public int Quantity { get; set; }
}

/// <summary>
/// Counterpart to <see cref="Widget"/> with <see cref="DatabaseGeneratedOption.None"/>
/// — EF Core treats <see cref="Id"/> as caller-supplied, so SaveChanges
/// emits a plain INSERT (no <c>OUTPUT INSERTED</c>). Combined with
/// <c>SET IDENTITY_INSERT Stickers ON</c>, the path exercises the
/// simulator's identity-column INSERT through EF Core.
/// </summary>
internal class Sticker
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; }

    [Column(TypeName = "nvarchar(20)")]
    public string Tag { get; set; } = null!;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Pin the Token.Id GUID to the server-side NEWSEQUENTIALID() default.
        // Without this, the SqlServer EF convention installs
        // SequentialGuidValueGenerator on the client and the database default
        // never fires.
        _ = modelBuilder.Entity<Token>()
            .Property(t => t.Id)
            .HasDefaultValueSql("newsequentialid()");

        // Pin the Receipt.Total decimal as a server-computed column. EF Core
        // recognizes this as DatabaseGeneratedOption.Computed and stops
        // including the column in INSERTs; SaveChanges reads the value back
        // via OUTPUT INSERTED.Total.
        _ = modelBuilder.Entity<Receipt>()
            .Property(r => r.Total)
            .HasComputedColumnSql("[Subtotal] + [Tax]", stored: false);

        // Pin Inventory's caller-supplied string PK so EF Core doesn't try
        // to generate values for it.
        _ = modelBuilder.Entity<Inventory>().HasKey(i => i.Sku);
    }

    public DbSet<TestRow> Rows => Set<TestRow>();

    public DbSet<Person> People => Set<Person>();

    public DbSet<Event> Events => Set<Event>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Article> Articles => Set<Article>();

    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<Token> Tokens => Set<Token>();

    public DbSet<Sticker> Stickers => Set<Sticker>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<Inventory> Inventory => Set<Inventory>();

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
                    Avatar varbinary(64) null,
                    Tag char(5) null,
                    Initials nchar(3) null,
                    Stamp binary(4) null
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

    public static Simulation CreateArticlesSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Articles (
                    Id int,
                    Body nvarchar(max) not null,
                    Summary varchar(max) null,
                    Attachment varbinary(max) null
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

    public static Simulation CreateWidgetsSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Widgets (
                    Id int identity(1, 1) not null,
                    Name nvarchar(50) not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateTokensSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Tokens (
                    Id uniqueidentifier not null default newsequentialid(),
                    Label nvarchar(50) not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateReceiptsSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Receipts (
                    Id int identity(1, 1) not null,
                    Subtotal decimal(10, 2) not null,
                    Tax decimal(10, 2) not null,
                    Total as Subtotal + Tax
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateInventorySimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Inventory (
                    Sku nvarchar(50) not null constraint pk_inventory primary key,
                    Quantity int not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    public static Simulation CreateStickersSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Stickers (
                    Id int identity(1, 1) not null,
                    Tag nvarchar(20) not null
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
