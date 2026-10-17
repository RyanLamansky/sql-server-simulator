# DBCC and `CHECKPOINT`

Every `DBCC` statement goes through one of two front ends in `Simulation/`.
`SHRINKDATABASE` / `SHRINKFILE`, `SHOW_STATISTICS`, `CHECKIDENT` and `INPUTBUFFER` keep their own parsers (`Simulation.Dbcc.cs`, `Simulation.CheckIdent.cs`; their behavior is in [`heap-storage.md`](heap-storage.md), [`indexes.md`](indexes.md), [`dml.md`](dml.md) and [`catalog-views.md`](catalog-views.md)).
Everything else reaches `ParseDbccCommand` (`Simulation.DbccCommands.cs`), which reads any `DBCC name [( argument, … )] [WITH option, …]` and hands it to a handler per subcommand; the consistency checks and table maintenance live in `Simulation.DbccCheck.cs`, `OPENTRAN` and `HELP` in `Simulation.DbccSession.cs`.
Everything below was probed 2026-09-28 against SQL Server 2025 through the `.vs/edge-probe` differential harness, where the modeled commands match real line for line apart from the environmental values listed under [Divergences](#divergences).

`DETACHDB`, which `sp_detach_db` runs, is in [`backup-restore.md`](backup-restore.md#detach-and-attach); it refuses an option it doesn't take only once it has found its database.

## The statement's shape

- **What settles at parse, what at run.**
  A `WITH` word that is no DBCC option at all is Msg 195 state 4 while the batch compiles, so none of it runs; `MAXDOP` without `= n` is the same.
  Everything else raises when the statement runs: an unknown subcommand (Msg 2526 state 3), an option the subcommand doesn't take (Msg 2532), the wrong argument count (Msg 2583) and an unusable argument (Msg 2560, state 9 unless noted).
  A known subcommand the simulator hasn't built (`MEMORYSTATUS`, `EXTENTINFO`, …) is `NotSupportedException`, not Msg 2526 — after the permission refusal real raises first for a caller outside `sysadmin`: Msg 2571 for `MEMORYSTATUS` and `OUTPUTBUFFER` (state 1), Msg 7983 for `PROCCACHE` without `db_owner` (probed 2026-10-04 against SQL Server 2025).
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

`FREEPROCCACHE` clears what [`plan-cache.md`](plan-cache.md#clearing-dbcc-freeproccache) describes; its argument is a pool name (`default`, `internal`), a binary handle of at least 44 bytes — a plan handle or a SQL handle, as the [plan-cache DMVs](plan-cache.md#the-plan-cache-dmvs) report them (a shorter one is Msg 2560 state 110) — or nothing.
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

- `SQLPERF(LOGSPACE)` lists every database in id order with its log file's size less the header page; `SQLPERF('sys.dm_os_wait_stats', CLEAR)` forgets the server's wait statistics, each session's own kept as on real (probed 2026-10-16 against SQL Server 2025), and the `sys.dm_os_latch_stats` form resets latch statistics the simulator never gathers.
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

- `CHECKDB [( database [, NOINDEX] )]`: Msg 2536 heading the database, Msg 7966 when `NOINDEX` is given, the eight Service Broker lines (Msg 8997) for the metadata every database carries, Msg 2536 and 2593 for each table by object id — a user table (`schema.table` outside `dbo`) or one of the [system base and internal tables](catalog-views.md#system-base-tables-and-internal-tables) (`sys.name`, the rows and data pages of its heap or clustered index) — and Msg 8989.
  `PHYSICAL_ONLY` keeps only the heading and summary, and with `DATA_PURITY` is Msg 2532 state 2, with `EXTENDED_LOGICAL_CHECKS` state 5 (probed 2026-10-04).
  `TABLERESULTS` returns the Msg 8997, 2593 and 8989 lines as rows of real's 23-column shape.
  `WITH TABLOCK` skips the catalog and Service Broker checks: Msg 5232 follows the heading (and the `NOINDEX` warning) in place of the Msg 8997 lines, as a row under `TABLERESULTS` (probed 2026-10-06).
- `CHECKFILEGROUP [( filegroup )]`: `CHECKDB` over the tables on one filegroup, the system tables on `PRIMARY`, without the Service Broker lines; an unknown filegroup name is Msg 3027 and an unknown id Msg 8932 state 0 (probed 2026-10-04).
- `CHECKTABLE ( table [, NOINDEX | index_id] )`: Msg 2536 and 2593 for the table, a `#temp` table by its padded internal name; an index id the table has no index under is Msg 7999 state 7 naming the number (probed 2026-10-04).
  Under a database-scoped identity — `EXECUTE AS USER`, `dbo` included — it is Msg 916 state 2 after the permission check, naming the server principal as a refused `USE` does (a login, or a `WITHOUT LOGIN` user's `S-1-9-3-…` SID string), and the batch ends unless a `TRY` catches it: real's check reads an internal snapshot of the database, which that identity can't reach, while `WITH TABLOCK` takes locks instead and runs.
  The other checks run under the same identity (probed 2026-09-28 against SQL Server 2025, with real's SID-derived name there where the simulator's is its own deterministic one).
- `CHECKALLOC`: the allocation report described under [`CHECKALLOC`](#checkalloc).
- `CHECKCATALOG`: Msg 2528 alone.
- `ESTIMATEONLY` answers Msg 5281 instead of checking, `NO_INFOMSGS` or not.
- Each check that completes writes real's completion line to the error log, and a `CHECKDB` short of `ESTIMATEONLY` sets the database's `LastGoodCheckDbTime` — see [the error log](catalog-views.md#the-sql-server-error-log).
- A `REPAIR_*` argument is `NotSupportedException`: real refuses it outside single-user mode, and probing it against a live server is off limits.

## The page ids

`SHOWCONTIG`, `IND`, `CHECKALLOC`, `sys.dm_db_database_page_allocations`, `sys.dm_os_buffer_descriptors` and `sys.system_internals_allocation_units` number a database's pages through one layout of file 1 (`BuiltInResources.MapPages`), computed when one of them reads it from the [storage figures](indexes.md#storage-figures), which place every page of a replayed rowset among the extents it reserves (`PageFigures.Slot`).
The layout follows what real shows of a database whose user rowsets take uniform extents and whose system base tables take mixed ones (probed 2026-10-17 against SQL Server 2025 with `DBCC IND`, `CHECKALLOC` and the page-allocation DMV):

- **Extents of eight pages from page 16**, the allocation maps' pages first — a mixed page and 7 used of an extent, which real reports under object 99 — then the system tables' units in object order, then the user rowsets', each unit's extents following one another.
- **Every IAM page sits in a mixed extent**, eight to an extent; a system base table's first eight pages sit there too, the rest in extents of its own (`sysschobjs`' 43 pages, 9 mixed, in five dedicated extents), while an internal table's pages take dedicated extents as a user table's do.
- **A user rowset's pages sit where the replay put them**: a leaf page at its place in the leaf's allocation order, the first root in the second place when the split of the index's only page made it — the page real's root takes, `(1:385)` between leaf pages 384 and 386 — and the other pages above the leaf after the leaf; a build's leaf and upper levels in extents of their own; a LOB or row-overflow unit's pages in order.
  So the leaf's ids read the same out-of-order pages the fragmentation figures count, a key-ordered load's one gap and all.
- Each level's pages are chained in key order (a heap's not at all), as `IND`'s and the DMV's next and previous pages show.

The ids are the simulator's own: real's follow its allocation history, interleaving a table's units and other tables' extents, so the same table sits at other pages there.
`%%physloc%%` and the lock DMVs' pages (`LockPageMap`, numbered per table) aren't on this layout.

## `SHOWCONTIG`

`DBCC SHOWCONTIG [( table [, index] )] [WITH ALL_INDEXES, FAST, TABLERESULTS, ALL_LEVELS, NO_INFOMSGS]` reports each index's first partition, as real does (probed 2026-10-17 against SQL Server 2025):

- **The figures** are the leaf's: its pages, the extents its pages sit in read in key order (a heap's in page order), the switches between them, the scan density of the best extent count (pages over eight, rounded up) against the actual (switches plus one), the logical fragmentation the storage figures' out-of-order pages, the extent fragmentation the switches to an extent other than the next, and the free bytes and page density, which reads each page's bytes less two over 8,094 as `sys.dm_db_index_physical_stats` does.
- **Messages** Msg 7942 through 7954, each a message of its own, a heap's without Msg 7950; `FAST` keeps the pages, switches, scan density and — for an index — logical fragmentation, or — for a heap — extents and extent fragmentation.
  `NO_INFOMSGS` silences Msg 2528 alone, the report still sent.
- **`TABLERESULTS`** returns real's 20 columns, a heap's index name empty, the record sizes and free bytes cut to three decimals, the forwarding stubs among a heap's rows; `ALL_LEVELS` adds every level above the leaf, whose scan figures read 0; `FAST` leaves what it didn't read NULL and the levels out.
  `@@ROWCOUNT` reads the rows, or 0 for the messages.
- **The table**: by name or object id; without one, every user table and queue of the current database in object-id order, a queue under `queue_clustered_index`, which takes `db_owner` (Msg 7983 state 10 naming `SHOWCONTIG`).
  One table takes `ALTER` on it (Msg 229), a `#temp` table only through `tempdb..` (Msg 2501 otherwise), and a `sys` system table reports nothing but Msg 2528.
- **The index**: by name (Msg 7999 state 8 when the table lacks it) or id (state 7), the base rowset without one; `ALL_INDEXES` reports every index, whatever the second argument names.
  A third argument is Msg 2583, and an option outside the five Msg 2532.

## `IND` and `sys.dm_db_database_page_allocations`

`DBCC IND ( database, table, index [, partition] )` is `sysadmin`'s (Msg 2571), and lists each allocation unit's IAM page and then its pages in page order — the in-row, row-overflow and LOB units in turn — with real's 15 columns: the IAM page's own IAM and level NULL, a missing neighbor `0:0` (probed 2026-10-17 against SQL Server 2025).
The table is named in the database the first argument gives, a `sys` system table among them; the index is an id — 0 the base rowset, -1 every index, -2 every index's IAM pages alone, one the table lacks Msg 7999 state 7, a string, NULL or a number below -2 Msg 2560 state 9 — and the partition 0 for all, a negative one Msg 2587, one past the index's Msg 2586 state 5, NULL Msg 2560.
Any `WITH` option is taken, `NO_INFOMSGS` silencing Msg 2528; no page sends no result set.

`sys.dm_db_database_page_allocations(database_id, object_id, index_id, partition_number, mode)` lists, per unit, its IAM page and then every page of its mixed and dedicated extents in page order, an extent's unallocated pages among them, with real's 30 columns and its `SECONDARY` clone state; a NULL object, index or partition reads every one.
`LIMITED` (or NULL) leaves the page type, level and neighbors NULL; a heap's or LOB unit's pages report the page free space map's bucket — 100 for an empty or unallocated page, then 50, 20, 5 and 0 — and an index's NULL.
A NULL or negative database is Msg 2561 state 110, an unknown one Msg 913 state 8, another mode Msg 2561 state 111, four arguments Msg 313 state 3; a caller without `VIEW SERVER PERFORMANCE STATE` is Msg 300 even in `db_owner`, and `EXECUTE AS USER` Msg 15562.

`sys.dm_os_buffer_descriptors` gives each page its level, records and free bytes from the same layout, an IAM page 2 records and 6 bytes free as real's, and `sys.system_internals_allocation_units` its first leaf page, root and IAM page.

## `CHECKALLOC`

`DBCC CHECKALLOC [( database [, NOINDEX] )]` reports real's allocation report over the [page ids](#the-page-ids) (probed 2026-10-17 against SQL Server 2025):

- **Per table** in object-id order, system tables among the user tables, each named as `CHECKDB` names it: Msg 7922's rule and Msg 7923, then per allocation unit Msg 7924 (its first IAM page, root and `Dpages`) and 7925 (its pages and dedicated extents), and Msg 7927 with the table's dedicated extents.
  A system table's units go in their allocation-unit id order, a user table's in-row unit ahead of its row-overflow and LOB units, and a table's heading waits for its first in-row unit — so a queue's message table, whose LOB unit real allocated first, lists that unit under the table before it, as real does.
  `Dpages` reads 0 for a heap or clustered index, a nonclustered index's leaf pages, and a LOB or row-overflow unit's pages.
- **Per data file** Msg 2538 and 8915, then Msg 8916 (a heap's or clustered index's in-row unit) or 8917 for each unit holding pages, the allocation maps' object 99 among them, an index's units the last allocated first; then the database's Msg 2539 and 8918 and Msg 8989.
  The totals add the units up: extents are the dedicated ones and the mixed ones, reserved pages eight per dedicated extent and every mixed page.
- **`TABLERESULTS`** returns the same lines as rows of the 23 columns, but for the rules and Msg 2536, with the file's totals as one Msg 8983 row and real's row-identifier values per kind of line; with `NO_INFOMSGS` it returns nothing.

## `PAGE`

`DBCC PAGE ( database, file, page [, printopt] )` is `sysadmin`'s (Msg 2571), and without trace flag 3604 real prints no dump: a page inside its file completes with Msg 2528 alone (probed 2026-10-06 against SQL Server 2025).
The arguments check in order — three or four of them (Msg 2583 state 3), the database (Msg 2520 / 2521, a negative id Msg 2560), then each other's type, an integer (Msg 2560 state 9) — then the address: a page past its file's size, a negative one or a file the database lacks is **Msg 8968**, which doesn't end the statement — Msg 2528 still follows and `@@ERROR` reads 8968 — and a log file's page other than its header (page 0) is **Msg 2514** state 9.
Only then is a print option past 3 Msg 2560 state 102.
The dump itself — under trace flag 3604 or `WITH TABLERESULTS` — is real's page image, which the simulator's pages don't carry: `NotSupportedException`.
An earlier record had `WITH TABLERESULTS` returning an empty `ParentObject` / `Object` / `Field` / `VALUE` set (probed 2026-09-30) in a shape it doesn't capture: re-probed 2026-10-08 on CU7 (17.0.4065.4), over sqlcmd and SqlClient, every form returned the page's rows — pages 0, 1 and 1000 of the data file, print options 0 through 3 and none, the database by id 0 or by name, the log file's header page, under `NO_INFOMSGS` and through `INSERT … EXEC`, as had the 2026-09-30 sweep's own `tempdb` page 1 case — while a page past the file is Msg 8968 with no result set; RTM-GDR (17.0.1135.8) refused the probe login with Msg 2571.

## `DBINFO`

`DBCC DBINFO [( [database] )]` is `sysadmin`'s (Msg 2571 state 14) and takes a database by name only, a number being Msg 2560 state 9.
Without `TABLERESULTS` real writes the boot page nowhere a client reads and sends Msg 2528 alone; with it, real's 108 `ParentObject` / `Object` / `Field` / `VALUE` rows, all `nvarchar(255)` (probed 2026-10-16 against SQL Server 2025).
The database's name, id, compatibility level, collation id, creation date, family and Service Broker GUIDs follow the simulator's state, and `dbi_dbccLastKnownGood` is the `LastGoodCheckDbTime` the last `CHECKDB` set; the log sequence numbers, status words and other internals are a fresh database's, and the structure address a stand-in.

## Table maintenance

`UPDATEUSAGE`, `CLEANTABLE` and `DBREINDEX` only validate and report — a `DBREINDEX` fill factor outside 0 .. 100 is Msg 129 state 2 (probed 2026-10-04) — the simulator's counts never going stale, its dropped columns reclaimed as they drop and its indexes never fragmenting; an index name the table lacks is Msg 7999 (state 4 from `DBREINDEX`, 8 from the others).
`INDEXDEFRAG` returns the pages scanned and none moved or removed — for the named index, or one row per index (the heap's with a NULL name) — and is Msg 8920 inside a user transaction.
Ola Hallengren's `DatabaseIntegrityCheck` (version 2026-09-12 15:02:38) runs its `CHECKDB`, `CHECKALLOC`, `CHECKTABLE`, `CHECKCATALOG` and `CHECKFILEGROUP` (`PHYSICAL_ONLY`, `NOINDEX`, `EXTENDED_LOGICAL_CHECKS`) commands here with real's text and `CommandLog` rows (probed 2026-10-16 against SQL Server 2025); the per-object messages differ only where real lists its allocation units and counts pages its storage decides, a LOB column's in-row pages among them.

## `SHOWFILESTATS [(file_id)]`

One row per data file of the current database — `Fileid` / `FileGroup` `int`, `TotalExtents` / `UsedExtents` `bigint`, `Name nvarchar(128)`, `FileName nvarchar(260)` — or of the one file the argument names, with no result set at all for a log file or an id no file holds; the extents are the file's size and used pages over eight, as `sys.dm_db_file_space_usage` counts them.
A caller outside `db_owner` is Msg 7983 state 36 and a non-integer argument Msg 2560 (probed 2026-10-16 against SQL Server 2025).

## Divergences

- Values that describe real's physical storage are read off the simulator's own: `WITH ESTIMATEONLY` adds the user tables' heap pages to what a fresh database reports (2435 KB for `CHECKDB`, 351 for `CHECKALLOC`), `SQLPERF(LOGSPACE)` reports a fixed 54 pages of log in use, `LOGINFO`'s active file and every `OPENTRAN` LSN carry sequence number 34, and the system databases' logs are the size of any other.
- **The page reports' figures** are the [storage figures'](indexes.md#storage-figures), with their divergences, and their ids [the simulator's own](#the-page-ids).
  Real's allocation interleaves a table's in-row, LOB and other rowsets' extents, so its extent fragmentation runs higher — 85.71% for a clustered index beside its LOB unit, 0 here — and a build's or an `INSERT … SELECT`'s first pages can sit in extents the replay doesn't give them (a partition's two leaf pages in one extent on real, in two here).
- `CHECKALLOC`'s system tables carry the fresh database's figures, where real's Query Store tables fill as it runs (three of them holding 14 pages here and none in a probed database), so the totals differ by those pages; real gives a few system tables' clustered indexes a `Dpages` count (`sysrscols` 18) and lists a few system tables' LOB units ahead of their headings (`sysxmitbody`) by an order it doesn't show.
- A queue's `SHOWCONTIG` figures read as empty, and `IND` and the page DMV find every unit on file 1, where real puts a partition's on its filegroup's file.
- Real leaves `@@ROWCOUNT` after the consistency checks at a count its internal queries leave behind; the simulator leaves 0.
- Real's Query Store opens a transaction of its own in each user database, which `OPENTRAN` reports as the oldest (`QDS batch nested transaction`) — and as written to a user transaction that has only read, where the simulator, keeping no such transaction, reports none (probed 2026-10-04 against SQL Server 2025).
- Messages other than Msg 2528, which follows `SET LANGUAGE` (`Language.DbccCompletedMessage`, the texts real's `sys.messages` holds), stay in English, as the engine's other messages do.

## Not modeled yet

- `OUTPUTBUFFER`, `PROCCACHE`, `MEMORYSTATUS`, `TUPLEMOVER`, `CLONEDATABASE`, `EXTENTINFO` and the other undocumented commands, the repair options, and `PAGE`'s dump.
  What these report is the server process's own state or real's page image (probed 2026-10-06 against SQL Server 2025); `PAGE`'s header fields could read off the [page ids](#the-page-ids), its records' bytes not.
- `SHOWCONTIG` over an indexed view, which real reports by its clustered index and the simulator, keeping no rows for one, refuses with Msg 5239; and `%%physloc%%`.
