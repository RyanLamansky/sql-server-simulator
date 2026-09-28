# Linked servers

Cross-`Simulation` four-part names, `OPENQUERY`, `EXEC … AT` and remote procedure calls.
Activation is two-step:

1. Host code calls `Simulation.AddRemoteSimulation(name, otherSim)` to bind the remote `Simulation` under a server name.
2. SQL text calls `EXEC sp_addlinkedserver @server = 'name'` to activate SQL-visible routing.

Both steps are required — a bare `AddRemoteSimulation` is silent until `sp_addlinkedserver` reads from `Simulation.AvailableRemotes` and stamps an entry into `Simulation.ActiveLinkedServers`.
A `Simulation` may name itself, which is a **loopback** and behaves as real's loopback linked server does where the two differ (the transaction refusal below).

The public API expansion is one method: `Simulation.AddRemoteSimulation(string, Simulation)`.
Listed in [`QualityTests.PublicApiWhitelist`](../../tests/SqlServerSimulator.Tests/QualityTests.cs).

Everything below was probed 2026-09-28 against SQL Server 2025 through a loopback MSOLEDBSQL linked server, and — for what a loopback can't show, the distributed-transaction refusal — against a second SQL Server 2025 instance.
The edge-probe harness (`.vs/edge-probe`, `--linked`) maps a loopback `lb` on both sides.

## Reads

