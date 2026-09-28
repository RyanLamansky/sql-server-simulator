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
That holds for `ANSI_PADDING`, `ANSI_WARNINGS`, `ARITHABORT`, `ARITHIGNORE`, `CONCAT_NULL_YIELDS_NULL`, `NUMERIC_ROUNDABORT`, `ANSI_NULL_DFLT_ON` / `_OFF`, `IMPLICIT_TRANSACTIONS`, `CURSOR_CLOSE_ON_COMMIT`, `NOEXEC`, `DEADLOCK_PRIORITY`, `XACT_ABORT`, `ROWCOUNT`, `DATEFIRST`, `DATEFORMAT`, `NOCOUNT`, `TEXTSIZE` and `STATISTICS IO` / `TIME` (probed 2026-09-28: a procedure setting five of the ANSI toggles reads `@@OPTIONS` 9568 inside and the caller 5432 after).
`ANSI_NULLS` and `QUOTED_IDENTIFIER` are the exceptions: a procedure or trigger body ignores its own `SET` of them, running under the setting captured when the module was created, while dynamic SQL applies them to its own batch.
`PARSEONLY` in a procedure, trigger or function body refuses the `CREATE` with Msg 1059, at line 0 wherever the `SET` sits (probed 2026-09-28).

## `@@OPTIONS`, `SESSIONPROPERTY` and `sys.dm_exec_sessions`

`@@OPTIONS` (`OptionsExpression`) carries `IMPLICIT_TRANSACTIONS` 2, `CURSOR_CLOSE_ON_COMMIT` 4, `ANSI_WARNINGS` 8, `ANSI_PADDING` 16, `ANSI_NULLS` 32, `ARITHABORT` 64, `ARITHIGNORE` 128, `QUOTED_IDENTIFIER` 256, `NOCOUNT` 512, `ANSI_NULL_DFLT_ON` 1024, `ANSI_NULL_DFLT_OFF` 2048, `CONCAT_NULL_YIELDS_NULL` 4096, `NUMERIC_ROUNDABORT` 8192 and `XACT_ABORT` 16384; a fresh SqlClient session reads 5432.
`NOEXEC`, `PARSEONLY` and `DEADLOCK_PRIORITY` have no bit.
`SESSIONPROPERTY` answers only its documented seven and NULL for the rest — `IMPLICIT_TRANSACTIONS`, `CURSOR_CLOSE_ON_COMMIT` and `ANSI_NULL_DFLT_ON` included (probed 2026-09-28).
`sys.dm_exec_sessions` / `sys.dm_exec_requests` report `ansi_null_dflt_on`, `deadlock_priority`, and `ansi_defaults`, which reads 1 only while all seven options the bundle sets are on, so a SqlClient session — whose login turns `IMPLICIT_TRANSACTIONS` and `CURSOR_CLOSE_ON_COMMIT` back off — reads 0.

## `ARITHIGNORE` and `ARITHABORT`

Probed 2026-09-28 against SQL Server 2025, a divide by zero or an arithmetic overflow (Msg 8134, 220, 232, 8115) under each pairing of `ANSI_WARNINGS` and `ARITHABORT`:

| `ANSI_WARNINGS` | `ARITHABORT` | Real |
| --- | --- | --- |
| on | either | the error ends its statement, and the batch runs on |
| off | off | NULL, then Msg 3606 / 3607 after the statement's rows ([`errors.md`](errors.md#the-message-stream)); under `ARITHIGNORE ON` the NULL alone |
| off | on | the error ends the batch and rolls the transaction back as under `XACT_ABORT` — a procedure's error ends its caller's batch, and a `TRY` that catches it is left with a doomed transaction (`Simulation.ApplyXactAbortPromotion`) |

So `ARITHIGNORE` only reaches the NULL-answering pairing (`BatchContext.AbsorbsArithmeticFault`), a write storing the NULL included.
`SESSIONPROPERTY('ARITHIGNORE')` is NULL, as for the other options outside its documented seven.

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

## `STATISTICS IO`

Probed 2026-09-28 against SQL Server 2025 through the `.vs/edge-probe` differential harness, whose `diffcase.py` blanks the figures: the message shape, presence and order match real, and the figures are the simulator's own.

- **What reports.**
  Each statement that read or wrote a table sends one Msg 3615 per table after it completes — a query's after its DONE, a write's ahead of it, an `IF` / `WHILE` condition's ahead of the branch it chose, a write's ahead of the triggers it fires — and a statement that failed, or touched no table, sends none.
  A write whose `OUTPUT` returned rows sends them after the rows' DONE and closes with a second DONE counting the rows written, as real does only while a statistics option reports.
  A scalar function's or view's reads count in the calling statement; a multi-statement function's body reports nothing, and the caller reports its scan of the return table under a table variable's `#` + 8-hex name.
  A procedure's, trigger's and dynamic batch's statements report for themselves, attributed to the module as its errors are (`IoStatistics`, `SimulatedDbConnection.StatementIo`).
