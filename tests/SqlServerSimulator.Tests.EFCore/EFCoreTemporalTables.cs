using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core 6+'s temporal-table support
/// (<c>IsTemporal()</c>). A temporal table records every row version
/// with system-managed <c>PERIOD FOR SYSTEM_TIME</c> columns and a
/// linked history table; SQL Server populates history rows on every
/// UPDATE / DELETE. EF exposes <c>TemporalAsOf</c>, <c>TemporalAll</c>,
/// <c>TemporalBetween</c> queries that emit <c>FOR SYSTEM_TIME …</c>
/// clauses. Exercises the simulator's <c>SYSTEM_VERSIONING</c> DDL +
/// history-row maintenance + <c>FOR SYSTEM_TIME</c> query surface.
/// </summary>
[TestClass]
public class EFCoreTemporalTables
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new CustomerContext(new Simulation()));

    private sealed class Customer
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Name { get; set; } = "";

        public decimal Credit { get; set; }
    }

    private sealed class CustomerContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Customer>().Property(c => c.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Customer>().Property(c => c.Credit).HasColumnType("decimal(18,2)");
            _ = modelBuilder.Entity<Customer>().ToTable("Customers", b => b.IsTemporal());
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Customers (
                    Id int not null primary key,
                    Name nvarchar(30) not null,
                    Credit decimal(18, 2) not null,
                    PeriodStart datetime2 generated always as row start hidden not null,
                    PeriodEnd datetime2 generated always as row end hidden not null,
                    period for system_time (PeriodStart, PeriodEnd)
                ) with (system_versioning = on (history_table = dbo.CustomersHistory))
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    [TestMethod]
    public void Insert_AppearsInCurrentTable()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();
        Assert.AreEqual(1, context.Customers.Count());
    }

    [TestMethod]
    public void Update_PreservesPriorVersionInHistory()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();

        // Keep the INSERT's statement time off the UPDATE's: a history row
        // whose period start equals its end has zero duration, which every
        // FOR SYSTEM_TIME form hides.
        System.Threading.Thread.Sleep(50);
        var alice = context.Customers.Single(c => c.Id == 1);
        alice.Credit = 200m;
        _ = context.SaveChanges();

        var allVersions = context.Customers
            .TemporalAll()
            .Where(c => c.Id == 1)
            .OrderBy(c => EF.Property<DateTime>(c, "PeriodStart"))
            .Select(c => c.Credit)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 100m, 200m }, allVersions);
    }

    [TestMethod]
    public void TemporalAsOf_ReturnsStateAtPointInTime()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();

        // Read the row's actual ROW START from the database — sidesteps host
        // clock granularity entirely. Windows DateTime.UtcNow's ~15.6ms tick
        // precision made the previous "DateTime.UtcNow as a midpoint" approach
        // fragile: under the wrong tick alignment T_insert and T_update could
        // both land on the same DateTime value, leaving no AS-OF window.
        var insertTime = context.Customers
            .Where(c => c.Id == 1)
            .Select(c => EF.Property<DateTime>(c, "PeriodStart"))
            .Single();

        // Sleep so the upcoming UPDATE's frozen UtcNow lands at a strictly
        // later tick than insertTime, even on coarse-tick hosts.
        System.Threading.Thread.Sleep(50);

        var alice = context.Customers.Single(c => c.Id == 1);
        alice.Credit = 999m;
        _ = context.SaveChanges();

        // Pick a time strictly inside [T_insert, T_update). T_insert + 1 tick
        // is guaranteed to satisfy ROW START <= t < ROW END for the history
        // row because T_update is at least 50ms (≥ 3 Windows ticks) later.
        var betweenTime = insertTime.AddTicks(1);
        var historical = context.Customers
            .TemporalAsOf(betweenTime)
            .Single(c => c.Id == 1);
        Assert.AreEqual(100m, historical.Credit);
    }

    [TestMethod]
    public void Delete_MovesRowToHistory()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();

        // Separate the INSERT's statement time from the DELETE's: a history
        // row whose period start equals its end has zero duration, and every
        // FOR SYSTEM_TIME form hides those.
        System.Threading.Thread.Sleep(50);
        _ = context.Customers.Remove(context.Customers.Single());
        _ = context.SaveChanges();

        Assert.AreEqual(0, context.Customers.Count());
        Assert.AreEqual(1, context.Customers.TemporalAll().Count());
    }

    /// <summary>
    /// The three range forms over one row with two versions: the original
    /// runs [insert, update), the current one [update, max). Their shared
    /// boundary is where the forms differ — BETWEEN takes the version
    /// starting there, FROM … TO doesn't, and CONTAINED IN takes only the
    /// version that ends there.
    /// </summary>
    [TestMethod]
    public void TemporalRangeForms_DifferAtTheVersionBoundary()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();

        // Read the boundaries back from the period columns rather than the
        // host clock — the same reason TemporalAsOf's test does, plus the
        // range endpoints have to be exact for the boundary claims to mean
        // anything.
        var insertTime = context.Customers.Where(c => c.Id == 1).Select(c => EF.Property<DateTime>(c, "PeriodStart")).Single();
        System.Threading.Thread.Sleep(50);

        var alice = context.Customers.Single(c => c.Id == 1);
        alice.Credit = 999m;
        _ = context.SaveChanges();
        var updateTime = context.Customers.Where(c => c.Id == 1).Select(c => EF.Property<DateTime>(c, "PeriodStart")).Single();

        static decimal[] Credits(IQueryable<Customer> query)
            => [.. query.OrderBy(c => EF.Property<DateTime>(c, "PeriodStart")).Select(c => c.Credit)];

        CollectionAssert.AreEqual(new[] { 100m, 999m }, Credits(context.Customers.TemporalBetween(insertTime, updateTime)));
        CollectionAssert.AreEqual(new[] { 100m }, Credits(context.Customers.TemporalFromTo(insertTime, updateTime)));
        CollectionAssert.AreEqual(new[] { 100m }, Credits(context.Customers.TemporalContainedIn(insertTime, updateTime)));
        // A range reaching max datetime2 contains the current version too.
        CollectionAssert.AreEqual(new[] { 100m, 999m }, Credits(context.Customers.TemporalContainedIn(insertTime, DateTime.MaxValue)));
    }
}
