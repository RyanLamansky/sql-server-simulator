# SQL Server Simulator for .NET

An in-process, _zero-dependency_ stand-in for `Microsoft.Data.SqlClient` and SQL Server.
Consumers create a `Simulation`, get a `DbConnection` from `CreateDbConnection()`, and use it with Entity Framework Core or raw ADO.NET the same way they would with a real SQL Server.

Intended for fast unit testing of SQL Server-backed applications.
Can create and discard thousands of databases every second, enabling test scenarios with conflicting data dependencies to run concurrently.

## Example

```C#
using Microsoft.EntityFrameworkCore;
using SqlServerSimulator;

var simulation = new Simulation();
// If you have a bacpac file, you can import it with simulation.ImportBacpac.

// Commands can be run directly against the simulation, used here to create a table.
using (var connection = simulation.CreateDbConnection())
using (var command = connection.CreateCommand())
{
    command.CommandText = "create table ExampleRecord ( Id int )";

    connection.Open();
    _ = command.ExecuteNonQuery();
}

// Entity Framework thinks it's talking to a real SQL Server.
using (var context = new SimulatedContext(simulation))
{
    _ = context.ExampleRecord.Add(new() { Id = 1 });
    _ = context.SaveChanges();
}

// The simulation state is preserved across EF DbContexts.
using (var context = new SimulatedContext(simulation))
{
    var receivedValue = context.ExampleRecord.Select(x => x.Id);

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

<!-- Not shipping this until someone asks for it.
The companion `SqlServerSimulator.EFCore` package adds `UseSqlServerSimulator(...)` for entities that use CLR/store-type pairs whose EF default mappings downcast to `SqlParameter` (`DateOnly`/`DateTime`→`date`/`smalldatetime`, `TimeOnly`/`TimeSpan`→`time(N)`, `decimal`→`money`/`smallmoney`). Without it, those mappings throw at SaveChanges. The base-ADO.NET types in the example above don't need it.
-->

## Network endpoint

A simulation can optionally listen on a real TDS endpoint over loopback TCP with TLS, so genuine SQL Server clients connect to it exactly as they would to a real server:

```C#
await using var listener = await simulation.ListenLocalAsync(11433);
// Now connect with any SQL Server client, e.g.:
//   Server=127.0.0.1,11433;User ID=dev;Password=anything;TrustServerCertificate=True
```

Real `Microsoft.Data.SqlClient` works end-to-end — including parameterized RPC, table-valued parameters, `SqlBulkCopy`, MARS, and query cancellation — as does Entity Framework Core over the wire via a plain connection string.
SQL Server Management Studio connects and browses: Object Explorer, the query editor, and object scripting run against the simulator, which presents itself as SQL Server 2025 (build 17.0.4065.4).
By default any credentials are accepted; run `CREATE LOGIN` to switch the endpoint to enforced authentication.
For clients on other machines, `ListenNetworkAsync` binds all interfaces — it requires at least one registered login up front, so an open endpoint can never face a network.

## Fidelity

Behavior was probed against a live SQL Server reference instance before being modeled.
SQL Server's quirks, inconsistencies, and surprises are mostly preserved.
Error messages usually match, down to the `Msg` number, severity, and wording of common diagnostics.

Entity Framework Core trusts the simulator end-to-end: LINQ queries, migrations, change tracking, and the SaveChanges pipeline all flow through unchanged.
The test suite — more than 8,000 cases — also drives real SqlClient, real SMO (the library behind SSMS), and EF Core against the simulator as independent oracles.

## Capabilities

Coverage is broad; the compact map below is the shape of it, not the full inventory:

- **Types and storage.**
  Every base scalar type family including MAX-typed LOBs, `sql_variant`, `xml`, `hierarchyid`, `geography`/`geometry`, and the legacy `text`/`ntext`/`image` trio; per-column collations; real 8KB pages with byte-encoded rows and off-row LOB storage.
- **Query surface.**
  All JOIN and APPLY forms, correlated subqueries at arbitrary depth, window functions, recursive CTEs, set operations, `PIVOT`/`UNPIVOT`, `OFFSET`/`FETCH`, cursors (T-SQL and API server cursors).
- **DML and DDL.**
  `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT INTO` with `OUTPUT`, statement-level atomicity, and `CREATE`/`ALTER`/`DROP` across tables, views, procedures, functions, triggers, sequences, indexes (including filtered and indexed views), types, and schemas.
- **Programmability.**
  Stored procedures, scalar UDFs and TVFs, DML + DDL triggers, dynamic SQL, table-valued parameters, control flow with `TRY`/`CATCH`/`THROW`.
- **Concurrency.**
  The full lock-mode matrix with escalation and timeouts, `SNAPSHOT` and `READ_COMMITTED_SNAPSHOT` isolation with a versioned store, deadlock detection, application locks, and nested transactions with savepoints.
- **Constraints.**
  `PRIMARY KEY`/`UNIQUE`/`CHECK`/`NOT NULL` and `FOREIGN KEY` with all four referential actions on both `ON DELETE` and `ON UPDATE`.
- **System surfaces.**
  A `sys.*` / `INFORMATION_SCHEMA.*` catalog broad enough to satisfy SSMS, SMO, and DacFx; temporal tables; `SERVERPROPERTY` and friends as true `sql_variant`.
- **JSON, XML, spatial, full-text DDL.**
  The `JSON_*`/`OPENJSON` family, XML methods and schema collections, the spatial method surface, and full-text catalog/index DDL.
- **Security.**
  Logins and users, database and server roles (fixed and custom), and `GRANT`/`DENY`/`REVOKE` enforced at object, schema, and database scope for restricted principals, with `EXECUTE AS` impersonation, module `WITH EXECUTE AS`, and ownership chaining.
- **Scale-out shapes.**
  Multiple databases with cross-database reads, linked servers between simulations, and BACPAC import for bootstrapping from a real database.

Deeper per-feature notes live under [`docs/claude/`](docs/claude/).

## Not modeled

SQL Server's surface is enormous, and while coverage is broad it is not complete: you may encounter a feature that hasn't been modeled.
In general, when that happens the simulator raises `NotSupportedException` naming the missing feature, so gaps fail loudly rather than returning wrong results.
Write a bug report if you're blocked.
A few examples:

- Cross-database DML - writes through a 3-part name targeting a different database.
  Cross-database reads work; issue `USE <db>` to switch first for writes.
- `BEGIN DISTRIBUTED TRANSACTION`, `BEGIN TRANSACTION ... WITH MARK`, `GOTO`/labels.
- `CREATE ASSEMBLY` and CLR functions; logon triggers; natively-compiled procedures beyond parser fidelity.
- `RANGE BETWEEN <N> PRECEDING/FOLLOWING` numeric-offset windows (`ROWS` numeric-offset ships).
- A few `ALTER TABLE` shapes: `DROP PERIOD FOR SYSTEM_TIME`, `REBUILD`, `SWITCH PARTITION`, identity-type changes.

## Limitations

- No physical storage - all data lives in memory for the lifetime of the `Simulation`.
  Suited to test runs and bounded workloads, not larger-than-RAM datasets.
- The network endpoint is meant for development tooling and tests, not untrusted clients.
  `ListenNetworkAsync` enforces authentication, and `GRANT`/`DENY` authorization applies once a session runs as a restricted principal - but a login with no `CREATE USER ... FOR LOGIN` mapping (and any `sysadmin` member) runs as `dbo` with unrestricted access, and the simulator makes no hardening claims as a security boundary.
