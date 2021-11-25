using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator.EFCore;

[TestClass]
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
}
