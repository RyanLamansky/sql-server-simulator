using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

internal class TestRow
{
    public int Id { get; set; }
}

internal class TestDbContext(Simulation simulation) : DbContext
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
}
