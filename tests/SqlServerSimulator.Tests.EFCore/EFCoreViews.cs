using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// Exercises EF Core's <c>ToView()</c> mapping against the simulator's
/// view machinery — the canonical EF-side use case for read-only views.
/// Maps a keyless entity to a CREATE VIEW-produced view and verifies
/// LINQ queries flow through the simulator's view-resolution path
/// (re-parses + executes the body per call).
/// </summary>
[TestClass]
public sealed class EFCoreViews
{
    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new ViewDbContext(new Simulation()));

    private static ViewDbContext WithOrderSummary()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table Orders (Id int primary key, Customer varchar(50), Amount decimal(10,2));
            insert Orders values (1, 'Alice', 100.00), (2, 'Bob', 250.00), (3, 'Alice', 50.00);
            """,
            "create view OrderSummary as select Customer, sum(Amount) as TotalAmount, count(*) as OrderCount from Orders group by Customer");
        return new ViewDbContext(simulation);
    }

    [TestMethod]
    public void ToView_BasicQuery_ReturnsAggregatedRows()
    {
        using var context = WithOrderSummary();
        var summaries = context.OrderSummaries.OrderBy(s => s.Customer).ToList();
        Assert.HasCount(2, summaries);
        Assert.AreEqual(("Alice", 150.00m, 2), (summaries[0].Customer, summaries[0].TotalAmount, summaries[0].OrderCount));
        Assert.AreEqual(("Bob", 250.00m, 1), (summaries[1].Customer, summaries[1].TotalAmount, summaries[1].OrderCount));
    }

    [TestMethod]
    public void ToView_WithFilter_PushesPredicateThroughView()
    {
        using var context = WithOrderSummary();
        var bigSpenders = context.OrderSummaries
            .Where(s => s.TotalAmount > 100m)
            .OrderBy(s => s.Customer)
            .Select(s => s.Customer)
            .ToList();
        CollectionAssert.AreEqual(new[] { "Alice", "Bob" }, bigSpenders);
    }

    /// <summary>
    /// A view column the LINQ query never projects is never computed, so its
    /// divide-by-zero (Alice's two orders) doesn't fail a query reading the
    /// other columns, as on SQL Server 2025 (probed 2026-10-17).
    /// </summary>
    [TestMethod]
    public void ToView_ColumnTheQueryNeverReads_IsNeverComputed()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table Orders (Id int primary key, Customer varchar(50), Amount decimal(10,2));
            insert Orders values (1, 'Alice', 100.00), (2, 'Bob', 250.00), (3, 'Alice', 50.00);
            """,
            "create view OrderSummary as select Customer, sum(Amount) / (count(*) - 2) as TotalAmount, count(*) as OrderCount from Orders group by Customer");
        using var context = new ViewDbContext(simulation);
        CollectionAssert.AreEqual(new[] { "Alice", "Bob" }, context.OrderSummaries.OrderBy(s => s.Customer).Select(s => s.Customer).ToList());
        Assert.AreEqual(2, context.OrderSummaries.Count());
        Assert.AreEqual(3, context.OrderSummaries.Sum(s => s.OrderCount));
    }

    /// <summary>
    /// A view column the query reads is evaluated where the query uses it, so
    /// the filter LINQ's <c>Where</c> emits and the <c>CASE</c> its ternary
    /// emits keep the row whose column divides by zero from computing it, as
    /// on SQL Server 2025 (probed 2026-10-17).
    /// </summary>
    [TestMethod]
    public void ToView_ColumnTheQueryReads_IsEvaluatedWhereTheQueryUsesIt()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table Orders (Id int primary key, Customer varchar(50), Amount decimal(10,2));
            insert Orders values (1, 'Alice', 100.00), (2, 'Bob', 250.00), (3, 'Alice', 50.00);
            """,
            "create view OrderSummary as select Customer, 100.00 / (Id - 2) as TotalAmount, Id as OrderCount from Orders");
        using var context = new ViewDbContext(simulation);
        CollectionAssert.AreEqual(new[] { -100m, 100m }, context.OrderSummaries.Where(s => s.OrderCount != 2).OrderBy(s => s.OrderCount).Select(s => s.TotalAmount).ToList());
        CollectionAssert.AreEqual(new[] { -100m, 0m, 100m }, context.OrderSummaries.OrderBy(s => s.OrderCount).Select(s => s.OrderCount == 2 ? 0m : s.TotalAmount).ToList());
    }
}

/// <summary>
/// Keyless entity mapped to the <c>OrderSummary</c> view via
/// <c>ToView</c>. EF Core treats <c>HasNoKey</c> + <c>ToView</c> as a
/// read-only projection — SaveChanges won't try to write back, matching
/// the simulator's read-only-views-only stance.
/// </summary>
internal class OrderSummary
{
    [Column(TypeName = "varchar(50)")]
    public string Customer { get; set; } = "";

    [Column(TypeName = "decimal(10,2)")]
    public decimal TotalAmount { get; set; }

    public int OrderCount { get; set; }
}

internal class ViewDbContext(Simulation simulation) : DbContext
{
    public Simulation Simulation { get; set; } = simulation;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        _ = modelBuilder.Entity<OrderSummary>()
            .HasNoKey()
            .ToView("OrderSummary");
    }

    public DbSet<OrderSummary> OrderSummaries => Set<OrderSummary>();
}
