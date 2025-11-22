# SQL Server Simulator for .NET

Provides embedded SQL Server emulation, intended for high performance parallel unit testing of .NET applications.

# Example

```C#
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
```

## Limitations

The feature set of this library is currently too small for use in real-world applications.

- Feature parity with SQL Server is less than 1%.
- No thread-safety or lock emulation.
- Syntax validation is prone to both false positives _and_ negatives due to limited feature support.

## Development Priorities

- The highest priority is improving SQL command processing enough to support all Entity Framework features, enabling the real-world use case of fast single-threaded unit testing of an Entity Framework-based app without requiring a SQL Server deployment.

The remaining priorities are so far below the EF compatibility goal that they won't be seriously started until it's done.
This project only has one main developer and that developer's time and motivation are limited, so an ETA is impossible.

- Thread-safe concurrency on a single simulation, enabling parallel unit testing.
- Realistic lock emulation, enabling deadlock testing.
- Unreliable connection simulation, to enable testing of application-side recovery/retry mechanisms.
- Support commonly-used non-Entity Framework features.
- Usage metrics, enabling test validation of proper techniques, such as avoiding looped queries or excessive parameter counts.
- Physical storage of data, enabling larger-than-RAM databases and faster initialization.
- Network protocol emulation, enabling applications to connect to the simulator as if it were a real SQL Server.
  - This almost certainly would be a separate library as it introduces a variety of new network and cryptography dependencies compared to the core engine which has no dependencies outside of .NET itself.
  - Following this would be work to enable compatibility with tools like SQL Server Management Studio.
