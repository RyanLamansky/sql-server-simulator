using Microsoft.EntityFrameworkCore;
using SqlServerSimulator;

var simulation = new Simulation();

// Commands can be run directly against the simulation, used here to create a table.
using (var connection = simulation.CreateDbConnection())
using (var command = connection.CreateCommand())
{
    command.CommandText = "create table ExampleRecord ( Id int )";

    connection.Open();
    _ = command.ExecuteNonQuery();
}

// Entity Framework thinks it's talking to a real SQL Server.
// (At least until you try something not yet supported by the simulation.)
using (var context = new SimulatedContext(simulation))
{
    _ = context.ExampleRecord.Add(new() { Id = 1 });
    _ = context.SaveChanges();
}

// The simulation state is preserved across EF DbContexts.
using (var context = new SimulatedContext(simulation))
{
    var receivedValue = context.ExampleRecord.Select(x => x.Id).AsEnumerable();

    Console.Write(receivedValue.FirstOrDefault()); // Will write "1", as we stored earlier.
}

// Entity Framework can be used mostly normally.
sealed class ExampleRecord
{
    public required int Id { get; set; }
}

// Below is the minimum required to get entity framework to use the simulation.
sealed class SimulatedContext(Simulation simulation) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Redirect database connection creation to the simulation instead of a real SQL Server.
        _ = optionsBuilder.UseSqlServer(simulation.CreateDbConnection());
    }

    public DbSet<ExampleRecord> ExampleRecord => Set<ExampleRecord>();
}