Four-part-name `srv.db.schema.t` references in FROM (parsed in [`Selection.cs::ParseSingleFromSource`](../../src/SqlServerSimulator/Parser/Selection.cs)), and a MERGE's `USING` source, route through [`BatchContext.TryResolveLinkedServerTable`](../../src/SqlServerSimulator/Parser/BatchContext.cs): leading segment → `Simulation.ActiveLinkedServers`, then 2nd/3rd/4th segments → the remote's table or view (direct in-process dict access, matching real SQL Server's "metadata at compile, data at execute" linked-server contract).
A `sys` or `INFORMATION_SCHEMA` name reads the remote's catalog view, its columns found by running it once as `OPENQUERY` does.

Execution opens a fresh `SimulatedDbConnection` on the remote, in the named database, and issues `SELECT * FROM [db].[schema].[t]` through the remote's full pipeline: parser, planner, lock manager, exception factories, session state.
An error the remote raises comes back as the remote raised it.
The remote materializes the projection via `RowEncoder.EncodeRow(SqlType[], SqlValue[])` (no LOB store), so the byte rows are self-contained and cross-`Simulation`-portable — the local plan reads them via the same `RowDecoder` path as any other `FromSource`.
`RemoteWrite.RunRemoteQuery` buffers the rows before the remote connection disposes, which drops remote locks promptly; matches the "fresh remote session per remote query" semantic of real SQL Server.

A read inside a local transaction needs no distributed transaction — only a `BEGIN DISTRIBUTED TRANSACTION` makes one enlist (see [Transactions](#transactions)).

## OPENQUERY

`OPENQUERY(server, 'query')` is the inline ad-hoc pass-through form over the same remote-execution seam as four-part-name reads.
It's a FROM / JOIN / derived-table source that runs a verbatim query string on a linked server and returns its **first result set** as a rowset, and a write target (see [Writes](#writes)).

Grammar (probed against SQL Server 2025): exactly two arguments — a bare **identifier** (plain or bracketed) naming the linked server, and a bare **string literal** (`'...'` / `N'...'`, doubled-quote `''` escaping handled by the tokenizer) carrying the pass-through query.
Parsed in [`Selection.LinkedServer.cs::ParseOpenQueryArguments`](../../src/SqlServerSimulator/Parser/Selection.LinkedServer.cs); dispatched from a `ReservedKeyword { Keyword: OpenQuery }` arm in `Selection.cs::ParseSingleFromSourceCore` (OPENQUERY is a reserved keyword, so it never rides the Name-token rowset dispatch used by OPENJSON / STRING_SPLIT), from a keyword check in `ParseLateralFromSource` so it works in the JOIN / APPLY position too, and from the INSERT / UPDATE / DELETE target positions.
Syntax errors fire **before** server resolution:

- Server slot a literal / number / dotted `a.b` → **Msg 102** (the token after the identifier isn't `,`).
- Query slot a variable (`@q`), a concatenation (`'a'+'b'`), or a 3rd argument → **Msg 102** (the token after the string literal isn't `)`).
- Too few args (`OPENQUERY(Srv)`) → **Msg 102**.
- Column-alias list (`OPENQUERY(...) q(c1, c2)`) → **Msg 102** near the opening `(`.
  Real SQL Server raises Msg 102 near the first alias identifier; the simulator's general FROM parser otherwise *tolerates and ignores* a trailing column-alias list on any source, so OPENQUERY carries an explicit guard to reject it (same Msg number, slightly different "near" token).

**Compile-time schema discovery + per-execution row fetch.**
OPENQUERY's columns aren't known until the remote query runs, so `ParseOpenQuery` executes the query **once at parse time** to capture the first `SimulatedSqlResultSet`'s `Schema` + `ColumnNames` (the discovery pass buffers no rows).
`Selection.ForOpenQuery` then builds a `Selection` whose `rowSource` **re-runs** the query on each `Execute`, streaming the first result set's rows.
The batch is disqualified from plan-cache promotion (`HasSessionScopedReference = true`) because it reads external remote state.

Divergences:
- **Side-effecting payload double-run**: the query runs once for schema discovery plus once per outer execution.
  Fine for the SELECT payloads OPENQUERY targets; a payload with side effects would execute more than a real single pass-through would.
- **No result set** (empty / all-whitespace string, a non-SELECT statement like `DECLARE @x int` or a bare `PRINT`) → `NotSupportedException` naming the condition.
  The exact real-server Msg for this case isn't probed, so the simulator declines to fabricate a number.
- **Scalar / select-list position** (`SELECT OPENQUERY(...)`) isn't handled by the FROM-source path; it falls through the generic unknown-scalar path rather than real SQL Server's Msg 156.

## Writes

`INSERT`, `UPDATE` and `DELETE` take a four-part name (a table or a view, directly or through a synonym) or an `OPENQUERY` as their target, and the joined `UPDATE` / `DELETE … FROM` forms take either through its alias.
[`RemoteWrite`](../../src/SqlServerSimulator/Parser/RemoteWrite.cs) makes it work in two halves:

- **Locally**, the statement runs unchanged against a stand-in: a table-variable-shaped `HeapTable` with the remote table's columns, nullable and unconstrained, loaded with the remote rows (none for an INSERT).
  So every source shape, join, `TOP`, conversion and length check the local engine has applies as-is, and so does `@@ROWCOUNT`.
  The stand-in heap records which addresses the statement's `UpdateAt` / `DeleteAt` reached (`Heap.TouchedSlots`), which is what an UPDATE setting a column to its own value needs.
- **Remotely**, once the local statement succeeds, `RemoteWrite.Replay` sends what it did as parameterized statements inside one remote transaction — the server's constraints, triggers, identity and rowversion judge them there, and the whole write commits or rolls back as one.

The replay's shape follows what real's provider sends, which a remote trigger's firings reveal:
an INSERT goes **row by row** (a two-row insert fires the trigger twice), an UPDATE or DELETE with no FROM clause as **one statement** (one firing for all its rows, and one with none when it matched nothing), and a joined one, like every `OPENQUERY` write, **row by row** through a cursor.
Rows are found again by the key browse mode reports — the primary key, a unique constraint or index, else a `rowversion` column — and on a table with none by matching every column, one row at a time (`UPDATE TOP (1)`), so duplicate rows are touched one each.
A `text`, `ntext`, `image` or `vector` column, which `=` can't compare, matches on its bytes through the MAX type it converts to; real positions its cursor by bookmark instead, which finds a keyless row whatever its types (probed 2026-09-28).

What real reports, all modeled:
- `@@ROWCOUNT` counts the write's rows; `SCOPE_IDENTITY()` and `@@IDENTITY` read NULL after it, whatever identity the server drew — even after a remote INSERT of no rows, and over an earlier local identity.
- **An error the server raises** arrives relayed: every entry in reverse order, so the Msg 3621 the server follows a failed write with, and a trigger's Msg 3609, come ahead of the error; each at the remote statement's line (1) with its own state, save a state of 0, which arrives as 1.
  It **ends the batch** where the same error on a local table ends only the statement, and a `TRY` catches it.
- A value too long for a remote column is the provider's legacy **Msg 8152 at state 14**, raised locally and ending only the statement — where a local column raises Msg 2628.
- An INSERT listing the remote **identity column** is Msg 7344 after the provider's Msg 7412; an UPDATE setting an identity, computed or `rowversion` column is **Msg 8180** ahead of the server's own Msg 8102 / 271 / 272.
- An `OUTPUT` clause on a remote target, or a nested DML source feeding one, is **Msg 405**; a remote `MERGE` target is **Msg 5315** (a remote `USING` source reads as any four-part source does).
- `srv.db..t` as a target is **Msg 7313**; a missing database, schema or table **Msg 7314** naming the written segments in double quotes, as a read's miss is; an unknown server **Msg 7202**.
- An `OPENQUERY` target whose query the provider can't open an updatable cursor over — no table, or a SET column reading an expression — is **Msg 16955** at line 1 after Msg 7412.
  The query runs under browse mode on the server, whose metadata names the one base table its columns write.

## Remote calls

`EXEC ('text' [, argument [OUTPUT]] …) AT server` and `EXEC [@rc =] server.db.schema.proc …` run in a fresh session of the server ([`Simulation.RemoteCalls.cs`](../../src/SqlServerSimulator/Simulation/Simulation.RemoteCalls.cs)), and hand their result sets, messages, counts, `@@ROWCOUNT`, `OUTPUT` values and return code back.
- Each `?` in `EXEC … AT`'s text outside a string, comment or bracketed name binds the next argument, a character one sent as `nvarchar`; an `OUTPUT` constant is Msg 179, and arguments without `AT` are Msg 102 at state 3.
- The server's batch runs on past an error, as a batch of its own does, and the error reaches the client in its place among the results, relayed in reverse as a write's is — but **outside the caller's control flow**: no `TRY` of the caller's catches it, and the caller's batch carries on.
  A procedure call's errors name the procedure as the call spelled it, without the server (`ep0.dbo.p`).
- A procedure call's session starts in the procedure's database; `EXEC … AT`'s, like `OPENQUERY`'s, starts in the server's `@catalog` when `sp_addlinkedserver` named one, else in the login's default database — `master`, as real's `sa` has it (`LinkedServer.SessionDatabaseName`, which a name omitting its database segment reads too).
- The caller's `@@ROWCOUNT` reads the last count the call reported — the rows of its last rowset or its last DML count, whatever `RETURN`, `DECLARE` or `SET` followed and `NOCOUNT` or not — 0 after an error, and stays as it was when the call reported none.
- Both need the server's `rpc out` option (Msg 7411 `… not configured for RPC.`), which `sp_addlinkedserver` turns on for a `SQL Server` product only; `INSERT … EXEC … AT` feeds a local insert.

## Types the provider can't carry

What the provider exposes of a four-part name's table or view (`RemoteWrite.ProviderColumns`), for reads and writes alike, probed 2026-09-28:

- An **`xml`** column anywhere in it refuses the object with Msg 9514 naming it as written (`lb.db.dbo.t`), reported at line 12 wherever the statement sits, while the batch compiles — so nothing in the batch runs.
  A rowset with one is refused too: `OPENQUERY`'s as Msg 9514 naming `OPENQUERY` at its statement's line, and `EXEC … AT`'s or a remote procedure call's naming `IROWSET` at line 1, after what the call sent ahead of it, ending the batch.
- A **CLR-typed** column — `geography`, `geometry`, `hierarchyid` — refuses the object with Msg 7325, which a pass-through query avoids: `OPENQUERY` and `EXEC … AT` return those values.
- A **`json`** column isn't listed: `SELECT *` leaves it out, naming it is Msg 207, and an object whose only columns are `json` is Msg 7357.
  Through a pass-through query it reads as `varchar`.
- A **`vector(n)`** column is listed as `varbinary(8 + 4n)`, its storage form's length; a NULL reads as NULL and a value is Msg 7346, raised as its row is reached and ending the batch, while a write whose statement doesn't set the column works.

## Server options

`sp_serveroption` keeps the three options behavior reads, which `sys.servers` projects: `rpc out`, `data access` (off: four-part names and `OPENQUERY` are Msg 7411 `… not configured for DATA ACCESS.`, while remote calls still run) and `remote proc transaction promotion`.
Real's other option names are accepted and discarded; a value other than `true` / `on` / `false` / `off` (a number or NULL included) or an unknown name is Msg 15600, an unknown server Msg 15015.

## Transactions

Real enlists a linked server in the session's transaction — promoting it to a distributed one — for a write inside a local transaction (an `IMPLICIT_TRANSACTIONS` one included), for a read inside a `BEGIN DISTRIBUTED TRANSACTION`, and for a remote call inside a transaction or feeding `INSERT … EXEC` while `remote proc transaction promotion` is on.
A read inside an ordinary transaction doesn't enlist.

**Out of the box no coordinator accepts it**, so none is modeled as committing: a remote server refuses with **Msg 7391** at the statement's line, after the coordinator's own refusal as Msg 7412 (`The partner transaction manager has disabled its support for remote/network transactions.`), and a loopback with **Msg 3910** (`Transaction context in use by another session.`) at line 1.
Both end the batch and roll the transaction back as under `XACT_ABORT`, and a `TRY` that catches either finds the transaction doomed.
A loopback's remote *call* inside a transaction runs outside it instead, with no error; with `remote proc transaction promotion` off every server's does.
Committing across two `Simulation`s would need a coordinator that real's defaults don't provide, so modeling the refusal is the faithful choice rather than a stopgap.
`SimulatedDbTransaction.IsDistributed` carries the `DISTRIBUTED` keyword.

## What's modeled

- **Sprocs**: `sp_addlinkedserver` (activate), `sp_dropserver` (Msg 15015 on miss), `sp_serveroption` (above), `sp_addlinkedsrvlogin` / `sp_droplinkedsrvlogin` (parse-and-discard — no principal-mapping model).
- **`sys.servers`**: local instance as row 0 (`is_linked = 0`, name `"SIMULATED"`), one row per active linked server carrying `sp_addlinkedserver`'s arguments and the three options — see [sys.servers shape](#sysservers-shape).
- **Provider name in messages**: every SQL Server provider name (`SQLNCLI`, `SQLNCLI11`, `MSOLEDBSQL`, `MSOLEDBSQL19`) reports as `MSOLEDBSQL19`, the driver SQL Server 2025 loads for it.

## Divergences

- **The replay isn't the statement real sends**: the simulator ships values it computed locally, where real ships a remotable single-table UPDATE's text for the server to evaluate — so a nondeterministic expression (`NEWID()`, `GETDATE()`) is evaluated by the local server, not the remote one.
- **A `vector` value read through a four-part name** is Msg 7346 whenever a fetched row holds one, where real raises it only for a query that projects the column — `SELECT id`, `COUNT(*)` and `WHERE v IS NOT NULL` read on real, since its provider fetches only what the query names.
- **A `varbinary` value written into a remote `vector` column** is refused by the server's own conversion, which is Msg 206 here and Msg 13609 on real, whose provider sends it differently.

## Not modeled yet

- **Predicate / projection pushdown**: every four-part-name read pulls the full remote table, and so does a write's stand-in.
- **LOB columns**: the remote projection uses the type-only `RowEncoder.EncodeRow` overload (no LOB store), so a MAX payload large enough to overflow the 65535-byte var-section cap raises during encoding on the remote.
- **`@@SERVERNAME`** isn't routed — the local-server row in `sys.servers` uses the constant `"SIMULATED"` for `name` regardless of any host-configured value.
- **`EXEC … AT DATA_SOURCE`** and `OPENROWSET` / `OPENDATASOURCE` (see [`backlog.md`](backlog.md)).

## sys.servers shape

The view carries real's 26 columns (probed 2026-09-26 against SQL Server 2025); the ones with a rule of their own:

| Column | Notes |
|---|---|
| `server_id` | 0 = local, 1+ = monotonic over linked servers in name-sort order |
| `name` | `"SIMULATED"` for local; the registered name for linked |
| `product` | `"SQL Server"` for local; `@srvproduct` for linked |
| `provider` | `"SQLNCLI"` for local (probed 2026-09-25); `@provider` (defaults to `"SQLNCLI"`) for linked |
| `data_source` | the server name for local and for a `SQL Server` product (probed 2026-09-25); `@datasrc` or NULL otherwise |
| `location` / `provider_string` / `catalog` | `@location` / `@provstr` / `@catalog`, NULL for local |
| `is_remote_login_enabled` | 1 for local and for a `SQL Server` product, 0 for any other product |
| `is_rpc_out_enabled` | as `is_remote_login_enabled` until `sp_serveroption` sets it |
| `is_data_access_enabled` | 1 for linked until `sp_serveroption` clears it, 0 for local |
| `is_remote_proc_transaction_promotion_enabled` | 1 until `sp_serveroption` clears it |
| `modify_date` | when `sp_addlinkedserver` ran; the simulation's seed date for local |

The rest are real's defaults for both kinds: timeouts 0, `uses_remote_collation` 1, the other flags 0 and `collation_name` NULL.

Stable ordering across runs (name-sorted with the local row first).
Distinct from real SQL Server's `server_id` allocation, which is `sys.servers`-row-driven and persists across restarts.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 15015 | `sp_dropserver 'X'` / `sp_serveroption 'X', …` where X isn't an active linked server: `"The server 'X' does not exist. Use sp_helpserver to show available servers."` |
| 201 / 8144 / 8145 | `sp_addlinkedserver` / `sp_dropserver` without `@server`, past their parameter lists, and (`sp_addlinkedserver`) with an unknown `@`-name — real's signatures, `@provstr` and the `bit` `@linkedstyle` included (probed 2026-09-25). |
| 15004 | `sp_addlinkedserver` with a NULL `@server`; `sp_dropserver`'s NULL is Msg 15015 naming `(null)`. |
| 15600 | `sp_dropserver` with a `@droplogins` other than `'droplogins'`, naming `sys.sp_dropserver`; `sp_serveroption`'s unknown option or value, naming `sys.sp_serveroption`. |
| 7202 | A four-part name, `OPENQUERY`, `EXEC … AT` or remote procedure call naming a server `sys.servers` lacks. |
| 7314 / 7313 | A four-part name's missing table; a write target's omitted schema. |
| 7411 | `rpc out` or `data access` off for what needs it. |
| 7391 / 3910 | The distributed transaction a remote server's or a loopback's enlistment needs. |
