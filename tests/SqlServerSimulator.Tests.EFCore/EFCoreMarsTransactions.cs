using Microsoft.EntityFrameworkCore;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SaveChanges</c> while a query is still being enumerated, over the
/// in-process connection, whose MARS rules are real's (probed 2026-10-06
/// against SQL Server 2025 over a MARS SqlClient connection, EF Core 10).
/// A query being enumerated counts as an outstanding request until the
/// enumeration has read past its last row.
/// </summary>
[TestClass]
public sealed class EFCoreMarsTransactions
{
    public sealed class Item
    {
        public int Id { get; set; }

        public string Pad { get; set; } = "";
    }

    private sealed class ItemContext(Action<DbContextOptionsBuilder> configure) : DbContext
    {
        public DbSet<Item> Items => this.Set<Item>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => configure(optionsBuilder);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Item>(b =>
            {
                _ = b.Property(x => x.Id).ValueGeneratedNever();
                _ = b.Property(x => x.Pad).HasColumnType("nvarchar(2000)");
            });
    }

    private static Simulation Seeded(int rows)
    {
        var simulation = new Simulation();
        _ = simulation
            .CreateOpenConnection()
            .CreateCommand($"create table Items (Id int not null primary key, Pad nvarchar(2000) not null); insert Items select value, replicate(N'x', 2000) from generate_series(1, {rows})")
            .ExecuteNonQuery();
        return simulation;
    }

    /// <summary>
    /// The connection announces MARS, so EF Core takes no savepoint for a
    /// <c>SaveChanges</c> inside the user's transaction — one would be
    /// Msg 3981 under the query still enumerating in that transaction.
    /// </summary>
    [TestMethod]
    public void SaveChanges_InTheUserTransaction_WhileEnumerating_Commits()
    {
        var simulation = Seeded(100);
        using (var context = new ItemContext(o => o.UseSqlServer(simulation.CreateDbConnection())))
        {
            using var transaction = context.Database.BeginTransaction();
            var seen = 0;
            foreach (var item in context.Items.OrderBy(i => i.Id))
            {
                if (++seen != 2)
                    continue;
                _ = context.Items.Add(new Item { Id = 1001, Pad = "a" });
                _ = context.Items.Add(new Item { Id = 1002, Pad = "b" });
                _ = context.SaveChanges();
            }
            transaction.Commit();
            AreEqual(100, seen);
        }

        using var check = new ItemContext(o => o.UseSqlServer(simulation.CreateDbConnection()));
        AreEqual(102, check.Items.Count());
    }

    /// <summary>
    /// A <c>SaveChanges</c> of more commands than one batch holds begins a
    /// transaction of its own, which real refuses with Msg 3988 while a query
    /// is still enumerating — however few its rows, since SqlClient counts a
    /// reader outstanding until it has read past the end.
    /// </summary>
    [TestMethod]
    public void SaveChanges_BeginningItsOwnTransaction_WhileEnumerating_IsMsg3988()
    {
        var simulation = Seeded(3);
        using var context = new ItemContext(o => o.UseSqlServer(simulation.CreateDbConnection(), sql => sql.MaxBatchSize(1)));
        var error = 0;
        var seen = 0;
        foreach (var item in context.Items.OrderBy(i => i.Id))
        {
            if (++seen != 2)
                continue;
            _ = context.Items.Add(new Item { Id = 1001, Pad = "a" });
            _ = context.Items.Add(new Item { Id = 1002, Pad = "b" });
            error = ThrowsExactly<SimulatedSqlException>(() => context.SaveChanges()).Number;
        }
        AreEqual(3988, error);
        AreEqual(3, seen);
    }
}
