# SQL Server Simulator - EF Core Adapter

A companion package to [SQL Server Simulator](https://github.com/RyanLamansky/sql-server-simulator) that closes the gaps where EF Core's SqlServer provider maps a CLR/store-type pair to a `SqlParameter` the simulator's connection can't accept.

Use it when your entities rely on any of these mappings:

- `DateOnly` / `DateTime` → `date` / `smalldatetime`
- `TimeOnly` / `TimeSpan` → `time(N)`
- `decimal` → `money` / `smallmoney`

Without the adapter, those mappings throw at `SaveChanges`. The MAX-string family and the base ADO.NET types flow through plain `UseSqlServer` and don't need it.

## Usage

Call `UseSqlServerSimulator(...)` instead of `UseSqlServer(...)` when configuring the context:

```C#
using Microsoft.EntityFrameworkCore;
using SqlServerSimulator;
using SqlServerSimulator.EFCore;

var simulation = new Simulation();

sealed class SimulatedContext(Simulation simulation) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlServerSimulator(simulation.CreateDbConnection());
}
```

EF Core's SqlServer provider keeps emitting SQL-Server-flavored SQL; the adapter only registers the type mappings the simulator's non-`SqlConnection` connection needs.

See the [main package README](https://github.com/RyanLamansky/sql-server-simulator#readme) for the full picture.
