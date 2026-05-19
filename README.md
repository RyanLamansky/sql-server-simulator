# SQL Server Simulator for .NET

An in-process, _zero-dependency_ .NET 10 stand-in for `Microsoft.Data.SqlClient`. Consumers create a `Simulation`, get a `DbConnection` from `CreateDbConnection()`, and use it with Entity Framework Core (or raw ADO.NET) the same way they would with a real SQL Server.

Intended for fast unit testing of SQL Server-backed applications. Can create and discard thousands of databases every second, enabling test scenarios with conflicting data dependencies to run concurrently.

## Example

```C#
using Microsoft.EntityFrameworkCore;
using SqlServerSimulator;

var simulation = new Simulation();
// If you have a bacpac file, you can import it with smulation.ImportBacpac.

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

The companion `SqlServerSimulator.EFCore` package adds `UseSqlServerSimulator(...)` for entities that use CLR/store-type pairs whose EF default mappings downcast to `SqlParameter` (`DateOnly`/`DateTime`→`date`/`smalldatetime`, `TimeOnly`/`TimeSpan`→`time(N)`, `decimal`→`money`/`smallmoney`). Without it, those mappings throw at SaveChanges. The base-ADO.NET types in the example above don't need it.

## Fidelity

Behavior was probed against a live SQL Server reference instance before being modeled. SQL Server's quirks, inconsistencies, and suprises are mostly preserved. Error messages usually match.

Entity Framework Core trusts the simulator end-to-end: LINQ queries, migrations, change tracking, and the SaveChanges pipeline all flow through unchanged.

## Capabilities

The simulator is feature-rich with more than 5,000 test cases covering a variety of capabilities:

- **Type system.** All base scalar families: `int`/`bigint`/`smallint`/`tinyint`, `decimal`/`numeric`, `float`/`real`, `money`/`smallmoney`, `bit`, `char`/`varchar`/`text`, `nchar`/`nvarchar`/`ntext`, `binary`/`varbinary`/`image`, `date`/`time`/`datetime`/`datetime2`/`datetimeoffset`/`smalldatetime`, `uniqueidentifier`, `rowversion`/`timestamp`, `xml`, `hierarchyid`, `geography`, `geometry`. MAX-typed strings and binaries flow through an 8KB LOB page chain.
- **Storage.** Real 8KB pages, byte-encoded rows navigated column-by-column without rehydrating, off-row LOB pushing to keep rows within the 8060-byte limit.
- **DDL.** `CREATE`/`ALTER`/`DROP` for tables, schemas, views, procedures, scalar UDFs, TVFs, triggers (DML + DDL), sequences, indexes (UNIQUE/CLUSTERED/INCLUDE/filtered), table types, alias (UDDT) types, and XML schema collections.
- **DML.** `INSERT`, `UPDATE`, `DELETE`, `MERGE` (all WHEN-clause families with multiple AND-conditioned forms), `SELECT INTO`, and `OUTPUT` (including `OUTPUT INTO`). Statement-level atomicity: a multi-row mutation failing partway through rolls back its partial writes.
- **Query.** All JOIN types including `CROSS APPLY`/`OUTER APPLY`; correlated subqueries at arbitrary nesting depth; `EXISTS`/`IN`/`ANY`/`SOME`/`ALL`; window functions; CTEs including recursive; set operations (`UNION`/`UNION ALL`/`INTERSECT`/`EXCEPT`); `OFFSET`/`FETCH`; `CASE`.
- **Constraints.** `PRIMARY KEY`, `UNIQUE`, `NOT NULL`, `CHECK` (inline + table-level), `FOREIGN KEY` with all four referential actions (`NO ACTION`/`CASCADE`/`SET NULL`/`SET DEFAULT`) on both `ON DELETE` and `ON UPDATE`, cascade-cycle detection at CREATE.
- **Transactions.** Implicit, ADO.NET API (`BeginTransaction`/`Commit`/`Rollback`), and T-SQL (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`) - all sharing one undo log. Nested `BEGIN TRAN` honors SQL Server's "only outermost commit actually commits" rule; `@@TRANCOUNT` reflects depth.
- **Locking & MVCC.** Full 8-mode lock matrix, row-X writers and row-mode readers, all standard table hints (`NOLOCK`/`HOLDLOCK`/`UPDLOCK`/`XLOCK`/`TABLOCK`/`READPAST`/`REPEATABLEREAD`), lock escalation at 5000 row-locks, `SNAPSHOT` and `READ_COMMITTED_SNAPSHOT` isolation with version chains and GC, `Msg 1205` deadlock detection, `Msg 1222` lock-timeout, lock-related DMVs.
- **Temporal tables.** `PERIOD FOR SYSTEM_TIME`, system-versioned history sibling tables, `FOR SYSTEM_TIME ALL/AS OF`, `temporal_type` exposed through `sys.tables`.
- **Programmable objects.** Scalar UDFs, table-valued functions, views, stored procedures with `OUTPUT` parameters and result sets, DML and DDL triggers (`INSERTED`/`DELETED` pseudo-tables, `TRIGGER_NESTLEVEL`, recursion control), dynamic SQL via `EXEC(@sql)` and `sp_executesql`, table-valued parameters with the ADO.NET `SqlDbType.Structured` flow.
- **Temp tables and table variables.** `#foo` local temp tables (per-session, cleaned up at connection dispose, transactional CREATE/DROP); `DECLARE @t TABLE` with column constraints (IDENTITY, UNIQUE, CHECK, computed, rowversion); `OUTPUT … INTO @t`.
- **Control flow.** `IF`/`ELSE`, `WHILE`/`BREAK`/`CONTINUE`, `RETURN`, `TRY`/`CATCH`/`THROW`, the `ERROR_*` family, `PRINT`, `WAITFOR DELAY`/`WAITFOR TIME`.
- **JSON.** `JSON_VALUE`, `JSON_QUERY`, `JSON_MODIFY`, `OPENJSON` (with and without WITH-clause schema).
- **XML.** `xml` data type, XML schema collections, the `.value()`/`.query()`/`.nodes()`/`.exist()`/`.modify()` method family, XML indexes.
- **Spatial.** `geography` and `geometry` types with their full method dispatch (`STDistance`, `STIntersects`, `STArea`, `STContains`, `STBuffer`, etc.), static constructors (`::Point`, `::STGeomFromText`), spatial indexes.
- **Catalog views.** `sys.tables`, `sys.columns`, `sys.indexes`, `sys.foreign_keys`, `sys.foreign_key_columns`, `sys.objects`, `sys.schemas`, `sys.triggers`, `sys.sequences`, `sys.types`, `sys.extended_properties`, plus the corresponding `INFORMATION_SCHEMA.*` surfaces.
- **Permissions and principals.** `GRANT`/`REVOKE`/`DENY`, principal DDL, fixed-principal seeding.
- **Built-in scalars.** The math, date, string-manipulation, current-time, `*FROMPARTS`, `AT TIME ZONE`, `CONCAT`/`CONCAT_WS`, `FORMAT`, `STRING_SPLIT`, `STRING_AGG`, `COMPRESS`/`DECOMPRESS`, char-code, and hash families.
- **Multi-database.** `USE <db>` switches the session's current database; 3-part names (`other.dbo.t`) route reads across databases; `Simulation.Databases` exposes the dictionary.
- **BACPAC import.** `Simulation.ImportBacpac(...)` loads a `.bacpac` file end-to-end (schema + data via BCP wire format), supporting the round-trip-from-real-SQL-Server bootstrap path.
- **Error fidelity.** Every modeled error path raises `SimulatedSqlException` with the matching `Msg` number - including the exact wording for common diagnostics like Msg 102 syntax, Msg 207 invalid column, Msg 208 invalid object, Msg 547 constraint conflict, Msg 2627 unique violation, Msg 1205 deadlock victim, Msg 8152 string-or-binary-truncation, and many more.

