using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's Table-Per-Concrete-type (TPC) inheritance
/// mapping. TPC stores each concrete entity in its own table and EF Core
/// queries the base set by emitting <c>UNION ALL</c> across all concrete
/// tables, wrapped in a derived table that's filtered / projected by the
/// outer query. Exercises the simulator's UNION-inside-subquery support
/// against the concrete shape EF Core 7+'s headline inheritance strategy
/// emits.
/// </summary>
[TestClass]
public class EFCoreInheritanceTpc
{
    public TestContext TestContext { get; set; } = null!;

    private abstract class Pet
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(50)")]
        public string Name { get; set; } = "";
    }

    private sealed class Dog : Pet
    {
        public int BarkVolume { get; set; }
    }

    private sealed class Cat : Pet
    {
        public bool Purrs { get; set; }
    }

    private sealed class PetContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Pet> Pets => Set<Pet>();
        public DbSet<Dog> Dogs => Set<Dog>();
        public DbSet<Cat> Cats => Set<Cat>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Caller-supplied PKs keep the test off the sequence path the
            // simulator doesn't model (EF Core's TPC default is HiLo via a
            // shared sequence, which would need a sequence object).
            _ = modelBuilder.Entity<Pet>()
                .UseTpcMappingStrategy()
                .Property(p => p.Id)
                .ValueGeneratedNever();
            _ = modelBuilder.Entity<Dog>().ToTable("Dogs");
            _ = modelBuilder.Entity<Cat>().ToTable("Cats");
        }
    }

    private static Simulation CreatePetsSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Dogs (
                    Id int not null primary key,
                    Name nvarchar(50) not null,
                    BarkVolume int not null
                );
                create table Cats (
                    Id int not null primary key,
                    Name nvarchar(50) not null,
                    Purrs bit not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static PetContext SeededContext()
    {
        var context = new PetContext(CreatePetsSimulation());
        context.Dogs.AddRange(
            new Dog { Id = 1, Name = "Rex", BarkVolume = 5 },
            new Dog { Id = 2, Name = "Buddy", BarkVolume = 7 });
        _ = context.Cats.Add(new Cat { Id = 3, Name = "Whiskers", Purrs = true });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void Pets_QueryBaseSet_UnionsAllConcreteTables()
    {
        // Querying the base set under TPC translates to a UNION ALL of selects
        // from each concrete table, wrapped in a derived table for downstream
        // operations.
        using var context = SeededContext();
        var names = context.Pets.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Rex", "Buddy", "Whiskers" }, names);
    }

    [TestMethod]
    public void Pets_FilterOnBaseProperty_PushesThroughUnion()
    {
        // Outer WHERE on a base property filters the union'd rowset.
        using var context = SeededContext();
        var names = context.Pets.Where(p => p.Name.StartsWith("R") || p.Name.StartsWith("W"))
            .OrderBy(p => p.Id)
            .Select(p => p.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Rex", "Whiskers" }, names);
    }

    [TestMethod]
    public void Pets_OfTypeDog_QueriesDogsTableOnly()
    {
        // OfType<Dog>() narrows the query to the concrete table without UNION.
        using var context = SeededContext();
        var dogs = context.Pets.OfType<Dog>().OrderBy(d => d.Id).ToArray();
        Assert.HasCount(2, dogs);
        Assert.AreEqual("Rex", dogs[0].Name); Assert.AreEqual(5, dogs[0].BarkVolume);
        Assert.AreEqual("Buddy", dogs[1].Name); Assert.AreEqual(7, dogs[1].BarkVolume);
    }

    [TestMethod]
    public void Pets_CountAcrossUnion_ReturnsTotalAcrossAllConcretes()
    {
        using var context = SeededContext();
        Assert.AreEqual(3, context.Pets.Count());
    }
}
