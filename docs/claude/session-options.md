# Session `SET` options

`Simulation.Set.cs` parses every `SET <option>` form from one closed accept-list; the options with an effect are handled by name there and the rest are accepted and discarded.
This doc is the catalog of which ones carry state, when a `SET` takes effect, and how far it reaches.
The option-specific behavior lives with its feature: `IMPLICIT_TRANSACTIONS` in [`transactions.md`](transactions.md#set-implicit_transactions), `CURSOR_CLOSE_ON_COMMIT` in [`cursors.md`](cursors.md#set-cursor_close_on_commit), `DEADLOCK_PRIORITY`'s victim choice in [`locking.md`](locking.md#cycle-detection), `XACT_ABORT` in [`transactions.md`](transactions.md#set-xact_abort), `QUOTED_IDENTIFIER` in [`grammar.md`](grammar.md), `TEXTSIZE` / `DATEFIRST` / `LANGUAGE` / `CONTEXT_INFO` in [`scalars.md`](scalars.md).

## When a `SET` applies

Two clocks, probed against SQL Server 2025:

- **Parse time** — `QUOTED_IDENTIFIER`, the `ANSI_DEFAULTS` bundle's share of it, and `PARSEONLY`.
  These apply as the batch parses, so they reach statements textually after them whatever the control flow, and a `SET` in a never-taken branch still counts.
  Because a whole batch parses before any of it runs, what the batch's `@@OPTIONS` reads for `QUOTED_IDENTIFIER` is the value its **last** such `SET` leaves, even at a read written ahead of it, and the last `SET PARSEONLY` decides whether any of the batch runs (probed 2026-09-28).
  The simulator parses and runs statement by statement, so `Simulation.ScanParseTimeOptions` reads the batch's tokens ahead of running it, for a batch whose text names one of the options.
- **Run time** — everything else: a `SET` applies as it runs, so the batch's compile walk and an un-taken branch leave it alone, and a statement ahead of it runs under the old setting (probed 2026-09-26).
  `RecordSessionStateOption` records the on/off ones; the value-taking ones are handled beside it.

## How far a `SET` reaches

A procedure, trigger or dynamic-SQL body's `SET` applies inside the body and reverts when the body returns, the caller's value restored by `SimulatedDbConnection.SessionOptionScope` at each invocation seam and around a parameterized ad-hoc command (which SqlClient sends as `sp_executesql`).
That holds for `ANSI_PADDING`, `ANSI_WARNINGS`, `ARITHABORT`, `CONCAT_NULL_YIELDS_NULL`, `NUMERIC_ROUNDABORT`, `ANSI_NULL_DFLT_ON` / `_OFF`, `IMPLICIT_TRANSACTIONS`, `CURSOR_CLOSE_ON_COMMIT`, `NOEXEC`, `DEADLOCK_PRIORITY`, `XACT_ABORT`, `ROWCOUNT`, `DATEFIRST`, `DATEFORMAT`, `NOCOUNT` and `TEXTSIZE` (probed 2026-09-28: a procedure setting five of the ANSI toggles reads `@@OPTIONS` 9568 inside and the caller 5432 after).
`ANSI_NULLS` and `QUOTED_IDENTIFIER` are the exceptions: a procedure or trigger body ignores its own `SET` of them, running under the setting captured when the module was created, while dynamic SQL applies them to its own batch.
`PARSEONLY` in a procedure, trigger or function body refuses the `CREATE` with Msg 1059, at line 0 wherever the `SET` sits (probed 2026-09-28).

## `@@OPTIONS`, `SESSIONPROPERTY` and `sys.dm_exec_sessions`

`@@OPTIONS` (`OptionsExpression`) carries `IMPLICIT_TRANSACTIONS` 2, `CURSOR_CLOSE_ON_COMMIT` 4, `ANSI_WARNINGS` 8, `ANSI_PADDING` 16, `ANSI_NULLS` 32, `ARITHABORT` 64, `QUOTED_IDENTIFIER` 256, `NOCOUNT` 512, `ANSI_NULL_DFLT_ON` 1024, `ANSI_NULL_DFLT_OFF` 2048, `CONCAT_NULL_YIELDS_NULL` 4096, `NUMERIC_ROUNDABORT` 8192 and `XACT_ABORT` 16384; a fresh SqlClient session reads 5432.
`NOEXEC`, `PARSEONLY` and `DEADLOCK_PRIORITY` have no bit.
`SESSIONPROPERTY` answers only its documented seven and NULL for the rest — `IMPLICIT_TRANSACTIONS`, `CURSOR_CLOSE_ON_COMMIT` and `ANSI_NULL_DFLT_ON` included (probed 2026-09-28).
`sys.dm_exec_sessions` / `sys.dm_exec_requests` report `ansi_null_dflt_on`, `deadlock_priority`, and `ansi_defaults`, which reads 1 only while all seven options the bundle sets are on, so a SqlClient session — whose login turns `IMPLICIT_TRANSACTIONS` and `CURSOR_CLOSE_ON_COMMIT` back off — reads 0.

## `ANSI_DEFAULTS`

Sets `ANSI_NULLS`, `ANSI_NULL_DFLT_ON`, `ANSI_PADDING`, `ANSI_WARNINGS`, `CURSOR_CLOSE_ON_COMMIT`, `IMPLICIT_TRANSACTIONS` and `QUOTED_IDENTIFIER` together, on or off — the first six as it runs, `QUOTED_IDENTIFIER` as it parses (probed 2026-09-28: 5432 → 5438 on, 4096 off).

## `ANSI_NULL_DFLT_ON` / `ANSI_NULL_DFLT_OFF`

What a `CREATE TABLE` column stating neither `NULL` nor `NOT NULL` gets (`Simulation.DefaultsColumnsToNull`): nullable under `ANSI_NULL_DFLT_ON`, `NOT NULL` under `ANSI_NULL_DFLT_OFF`, and with both off the database's `ANSI_NULL_DEFAULT` — tempdb's, off, for a `#temp` table.
Setting either on turns the other off; setting one off leaves the other alone, so `SET ANSI_NULL_DFLT_ON OFF` alone already makes columns `NOT NULL` under a database whose option is off.
An alias type's own nullability wins, a computed column's follows its expression, and the rule reaches no other column source: a table variable, a table type, `ALTER TABLE … ADD`, `ALTER COLUMN` and `SELECT … INTO` keep their own (all probed 2026-09-28).

## `NOEXEC`

While on, each statement compiles — the batch's compile still raises what compiling raises, Msg 207 on a known table's missing column — but nothing runs: no rows, no messages, no `PRINT` / `RAISERROR`, no DDL, no variable assignment, no `USE`, and a deferred missing object raises nothing.
`SET NOEXEC OFF` is the one statement that still runs, so `SET NOEXEC ON; SELECT 1; SET NOEXEC OFF; SELECT 2` returns only 2 (probed 2026-09-28).
The batch walks its statements in skip mode (`BatchContext.NoExecActive`, part of `IsSkipping`); `SET NOEXEC OFF` consults `SkipsForControlFlow`, the skip mode short of it, so one in an un-taken branch still doesn't run.

## `PARSEONLY`

While on, a batch is checked for syntax alone: a syntax error (class 15) is reported, and binder errors and the batch itself are not.
It applies as the batch parses, so the batch that sets it runs none of its statements — those ahead of the `SET` included — and a batch turning it off runs (probed 2026-09-28).

## `DEADLOCK_PRIORITY`

Takes `LOW` (-5), `NORMAL` (0), `HIGH` (5) or an integer in -10..10, from a literal, a quoted string or a variable of an integer or string type, a NULL variable reading as `NORMAL`; anything else — 11, `MEDIUM`, 2.5 — is Msg 2755.

## Not modeled yet

- `SET STATISTICS IO` / `TIME` report page counts and timings that depend on the engine's storage and the machine, and `STATISTICS XML` / `PROFILE` and the `SHOWPLAN_*` family return plans; all parse and are discarded, so no extra message or result set arrives and a `SHOWPLAN` batch runs where real only describes it.
- `ARITHIGNORE`, `FORCEPLAN`, `QUERY_GOVERNOR_COST_LIMIT`, `REMOTE_PROC_TRANSACTIONS` and `DISABLE_DEF_CNST_CHK` parse and are discarded.
- `DBCC USEROPTIONS`.

## Divergences

- `ANSI_NULL_DEFAULT` changed by `ALTER DATABASE` in the same batch as the `CREATE TABLE` it should govern: real had already settled the column's nullability as the batch compiled, under the old value, where the simulator reads the new one (probed 2026-09-28).
