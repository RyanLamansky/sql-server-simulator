using System.Linq;
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
            optionsBuilder.UseSqlServer(new Simulation().CreateDbConnection());
        }

        public DbSet<TestRow> Rows { get; set; }
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
