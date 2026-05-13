using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's Table-Per-Type (TPT) inheritance mapping.
/// TPT stores the base properties in one table and each subtype's extra
/// properties in its own table, joined on the shared primary key. EF Core
/// queries the base set by LEFT JOINing every subtype table and computing
/// a synthetic <c>Discriminator</c> column via CASE over each subtype's PK
/// nullability; <c>OfType&lt;T&gt;()</c> swaps the JOIN to that subtype and
/// adds a NOT NULL filter. SaveChanges issues one INSERT per affected table.
/// Exercises the simulator's LEFT JOIN + CASE-discriminator combination
/// against EF Core 7+'s TPT emit shape.
/// </summary>
[TestClass]
public class EFCoreInheritanceTpt
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
                .UseTptMappingStrategy()
                .Property(p => p.Id)
                .ValueGeneratedNever();
            _ = modelBuilder.Entity<Pet>().ToTable("Pets");
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
                create table Pets (
                    Id int not null primary key,
                    Name nvarchar(50) not null
                );
                create table Dogs (
                    Id int not null primary key,
                    BarkVolume int not null
                );
                create table Cats (
                    Id int not null primary key,
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
    public void Pets_QueryBaseSet_LeftJoinsAllSubtypeTables()
    {
        // TPT base-set query LEFT JOINs each subtype table on the shared PK
        // and synthesizes the discriminator from per-row nullability.
        using var context = SeededContext();
        var names = context.Pets.OrderBy(p => p.Id).Select(p => p.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "Rex", "Buddy", "Whiskers" }, names);
    }

    [TestMethod]
    public void Pets_HydratesConcreteSubtypes()
    {
        // The materializer picks the concrete CLR type using the CASE-derived
        // discriminator over each subtype's PK nullability.
        using var context = SeededContext();
        var pets = context.Pets.OrderBy(p => p.Id).ToArray();
        _ = Assert.IsInstanceOfType<Dog>(pets[0]);
        _ = Assert.IsInstanceOfType<Dog>(pets[1]);
        _ = Assert.IsInstanceOfType<Cat>(pets[2]);
        Assert.AreEqual(5, ((Dog)pets[0]).BarkVolume);
        Assert.IsTrue(((Cat)pets[2]).Purrs);
    }

    [TestMethod]
    public void Pets_OfTypeDog_NarrowsToDogsTable()
    {
        // OfType<Dog>() drops the unrelated subtype JOINs and adds
        // WHERE [Dogs].[Id] IS NOT NULL.
        using var context = SeededContext();
        var dogs = context.Pets.OfType<Dog>().OrderBy(d => d.Id).ToArray();
        Assert.HasCount(2, dogs);
        Assert.AreEqual("Rex", dogs[0].Name); Assert.AreEqual(5, dogs[0].BarkVolume);
        Assert.AreEqual("Buddy", dogs[1].Name); Assert.AreEqual(7, dogs[1].BarkVolume);
    }

    [TestMethod]
    public void Pets_OfTypeCat_NarrowsToCatsTable()
    {
        using var context = SeededContext();
        var cats = context.Pets.OfType<Cat>().OrderBy(c => c.Id).ToArray();
        Assert.HasCount(1, cats);
        Assert.AreEqual("Whiskers", cats[0].Name);
        Assert.IsTrue(cats[0].Purrs);
    }

    [TestMethod]
    public void Pets_FilterOnBaseProperty_AppliesToJoinedSet()
    {
        using var context = SeededContext();
        var names = context.Pets.Where(p => p.Name.StartsWith("R") || p.Name.StartsWith("W"))
            .OrderBy(p => p.Id)
            .Select(p => p.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Rex", "Whiskers" }, names);
    }

    [TestMethod]
    public void Pets_CountAcrossSubtypes_ReturnsTotalFromBaseTable()
    {
        // Count() collapses to a COUNT over the base table — no JOINs needed.
        using var context = SeededContext();
        Assert.AreEqual(3, context.Pets.Count());
    }

    [TestMethod]
    public void SaveChanges_InsertsBaseRowAndSubtypeRow()
    {
        using var context = new PetContext(CreatePetsSimulation());
        context.AddRange(
            new Dog { Id = 1, Name = "Rex", BarkVolume = 5 },
            new Cat { Id = 2, Name = "Whiskers", Purrs = true });
        Assert.AreEqual(4, context.SaveChanges());
        Assert.AreEqual(2, context.Pets.Count());
        Assert.AreEqual(1, context.Dogs.Count());
        Assert.AreEqual(1, context.Cats.Count());
    }

    [TestMethod]
    public void DogsSet_DirectQuery_ReadsDogsJoinedWithPets()
    {
        // Querying the Dogs DbSet directly emits Dogs INNER JOIN Pets ON Id=Id
        // to pull the base-type properties alongside the subtype's own columns.
        using var context = SeededContext();
        var dogs = context.Dogs.OrderBy(d => d.Id).ToArray();
        Assert.HasCount(2, dogs);
        Assert.AreEqual("Rex", dogs[0].Name);
        Assert.AreEqual("Buddy", dogs[1].Name);
    }
}
