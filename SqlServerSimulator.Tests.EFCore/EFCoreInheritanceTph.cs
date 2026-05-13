using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's Table-Per-Hierarchy (TPH) inheritance
/// mapping. TPH is EF Core's default strategy: all subtypes share one table
/// with a discriminator column that records the concrete type. Querying the
/// base set is a single-table read; <c>OfType&lt;T&gt;()</c> adds a
/// discriminator filter; SaveChanges emits one INSERT per concrete type with
/// the matching subset of columns. None of the SQL shapes are inheritance-
/// specific — coverage exists to lock in fidelity against the strategy EF
/// applications use most.
/// </summary>
[TestClass]
public class EFCoreInheritanceTph
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
            _ = modelBuilder.Entity<Pet>()
                .Property(p => p.Id)
                .ValueGeneratedNever();
            _ = modelBuilder.Entity<Pet>()
                .HasDiscriminator<string>("Disc")
                .HasValue<Dog>("Dog")
                .HasValue<Cat>("Cat");
        }
    }

    private static Simulation CreatePetsSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Pets (
                    Id int not null primary key,
                    Disc nvarchar(13) not null,
                    Name nvarchar(50) not null,
                    BarkVolume int null,
                    Purrs bit null
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
    public void Pets_QueryBaseSet_ReadsSingleTable()
    {
        // TPH base-set query is a plain single-table SELECT — EF doesn't
        // narrow on the discriminator because the model is complete.
        using var context = SeededContext();
        var names = context.Pets.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Rex", "Buddy", "Whiskers" }, names);
    }

    [TestMethod]
    public void Pets_HydratesConcreteSubtypes()
    {
        // The materializer picks the concrete CLR type by discriminator value.
        using var context = SeededContext();
        var pets = context.Pets.OrderBy(p => p.Id).ToArray();
        _ = Assert.IsInstanceOfType<Dog>(pets[0]);
        _ = Assert.IsInstanceOfType<Dog>(pets[1]);
        _ = Assert.IsInstanceOfType<Cat>(pets[2]);
        Assert.AreEqual(5, ((Dog)pets[0]).BarkVolume);
        Assert.IsTrue(((Cat)pets[2]).Purrs);
    }

    [TestMethod]
    public void Pets_OfTypeDog_FiltersByDiscriminator()
    {
        // OfType<Dog>() emits WHERE [Disc] = N'Dog'.
        using var context = SeededContext();
        var dogs = context.Pets.OfType<Dog>().OrderBy(d => d.Id).ToArray();
        Assert.HasCount(2, dogs);
        Assert.AreEqual("Rex", dogs[0].Name); Assert.AreEqual(5, dogs[0].BarkVolume);
        Assert.AreEqual("Buddy", dogs[1].Name); Assert.AreEqual(7, dogs[1].BarkVolume);
    }

    [TestMethod]
    public void Pets_OfTypeCat_FiltersByDiscriminator()
    {
        using var context = SeededContext();
        var cats = context.Pets.OfType<Cat>().OrderBy(c => c.Id).ToArray();
        Assert.HasCount(1, cats);
        Assert.AreEqual("Whiskers", cats[0].Name);
        Assert.IsTrue(cats[0].Purrs);
    }

    [TestMethod]
    public void Pets_FilterOnBaseProperty_PushesThroughSingleTable()
    {
        using var context = SeededContext();
        var names = context.Pets.Where(p => p.Name.StartsWith("R") || p.Name.StartsWith("W"))
            .OrderBy(p => p.Id)
            .Select(p => p.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Rex", "Whiskers" }, names);
    }

    [TestMethod]
    public void Pets_CountAcrossDiscriminator_ReturnsTotal()
    {
        using var context = SeededContext();
        Assert.AreEqual(3, context.Pets.Count());
    }

    [TestMethod]
    public void SaveChanges_EmitsOneInsertPerConcreteType()
    {
        // EF batches inserts per concrete type — two Dogs in one INSERT,
        // one Cat in another. Both target the same Pets table.
        using var context = new PetContext(CreatePetsSimulation());
        context.AddRange(
            new Dog { Id = 1, Name = "Rex", BarkVolume = 5 },
            new Cat { Id = 2, Name = "Whiskers", Purrs = true },
            new Dog { Id = 3, Name = "Buddy", BarkVolume = 7 });
        Assert.AreEqual(3, context.SaveChanges());
        Assert.AreEqual(3, context.Pets.Count());
        Assert.AreEqual(2, context.Pets.OfType<Dog>().Count());
        Assert.AreEqual(1, context.Pets.OfType<Cat>().Count());
    }
}
