# Linked servers

Cross-`Simulation` four-part names, `OPENQUERY`, `EXEC … AT` and remote procedure calls.
Activation is two-step:

1. Host code calls `Simulation.AddRemoteSimulation(name, otherSim)` to bind the remote `Simulation` under a server name.
2. SQL text calls `EXEC sp_addlinkedserver @server = 'name'` to activate SQL-visible routing.

Both steps are required — a bare `AddRemoteSimulation` is silent until `sp_addlinkedserver` reads from `Simulation.AvailableRemotes` and stamps an entry into `Simulation.ActiveLinkedServers`.
`sp_addlinkedserver` and its sibling procedures take `ALTER ANY LINKED SERVER` — see [`permissions.md`](permissions.md#statement-gates).
A `Simulation` may name itself, which is a **loopback** and behaves as real's loopback linked server does where the two differ (the transaction refusal below).

The public API expansion is one method: `Simulation.AddRemoteSimulation(string, Simulation)`.
Listed in [`QualityTests.PublicApiWhitelist`](../../tests/SqlServerSimulator.Tests/QualityTests.cs).

Everything below was probed 2026-09-28 against SQL Server 2025 through a loopback MSOLEDBSQL linked server, and — for what a loopback can't show, the distributed-transaction refusal — against a second SQL Server 2025 instance; a 717-case differential sweep (2026-10-05, `.vs/edge-probe` `zzls_*`) re-probed and extended it where a statement says so.
The edge-probe harness (`.vs/edge-probe`, `--linked`) maps a loopback `lb` on both sides.

## Reads

Four-part-name `srv.db.schema.t` references in FROM (parsed in [`Selection.FromClause.cs::ParseSingleFromSource`](../../src/SqlServerSimulator/Parser/Selection.FromClause.cs)), and a MERGE's `USING` source, route through [`BatchContext.TryResolveLinkedServerTable`](../../src/SqlServerSimulator/Parser/BatchContext.Resolution.cs): leading segment → `Simulation.ActiveLinkedServers`, then 2nd/3rd/4th segments → the remote's table or view (direct in-process dict access, matching real SQL Server's "metadata at compile, data at execute" linked-server contract).
A `sys` or `INFORMATION_SCHEMA` name reads the remote's catalog view, its columns found by running it once as `OPENQUERY` does.

Execution opens a fresh `SimulatedDbConnection` on the remote, in the named database, and issues `SELECT <the columns the query names> FROM [db].[schema].[t]` through the remote's full pipeline: parser, planner, lock manager, exception factories, session state.
An error the remote raises compiling the query comes back as the remote raised it; one it raises reading the rows (a view's `1 / 0`, a failed conversion) reaches the reader after the rows ahead of it, relayed at the remote's line, and ends the batch — a `TRY` catches it (probed 2026-10-05 against SQL Server 2025).
A synonym over a four-part name reads the server's table under the synonym's name, and a four-part name called as a function is Msg 4122.
A missing table's Msg 7314 names the server as the query wrote it (`LB`), and like the provider's other metadata refusals (Msg 7325, 7357, 9514) stops the batch at the first statement meeting it — one run through dynamic SQL ending its caller's too.
The remote materializes the projection via `RowEncoder.EncodeRow(SqlType[], SqlValue[])` (no LOB store), so the byte rows are self-contained and cross-`Simulation`-portable — the local plan reads them via the same `RowDecoder` path as any other `FromSource`.
`RemoteWrite.RunRemoteQuery` buffers the rows before the remote connection disposes, which drops remote locks promptly; matches the "fresh remote session per remote query" semantic of real SQL Server.

A read inside a local transaction needs no distributed transaction — only a `BEGIN DISTRIBUTED TRANSACTION` makes one enlist (see [Transactions](#transactions)).

## OPENQUERY

`OPENQUERY(server, 'query')` is the inline ad-hoc pass-through form over the same remote-execution seam as four-part-name reads.
It's a FROM / JOIN / derived-table source that runs a verbatim query string on a linked server and returns its **first result set** as a rowset, and a write target (see [Writes](#writes)).

Grammar (probed against SQL Server 2025): exactly two arguments — a bare **identifier** (plain or bracketed) naming the linked server, and a bare **string literal** (`'...'` / `N'...'`, doubled-quote `''` escaping handled by the tokenizer) carrying the pass-through query.
Parsed in [`Selection.LinkedServer.cs::ParseOpenQueryArguments`](../../src/SqlServerSimulator/Parser/Selection.LinkedServer.cs); dispatched from a `ReservedKeyword { Keyword: OpenQuery }` arm in `Selection.FromClause.cs::ParseSingleFromSourceCore` (OPENQUERY is a reserved keyword, so it never rides the Name-token rowset dispatch used by OPENJSON / STRING_SPLIT), from a keyword check in `ParseLateralFromSource` so it works in the JOIN / APPLY position too, and from the INSERT / UPDATE / DELETE target positions.
Syntax errors fire **before** server resolution:

- Server slot a literal / number / dotted `a.b` → **Msg 102** (the token after the identifier isn't `,`).
- Query slot a variable (`@q`), a concatenation (`'a'+'b'`), or a 3rd argument → **Msg 102** (the token after the string literal isn't `)`).
- Too few args (`OPENQUERY(Srv)`) → **Msg 102**.
- Column-alias list (`OPENQUERY(...) q(c1, c2)`) → **Msg 102** near the first alias name.
  The simulator's general FROM parser otherwise *tolerates and ignores* a trailing column-alias list on any source, so OPENQUERY carries an explicit guard to reject it.

**Compile-time description + per-execution row fetch.**
OPENQUERY's columns aren't known until the server describes the query, so `ParseOpenQuery` runs it **once at parse time under `SET FMTONLY ON`**, which reads its first rowset's shape — types, names and nullability, a key column NOT NULL — without running a statement.
`Selection.ForOpenQuery` then builds a `Selection` whose `rowSource` runs the query on each `Execute`, streaming the first result set's rows.
The batch is disqualified from plan-cache promotion (`HasSessionScopedReference = true`) because it reads external remote state.

What real's provider refuses describing the query, each at the statement's line unless said (probed 2026-10-05 against SQL Server 2025):
- an empty query: the provider's Msg 7412 (`Command text was not set for the command object.`), then Msg 7399 and Msg 7321;
- a name that doesn't bind: Msg 7412 (`Deferred prepare could not be completed.`), then Msg 8180 and the server's error, both at line 1;
- a query that doesn't parse (`'selec 1'`): Msg 11529 and the server's error, both from `sys.sp_describe_first_result_set` at line 1;
- no rowset — a `DECLARE`, a `PRINT`, a `RAISERROR` alone: Msg 7357 quoting the query as the object with no columns;
- two columns of one name: Msg 492.

An error the query raises running reaches the reader after the rows ahead of it and ends the batch, as a four-part read's does.
A column alias list after the source is Msg 102 near its first name, as real reports it.

Divergence: a **scalar / select-list position** (`SELECT OPENQUERY(...)`) isn't handled by the FROM-source path; it falls through the generic unknown-scalar path rather than real SQL Server's Msg 156.

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
- An INSERT listing the remote **identity column** is Msg 7344 after the provider's Msg 7412, as is one giving a NOT NULL column a NULL, in its `The data value violated the integrity constraints for the column.` wording (probed 2026-10-05); an UPDATE setting an identity, computed or `rowversion` column is **Msg 8180** ahead of the server's own Msg 8102 / 271 / 272.
- An `INSERT … EXEC` into a remote table needs a distributed transaction whether or not one is open — Msg 3910 for a loopback, Msg 7391 otherwise — and a transaction holding a savepoint can't be promoted at all (Msg 3933, for a remote call too; probed 2026-10-05).
- An `OUTPUT` clause on a remote target, or a nested DML source feeding one, is **Msg 405**; a remote `MERGE` target is **Msg 5315** (a remote `USING` source reads as any four-part source does).
- `srv.db..t` as a target is **Msg 7313**; a missing database, schema or table **Msg 7314** naming the written segments in double quotes, as a read's miss is; an unknown server **Msg 7202**.
- An `OPENQUERY` target whose query the provider can't open an updatable cursor over — no table, or no base column at all, as an aggregate's — is **Msg 16955** at line 1 after Msg 7412; one whose SET column reads an expression beside base columns is Msg 7344 state 2 naming the table `[MSOLEDBSQL19]` (probed 2026-10-05).
  The query runs under browse mode on the server, whose metadata names the one base table its columns write.

## Remote calls

`EXEC ('text' [, argument [OUTPUT]] …) AT server` and `EXEC [@rc =] server.db.schema.proc …` run in a fresh session of the server ([`Simulation.RemoteCalls.cs`](../../src/SqlServerSimulator/Simulation/Simulation.RemoteCalls.cs)), and hand their result sets, messages, counts, `@@ROWCOUNT`, `OUTPUT` values and return code back.
- Each `?` in `EXEC … AT`'s text outside a string, comment or bracketed name binds the next argument, a character one sent as `nvarchar`; an `OUTPUT` constant is Msg 179, and arguments without `AT` are Msg 102 at state 3.
- The server's batch runs on past an error, as a batch of its own does, and the error reaches the client in its place among the results, relayed in reverse as a write's is — but **outside the caller's control flow**: no `TRY` of the caller's catches it, and the caller's batch carries on.
  An `EXEC … AT` whose **last** statement fails is the exception: that error is the call's own failure, which a `TRY` catches, `@@ERROR` reads and `XACT_ABORT` ends the batch on; a procedure call's is only an error no procedure ran to raise — the procedure not found, its arguments refused (probed 2026-10-05).
  A procedure call's errors name the procedure as the call spelled it, without the server (`ep0.dbo.p`), and a procedure it ran by schema and name (`dbo.inner`).
- The provider's session is a MARS one: a call leaving a transaction open sends Msg 3997 at line 1 ahead of the procedure's one Msg 266, and the transaction rolls back.
- The provider refuses an `EXEC … AT` whose text is empty, or whose arguments outnumber or fall short of its `?` placeholders, with Msg 7412 (`Command text was not set …`, `Multiple-step OLE DB operation …`, `No value given for one or more required parameters.`) then the class-17 Msg 7215, ending the batch.
- A procedure call's session starts in the procedure's database; `EXEC … AT`'s, like `OPENQUERY`'s, starts in the server's `@catalog` when `sp_addlinkedserver` named one, else in the login's default database — `master`, as real's `sa` has it (`LinkedServer.SessionDatabaseName`, which a name omitting its database segment reads too).
- The caller's `@@ROWCOUNT` reads the last count the call reported — the rows of its last rowset or its last DML count, whatever `RETURN`, `DECLARE` or `SET` followed — 0 after an error or for a count the server's `NOCOUNT` withheld (probed 2026-10-05), and stays as it was when the call reported none.
- Both need the server's `rpc out` option (Msg 7411 `… not configured for RPC.`), which `sp_addlinkedserver` turns on for a `SQL Server` product only; `INSERT … EXEC … AT` feeds a local insert.

## Types the provider can't carry

What the provider exposes of a four-part name's table or view (`RemoteWrite.ProviderColumns`), for reads and writes alike, probed 2026-09-28:

- An **`xml`** column anywhere in it refuses the object with Msg 9514 naming it as written (`lb.db.dbo.t`), reported at line 12 wherever the statement sits, while the batch compiles — so nothing in the batch runs.
  A rowset with one is refused too: `OPENQUERY`'s as Msg 9514 naming `OPENQUERY`, and `EXEC … AT`'s or a remote procedure call's naming `IROWSET` after what the call sent ahead of it, each at the statement's line, ending the batch.
- A **CLR-typed** column — `geography`, `geometry`, `hierarchyid` — refuses the object with Msg 7325, which a pass-through query avoids: `OPENQUERY` and `EXEC … AT` return those values.
- A **`json`** column isn't listed: `SELECT *` leaves it out, naming it is Msg 207, and an object whose only columns are `json` is Msg 7357.
- A **`decimal`** column is listed as `numeric`, a **`smallmoney`** as `money` and a **`sysname`** as `nvarchar(128)` (probed 2026-10-05), which a `SELECT … INTO` keeps.

A pass-through rowset — `OPENQUERY`'s, `EXEC … AT`'s, a remote procedure call's — reads a `smallmoney` as `money`, a `sysname` as `nvarchar(128)`, and a `json` or `vector` as its text in a `varchar(max)` under `Latin1_General_100_BIN2_UTF8` (`RemoteWrite.ProviderRowsetType`, probed 2026-10-05).
- A **`vector(n)`** column is listed as `varbinary(8 + 4n)`, its storage form's length; a NULL reads as NULL and a value is Msg 7346, raised as its row is reached and ending the batch, while a read whose query never names the column, and a write whose statement doesn't set it, work.

## Procedures

What the linked-server procedures check and report, probed 2026-10-05 against SQL Server 2025 (each refusal's line and procedure live in `Simulation.SystemProcedureErrorSite`):

- **`sp_addlinkedserver`**: a NULL or empty name is Msg 15004 from `sys.sp_validname`.
  An omitted or NULL `@srvproduct` with no provider is `SQL Server` (any spelling of it is recorded as `SQL Server`), which takes neither a provider (Msg 15428) nor `@datasrc` / `@location` / `@provstr` / `@catalog` (Msg 15426, the error any properties without a provider earn) and names the server itself as its data source; another product needs a provider (Msg 15427), a NULL product with one is Msg 15429.
  Then a transaction is Msg 15002; a provider other than SQL Server's own — `SQLNCLI*`, `MSOLEDBSQL*`, `SQLOLEDB`, the last recorded as `SQLNCLI` — is Msg 7222 from `sys.sp_MSaddserver_internal`, as is `@linkedstyle = 0`'s Msg 15663; and a name already taken is Msg 15028 naming it as written.
  The nvarchar(4000) properties keep what fits, the product its first 128 characters.
- **Login mappings**: a new server maps every login to itself — principal 0, `uses_self_credential` 1 — and `sp_addlinkedsrvlogin (@rmtsrvname, @useself = 'TRUE', @locallogin, @rmtuser, @rmtpassword)` replaces a login's mapping, every login's when `@locallogin` is NULL; `@useself` other than true / false is Msg 15600, an unknown server Msg 15015 and an unknown login Msg 15007.
  `sp_droplinkedsrvlogin (@rmtsrvname, @locallogin)` needs both (Msg 201) and removes a mapping, doing nothing when there is none; `sp_helplinkedsrvlogin` lists them.
  A server mapping no login refuses every access with Msg 7416.
- **`sp_dropserver`**: Msg 15002 inside a transaction, Msg 15015 for an unknown server, and Msg 15190 while it maps a login other than its default self-mapping, unless `@droplogins` is `'droplogins'`.

## Server options

`sp_serveroption` keeps every option real takes, which `sys.servers` projects: `rpc out`, `data access` (off: four-part names and `OPENQUERY` are Msg 7411 `… not configured for DATA ACCESS.`, while remote calls still run) and `remote proc transaction promotion`, which behavior reads, and `rpc` (`is_remote_login_enabled`), `collation compatible`, `use remote collation`, `collation name`, `lazy schema validation`, `pub`, `sub`, `dist`, `system`, `connect timeout` and `query timeout`, which only the catalog reports (probed 2026-10-05).
An on/off option takes `true` / `on` / `false` / `off`, `system` only the first two; a timeout takes a whole number of seconds and `collation name` a known collation or NULL.
Any other value, or an unknown name, is Msg 15600, an unknown server Msg 15015.
Msg 7411 ends the batch and rolls back an open transaction.

## Transactions

Real enlists a linked server in the session's transaction — promoting it to a distributed one — for a write inside a local transaction (an `IMPLICIT_TRANSACTIONS` one included), for a read inside a `BEGIN DISTRIBUTED TRANSACTION`, and for a remote call inside a transaction or feeding `INSERT … EXEC` while `remote proc transaction promotion` is on.
A read inside an ordinary transaction doesn't enlist.

**Out of the box no coordinator accepts it**, so none is modeled as committing: a remote server refuses with **Msg 7391** at the statement's line, after the coordinator's own refusal as Msg 7412 (`The partner transaction manager has disabled its support for remote/network transactions.`), and a loopback with **Msg 3910** (`Transaction context in use by another session.`) at line 1.
Both end the batch and roll the transaction back as under `XACT_ABORT`, and a `TRY` that catches either finds the transaction doomed.
A loopback's remote *call* inside a transaction runs outside it here, with no error; with `remote proc transaction promotion` off every server's does — see [Not modeled yet](#not-modeled-yet) for what real does with a loopback's.
Committing across two `Simulation`s would need a coordinator that real's defaults don't provide, so modeling the refusal is the faithful choice rather than a stopgap.
`SimulatedDbTransaction.IsDistributed` carries the `DISTRIBUTED` keyword.

## What's modeled

- **Sprocs**: `sp_addlinkedserver` (activate), `sp_dropserver`, `sp_serveroption` (above), `sp_addlinkedsrvlogin` / `sp_droplinkedsrvlogin` / `sp_helplinkedsrvlogin` — see [Procedures](#procedures).
- **`sys.linked_logins`** lists each server's login mappings under its `server_id`; `sys.remote_logins` is empty.
- **`sys.servers`**: local instance as row 0 (`is_linked = 0`, name `"SIMULATED"`), one row per active linked server carrying `sp_addlinkedserver`'s arguments and the three options — see [sys.servers shape](#sysservers-shape).
- **Provider name in messages**: every SQL Server provider name (`SQLNCLI`, `SQLNCLI11`, `MSOLEDBSQL`, `MSOLEDBSQL19`) reports as `MSOLEDBSQL19`, the driver SQL Server 2025 loads for it.

## Divergences

- **The replay isn't the statement real sends**: the simulator ships values it computed locally, where real ships a remotable single-table UPDATE's text for the server to evaluate — so a nondeterministic expression (`NEWID()`, `GETDATE()`) is evaluated by the local server, not the remote one.
- **A `vector` column a query names in anything but its output** is fetched, and a row holding a value is Msg 7346, where real remotes what it can and reads: `WHERE v IS NOT NULL`, `COUNT(v)`, `DATALENGTH(v)` and `CAST(v AS varchar(…))` (which answers the vector's text form) all run on the server there, and a query projecting `v` whose `WHERE` excludes every non-NULL row (`WHERE id = 2`) never fetches one (probed 2026-09-30 against SQL Server 2025).
  A column named only inside a derived table's `SELECT *` is fetched too, where real's optimizer drops it.
- **A `varbinary` value written into a remote `vector` column** is refused by the server's own conversion, which is Msg 206 here and Msg 13609 on real, whose provider sends it differently.
- **A remote UPDATE's failing expression** (`SET b = 1 / 0`) raises locally at the statement's line, where real's server evaluates it and relays Msg 3621 and the error at line 1 (probed 2026-10-05), and a conversion a four-part read's query applies (`CAST(a AS int)`) fails locally too, where real remotes it.
- **An INSERT listing a computed or `rowversion` column** is refused locally with Msg 271 / 273 as it compiles, where real's provider refuses it with Msg 7344 after its Msg 7412 (probed 2026-10-05).

## Not modeled yet

- **Predicate pushdown**: every four-part-name read pulls every remote row, and a write's stand-in the full table.
  The projection is pushed: a read's remote query names only the columns its query does (`Selection.UnfetchedRemoteColumns`), reading the others as NULL, so a `vector` column real's provider can't convert stops the read only when the query names it (probed 2026-09-30 against SQL Server 2025).
  A query whose reach the walk can't see all of — a subquery or an `APPLY` — fetches every column.
- **LOB columns**: the remote projection uses the type-only `RowEncoder.EncodeRow` overload (no LOB store), so a MAX payload large enough to overflow the 65535-byte var-section cap raises during encoding on the remote.
- **`@@SERVERNAME`** isn't routed — the local-server row in `sys.servers` uses the constant `"SIMULATED"` for `name` regardless of any host-configured value.
- **`EXEC … AT DATA_SOURCE`**.
  The ad hoc `OPENROWSET` over a provider rides this machinery with a transient server named `(null)` — see [`bulk-and-adhoc.md`](bulk-and-adhoc.md#ad-hoc-provider-rowsets).
- **A loopback's remote call inside a transaction** runs in the caller's transaction on real — `@@TRANCOUNT` reads 1 inside it and a `ROLLBACK` undoes its writes, for a procedure call and `EXEC … AT` alike (probed 2026-10-05) — where here it runs outside it; `OPENQUERY`'s query reads `@@TRANCOUNT` 1 on real too.
- **The catalog procedures over a linked server** — `sp_linkedservers`, `sp_testlinkedserver` (which refuses a non-`sysname` argument with Msg 214), `sp_tables_ex`, `sp_columns_ex`, `sp_catalogs` — and the compatibility views `sys.sysservers` / `master.dbo.sysservers` (probed 2026-10-05).
- **Names real reads past the four-part grammar**: `srv...t` and `EXEC srv...p` (a name with two empty middle segments, Msg 7314 / 2812 on real), five-part names (Msg 117), a remote scalar function `srv.db.dbo.f(1)` (Msg 344), a four-part name over the server's own synonym (Msg 7357) and over a `#temp` table (Msg 2701), all of which fail differently here.
- **A parameter's type through `EXEC … AT`**: a `decimal` argument arrives as `numeric` on real.
- **A supplementary-character collation's column** through a four-part name: real's provider lists it under the collation without `_SC`, so `LEN` counts a surrogate pair as two there (probed 2026-10-05).

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
| `is_remote_login_enabled` | 1 for local and for a `SQL Server` product, 0 for any other product, until `sp_serveroption … 'rpc'` sets it |
| `is_rpc_out_enabled` | as `is_remote_login_enabled` until `sp_serveroption` sets it |
| `is_data_access_enabled` | 1 for linked until `sp_serveroption` clears it, 0 for local |
| `is_remote_proc_transaction_promotion_enabled` | 1 until `sp_serveroption` clears it |
| `modify_date` | when `sp_addlinkedserver` ran; the simulation's seed date for local |

The rest are real's defaults for both kinds until `sp_serveroption` sets them: timeouts 0, `uses_remote_collation` 1, the other flags 0 and `collation_name` NULL.

Stable ordering across runs (name-sorted with the local row first).
Distinct from real SQL Server's `server_id` allocation, which is `sys.servers`-row-driven and persists across restarts.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 15015 | `sp_dropserver 'X'` / `sp_serveroption 'X', …` where X isn't an active linked server: `"The server 'X' does not exist. Use sp_helpserver to show available servers."` |
| 201 / 8144 / 8145 | `sp_addlinkedserver` / `sp_dropserver` without `@server`, past their parameter lists, and (`sp_addlinkedserver`) with an unknown `@`-name — real's signatures, `@provstr` and the `bit` `@linkedstyle` included (probed 2026-09-25). |
| 15004 | `sp_addlinkedserver` with a NULL or empty `@server`, from `sys.sp_validname`; `sp_dropserver`'s NULL is Msg 15015 naming `(null)`. |
| 15426 / 15427 / 15428 / 15429 / 7222 / 15663 / 15028 / 15002 | `sp_addlinkedserver`'s product, provider, duplicate and transaction refusals — see [Procedures](#procedures). |
| 15190 / 15007 | `sp_dropserver` of a server still mapping a login; a login mapping naming a login that doesn't exist. |
| 7416 | Any access to a server that maps no login. |
| 7215 | An `EXEC … AT` the provider refused before sending. |
| 4122 | A four-part name called as a function. |
| 15600 | `sp_dropserver` with a `@droplogins` other than `'droplogins'`, naming `sys.sp_dropserver`; `sp_serveroption`'s unknown option or value, naming `sys.sp_serveroption`. |
| 7202 | A four-part name, `OPENQUERY`, `EXEC … AT` or remote procedure call naming a server `sys.servers` lacks. |
| 7314 / 7313 | A four-part name's missing table; a write target's omitted schema. |
| 7411 | `rpc out` or `data access` off for what needs it. |
| 7391 / 3910 | The distributed transaction a remote server's or a loopback's enlistment needs. |