- **Names.**
  A table by its bare name, a `#temp` table by its padded internal name, a table variable by its `#` + 8-hex one; a trigger's `inserted` / `deleted` are never listed, except that an `INSTEAD OF` trigger's read of them is a `Worktable` line and the firing statement lists one too.
- **Work tables.**
  `Worktable` (with `Workfile` ahead of it for a hash join) lists with no scans and no reads where real's plan sorts or hashes: an `ORDER BY` or window ordering a single table's key or index doesn't already supply (a bounded `TOP` sort lists none), a `GROUP BY` / `DISTINCT` its keys don't already group or make distinct, `UNION` / `INTERSECT` / `EXCEPT`, a `MERGE`, a cursor fetch, and a query's hash join — a write's joins are nested loops on real and list none.
  The tests (`Selection.IoWorktables.cs`) approximate real's plan choice from the one table's keys, since the simulator sorts and hashes either way.
- **Order.**
  The most recently first-touched table first, which is real's order for an `INSERT … SELECT` (target first), a nested-loop join (inner first), a `MERGE` (target, `Worktable`, source) and a sort or hash over what it read (`Worktable` first).
- **Figures.**
  A scan counts one, a seek one per key it probes except a single lookup of a whole unique key or index, which counts none as on real; a logical read is one heap page the read entered, and a row an `INSERT` writes one more, so a seek that finds nothing reads none where real reads its B-tree's depth; a LOB read is one LOB-chain page decoded.
  Physical and read-ahead reads are always 0: after `DBCC DROPCLEANBUFFERS` real reports a physical read for the first scan, and the simulator, having no disk, has no cold cache to model.

Cost when off: each scan or seek tests `SimulatedDbConnection.StatementIo` once and each row a local for null, and the LOB reader bumps a per-heap counter; the index sqllogictest replay measured no difference (61.0 s / 61.3 s against 60.1 s / 62.9 s with the change stashed).

## `STATISTICS TIME`

Probed 2026-09-28 against SQL Server 2025.

- **Msg 3613** (`SQL Server parse and compile time: `, then `   CPU time = n ms, elapsed time = n ms.` on its own line) goes out for each compile: a batch's, ahead of its first output and at the line of its last top-level statement (-1 when that is a `TRY`); a procedure's and a trigger's on every call, at the body's last statement's line and naming the module; a dynamic batch's; a statement real compiles through simple parameterization (`BindErrorReport.IsSimplyParameterizable`) and one calling a user function, each ahead of its output.
  The batch that turns the option on sends no compile of its own, and a batch creating a procedure, function, trigger or view attributes its compile and its statement's time to the module.
- **Msg 3612** (a newline, ` SQL Server Execution Times:`, then `   CPU time = n ms,  elapsed time = n ms.`) follows every statement that closes with a DONE ([`tds-endpoint.md`](tds-endpoint.md#per-statement-done-tokens)) — a bare `DECLARE` and a label send none — an `IF` / `WHILE` condition each time it runs, `BEGIN TRY`, a `CATCH` entered and `END CATCH`, and an `EXEC` after its body; placed as `STATISTICS IO`'s lines are, after them.
  A statement its error ended reports after the error and ahead of Msg 3621; one whose error ended the batch reports nothing; the `SET` turning the option on or off reports nothing.
- **Figures.**
  Elapsed time is the statement's wall clock, truncated to the millisecond; CPU time is the same less what the session spent in `WAITFOR`, the simulator running a statement on one thread.
  A module's and a parameterized statement's compile report 0, the simulator compiling them as they run.

While either option is on the session skips the plan cache and the compiled-batch memo, so each batch compiles and reports.

## Not modeled yet

- `STATISTICS XML` / `PROFILE` and the `SHOWPLAN_*` family return plans; they parse and are discarded, so no result set arrives and a `SHOWPLAN` batch runs where real only describes it.
- `STATISTICS IO` lists nothing for a catalog view, where real lists the system base tables it read (`sysschobjs` …), and a system procedure's statements report nothing, where real's report each of its own (`sp_help`'s compile and a Msg 3612 per statement).
- `STATISTICS IO` orders a hash join's, an `EXCEPT`'s and a foreign-key check's tables by the simulator's own read order, which is not always real's (a hash join's build side first, a referenced table ahead of the written one), and lists no `Worktable` for an `UPDATE` of a key column, where real's split-sort lists one.
- `FORCEPLAN`, `QUERY_GOVERNOR_COST_LIMIT`, `REMOTE_PROC_TRANSACTIONS` and `DISABLE_DEF_CNST_CHK` have no effect; `FORCEPLAN`, `REMOTE_PROC_TRANSACTIONS` and the `STATISTICS XML` / `PROFILE` switches are kept only for `DBCC USEROPTIONS` to list ([`dbcc.md`](dbcc.md#useroptions)), and not reverted when a module body that set them returns.

## Divergences

- `ANSI_NULL_DEFAULT` changed by `ALTER DATABASE` in the same batch as the `CREATE TABLE` it should govern: real had already settled the column's nullability as the batch compiled, under the old value, where the simulator reads the new one (probed 2026-09-28).
