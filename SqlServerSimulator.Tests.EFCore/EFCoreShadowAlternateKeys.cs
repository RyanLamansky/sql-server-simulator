using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core's shadow properties and alternate keys.
/// A shadow property is a column tracked in the model but absent from the
/// CLR type; the change tracker holds the value and EF includes it in
/// reads / writes via <c>EF.Property&lt;T&gt;(...)</c>. An alternate key
/// is a non-PK unique constraint (HasAlternateKey) that EF enforces by
/// emitting a UNIQUE index in the model and using the column for FK
/// targeting. Exercises the simulator's UNIQUE-constraint surface and
/// EF.Property column access.
/// </summary>
[TestClass]
public class EFCoreShadowAlternateKeys
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new AccountContext(new Simulation()));

    private sealed class Account
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Username { get; set; } = "";

        [Column(TypeName = "nvarchar(40)")]
        public string Email { get; set; } = "";
    }

    private sealed class AccountContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<Account> Accounts => Set<Account>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            _ = modelBuilder.Entity<Account>().Property(a => a.Id).ValueGeneratedNever();
            _ = modelBuilder.Entity<Account>()
                .Property<DateTime>("LastModified")
                .HasColumnType("datetime2(0)")
                .HasDefaultValueSql("sysutcdatetime()");
            _ = modelBuilder.Entity<Account>().HasAlternateKey(a => a.Email);
        }
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("""
                create table Accounts (
                    Id int not null primary key,
                    Username nvarchar(30) not null,
                    Email nvarchar(40) not null unique,
                    LastModified datetime2(0) not null default sysutcdatetime()
                )
                """)
            .ExecuteNonQuery();
        return simulation;
    }

    private static AccountContext SeededContext()
    {
        var context = new AccountContext(CreateSimulation());
        context.Accounts.AddRange(
            new Account { Id = 1, Username = "alice", Email = "alice@example.com" },
            new Account { Id = 2, Username = "bob", Email = "bob@example.com" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void ShadowProperty_PopulatesOnInsert()
    {
        using var context = SeededContext();
        var entry = context.Entry(context.Accounts.Single(a => a.Id == 1));
        var modified = entry.Property<DateTime>("LastModified").CurrentValue;
        Assert.IsGreaterThan(DateTime.UtcNow.AddMinutes(-1), modified);
    }

    [TestMethod]
    public void ShadowProperty_AccessibleViaEFProperty()
    {
        using var context = SeededContext();
        var usernames = context.Accounts
            .OrderByDescending(a => EF.Property<DateTime>(a, "LastModified"))
            .Select(a => a.Username)
            .ToArray();
        Assert.HasCount(2, usernames);
        CollectionAssert.AreEquivalent(new[] { "alice", "bob" }, usernames);
    }

    [TestMethod]
    public void AlternateKey_EnforcesUniqueness()
    {
        // EF detects the alternate-key collision in the change tracker before
        // any DML, throwing InvalidOperationException directly from Add.
        var simulation = CreateSimulation();
        using (var first = new AccountContext(simulation))
        {
            _ = first.Accounts.Add(new Account { Id = 1, Username = "alice", Email = "alice@example.com" });
            _ = first.SaveChanges();
        }
        using var second = new AccountContext(simulation);
        _ = second.Accounts.Add(new Account { Id = 2, Username = "alice2", Email = "alice@example.com" });
        _ = Assert.Throws<DbUpdateException>(() => second.SaveChanges());
    }

    [TestMethod]
    public void AlternateKey_AllowsNonConflictingInsert()
    {
        using var context = SeededContext();
        _ = context.Accounts.Add(new Account { Id = 3, Username = "carol", Email = "carol@example.com" });
        Assert.AreEqual(1, context.SaveChanges());
        Assert.AreEqual(3, context.Accounts.Count());
    }
}
