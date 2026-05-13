using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's global query filters (HasQueryFilter).
/// A filter is a predicate registered on the entity type that EF prepends
/// to the WHERE clause of every query against that DbSet. The canonical
/// use is soft-delete: rows with <c>IsDeleted = 1</c> stay in the table
/// but disappear from default queries. <c>IgnoreQueryFilters()</c> opts
/// out per query. Exercises the simulator's ability to compose the
/// filter predicate with user-supplied WHERE clauses.
/// </summary>
[TestClass]
public class EFCoreQueryFilters
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Customer
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(20)")]
        public string Name { get; set; } = "";

        public bool IsDeleted { get; set; }
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
            _ = modelBuilder.Entity<Customer>().HasQueryFilter(c => !c.IsDeleted);
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
                    Name nvarchar(20) not null,
                    IsDeleted bit not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static CustomerContext SeededContext()
    {
        var context = new CustomerContext(CreateSimulation());
        context.Customers.AddRange(
            new Customer { Id = 1, Name = "alice", IsDeleted = false },
            new Customer { Id = 2, Name = "bob", IsDeleted = true },
            new Customer { Id = 3, Name = "carol", IsDeleted = false });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void DefaultQuery_AppliesFilter()
    {
        using var context = SeededContext();
        var names = context.Customers.OrderBy(c => c.Id).Select(c => c.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "alice", "carol" }, names);
    }

    [TestMethod]
    public void FilterComposesWithUserPredicate()
    {
        using var context = SeededContext();
        var names = context.Customers
            .Where(c => c.Name.StartsWith("a") || c.Name.StartsWith("b"))
            .OrderBy(c => c.Id)
            .Select(c => c.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "alice" }, names);
    }

    [TestMethod]
    public void IgnoreQueryFilters_SeesAllRows()
    {
        using var context = SeededContext();
        var names = context.Customers
            .IgnoreQueryFilters()
            .OrderBy(c => c.Id)
            .Select(c => c.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "alice", "bob", "carol" }, names);
    }

    [TestMethod]
    public void CountAppliesFilter()
    {
        using var context = SeededContext();
        Assert.AreEqual(2, context.Customers.Count());
        Assert.AreEqual(3, context.Customers.IgnoreQueryFilters().Count());
    }
}
