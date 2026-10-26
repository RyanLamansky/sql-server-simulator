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

## BACPAC import

A simulation can be seeded from a `.bacpac`, the schema-and-data archive SSMS writes under **Tasks → Export Data-tier Application**, and `sqlpackage /Action:Export` produces from the command line.
It's the quick way to point a test suite at the shape of a real database instead of maintaining a creation script that might drift from it:

```C#
var simulation = new Simulation();

// Without options, the database takes its name from the file.
simulation.ImportBacpac("prod-export.bacpac", out var result, new() { DatabaseName = "Northwind" });

// Elements the simulator doesn't model end-to-end are reported rather than thrown.
// A single gap can't stop the rest of the database from loading.
foreach (var skipped in result.Skipped)
    Console.WriteLine($"{skipped.ElementType} {skipped.ElementName}: {skipped.Reason}");
```

That reporting is worth wiring up: `Skipped` names what the loader couldn't translate at all, `Warnings` what it accepted with reduced fidelity, and together they're a feature-gap report.
Import is create-only, so a database name already present in the simulation raises `InvalidOperationException`.
A `Stream` overload covers bacpacs that don't come from a file path.

## Backup and restore

`BACKUP DATABASE … TO DISK` and `RESTORE DATABASE … FROM DISK` work, so a suite can capture a database once and put it back before every test the way it would against SQL Server.
Restoring a database that still has the backup's definitions only replaces its data, which takes a fraction of a second even for a database of a few hundred tables.

The backup file is the simulator's own format, readable only by the simulator build that wrote it — not a real `.bak`.
By default the simulated server's disk lives in memory inside the simulation.
To read and write real files, supply `OpenServerFile`, which serves `BULK INSERT` and `OPENROWSET(BULK …)` too.
It receives each path as SQL Server on Linux reads it — rooted, `/` between names — so map it into a folder kept for the purpose:

```C#
var root = Path.GetFullPath("server-disk");
var simulation = new Simulation
{
    OpenServerFile = (path, access) =>
    {
        var full = Path.GetFullPath(Path.Join(root, path));
        if (path.Contains(':') || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        if (access == FileAccess.Read)
            return File.Exists(full) ? File.OpenRead(full) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return File.Create(full);
    },
};
```

## Fidelity

Behavior was probed against a live SQL Server reference instance before being modeled.
SQL Server's quirks, inconsistencies, and surprises are mostly preserved.
Error messages usually match, down to the `Msg` number, severity, and wording of common diagnostics.

Entity Framework Core trusts the simulator end-to-end: LINQ queries, migrations, change tracking, and the SaveChanges pipeline all flow through unchanged.
The test suite — more than 30,000 cases — also drives real SqlClient, real SMO (the library behind SSMS), and EF Core against the simulator as independent oracles.

## Capabilities

Coverage is broad; the compact map below is the shape of it, not the full inventory:

- **Types and storage.**
  Every base scalar type family including MAX-typed LOBs, `sql_variant`, `xml`, `hierarchyid`, `geography`/`geometry`, and the legacy `text`/`ntext`/`image` trio; per-column collations; real 8KB pages with byte-encoded rows and off-row LOB storage.
- **Query surface.**
  All JOIN and APPLY forms, correlated subqueries at arbitrary depth, window functions, recursive CTEs, set operations, `PIVOT`/`UNPIVOT`, `OFFSET`/`FETCH`, cursors (T-SQL and API server cursors).
- **DML and DDL.**
  `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT INTO` with `OUTPUT` and composable DML, statement-level atomicity, and `CREATE`/`ALTER`/`DROP` across tables, views, procedures, functions, triggers, sequences, indexes (including filtered, columnstore and indexed views), types, and schemas.
- **Table features.**
  Temporal tables, change tracking, partitioning, memory-optimized tables with natively compiled modules, graph tables with `MATCH` and `SHORTEST_PATH`, and rules and defaults.
