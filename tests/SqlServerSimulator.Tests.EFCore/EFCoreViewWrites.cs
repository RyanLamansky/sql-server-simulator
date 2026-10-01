using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace SqlServerSimulator;

/// <summary>
/// An entity mapped with <c>ToTable</c> onto an updatable view — EF refuses
/// <c>ExecuteUpdate</c> / <c>ExecuteDelete</c> on a <c>ToView</c> mapping, so
/// this is how a model writes through one. EF emits the aliased target form,
/// <c>UPDATE [a] SET … FROM [ActiveUsers] AS [a] WHERE …</c>, which writes
/// through the view to its base table, only to the rows the view shows.
/// </summary>
[TestClass]
public sealed class EFCoreViewWrites
{
    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new ActiveUserContext(new Simulation()));

    private sealed class ActiveUser
    {
        public int Id { get; set; }

        [Column(TypeName = "nvarchar(30)")]
        public string Name { get; set; } = "";

        public int LoginCount { get; set; }
    }

    private sealed class ActiveUserContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; } = simulation;

        public DbSet<ActiveUser> ActiveUsers => Set<ActiveUser>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            _ = modelBuilder.Entity<ActiveUser>().ToTable("ActiveUsers").Property(u => u.Id).ValueGeneratedNever();
    }

    private static ActiveUserContext SeededContext()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table Users (Id int primary key, Name nvarchar(30) not null, Active bit not null, LoginCount int not null); insert Users values (1, N'alice', 1, 3), (2, N'bob', 0, 9), (3, N'carol', 1, 7)",
            "create view ActiveUsers as select Id, Name, LoginCount from Users where Active = 1");
        return new ActiveUserContext(simulation);
    }

    [TestMethod]
    public void ExecuteUpdate_WritesTheRowsTheViewShows()
    {
        using var context = SeededContext();
        var updated = context.ActiveUsers
            .Where(u => u.LoginCount > 3)
            .ExecuteUpdate(s => s.SetProperty(u => u.LoginCount, u => u.LoginCount + 1));
        Assert.AreEqual(1, updated);
        Assert.AreEqual("1:3 2:9 3:8", context.Simulation.ExecuteScalar("select string_agg(concat(Id, ':', LoginCount), ' ') within group (order by Id) from Users"));
    }

    [TestMethod]
    public void ExecuteDelete_RemovesTheRowsTheViewShows()
    {
        using var context = SeededContext();
        var deleted = context.ActiveUsers.Where(u => u.Name != "carol").ExecuteDelete();
        Assert.AreEqual(1, deleted);
        Assert.AreEqual("2 3", context.Simulation.ExecuteScalar("select string_agg(Id, ' ') within group (order by Id) from Users"));
    }
}
