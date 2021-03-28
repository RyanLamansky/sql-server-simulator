using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator.EFCore
{
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
        [Ignore("Requires basic command text parsing.")]
        public void InsertAndRetrieveRowSync()
        {
            using var context = new TestContext();

            var row = new TestRow { Id = 1 };

            context.Rows.Add(row);

            context.SaveChanges();

            var rows = context.Rows.ToArray();

            Assert.AreEqual(1, rows.Length);
        }
    }
}
