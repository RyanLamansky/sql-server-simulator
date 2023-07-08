using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator;

[TestClass]
public class EFCoreBasics
{
    public static Simulation CreateDefaultSimulation()
    {
        var simulation = new Simulation();
        simulation
            .CreateOpenConnection()
            .CreateCommand("create table Rows ( Id int )")
            .ExecuteNonQuery();

        return simulation;
    }

    class TestRow
    {
        public int Id { get; set; }
    }

    class TestContext : DbContext
    {
        public Simulation Simulation { get; set; }

        public TestContext()
            : this(CreateDefaultSimulation())
        {
        }

        public TestContext(Simulation simulation)
        {
            this.Simulation = simulation;
        }


        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

        public DbSet<TestRow> Rows => Set<TestRow>();
    }

    [TestMethod]
    public void InsertRowSync()
    {
        using var context = new TestContext();

        var row = new TestRow { Id = 1 };

        context.Rows.Add(row);

        context.SaveChanges();
    }

    /// <summary>
    /// Same as <see cref="InsertRowSync"/> except using async logic.
    /// The simulator is 100% sync so this really just ensures the bult-in default async-over-sync wrapper works.
    /// </summary>
    [TestMethod]
    public async Task InsertRowAsync()
    {
        await using var context = new TestContext();

        var row = new TestRow { Id = 1 };

        context.Rows.Add(row);

        await context.SaveChangesAsync();
    }

    [TestMethod]
    public void RoundTrip()
    {
        var simulation = CreateDefaultSimulation();
        const int storedValue = 3;

        using (var context = new TestContext(simulation))
        {
            var row = new TestRow { Id = storedValue };

            context.Rows.Add(row);

            context.SaveChanges();
        }

        using (var context = new TestContext(simulation))
        {
            var receivedValue = context.Rows.Select(x => x.Id).AsEnumerable();

            Assert.AreEqual(storedValue, receivedValue.FirstOrDefault());
        }
    }
}
