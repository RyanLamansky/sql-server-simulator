# Temp tables and TRUNCATE

## `TRUNCATE TABLE`

`TRUNCATE TABLE <name>` empties the heap (clears `Pages` / `LobPages`) and resets every identity column's high-water mark to its declared seed — probe-confirmed against SQL Server 2025 that a subsequent INSERT receives the seed, not the next-after-prior-max.
Routing reuses the same `#`-prefix dispatch as DROP TABLE (`#foo` → connection's `TempTables`; else the named schema's heap-table dict via `Database.Schemas`).
Missing target raises **Msg 4701** (`"Cannot find the object \"X\" because it does not exist or you do not have permissions."` — note this is distinct from DROP's Msg 3701 and from generic INSERT/UPDATE/DELETE's Msg 208; TRUNCATE has its own error path, and its wording carries **only the leaf** of a multi-part name — probe-confirmed asymmetric with 208 / 3701 which embed the full qualifier).
A view named there is Msg 4708 (`Could not truncate object 'v' because it is not a table.`, probed 2026-10-01 against SQL Server 2025).
`@@ROWCOUNT` resets to 0.
Skip-mode suppresses the entire action including name resolution.
`WHERE` clause fails at parse (Msg 102 — TRUNCATE doesn't accept predicates).

**Transactional rollback is supported** via a single `HeapTruncation` undo entry that snapshots the pre-truncate `Pages` / `LobPages` lists AND each identity column's pre-truncate high-water mark.
`BEGIN TRAN; TRUNCATE; ROLLBACK` restores both — including the identity counter.
**This diverges from the simulator's general "identity counters bypass the undo log" rule, which is INSERT-only**: INSERT-advanced counters stay advanced through rollback (probe-confirmed for real SQL Server too); TRUNCATE's explicit reset action gets undone on rollback.
Outside an explicit transaction, TRUNCATE commits immediately (no log entry — same pattern as DROP TABLE on regular tables).

## Local temp tables (`#foo`)
Per-connection `Dictionary<string, HeapTable> TempTables` on `SimulatedDbConnection`; routed by `BatchContext.TryResolveTable` (`#`-prefix leaf → connection dict, ignoring any qualifier; else the named schema's heap-table dict).
Auto-cleared on `Dispose`, matching real SQL Server's session-close drop.
Lifecycle, cross-conn isolation, and Msg 208 from other sessions all probe-confirmed against SQL Server 2025.

- **`CREATE TABLE #foo`** reuses the full CREATE TABLE grammar (constraints, identity, computed, defaults).
  Tokenizer: `#` is a leading-identifier char (`Parser/Tokenizer.cs:ParseHashPrefixedName`), so `#foo` / `##foo` / bare `#` all lex as a `Name` (CheckReserved short-circuits because no keyword begins with `#`).
- **`DROP TABLE [IF EXISTS] name[, name...]`** — new statement (`Simulation.Drop.cs`).
  Routes by `#`-prefix; comma-list form supported.
  Missing target → **Msg 3701 St 5 Class 11** verbatim (`"Cannot drop the table '<name>', because it does not exist or you do not have permission."`) — **identical for temp (`#foo`) and regular targets**, both flowing through `SimulatedSqlException.CannotDropTableDoesNotExist`; `IF EXISTS` suppresses either.
  Covers regular (non-`#`) tables too — first user-visible DROP TABLE support in the simulator.
- **Multi-part qualifiers on `#`-prefixed names** (`tempdb..#foo`, `tempdb.dbo.#foo`, `claude..#foo`) all resolve to the session's `#foo` regardless of qualifier (DB and schema segments are cosmetic on `#` leaves — probe-confirmed), a server segment included — `srv.db.dbo.#foo` reads the local table though `srv` names no server.
  A read naming a database sends the class-0 **Msg 2701** state 99 (`Database name 'db' ignored, referencing object in tempdb.`) as the batch compiles, at the line of the token after the name (probed 2026-10-06 against SQL Server 2025).
  A query or write naming a table its own batch creates sends it a second time just ahead of its results, as real recompiles the statement it couldn't bind at first (`BatchContext.TemporaryNamesRecompiled`); a text run again on real's cached plan sends neither copy, so both go out the first time the text compiles under the current schema only (probed 2026-10-17 against SQL Server 2025).
  The routing happens in `BatchContext.TryResolveTable` based on the leaf alone, and the note goes out wherever a statement names the table (`BatchContext.TemporaryTableName`, probed 2026-10-16): reads and DML, `CREATE TABLE`, `SELECT … INTO`, `ALTER TABLE` and its `SWITCH` target, a foreign key's `REFERENCES`, `CREATE` / `ALTER` / `DROP INDEX` (the deprecated `db..#t.ix` form too), `CREATE` / `UPDATE STATISTICS`, `SET IDENTITY_INSERT`, `TRUNCATE` and `DROP TABLE`, once per name.
  A message names the table by its leaf, however it was qualified — `master.dbo.#t` and `dbo.#t` alike are `Invalid object name '#t'`.
  A server part where a table is created, dropped or indexed — `CREATE TABLE`, `SELECT … INTO`, `DROP TABLE`, `CREATE INDEX … ON` — is Msg 117 (`The maximum is 2`) as the batch compiles, temporary or not; `ALTER TABLE` is the same, with a Msg 102 after it near the column an `ADD`, `ALTER COLUMN` or `DROP COLUMN` goes on to name; `TRUNCATE` drops the server part as a read does.
  Divergence: a name whose empty segment is the database (`srv..dbo.t`) is spelled `srv.dbo..t` in that Msg 117, the parsed name not recording which segment was left empty.
  `DBCC CHECKIDENT`'s string finds the table through `tempdb..#t` alone: another database's prefix is Msg 2501.
- **Transactional CREATE / DROP**: probe-confirmed that `BEGIN TRAN; CREATE TABLE #foo; ROLLBACK` undoes the table, and DROP TABLE inside a tran is similarly reversible.
  `UndoLog` (re-shaped to a polymorphic `UndoEntry` hierarchy) records `TempTableCreation` and `TempTableRemoval` entries alongside the existing slot-mutation kinds.
- **Persists across batches** in the same connection; **invisible to other connections** (Msg 208).
  Two sessions can independently hold a `#foo` of the same name — real SQL Server mangles internally; the simulator achieves the same effect by giving each connection its own dict (user-visible names stay un-mangled).
- **Module-scoped lifetime**: a `#foo` created inside a stored procedure, trigger, or dynamic-SQL (`EXEC('…')` / `sp_executesql`) body is dropped when that module exits, not left on the session — a following statement sees Msg 208, and a re-entrant call (the same proc invoked twice on one connection, or tedious's `execSql` re-running a `create table #t` through `sp_executesql`) re-creates the name without a Msg 2714 collision.
  A session-level `#foo` (created outside any module) persists until the session ends; module-created temps stay visible to nested modules down the call stack during execution.
  Probe-confirmed against SQL Server 2025 for procs, triggers, `EXEC('…')`, and `SELECT … INTO #t`.
  Mechanism: the body's `BatchContext` records the temps it created (`RegisterScopedTempTable`, gated by `ScopesTempTables` = has a proc / trigger frame, or the RPC ad-hoc-statement `ForceTempTableScope`), and each module-body dispatch drops them in its `finally` (`DropScopedTempTables`) — so it runs on a body error too.
  The RPC `sp_executesql` / `sp_execute` / `sp_prepexec` path (which builds a top-level command rather than a dynamic-SQL frame) opts in via `SimulatedDbCommand.ScopeTempTablesToBatch`, dropped in `CreateResultSetsForCommand`'s finally.
- **A nested scope may create a `#foo` its caller already has** (probed 2026-09-24 against SQL Server 2025).
  Its own hides the caller's — for its statements and for the modules it calls — until it's dropped or the scope ends, and then the name is the caller's again; a `DROP` with no table of the scope's own drops the caller's, and a `ROLLBACK` restores whichever was visible.
  Only a second `#foo` in the *same* scope is Msg 2714.
  `SimulatedDbConnection.TempTables` holds the visible table of each name and hides the rest behind it (`TryAddTempTable` / `RemoveTempTable`), each table carrying the scope that created it (`HeapTable.TempScopeId`); a scope drops its tables by object rather than name at exit, since the name may be the caller's again by then.
  Real compiles the inner batch against the table visible *before* it runs, so a column only the inner table has is Msg 207 on both engines (`CREATE TABLE #t (b int); SELECT b FROM #t` inside `EXEC` while the caller holds `#t (a int)`), and so is a `CREATE PROC` whose body does the same in a session holding one.
- **A name over 116 characters** is Msg 193, the room tempdb leaves beside its twelve-digit suffix; a missing temp table's Msg 208 is at state 0, where a permanent table's is at 1; and `OBJECT_ID('#foo')` is NULL unless tempdb is the current database — `OBJECT_ID('tempdb..#foo')` is the spelling that finds it (probed 2026-10-01 against SQL Server 2025).
- **A view or function can't read one**: a view's body naming `#foo` is Msg 4508, a function's — scalar, inline or multi-statement — Msg 2772, each at its `CREATE` (probed 2026-10-01).
- **Locked whole**: a read takes the table's object S and a write its X, never a row lock, so a request of the session writing a table its other request is reading mid-result waits, and `sys.dm_tran_locks` lists the lock in tempdb — the modes and durations by level and hint are in [`locking.md`](locking.md#local-temp-tables).
- **Bare `#`** is a valid temp-table name (one-char `#`).
  Identity / SCOPE_IDENTITY work identically to regular tables.
  CTE prefix, JOINs across multiple `#`-tables, all queries against `#foo` flow through the same Selection / Insert / Update / Delete / Merge machinery via `TryResolveTable`.

## `tempdb`'s catalog lists them

`tempdb`'s catalog views and metadata scalars list every session's `#temp` tables, every `##` table and the running batch's table variables under `dbo`, each by its name inside `tempdb` (`HeapTable.CatalogName`), as real does: `tempdb.sys.tables`, `sys.objects` (with the tables' constraints), `INFORMATION_SCHEMA.TABLES` / `COLUMNS`, `OBJECT_NAME(id, DB_ID('tempdb'))` (probed 2026-09-24 and 2026-10-06 against SQL Server 2025).
So `WHERE name = '#t'` finds nothing on either — `name LIKE '#t[_]%'` or `object_id = OBJECT_ID('tempdb..#t')` is the working spelling — and `OBJECT_ID('tempdb..<padded name>')` finds the table whichever session made it.
The views read them through `BuiltInResources.TempCatalogTables`, which `CatalogTables` appends to `tempdb.dbo`'s own tables; a temp table's id comes from `tempdb`'s counter so the two can't collide there.

A `#temp` table's messages — Msg 515, Msg 2628 — name it as real does, by its name inside `tempdb`: the written name padded with underscores to 116 characters and twelve hex digits of a server-wide counter each local temp table's creation advances (`HeapTable.InternalName`, probed 2026-09-28), real's counter carrying its instance's history where this one starts at 1; a constraint conflict (Msg 547) names it bare, `table "#t"`.
A table variable's internal name is `#` and eight hex digits of its negative object id — see [`table-variables.md`](table-variables.md#fidelity-gaps-remaining).

`EXEC tempdb..sp_help '#t'` describes the table under that name, and `tempdb..sp_columns '#t'` lists its columns (probed 2026-10-06).

**Divergences**: real lists every session's table variables, where the simulator lists the running batch's alone (another batch's live on it, out of reach).

## Global temp tables (`##foo`)
Instance-wide `ConcurrentDictionary<string, HeapTable> GlobalTempTables` on `Simulation`; routed by `BatchContext.TryResolveTable` via `IsGlobalTempName` (leading `##`, length ≥ 2 — bare `##` is a valid name, probe-confirmed).
Any session can SELECT / INSERT / UPDATE / DELETE / DROP / TRUNCATE a `##foo` regardless of which session created it.
Cross-session visibility, the creating session's identity tracked via `HeapTable.OwnerSession`, and owner-disconnect auto-drop all probe-confirmed against SQL Server 2025 (pooling disabled — the prior probe with default `SqlConnection` pooling masked the auto-drop because `Close()` returned the connection to the pool rather than disconnecting).

- **Auto-drop on owner disconnect**: `SimulatedDbConnection.Dispose` walks `Simulation.GlobalTempTables` and removes every entry whose `OwnerSession` is this connection's.
  The stamp is the session token rather than the connection so a `##temp` doesn't pin the session that created it — a connection abandoned without a `Dispose` drops its `##temp` tables through the same walk when its session is reclaimed (see [`locking.md`](locking.md#abandoned-session-reclamation)).
  Probe-confirmed against SQL Server 2025 that the drop fires unconditionally on owner-disconnect, regardless of other sessions' prior or in-flight references — Microsoft Learn's "dropped when all tasks have stopped referencing" wording is misleading.
  A non-owner session mid-statement at the moment of owner-disconnect observes Msg 208 on its next reference to the table; no reference counting modeled (or needed).
- **Multi-part qualifiers** (`tempdb..##q`, `tempdb.dbo.##q`) accepted and routed the same as bare `##q` — `BatchContext.TryResolveTable`'s `##` arm ignores qualifier segments, same convention as `#foo`.
  Probe-confirmed.
- **Duplicate CREATE from any session** raises Msg 2714 with the verbatim wording `There is already an object named '##X' in the database.` (same factory as regular-table dup, since the dict-add fails identically).
- **Transactional CREATE / DROP**: `##foo` DDL participates in the creating session's undo log via the existing `RecordTempTableCreation` / `RecordTempTableRemoval` plumbing (the dict argument lets the same entries point at `GlobalTempTables`).
  Probe-confirmed.

## Temporary procedures (`#p` and `##p`)

A temporary procedure is an ordinary `Procedure` in `tempdb`'s `dbo`, so every surface listing `tempdb`'s modules lists it — `sys.objects`, `sys.procedures`, `sys.sql_modules`, `sys.parameters`, `INFORMATION_SCHEMA.ROUTINES`, `OBJECTPROPERTY`, `tempdb..sp_help`, `sp_stored_procedures` and `sys.dm_exec_procedure_stats` (under `database_id` 2) — with the `principal_id` NULL and the schema `dbo` real reports (probed 2026-10-16 against SQL Server 2025).
A local one is stored under its name in `tempdb` (`SimulatedDbConnection.TemporaryProcedureName`): the written name padded with underscores to 120 characters and eight hex digits naming the session, which all of one session's temporary procedures share and each session draws from a server-wide counter — real's counter advances on every login, this one as a session first needs it.
Msg 201 and 8144 spell that name out, while an error its body raises, `ERROR_PROCEDURE()` and Msg 2812 name the procedure by its leaf alone, however the call qualified it.
A global one is stored under its written name.

- **Scope.**
  A local one is the creating session's alone: another session's `#p` is a different object, `EXEC`, `ALTER` and `DROP` from elsewhere miss it (Msg 2812, Msg 208 state 6, Msg 3701), and `OBJECT_ID('tempdb..#p')` there is NULL while `tempdb.sys.objects` still lists it.
  A global one is every session's: any of them runs, alters or drops it, and a second `CREATE` is Msg 2714 state 3 from any session.
  Both last until the session that created them ends — `Dispose`, a pooled connection's `sp_reset_connection`, or an abandoned session's reclamation — even when another session altered a global one, and a `CREATE` inside dynamic SQL or a procedure's body outlives that scope, unlike a temp table's.
  A call already running when its creator ends finishes, but anything it calls by name finds the procedure gone (Msg 2812), whoever is still using it.
- **Names.**
  A database part is ignored with the class-0 Msg 2701 as for a temp table, a schema part is ignored and a server part dropped, at `CREATE`, `ALTER`, `EXEC` and `DROP` alike; a name over 116 characters is Msg 193.
  The name shares the session's temporary namespace with its temp tables: `CREATE PROC #t` over a temp table `#t` is Msg 2714 state 6 (state 3 for `##`), `ALTER` or `CREATE OR ALTER` over one Msg 2010, `EXEC #t` Msg 2809 naming the table as `tempdb` holds it, `DROP PROC #t` Msg 3705, and `CREATE TABLE #p` or `SELECT … INTO #p` over a procedure Msg 2714.
- **Binding.**
  The body binds and runs in the database that was current when the procedure was created — its unqualified names, `OBJECT_ID`, the catalog views — while `DB_NAME()`, `DB_ID()` and its dynamic SQL see the database the call came from (`SimulatedDbConnection.TemporaryModuleCallerDatabase`, entered through `ModuleDatabaseScope.EnterTemporary`), so `OBJECT_NAME(@@PROCID)` is NULL outside `tempdb` and `OBJECT_NAME(@@PROCID, 2)` the padded name.
  A missing object it names fails when it runs, as a permanent procedure's does, and a call naming another temporary procedure draws no Msg 2007 at `CREATE`.
- **Permissions and events.**
  Any session that sees one may create, run, alter or drop it without a permission of its own, and no database DDL trigger fires for it.
  `GRANT` on a local one is Msg 15151, and on a global one from outside `tempdb` Msg 4610.
- **Text.**
  `sys.sql_modules.definition` and, from `tempdb`, `OBJECT_DEFINITION` carry the text, but `sp_helptext` reports none (Msg 15197, from line 91 of its own source).
- **Transactions.**
  `CREATE` and `DROP` roll back with the transaction.
- **Refused.**
  `NATIVE_COMPILATION` is Msg 12321 state 74; a temporary function is Msg 1074, a scalar one's `RETURN` value adding Msg 178; a view Msg 4103; a trigger named `#…` Msg 208 state 77 (one on a temp table being Msg 167); a synonym Msg 208 state 80, a rule or default Msg 208 state 77; a sequence Msg 11714.
  `CREATE TYPE #t FROM int` is legal and makes a type in the current database, as on real.

**Divergences**: `EXECUTE AS OWNER` in a temporary procedure runs as `dbo` where real's runs as `guest`, and real's `sys.all_sql_modules` lists a temporary procedure twice, the simulator once.
