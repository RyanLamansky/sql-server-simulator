using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's owned-entity surface (OwnsOne / OwnsMany).
/// <c>OwnsOne</c> stores the value-object's properties in the parent's
/// table with the navigation name as a column prefix (e.g.
/// <c>ShippingAddress_Street</c>). <c>OwnsMany</c> maps the collection to
/// a separate child table with a shadow FK back to the parent and a
/// composite PK. The materializer hydrates the value object inline; LINQ
/// can navigate into owned properties in predicates and projections.
/// </summary>
[TestClass]
public class EFCoreOwnedEntities
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new CustomerContext(new Simulation()));

    private sealed class Address
    {
        [Column(TypeName = "nvarchar(60)")]
        public string Street { get; set; } = "";

        [Column(TypeName = "nvarchar(30)")]
        public string City { get; set; } = "";
    }

    private sealed class Phone
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Number { get; set; } = "";

        [Column(TypeName = "nvarchar(15)")]
        public string Kind { get; set; } = "";
    }

    private sealed class Customer
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Name { get; set; } = "";

        public Address ShippingAddress { get; set; } = new();

        public List<Phone> Phones { get; set; } = [];
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
            _ = modelBuilder.Entity<Customer>().OwnsOne(c => c.ShippingAddress);
            _ = modelBuilder.Entity<Customer>().OwnsMany(c => c.Phones, p =>
            {
                _ = p.WithOwner().HasForeignKey("CustomerId");
                _ = p.Property(x => x.Id).ValueGeneratedNever();
                _ = p.HasKey("CustomerId", "Id");
            });
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
                    ShippingAddress_Street nvarchar(60) null,
                    ShippingAddress_City nvarchar(30) null
                );
                create table Phone (
                    CustomerId int not null,
                    Id int not null,
                    Number nvarchar(30) not null,
                    Kind nvarchar(15) not null,
                    primary key (CustomerId, Id)
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static CustomerContext SeededContext()
    {
        var context = new CustomerContext(CreateSimulation());
        context.Customers.AddRange(
            new Customer
            {
                Id = 1,
                Name = "Alice",
                ShippingAddress = new Address { Street = "10 Main St", City = "Springfield" },
                Phones =
                {
                    new Phone { Id = 1, Number = "555-0001", Kind = "mobile" },
                    new Phone { Id = 2, Number = "555-0002", Kind = "home" },
                },
            },
            new Customer
            {
                Id = 2,
                Name = "Bob",
                ShippingAddress = new Address { Street = "20 Oak Ave", City = "Riverside" },
                Phones = { new Phone { Id = 1, Number = "555-1000", Kind = "mobile" } },
            });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void OwnsOne_HydratesValueObjectInline()
    {
        using var context = SeededContext();
        var alice = context.Customers.Single(c => c.Id == 1);
        Assert.AreEqual("10 Main St", alice.ShippingAddress.Street);
        Assert.AreEqual("Springfield", alice.ShippingAddress.City);
    }

    [TestMethod]
    public void OwnsOne_FilterOnOwnedProperty()
    {
        using var context = SeededContext();
        var names = context.Customers
            .Where(c => c.ShippingAddress.City == "Riverside")
            .Select(c => c.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Bob" }, names);
    }

    [TestMethod]
    public void OwnsOne_ProjectionOnOwnedProperty()
    {
        using var context = SeededContext();
        var cities = context.Customers
            .OrderBy(c => c.Id)
            .Select(c => c.ShippingAddress.City)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Springfield", "Riverside" }, cities);
    }

    [TestMethod]
    public void OwnsMany_LoadsCollectionViaInclude()
    {
        using var context = SeededContext();
        var alice = context.Customers
            .Include(c => c.Phones)
            .Single(c => c.Id == 1);
        Assert.HasCount(2, alice.Phones);
        CollectionAssert.AreEquivalent(
            new[] { "555-0001", "555-0002" },
            alice.Phones.Select(p => p.Number).ToArray());
    }

    [TestMethod]
    public void OwnsMany_UpdateChildAndPersist()
    {
        using var context = SeededContext();
        var alice = context.Customers.Include(c => c.Phones).Single(c => c.Id == 1);
        alice.Phones.First(p => p.Kind == "home").Number = "555-9999";
        _ = context.SaveChanges();

        using var refresh = new CustomerContext(context.Simulation);
        var updated = refresh.Customers.Include(c => c.Phones).Single(c => c.Id == 1);
        Assert.AreEqual("555-9999", updated.Phones.Single(p => p.Kind == "home").Number);
    }
}
