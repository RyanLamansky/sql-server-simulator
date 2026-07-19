using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core 7+'s bulk operations:
/// <see cref="EntityFrameworkQueryableExtensions.ExecuteUpdate"/> and
/// <see cref="EntityFrameworkQueryableExtensions.ExecuteDelete"/>. Both
/// bypass change tracking and emit a single UPDATE / DELETE statement
/// directly against the queryable's filter. SQL Server's emit shape is
/// <c>UPDATE [u] SET … FROM [Users] AS [u] WHERE …</c> for ExecuteUpdate
/// and <c>DELETE FROM [u] FROM [Users] AS [u] WHERE …</c> for
/// ExecuteDelete — the aliased target form rather than plain
/// <c>UPDATE Users SET …</c>.
/// </summary>
[TestClass]
public class EFCoreExecuteUpdateDelete
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new UserContext(new Simulation()));

    private sealed class User
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Name { get; set; } = "";

        public bool Active { get; set; }

        public int LoginCount { get; set; }
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
                    Name nvarchar(30) not null,
                    Active bit not null,
                    LoginCount int not null
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static UserContext SeededContext()
    {
        var context = new UserContext(CreateSimulation());
        context.Users.AddRange(
            new User { Id = 1, Name = "alice", Active = true, LoginCount = 3 },
            new User { Id = 2, Name = "bob", Active = false, LoginCount = 0 },
            new User { Id = 3, Name = "carol", Active = true, LoginCount = 7 },
            new User { Id = 4, Name = "dave", Active = false, LoginCount = 0 });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void ExecuteUpdate_SetsLiteralValue()
    {
        using var context = SeededContext();
        var affected = context.Users
            .Where(u => !u.Active)
            .ExecuteUpdate(s => s.SetProperty(u => u.LoginCount, 0));
        Assert.AreEqual(2, affected);
        Assert.AreEqual(0, context.Users.Where(u => !u.Active).Sum(u => u.LoginCount));
    }

    [TestMethod]
    public void ExecuteUpdate_SetsComputedExpression()
    {
        using var context = SeededContext();
        var affected = context.Users
            .Where(u => u.Active)
            .ExecuteUpdate(s => s.SetProperty(u => u.LoginCount, u => u.LoginCount + 1));
        Assert.AreEqual(2, affected);
        // ExecuteUpdate bypasses the change tracker — verify via AsNoTracking()
        // so the identity map doesn't return the stale pre-update entity.
        Assert.AreEqual(4, context.Users.AsNoTracking().Single(u => u.Id == 1).LoginCount);
        Assert.AreEqual(8, context.Users.AsNoTracking().Single(u => u.Id == 3).LoginCount);
    }

    [TestMethod]
    public void ExecuteUpdate_MultipleSetProperty()
    {
        using var context = SeededContext();
        var affected = context.Users
            .Where(u => u.Id == 1)
            .ExecuteUpdate(s => s
                .SetProperty(u => u.Name, u => u.Name + "!")
                .SetProperty(u => u.LoginCount, u => u.LoginCount + 10));
        Assert.AreEqual(1, affected);
        var alice = context.Users.AsNoTracking().Single(u => u.Id == 1);
        Assert.AreEqual("alice!", alice.Name);
        Assert.AreEqual(13, alice.LoginCount);
    }

    [TestMethod]
    public void ExecuteDelete_RemovesFiltered()
    {
        using var context = SeededContext();
        var affected = context.Users.Where(u => !u.Active).ExecuteDelete();
        Assert.AreEqual(2, affected);
        Assert.AreEqual(2, context.Users.Count());
        Assert.IsFalse(context.Users.Any(u => !u.Active));
    }

    [TestMethod]
    public void ExecuteDelete_NoMatch_ReturnsZero()
    {
        using var context = SeededContext();
        var affected = context.Users.Where(u => u.Id < 0).ExecuteDelete();
        Assert.AreEqual(0, affected);
        Assert.AreEqual(4, context.Users.Count());
    }
}
