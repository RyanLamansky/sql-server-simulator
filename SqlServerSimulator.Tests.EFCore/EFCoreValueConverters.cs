using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's value-converter surface. A converter
/// translates a CLR property to a different store representation: enum
/// stored as string, comma-joined collection stored as a single column,
/// custom <see cref="ValueConverter{TModel,TProvider}"/>. EF translates
/// equality / WHERE predicates against the converted value, so a query
/// over an enum property whose CLR side is <c>Status.Active</c> emits
/// SQL comparing the store column against <c>N'Active'</c>.
/// </summary>
[TestClass]
public class EFCoreValueConverters
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new UserContext(new Simulation()));

    private enum Status { Active, Inactive, Archived }

    private sealed class User
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(20)")]
        public string Name { get; set; } = "";

        public Status Status { get; set; }

        public string[] Tags { get; set; } = [];
    }

    private sealed class UserContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<User> Users => Set<User>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<User>().Property(u => u.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<User>()
                .Property(u => u.Status)
                .HasConversion<string>()
                .HasColumnType("nvarchar(16)");
            _ = modelBuilder.Entity<User>()
                .Property(u => u.Tags)
                .HasConversion(
                    new ValueConverter<string[], string>(
                        v => string.Join(',', v),
                        v => v.Length == 0 ? new string[0] : v.Split(',', System.StringSplitOptions.None)))
                .HasColumnType("nvarchar(200)");
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Users (
                    Id int not null primary key,
                    Name nvarchar(20) not null,
                    Status nvarchar(16) not null,
                    Tags nvarchar(200) not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static UserContext SeededContext()
    {
        var context = new UserContext(CreateSimulation());
        context.Users.AddRange(
            new User { Id = 1, Name = "alice", Status = Status.Active, Tags = ["admin", "ops"] },
            new User { Id = 2, Name = "bob", Status = Status.Inactive, Tags = ["readonly"] },
            new User { Id = 3, Name = "carol", Status = Status.Archived, Tags = [] });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void EnumAsString_RoundTripsThroughStoreColumn()
    {
        using var context = SeededContext();
        var alice = context.Users.Single(u => u.Id == 1);
        Assert.AreEqual(Status.Active, alice.Status);
    }

    [TestMethod]
    public void EnumAsString_PredicateTranslatesToStringCompare()
    {
        // WHERE [u].[Status] = N'Inactive' under the hood.
        using var context = SeededContext();
        var names = context.Users
            .Where(u => u.Status == Status.Inactive)
            .Select(u => u.Name)
            .ToArray();
        CollectionAssert.AreEqual(new[] { "bob" }, names);
    }

    [TestMethod]
    public void EnumAsString_StorageColumnIsHumanReadable()
    {
        using var context = SeededContext();
        // The store value is the enum *name*, not its underlying int.
        var rawStatuses = context.Database
            .SqlQueryRaw<string>("select Status from Users order by Id")
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Active", "Inactive", "Archived" }, rawStatuses);
    }

    [TestMethod]
    public void CustomConverter_RoundTripsStringArray()
    {
        using var context = SeededContext();
        var alice = context.Users.Single(u => u.Id == 1);
        CollectionAssert.AreEqual(new[] { "admin", "ops" }, alice.Tags);
    }

    [TestMethod]
    public void CustomConverter_StoresJoinedRepresentation()
    {
        using var context = SeededContext();
        var rawTags = context.Database
            .SqlQueryRaw<string>("select Tags from Users where Id = 1")
            .ToArray();
        CollectionAssert.AreEqual(new[] { "admin,ops" }, rawTags);
    }
}
