using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace SqlServerSimulator;

[TestClass]
[Ignore("Blocked by simulator inability to handle parameterized inserts.")]
public class EFCoreBasics
{
    class TestRow
    {
        public int Id { get; set; }
    }

    class TestContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var simulation = new Simulation();
            simulation
                .CreateOpenConnection()
                .CreateCommand("create table Rows ( Id int )")
                .ExecuteNonQuery();

            optionsBuilder.UseSqlServer(simulation.CreateDbConnection());
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
}
