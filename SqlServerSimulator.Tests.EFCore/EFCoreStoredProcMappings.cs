using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core 7+'s stored-procedure CRUD mappings
/// (<c>InsertUsingStoredProcedure</c> / <c>UpdateUsingStoredProcedure</c>
/// / <c>DeleteUsingStoredProcedure</c>). When configured, SaveChanges
/// routes inserts / updates / deletes through user-defined procedures
/// instead of emitting INSERT / UPDATE / DELETE directly. EF emits
/// <c>EXEC Pet_Insert @Name = @p0, @Id = @p1 OUTPUT</c> at SaveChanges
/// and relies on the procedure to return identity / rowversion values.
/// Exercises the simulator's CREATE PROCEDURE + EXEC + output-parameter
/// surface against EF's mapping shape.
/// </summary>
[TestClass]
public class EFCoreStoredProcMappings
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class Pet
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Name { get; set; } = "";

        public int Age { get; set; }
    }

    private sealed class PetContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Pet> Pets => Set<Pet>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Pet>().Property(p => p.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Pet>()
                .InsertUsingStoredProcedure("Pet_Insert", b => b
                    .HasParameter(p => p.Id)
                    .HasParameter(p => p.Name)
                    .HasParameter(p => p.Age))
                .UpdateUsingStoredProcedure("Pet_Update", b => b
                    .HasOriginalValueParameter(p => p.Id)
                    .HasParameter(p => p.Name)
                    .HasParameter(p => p.Age))
                .DeleteUsingStoredProcedure("Pet_Delete", b => b
                    .HasOriginalValueParameter(p => p.Id));
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table Pets (
                Id int not null primary key,
                Name nvarchar(30) not null,
                Age int not null
            )
            """).ExecuteNonQuery();
        // Each CREATE PROCEDURE in its own batch — the simulator's body
        // capture treats everything after AS as the procedure body, so
        // batching procs together swallows the trailing CREATE statements.
        _ = connection.CreateCommand("""
            create procedure Pet_Insert
                @Id int,
                @Name nvarchar(30),
                @Age int
            as
            begin
                insert into Pets (Id, Name, Age) values (@Id, @Name, @Age);
            end
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create procedure Pet_Update
                @Id int,
                @Name nvarchar(30),
                @Age int
            as
            begin
                update Pets set Name = @Name, Age = @Age where Id = @Id;
            end
            """).ExecuteNonQuery();
        _ = connection.CreateCommand("""
            create procedure Pet_Delete
                @Id int
            as
            begin
                delete from Pets where Id = @Id;
            end
            """).ExecuteNonQuery();
        return simulation;
    }

    [TestMethod]
    public void Insert_RoutesThroughStoredProc()
    {
        using var context = new PetContext(CreateSimulation());
        _ = context.Pets.Add(new Pet { Id = 1, Name = "Rex", Age = 3 });
        _ = context.SaveChanges();
        Assert.AreEqual("Rex", context.Pets.Single().Name);
    }

    [TestMethod]
    public void Update_RoutesThroughStoredProc()
    {
        using var context = new PetContext(CreateSimulation());
        _ = context.Pets.Add(new Pet { Id = 1, Name = "Rex", Age = 3 });
        _ = context.SaveChanges();

        var rex = context.Pets.Single();
        rex.Age = 4;
        _ = context.SaveChanges();
        Assert.AreEqual(4, context.Pets.Single().Age);
    }

    [TestMethod]
    public void Delete_RoutesThroughStoredProc()
    {
        using var context = new PetContext(CreateSimulation());
        _ = context.Pets.Add(new Pet { Id = 1, Name = "Rex", Age = 3 });
        _ = context.SaveChanges();

        var rex = context.Pets.Single();
        _ = context.Pets.Remove(rex);
        _ = context.SaveChanges();
        Assert.AreEqual(0, context.Pets.Count());
    }

    [TestMethod]
    public void MixedOperations_AllRouteThroughProcs()
    {
        using var context = new PetContext(CreateSimulation());
        context.Pets.AddRange(
            new Pet { Id = 1, Name = "Rex", Age = 3 },
            new Pet { Id = 2, Name = "Buddy", Age = 5 });
        _ = context.SaveChanges();

        var rex = context.Pets.Single(p => p.Id == 1);
        rex.Age = 4;
        _ = context.Pets.Remove(context.Pets.Single(p => p.Id == 2));
        _ = context.SaveChanges();

        Assert.AreEqual(1, context.Pets.Count());
        Assert.AreEqual(4, context.Pets.Single().Age);
    }
}
