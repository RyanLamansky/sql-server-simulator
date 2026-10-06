# DBCC and `CHECKPOINT`

Every `DBCC` statement goes through one of two front ends in `Simulation/`.
`SHRINKDATABASE` / `SHRINKFILE`, `SHOW_STATISTICS`, `CHECKIDENT` and `INPUTBUFFER` keep their own parsers (`Simulation.Dbcc.cs`, `Simulation.CheckIdent.cs`; their behavior is in [`heap-storage.md`](heap-storage.md), [`indexes.md`](indexes.md), [`dml.md`](dml.md) and [`catalog-views.md`](catalog-views.md)).
Everything else reaches `ParseDbccCommand` (`Simulation.DbccCommands.cs`), which reads any `DBCC name [( argument, … )] [WITH option, …]` and hands it to a handler per subcommand; the consistency checks and table maintenance live in `Simulation.DbccCheck.cs`, `OPENTRAN` and `HELP` in `Simulation.DbccSession.cs`.
Everything below was probed 2026-09-28 against SQL Server 2025 through the `.vs/edge-probe` differential harness, where the modeled commands match real line for line apart from the environmental values listed under [Divergences](#divergences).

## The statement's shape

- **What settles at parse, what at run.**
  A `WITH` word that is no DBCC option at all is Msg 195 state 4 while the batch compiles, so none of it runs; `MAXDOP` without `= n` is the same.
  Everything else raises when the statement runs: an unknown subcommand (Msg 2526 state 3), an option the subcommand doesn't take (Msg 2532), the wrong argument count (Msg 2583) and an unusable argument (Msg 2560, state 9 unless noted).
  A known subcommand the simulator hasn't built (`PAGE`, `IND`, `SHOWCONTIG`, `MEMORYSTATUS`, …) is `NotSupportedException`, not Msg 2526 — after the permission refusal real raises first for a caller outside `sysadmin`: Msg 2571 for `PAGE`, `IND`, `MEMORYSTATUS` and `OUTPUTBUFFER` (state 1) and `DBINFO` (state 14), Msg 7983 for `PROCCACHE` without `db_owner` (probed 2026-10-04 against SQL Server 2025).
- **Messages.**
  Msg 2528 closes every successful run, `WITH NO_INFOMSGS` silencing it and every other informational line — and, for `INDEXDEFRAG` and `SHRINKFILE`, the result set too.
  A command's rows close with their own counted DONE before Msg 2528, which a second DONE follows; `CHECKCONSTRAINTS` alone sends Msg 2528 before its rows' DONE and leaves that DONE uncounted (`AfterDbccRows`, and the wire test in `StatementDoneWireTests`).
- **`@@ROWCOUNT`.**
  A command returning rows leaves it at their count, `DBREINDEX` at the table's row count, and `CHECKPOINT`, `CHECKCONSTRAINTS` and the consistency checks at 0; every other command leaves it alone, `CHECKIDENT`, `SHRINK*`, `SHOW_STATISTICS` and `TRACEON` included.
- **`SET FMTONLY ON`** makes a DBCC statement do nothing and report nothing.
- **Permissions.**
  The server-scope commands refuse with Msg 2571 naming the command, each with its own state: `FREEPROCCACHE`, `DROPCLEANBUFFERS` and `FREESYSTEMCACHE` take `ALTER SERVER STATE`, while `FREESESSIONCACHE`, `FLUSHAUTHCACHE`, `PINTABLE` / `UNPINTABLE`, `TRACEON` / `TRACEOFF`, `LOGINFO` and `HELP` take a sysadmin login, `CONTROL SERVER` not sufficing — see [`permissions.md`](permissions.md#statement-gates).
  The database checks (`CHECKDB`, `CHECKALLOC`, `CHECKCATALOG`, `CHECKFILEGROUP`, `CHECKCONSTRAINTS`, `UPDATEUSAGE`, `OPENTRAN`) take `db_owner`, else Msg 7983; `CHECKTABLE` and `DBREINDEX` take the table's ownership or `ALTER` (Msg 2557), `CLEANTABLE` and `INDEXDEFRAG` `ALTER` on the table (Msg 229 state 1); `SQLPERF(LOGSPACE)` takes `VIEW SERVER STATE` and its `CLEAR` form `ALTER SERVER STATE` (Msg 297, which ends the batch); `USEROPTIONS` and `TRACESTATUS` are open to everyone.
- **Arguments.**
  A database argument is a name, quoted or not, a database id, or 0 for the current database: an unknown name is Msg 2520 state 5, an unknown id Msg 2521, a negative one Msg 2560.
  A table argument is a name, quoted or not, or an object id (Msg 2573 when no table has it); a view, procedure or function is Msg 5239, and a miss Msg 2501.

## `CHECKPOINT [duration]`

Flushes nothing, every write already being in the heap, but runs real's permission check — `db_owner` or `db_backupoperator`, else Msg 3505 — and resets `@@ROWCOUNT`.
The duration is a positive `int` literal; 0, a negative, a decimal, a variable or a string is Msg 102 at compile.

## The cache commands

`FREEPROCCACHE` clears what [`plan-cache.md`](plan-cache.md#clearing-dbcc-freeproccache) describes; its argument is a pool name (`default`, `internal`), a binary handle of at least 44 bytes (a shorter one is Msg 2560 state 110), or nothing.
`FREESYSTEMCACHE` takes `ALL`, a cache store name real lists in `sys.dm_os_memory_cache_counters`, a database's name or its `ObjPerm - ` store, and an optional pool; an unknown store or pool is Msg 2560 naming its position.
`DROPCLEANBUFFERS` and `FREESESSIONCACHE` only report.
`FLUSHAUTHCACHE` (no argument) and `PINTABLE` / `UNPINTABLE` (two integers, never looked up: an unknown database or object completes quietly, a string is Msg 2560) only report too, and every `WITH` option is Msg 2532 for them (probed 2026-09-30 against SQL Server 2025).

## `USEROPTIONS`

The session's `SET` options in real's fixed order: `textsize`, `rowcount` when set, `language`, `dateformat`, `datefirst`, the `statistics` switches, `lock_timeout`, then each on/off switch while on, `ansi_defaults` while all seven of its options are on, and `isolation level` last, reading `read committed snapshot` under `READ_COMMITTED_SNAPSHOT`.
`quoted_identifier` reads the batch's parse-time value, as `@@OPTIONS` does ([`session-options.md`](session-options.md#when-a-set-applies)).
`DEADLOCK_PRIORITY`, `QUERY_GOVERNOR_COST_LIMIT`, `CONTEXT_INFO`, `IDENTITY_INSERT` and `FMTONLY` never appear.
`STATISTICS XML` / `PROFILE`, `FORCEPLAN` and `REMOTE_PROC_TRANSACTIONS` do nothing else here and are kept only to be listed (`SimulatedDbConnection.ListedOnlyOptions`); `STATISTICS IO` / `TIME` report as [`session-options.md`](session-options.md#statistics-io) describes.

## `OPENTRAN`

A refusal for want of `db_owner` is followed by Msg 2528 unless `NO_INFOMSGS` (probed 2026-10-04 against SQL Server 2025).

Reports the oldest transaction that has *written* to the database — changed a row or the catalog there, or holds a write lock (an `UPDATE` finding no row still takes one) — since real counts only a transaction with a logged change; a transaction that has only read is "No active open transactions." (Msg 7969).
`tempdb` counts writes to temporary tables.
The report is Msg 7968 through 7978, naming the transaction for its `BEGIN TRAN`, else `user_transaction` or `implicit_transaction`, with UID -1, the login's SID and a start time in style 109; `TABLERESULTS` returns the same lines as `OLDACT_*` rows under a column named for the database.

## `SQLPERF`, `LOGINFO`, `TRACE*`, `HELP`

- `SQLPERF(LOGSPACE)` lists every database in id order with its log file's size less the header page; `SQLPERF('sys.dm_os_wait_stats' | 'sys.dm_os_latch_stats', CLEAR)` resets statistics the simulator doesn't gather.
  Another keyword is Msg 2526 state 12, the wait-stats name without `CLEAR` state 15.
- `LOGINFO` cuts the log file into four virtual log files the way real cuts a log under 64 MB — each a whole number of 64 KB units, the last taking the remainder — the first active.
- `TRACEON` / `TRACEOFF` take flags 0 through 17798 (0 accepted and never listed; others are Msg 2560 state 17 / 30 and change nothing), `-1` among them making the change server-wide (`Simulation.GlobalTraceFlags`).
  `TRACESTATUS` lists every flag on with no argument or `-1`, else a row per named flag; no row sends no result set, and a flag outside -1 .. 17798 is Msg 2560 state 31 at its position (probed 2026-10-04).
- `INPUTBUFFER` of a session id no session holds is Msg 7955 state 1, and one past `smallint` Msg 2560 (probed 2026-10-04).
- `HELP` prints a documented subcommand's syntax verbatim, echoing the name as written, or with `'?'` every documented name, each as a message numbered 0 at line 0; an undocumented subcommand real knows is Msg 8987 state 1, another word state 2.

## `CHECKCONSTRAINTS`

The rows violating the `FOREIGN KEY` and `CHECK` constraints, modeled exactly because the result is observable:

- **Scope.**
  No argument checks every table in the current database in object-id order; a table (by name or id) checks that table; a check or foreign-key constraint's name or id checks it alone, disabled or not; a key constraint's name checks nothing.
  A `#temp` table isn't found (Msg 2501).
  Without `ALL_CONSTRAINTS` a disabled constraint is skipped, while an enabled but untrusted one is checked; checking never marks a constraint trusted.
- **Groups.**
  Per table, the foreign keys form one group and the check constraints another, each reported against a row's *first* violated constraint in creation order — so a row breaking both a foreign key and a check appears twice, and one breaking two checks once.
  A foreign key with a NULL in any column is satisfied.
- **Rows.**
  `Table` is `[schema].[table]`, `Constraint` `[name]`, and `Where` `[col] = 'value'` joined by `AND` over the constraint's columns — a check constraint's in table order — with each value as `CONVERT(nvarchar(max), value)` renders it (style 121 for dates and times, 1 for binary, a `char` without its padding, quotes doubled), and NULL when any of the values is NULL.
  A group's rows are distinct under the database collation, so `'b'` and `'B'` collapse, sorted by constraint name then text (NULL first), and capped at 200 unless `ALL_ERRORMSGS`.
  `Table` and `Constraint` are `nvarchar` one wider than their longest value, `Where` `nvarchar(max)`; finding nothing sends no result set.

## The consistency checks

A simulated database is always consistent, so each check reports a healthy one:

- `CHECKDB [( database [, NOINDEX] )]`: Msg 2536 heading the database, Msg 7966 when `NOINDEX` is given, the eight Service Broker lines (Msg 8997) for the metadata every database carries, Msg 2536 and 2593 for each user table (`schema.table` outside `dbo`), and Msg 8989.
  `PHYSICAL_ONLY` keeps only the heading and summary, and with `DATA_PURITY` is Msg 2532 state 2, with `EXTENDED_LOGICAL_CHECKS` state 5 (probed 2026-10-04).
  `TABLERESULTS` returns the Msg 8997, 2593 and 8989 lines as rows of real's 23-column shape.
  `WITH TABLOCK` skips the catalog and Service Broker checks: Msg 5232 follows the heading (and the `NOINDEX` warning) in place of the Msg 8997 lines, as a row under `TABLERESULTS` (probed 2026-10-06).
- `CHECKFILEGROUP [( filegroup )]`: `CHECKDB` over the tables on one filegroup, without the Service Broker lines; an unknown filegroup name is Msg 3027 and an unknown id Msg 8932 state 0 (probed 2026-10-04).
- `CHECKTABLE ( table [, NOINDEX | index_id] )`: Msg 2536 and 2593 for the table, a `#temp` table by its padded internal name; an index id the table has no index under is Msg 7999 state 7 naming the number (probed 2026-10-04).
  Under a database-scoped identity — `EXECUTE AS USER`, `dbo` included — it is Msg 916 state 2 after the permission check, naming the server principal as a refused `USE` does (a login, or a `WITHOUT LOGIN` user's `S-1-9-3-…` SID string), and the batch ends unless a `TRY` catches it: real's check reads an internal snapshot of the database, which that identity can't reach, while `WITH TABLOCK` takes locks instead and runs.
  The other checks run under the same identity (probed 2026-09-28 against SQL Server 2025, with real's SID-derived name there where the simulator's is its own deterministic one).
- `CHECKALLOC`: the heading, Msg 2538 / 8915 for the data file and 2539 / 8918 for the database, and Msg 8989.
- `CHECKCATALOG`: Msg 2528 alone.
- `ESTIMATEONLY` answers Msg 5281 instead of checking, `NO_INFOMSGS` or not.
- A `REPAIR_*` argument is `NotSupportedException`: real refuses it outside single-user mode, and probing it against a live server is off limits.

## `PAGE`

`DBCC PAGE ( database, file, page [, printopt] )` is `sysadmin`'s (Msg 2571), and without trace flag 3604 real prints no dump: a page inside its file completes with Msg 2528 alone (probed 2026-10-06 against SQL Server 2025).
The arguments check in order — three or four of them (Msg 2583 state 3), the database (Msg 2520 / 2521, a negative id Msg 2560), then each other's type, an integer (Msg 2560 state 9) — then the address: a page past its file's size, a negative one or a file the database lacks is **Msg 8968**, which doesn't end the statement — Msg 2528 still follows and `@@ERROR` reads 8968 — and a log file's page other than its header (page 0) is **Msg 2514** state 9.
Only then is a print option past 3 Msg 2560 state 102.
The dump itself — under trace flag 3604 or `WITH TABLERESULTS` — is real's page image, which the simulator's pages don't carry: `NotSupportedException`.

## Table maintenance

`UPDATEUSAGE`, `CLEANTABLE` and `DBREINDEX` only validate and report — a `DBREINDEX` fill factor outside 0 .. 100 is Msg 129 state 2 (probed 2026-10-04) — the simulator's counts never going stale, its dropped columns reclaimed as they drop and its indexes never fragmenting; an index name the table lacks is Msg 7999 (state 4 from `DBREINDEX`, 8 from the others).
`INDEXDEFRAG` returns the pages scanned and none moved or removed — for the named index, or one row per index (the heap's with a NULL name) — and is Msg 8920 inside a user transaction.

## Divergences

- Values that describe real's physical storage are read off the simulator's own: a table's page count is its heap's data pages, `CHECKALLOC`'s totals are the user tables' pages in whole uniform extents, `WITH ESTIMATEONLY` adds those pages to what a fresh database reports (2435 KB for `CHECKDB`, 351 for `CHECKALLOC`), `SQLPERF(LOGSPACE)` reports a fixed 54 pages of log in use, `LOGINFO`'s active file and every `OPENTRAN` LSN carry sequence number 34, and the system databases' logs are the size of any other.
- `CHECKDB`, `CHECKFILEGROUP` and `CHECKALLOC` list no system base tables and no per-allocation-unit lines, which real prints for its catalog (over a hundred objects in a fresh database).
- Real leaves `@@ROWCOUNT` after the consistency checks at a count its internal queries leave behind; the simulator leaves 0.
- Real's Query Store opens a transaction of its own in each user database, which `OPENTRAN` reports as the oldest (`QDS batch nested transaction`) — and as written to a user transaction that has only read, where the simulator, keeping no such transaction, reports none (probed 2026-10-04 against SQL Server 2025).
- Messages other than Msg 2528, which follows `SET LANGUAGE` (`Language.DbccCompletedMessage`, the texts real's `sys.messages` holds), stay in English, as the engine's other messages do.

## Not modeled yet

- `IND`, `SHOWCONTIG`, `OUTPUTBUFFER`, `PROCCACHE`, `MEMORYSTATUS`, `TUPLEMOVER`, `CLONEDATABASE` and the other undocumented commands, the repair options, and `CHECKALLOC … WITH TABLERESULTS`.
  `SHOWCONTIG` over one table first asks `ALTER` on it (Msg 229 naming the `DBCC` permission), as real does, and then is `NotSupportedException`.
  What these report is real's storage — `IND`'s page list (none over an empty table), `SHOWCONTIG`'s fragmentation, `CHECKALLOC`'s per-allocation-unit rows, `PAGE`'s dump — or the server process's own state, which a flat page list doesn't reproduce: not chased (probed 2026-10-06 against SQL Server 2025).
