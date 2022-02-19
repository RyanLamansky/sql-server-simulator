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

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public TestContext(Simulation simulation)
#pragma warning restore
        {
            this.Simulation = simulation;
        }


        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlServer(this.Simulation.CreateDbConnection());
        }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public DbSet<TestRow> Rows { get; set; }
#pragma warning restore
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
    [Ignore("Unsupported query syntax: SELECT [r].[Id] FROM [Rows] AS [r]")]
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