Deeper per-feature notes live under [`docs/claude/`](docs/claude/).

## Not modeled

The simulator raises `NotSupportedException` (naming the missing feature) for known valid SQL Server features it hasn't built. Write a bug report if you're blocked. Some examples:

- Cross-database DML - writes through a 3-part name targeting a different database. Cross-database reads work; issue `USE <db>` to switch first for writes.
- `BEGIN DISTRIBUTED TRANSACTION`, `BEGIN TRANSACTION ... WITH MARK`, `GOTO`/labels.
- `RANGE BETWEEN <N> PRECEDING/FOLLOWING` numeric-offset windows (`ROWS` numeric-offset ships).
- CLR functions, logon triggers, natively-compiled procedures beyond parser fidelity.
- `LIKE COLLATE` override, `CONVERT` styles outside the shipped set.
- A few `ALTER TABLE` shapes: `DROP PERIOD FOR SYSTEM_TIME`, `REBUILD`, `SWITCH PARTITION`, identity-type changes.
- Byte-identical CAST encoding for `hierarchyid` / `geography` / `geometry` - simulator-native encoding is used internally; cross-engine byte transfer is deferred.

## Limitations

- No physical storage - all data lives in memory for the lifetime of the `Simulation`. Suited to test runs and bounded workloads, not larger-than-RAM datasets.
- No network protocol. Tools like SQL Server Management Studio can't connect; the simulator is reached only through the in-process `DbConnection` it hands out.
