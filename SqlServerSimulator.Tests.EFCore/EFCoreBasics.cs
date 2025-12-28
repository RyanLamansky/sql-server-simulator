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
        using var dbContext = new TestDbContext(1, 2);
        _ = await dbContext.Rows.Select(x => x.Id).FirstOrDefaultAsync(context.CancellationToken);
    }

    public static Simulation CreateDefaultSimulation(params ReadOnlySpan<int> values)
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand("create table Rows ( Id int )")
            .ExecuteNonQuery();

        if (values.Length != 0)
        {
            using var context = new TestDbContext(simulation);
            foreach (var value in values)
            {
                var row = new TestRow { Id = value };
                _ = context.Rows.Add(row);
            }
            _ = context.SaveChanges();
        }

        return simulation;
    }

    class TestRow
    {
        public int Id { get; set; }
    }

    class TestDbContext(Simulation simulation) : DbContext
    {
        public Simulation Simulation { get; set; } = simulation;

        public TestDbContext(params ReadOnlySpan<int> values)
            : this(CreateDefaultSimulation(values))
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
        using var context = new TestDbContext(1);
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
        const int storedValue = 3;
        using var context = new TestDbContext(storedValue);
        var receivedValue = context.Rows.Select(x => x.Id).AsEnumerable();

        Assert.AreEqual(storedValue, receivedValue.FirstOrDefault());
    }

    [TestMethod]
    public void MultiRowInsert()
    {
        int[] storedValues = [2, 3];
        using var context = new TestDbContext(storedValues);
        CollectionAssert.AreEquivalent(storedValues, context.Rows.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public void FirstOrDefault()
    {
        int[] storedValues = [4, 5];
        using var context = new TestDbContext(storedValues);
        var receivedValue = context.Rows.Select(x => x.Id);
        // Without an OrderBy, we can't guarantee which of the two possibilities is returned.
        CollectionAssert.Contains(storedValues, receivedValue.FirstOrDefault());
    }

    [TestMethod]
    public void SingleOrDefault()
    {
        const int storedValue = 6; // Until `Where` is supported, this won't pass if multiple rows exist.
        using var context = new TestDbContext(storedValue);
        var receivedValue = context.Rows.Select(x => x.Id);
        Assert.AreEqual(storedValue, receivedValue.SingleOrDefault());
    }

    [TestMethod]
    public void Take()
    {
        int[] storedValues = [4, 5];
        using var context = new TestDbContext(storedValues);
        var receivedValue = context.Rows.Select(x => x.Id);
        // Without an OrderBy, we can't guarantee which of the two possibilities is returned.
        CollectionAssert.Contains(storedValues, receivedValue.Take(1).AsEnumerable().First());
    }
}