- **Programmability.**
  Stored procedures, scalar UDFs and TVFs, DML, DDL and logon triggers, dynamic SQL, table-valued parameters, control flow with `TRY`/`CATCH`/`THROW`, and CLR assemblies (set `EnableClr` on the `Simulation`).
- **Concurrency.**
  The full lock-mode matrix with escalation and timeouts, `SNAPSHOT` and `READ_COMMITTED_SNAPSHOT` isolation with a versioned store, deadlock detection, application locks, and nested transactions with savepoints.
- **Constraints.**
  `PRIMARY KEY`/`UNIQUE`/`CHECK`/`NOT NULL` and `FOREIGN KEY` with all four referential actions on both `ON DELETE` and `ON UPDATE`.
- **System surfaces.**
  A `sys.*` / `INFORMATION_SCHEMA.*` catalog broad enough to satisfy SSMS, SMO, and DacFx; `SERVERPROPERTY` and friends as true `sql_variant`; `DBCC`; Query Store; and `SET SHOWPLAN_*` plans.
- **Server infrastructure.**
  Service Broker with activation, Extended Events, SQL Trace, SQL Server Audit, SQL Server Agent's `msdb` procedures (as a server whose Agent is stopped answers them), and Database Mail's configuration and queue.
- **JSON, XML, spatial, full-text, vectors.**
  The `JSON_*`/`OPENJSON` family and the native `json` type, XML methods and schema collections, the spatial method surface, full-text search with `CONTAINS`/`FREETEXT`, and SQL Server 2025's `vector` type and `REGEXP_*` functions.
- **Security.**
  Logins, users, and database + server roles (fixed and custom), with authenticated logins mapped to their database user the way SQL Server does.
  `GRANT`/`DENY`/`REVOKE` are enforced for restricted principals at column, object, schema, and database scope — alongside `EXECUTE AS` impersonation, module `WITH EXECUTE AS`, ownership chaining, catalog metadata filtered to a principal's visibility, and `VIEW SERVER STATE`-gated DMVs.
  Row-level security, dynamic data masking, and the master key, certificates, asymmetric and symmetric keys with the functions that encrypt, decrypt and sign with them.
- **Scale-out shapes.**
  Multiple databases with cross-database reads and writes, linked servers between simulations, BACPAC import for bootstrapping from a real database, full and differential `BACKUP` / `RESTORE` with log backups, and `BULK INSERT` / `OPENROWSET(BULK …)` over files supplied through `OpenServerFile`.

Deeper per-feature notes live under [`docs/claude/`](docs/claude/).

## Not modeled

SQL Server's surface is enormous, and while coverage is broad it is not complete: you may encounter a feature that hasn't been modeled.
When that happens the simulator raises `NotSupportedException` naming the missing feature, so gaps fail loudly rather than returning wrong results.
Write a bug report if you're blocked.
The larger areas not built yet:

- Encryption: certificates and keys loaded from files, module signing, Always Encrypted and transparent data encryption.
- Server infrastructure: endpoints, Resource Governor, availability groups and replication.
- External data and storage: PolyBase external tables and data sources, change data capture, ledger tables, database snapshots and FILESTREAM.
- Execution plans: `SET SHOWPLAN_*` describes each statement without an operator tree, and `SET STATISTICS XML` returns no plans.

Running a `CREATE ASSEMBLY` routine executes the assembly's code inside your process.
The simulator verifies a `SAFE` assembly statically, but .NET has no Code Access Security, so that check is not a sandbox: only register assemblies you trust.

## Limitations

- No physical storage - all data lives in memory for the lifetime of the `Simulation`.
  Suited to test runs and bounded workloads, not larger-than-RAM datasets.
- The network endpoint is meant for development tooling and tests, not untrusted clients.
  Authorization is enforced once you configure principals - logins, users, roles, and grants; with none configured a `Simulation` runs open (every connection is `dbo`), and sysadmin members and unauthenticated in-process connections are `dbo` by design.
  It's an in-memory simulator built for fidelity, not a hardened security boundary - so lean on it to test your access model, not to stand in front of hostile input.
