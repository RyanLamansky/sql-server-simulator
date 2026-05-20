# Linked servers

Cross-`Simulation` four-part-name reads. Activation is two-step:

1. Host code calls `Simulation.AddRemoteSimulation(name, otherSim)` to bind the remote `Simulation` under a server name.
2. SQL text calls `EXEC sp_addlinkedserver @server = 'name'` to activate SQL-visible routing.

Both steps are required — a bare `AddRemoteSimulation` is silent until `sp_addlinkedserver` reads from `Simulation.AvailableRemotes` and stamps an entry into `Simulation.ActiveLinkedServers`.

The public API expansion is one method: `Simulation.AddRemoteSimulation(string, Simulation)`. Listed in [`QualityTests.PublicApiWhitelist`](../../SqlServerSimulator.Tests/QualityTests.cs).

## Routing

Four-part-name `srv.db.schema.t` references in FROM (parsed in [`Selection.cs::ParseSingleFromSource`](../../SqlServerSimulator/Parser/Selection.cs)) route through [`BatchContext.TryResolveLinkedServerTable`](../../SqlServerSimulator/Parser/BatchContext.cs): leading segment → `Simulation.ActiveLinkedServers`, then 2nd/3rd/4th segments → remote's `Databases` / `Schemas` / `HeapTables` (direct in-process dict access, matching real SQL Server's "metadata at compile, data at execute" linked-server contract).

Execution opens a fresh `SimulatedDbConnection` on the remote and issues `SELECT * FROM [db].[schema].[t]` through the remote's full pipeline: parser, planner, lock manager, exception factories, session state. The remote materializes the projection via `RowEncoder.EncodeRow(SqlType[], SqlValue[])` (no LOB store), so the byte rows are self-contained and cross-`Simulation`-portable — the local plan reads them via the same `RowDecoder` path as any other `FromSource`.

[`Selection.LinkedServer.cs::StreamRemoteRows`](../../SqlServerSimulator/Parser/Selection.LinkedServer.cs) buffers the remote rows into a list before returning so the remote connection / command / reader chain disposes before the local plan consumes them. Drops remote locks promptly; matches the "fresh remote session per remote query" semantic of real SQL Server.

## What's shipped

- **Read paths**: SELECT, JOIN (INNER / LEFT / RIGHT / FULL / CROSS / APPLY) across a four-part reference. Correlated subqueries re-execute the remote query per outer row via the existing lateral-plan re-execution pattern.
- **Sprocs**: `sp_addlinkedserver` (activate), `sp_dropserver` (Msg 15015 on miss), `sp_addlinkedsrvlogin` / `sp_droplinkedsrvlogin` / `sp_serveroption` (parse-and-discard — no principal-mapping or per-server-option model).
- **`sys.servers`**: local instance as row 0 (`is_linked = 0`, name `"SIMULATED"`), one row per active linked server with the `srvproduct` / `provider` / `datasrc` from `sp_addlinkedserver`. Load-bearing 6-column subset (`server_id`, `name`, `product`, `provider`, `data_source`, `is_linked`) of real SQL Server's ~26-column shape.

## What's not shipped

- **Writes** through a four-part name (INSERT / UPDATE / DELETE / MERGE targeting `srv.db.schema.t`) raise `NotSupportedException` via [`BatchContext.RejectCrossDatabaseMutation`](../../SqlServerSimulator/Parser/BatchContext.cs). Lock-manager and undo-log coordination across `Simulation` boundaries aren't modeled — parallels the existing `BEGIN DISTRIBUTED TRANSACTION` rejection. Open a `SimulatedDbConnection` directly on the target `Simulation` to mutate it.
- **Catalog views through four-part names** (`srv.db.sys.tables`): the resolver returns false (catalog views are 2- or 3-part-only — see [`BatchContext.TryResolveCatalogView`](../../SqlServerSimulator/Parser/BatchContext.cs)), so the call falls through to Msg 208 instead of routing to the remote's `sys.tables`. Cross-server diagnostic queries should run against the remote `Simulation` directly.
- **Predicate / projection pushdown**: every four-part-name reference pulls the full remote table. Correct but slow; matches the agreed initial scope (no use case demands optimization yet).
- **LOB columns**: SELECT-projection output uses the type-only `RowEncoder.EncodeRow` overload (no LOB store), so values stay inline. A `varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` payload large enough to overflow the 65535-byte var-section cap would raise during encoding on the remote — pre-existing simulator behavior, just rarely hit. No round-trip-specific gap.
- **Unknown linked-server leading segment**: surfaces as Msg 208 (the simulator's existing default for missing objects), not real SQL Server's Msg 7202 ("Could not find server '<X>' in sys.servers"). Different error code; same end state.
- **`@@SERVERNAME`** isn't routed — the local-server row in `sys.servers` uses the constant `"SIMULATED"` for `name` regardless of any host-configured value.

## sys.servers shape

| Column | Type | Notes |
|---|---|---|
| `server_id` | int | 0 = local, 1+ = monotonic over linked servers in name-sort order |
| `name` | sysname | `"SIMULATED"` for local; the registered name for linked |
| `product` | nvarchar(128) | `"SQL Server"` for local; `@srvproduct` arg from sp_addlinkedserver for linked |
| `provider` | nvarchar(128) | NULL for local; `@provider` arg (defaults to `"SQLNCLI"`) for linked |
| `data_source` | nvarchar(4000) | NULL for local; `@datasrc` arg or NULL if unspecified |
| `is_linked` | bit | 0 / 1 |

Stable ordering across runs (name-sorted with the local row first). Distinct from real SQL Server's `server_id` allocation, which is `sys.servers`-row-driven and persists across restarts.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 15015 | `sp_dropserver 'X'` where X isn't an active linked server: `"The server 'X' does not exist. Use sp_helpserver to show available servers."` |
| 15600 | `sp_addlinkedserver` / `sp_dropserver` / etc. with an invalid parameter (unknown @-name, missing required arg, positional past the parameter list). |
| 208 | Four-part name whose leading segment isn't an active linked server. (Real SQL Server raises Msg 7202; the simulator routes through the same `InvalidObjectName` path as a missing 1- to 3-part table.) |
