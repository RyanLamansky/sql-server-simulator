using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

[TestClass]
public class EFCoreBasics
{
    [AssemblyInitialize]
    public static async Task HotPath(TestContext context)
    {
        if (System.Diagnostics.Debugger.IsAttached)
            return;

        // Triggers JIT compilation of the most common path among all tests, improving the accuracy of their timings.
        // Also functions as a sanity check against the simulator being completely broken.
        using var dbContext = new TestDbContext();

        Assert.IsEmpty(dbContext.Rows.Select(x => x.Id).AsEnumerable());

        Assert.AreEqual(0, await dbContext.SaveChangesAsync(context.CancellationToken));
    }

    public static Simulation CreateDefaultSimulation()
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("create table Rows ( Id int )")
            .ExecuteNonQuery();

        return simulation;
    }

    class TestRow
    {
        public int Id { get; set; }
    }

    class TestDbContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; set; } = simulation;

        public TestDbContext()
            : this(CreateDefaultSimulation())
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            _ = optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        public DbSet<TestRow> Rows => Set<TestRow>();
    }

    [TestMethod]
    public void InsertRowSync()
    {
        using var context = new TestDbContext();

        var row = new TestRow { Id = 1 };

        _ = context.Rows.Add(row);

        _ = context.SaveChanges();
    }

    /// <summary>
    /// Same as <see cref="InsertRowSync"/> except using async logic.
    /// The simulator is 100% sync so this really just ensures the bult-in default async-over-sync wrapper works.
    /// </summary>
    [TestMethod]
    public async Task InsertRowAsync()
    {
        await using var context = new TestDbContext();

        var row = new TestRow { Id = 1 };

        _ = context.Rows.Add(row);

        _ = await context.SaveChangesAsync();
    }

    [TestMethod]
    public void RoundTrip()
    {
        var simulation = CreateDefaultSimulation();
        const int storedValue = 3;

        using (var context = new TestDbContext(simulation))
        {
            var row = new TestRow { Id = storedValue };

            _ = context.Rows.Add(row);

            _ = context.SaveChanges();
        }

        using (var context = new TestDbContext(simulation))
        {
            var receivedValue = context.Rows.Select(x => x.Id).AsEnumerable();

            Assert.AreEqual(storedValue, receivedValue.FirstOrDefault());
        }
    }
}
