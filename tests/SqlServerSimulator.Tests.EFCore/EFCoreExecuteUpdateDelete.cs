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
    public static void WarmModel(TestContext _)
    {
        AssemblyHooks.WarmModel(() => new UserContext(new Simulation()));
        AssemblyHooks.WarmModel(() => new TeamContext(new Simulation()));
    }

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
    public void ExecuteUpdate_SetsCorrelatedCount()
    {
        // The value subquery correlates to the aliased target:
        // UPDATE [u] SET … = (SELECT COUNT(*) FROM [Users] AS [u0] WHERE [u0].[Active] = [u].[Active]) FROM [Users] AS [u].
        using var context = SeededContext();
        var affected = context.Users
            .ExecuteUpdate(s => s.SetProperty(u => u.LoginCount, u => context.Users.Count(o => o.Active == u.Active && o.Id <= u.Id)));
        Assert.AreEqual(4, affected);
        CollectionAssert.AreEqual(
            new[] { 1, 1, 2, 2 },
            context.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.LoginCount).ToArray());
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

    private sealed class Team
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Name { get; set; } = "";
    }

    private sealed class Member
    {
        public int Id { get; set; }

        public int TeamId { get; set; }

        public Team Team { get; set; } = null!;

        public int Points { get; set; }
    }

    private sealed class TeamContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Member> Members => Set<Member>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Team>().ToTable("Teams").Property(t => t.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Member>().Property(m => m.Id).ValueGeneratedNever();
        }
    }

    // Two teams, red and blue, with the members 1 through memberCount
    // alternating between them.
    private static Simulation CreateTeams(int memberCount)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table Teams (Id int not null primary key, Name nvarchar(30) not null);
            create table Members (Id int not null primary key, TeamId int not null references Teams (Id), Points int not null);
            insert Teams values (0, N'red'), (1, N'blue');
            insert Members select value, value % 2, 0 from generate_series(1, {memberCount});
            """);
        return simulation;
    }

    /// <summary>
    /// An ExecuteUpdate filtered through a navigation is EF's joined
    /// <c>UPDATE … FROM [Members] AS [m] INNER JOIN [Teams] AS [t]</c>, and
    /// contexts running it at once never lose an increment.
    /// </summary>
    [TestMethod]
    public void ExecuteUpdate_ThroughNavigation_ConcurrentIncrementsAllLand()
    {
        const int Workers = 8, Rounds = 20;
        var simulation = CreateTeams(4);
        _ = Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = TestContext.CancellationToken }, _ =>
        {
            using var context = new TeamContext(simulation);
            for (var round = 0; round < Rounds; round++)
                Assert.AreEqual(2, context.Members.Where(m => m.Team.Name == "red").ExecuteUpdate(s => s.SetProperty(m => m.Points, m => m.Points + 1)));
        });
        using var check = new TeamContext(simulation);
        CollectionAssert.AreEqual(
            new[] { 0, Workers * Rounds, 0, Workers * Rounds },
            check.Members.OrderBy(m => m.Id).Select(m => m.Points).ToArray());
    }

    /// <summary>
    /// An ExecuteDelete filtered through a navigation is EF's joined
    /// <c>DELETE TOP(@p) FROM [m] FROM [Members] AS [m] INNER JOIN [Teams] AS [t]</c>,
    /// and contexts draining one team at once delete each member once.
    /// </summary>
    [TestMethod]
    public void ExecuteDelete_ThroughNavigation_ConcurrentDeletesEachRowOnce()
    {
        const int Workers = 8, MemberCount = 200;
        var simulation = CreateTeams(MemberCount);
        var deleted = 0;
        _ = Parallel.For(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = TestContext.CancellationToken }, worker =>
        {
            using var context = new TeamContext(simulation);
            var team = worker % 2 == 0 ? "red" : "blue";
            while (context.Members.Any(m => m.Team.Name == team))
                _ = Interlocked.Add(ref deleted, context.Members.Where(m => m.Team.Name == team).Take(3).ExecuteDelete());
        });
        Assert.AreEqual(MemberCount, deleted);
    }
}
