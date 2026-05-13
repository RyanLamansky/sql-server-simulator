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
                    ValidFrom datetime2 generated always as row start hidden not null,
                    ValidTo datetime2 generated always as row end hidden not null,
                    period for system_time (ValidFrom, ValidTo)
                ) with (system_versioning = on (history_table = dbo.CustomersHistory))
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    [TestMethod]
    [Ignore("Needs: temporal table DDL")]
    public void Insert_AppearsInCurrentTable()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();
        Assert.AreEqual(1, context.Customers.Count());
    }

    [TestMethod]
    [Ignore("Needs: temporal table DDL + history tracking")]
    public void Update_PreservesPriorVersionInHistory()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();

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
    [Ignore("Needs: temporal table DDL + FOR SYSTEM_TIME queries")]
    public void TemporalAsOf_ReturnsStateAtPointInTime()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();

        var beforeUpdate = DateTime.UtcNow.AddMilliseconds(-1);
        System.Threading.Thread.Sleep(50);

        var alice = context.Customers.Single(c => c.Id == 1);
        alice.Credit = 999m;
        _ = context.SaveChanges();

        var historical = context.Customers
            .TemporalAsOf(beforeUpdate)
            .Single(c => c.Id == 1);
        Assert.AreEqual(100m, historical.Credit);
    }

    [TestMethod]
    [Ignore("Needs: temporal table DDL + history tracking")]
    public void Delete_MovesRowToHistory()
    {
        using var context = new CustomerContext(CreateSimulation());
        _ = context.Customers.Add(new Customer { Id = 1, Name = "alice", Credit = 100m });
        _ = context.SaveChanges();

        _ = context.Customers.Remove(context.Customers.Single());
        _ = context.SaveChanges();

        Assert.AreEqual(0, context.Customers.Count());
        Assert.AreEqual(1, context.Customers.TemporalAll().Count());
    }
}
