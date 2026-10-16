# BACKUP and RESTORE

`BACKUP DATABASE` (full and differential) and `BACKUP LOG` to `DISK`, tail-log backups, detaching and attaching databases, `RESTORE DATABASE` and `RESTORE LOG` sequences — a full set, then differential and log sets onto a database left restoring, point-in-time stops and marks, `STANDBY`, and the recovery that ends them — the `RESTORE` forms that only read a device, msdb's history of all of them, and the server's disk the backup tooling reads and tidies.
Behavior was probed 2026-10-11 against SQL Server 2022 (16.0.1200) on Windows.
The progress and summary messages, the refusals for a missing set or device, a new name over existing files, the restoring session's own database and a transaction, and the whole single-user admission sequence (the holder's `ROLLBACK IMMEDIATE`, Msg 924 on `USE` and on a three-part name, Msg 4060 at login, Msg 5064 to another session's `MULTI_USER`, the holder's restore, the first session in once the holder leaves) were re-checked the same day against SQL Server 2025 on Linux and matched.
SQL Server 2025 on Linux creates a missing directory on a backup's path, nested ones included, refusing only where it can't (operating system error 5); with `OpenBackupFile`, whether a path can be written is the host's answer, and the in-memory store writes any.

Code: [`Simulation.BackupRestore.cs`](../../src/SqlServerSimulator/Simulation/Simulation.BackupRestore.cs) (the statements), [`Simulation.RestoreSequence.cs`](../../src/SqlServerSimulator/Simulation/Simulation.RestoreSequence.cs) (`RESTORE` and its sequences), [`Simulation.RestoreInformation.cs`](../../src/SqlServerSimulator/Simulation/Simulation.RestoreInformation.cs) (the forms that only read a device), [`Simulation.BackupHistory.cs`](../../src/SqlServerSimulator/Simulation/Simulation.BackupHistory.cs) (msdb), [`Simulation.ServerFiles.cs`](../../src/SqlServerSimulator/Simulation/Simulation.ServerFiles.cs) and [`Simulation.ServerFileProcedures.cs`](../../src/SqlServerSimulator/Simulation/Simulation.ServerFileProcedures.cs) (the disk), [`Storage/Backup/`](../../src/SqlServerSimulator/Storage/Backup/) (the format, capture and replay, `DatabaseBackupChain`, `DatabaseLog` and `LogReplay`), [`SimulatedSqlException.BackupErrors.cs`](../../src/SqlServerSimulator/Errors/SimulatedSqlException.BackupErrors.cs).

## Devices

A backup device is a file the statement names with `DISK = 'path'` (a variable works too).
Following [`bulk-and-adhoc.md`](bulk-and-adhoc.md#file-access)'s settled rule, the simulator never reaches the host's files on its own: with no `Simulation.OpenBackupFile` delegate, devices live in memory for the life of the `Simulation`, keyed by the path as spelled, which is the simulated server's own disk.
A host that needs real files — a test harness checking `File.Exists` on the backup it asked for — supplies the delegate, which opens the path for reading (null for a missing file) or for writing the device's whole new content (null for a path that can't be written).

A statement may instead name a **logical device** `sp_addumpdevice` registered ([`Simulation.BackupDevices.cs`](../../src/SqlServerSimulator/Simulation/Simulation.BackupDevices.cs)), written bare or held by a variable; devices are server scope and list in `sys.backup_devices`, `sysdevices` and `sp_helpdevice` in the order they were added.
A disk device opens its physical name as spelled, through the same delegate, and every message naming the device names it `logical(physical)`, as real does; a tape device is real's Linux Msg 3201 state 5 and a virtual device state 7 naming only its physical name (probed 2026-10-15 against SQL Server 2025).
A relative `DISK =` path resolves against the default backup directory, `/var/opt/mssql/data`, which every message then names, and the server's file layer matches a path case aside (probed 2026-10-16 against SQL Server 2025); `OpenBackupFile` still receives the path as spelled, and a registered logical device keeps its physical name as spelled.
`sp_dropdevice … 'delfile'` deletes an in-memory device's content with real's Msg 2503; a file the host's delegate holds stays, since the delegate has no delete.

The file holds the simulator's own format ([`BackupMedia`](../../src/SqlServerSimulator/Storage/Backup/BackupMedia.cs)), not Microsoft's Tape Format: a real `.bak` is refused as an incorrectly formed media family, which is what real says of a file that isn't one.
Row values are stored in the engine's own storage encodings, so the header records the writing build (the assembly's informational version, commit included) and any other build refuses the backup.

## The server's disk

The simulator models the disk of SQL Server 2025 on Linux as the surfaces that ask about paths see it (probed 2026-10-16): a path is read case aside, `C:\` as the root, `.` and `..` settled and repeated `/` collapsed, and a relative path names nothing.
Its directories are the root, `/tmp` and `/var/opt/mssql` with its `data` and `log`, those `xp_create_subdir` creates and those a backup creates above its device; its files are every database's, the error log and its archives, and the backup devices, with `OpenBackupFile` set the paths the host opens for reading.
`sys.dm_os_file_exists`, `xp_fileexist` (a row, or with an output argument the file flag alone), `xp_dirtree` (depth first, to a depth, files on request), `xp_create_subdir` and `xp_delete_file` read and change it; the last two are `sysadmin`'s alone (Msg 229), the first open to any login, and the other two answer a caller short of `sysadmin` as though nothing existed.
A refusal of `xp_create_subdir` (Msg 22048) or `xp_delete_file` (Msg 22049) is an error the batch runs past, neither entering a `CATCH` nor setting `@@ERROR`, the procedure answering 1: severity 15 for an argument it can't read, 16 naming the operating system's error for a path it can't use.
`xp_delete_file 0, folder, extension, date[, subfolders]` deletes the backup files whose last set finished before the date, in the folder and with `subfolders` the folders directly inside it; `xp_delete_file 0, file` deletes one.

## Detach and attach

`sp_detach_db` and `DBCC DETACHDB` write a database to its own files on the server's disk and take it off the server; `CREATE DATABASE … FOR ATTACH` / `FOR ATTACH_REBUILD_LOG`, `sp_attach_db` and `sp_attach_single_file_db` bring it back from them ([`Simulation.DetachAttach.cs`](../../src/SqlServerSimulator/Simulation/Simulation.DetachAttach.cs)), all as SQL Server 2025 was probed doing them (2026-10-16).
A detached database is a backup written into its primary data file — the same `DatabaseImage`, so a database holding something a backup refuses refuses to detach — and each other file holds the database's identity and its own file id ([`DetachedDatabaseFile`](../../src/SqlServerSimulator/Storage/Backup/DetachedDatabaseFile.cs)).
The files go where any device goes: the in-memory store, or `OpenBackupFile`'s host; their format is the simulator build's own, so real's `.mdf` / `.ldf` files don't attach here, nor these on real.

- **`sp_detach_db [@dbname] [, @skipchecks] [, @keepfulltextindexfile]`** checks in real's order, each from its own line: a NULL name, or an option other than `true` / `false` in any case, is the class-0 usage message (Msg 15354 from line 17, 25 or 33), a missing database Msg 15010 from line 40, a transaction Msg 15002 from line 47, each returning 1.
  `@skipchecks = 'false'` alone runs `sys.sp_updatestats` in the database first — NULL, the default, skips it as `true` does.
  The detach itself is `DBCC DETACHDB` run as a dynamic batch: its refusals come at line 1 naming no procedure, and the procedure returns 0 whatever they were.
- **`DBCC DETACHDB (database [, int])`** detaches without a message: an empty name is the current database and a NULL one Msg 2560; then a missing database is Msg 7939, a system database Msg 7940, a principal neither `sysadmin` nor in the database's `db_owner` Msg 7983, and the session's own current database Msg 3703, which ends the batch.
  An `OFFLINE`, `EMERGENCY` or single-user database detaches, and a `RESTORING` one does with Msg 947 in the client and the error log, leaving files that attach refuses (Msg 1824).
- **Attaching** needs the file list (Msg 1829 state 2 without one, state 3 when the first file is a log or secondary file), refuses `CONTAINMENT` (Msg 1841) and a taken name (Msg 1801) before it reads a file, and reads each listed file in order: a relative path is Msg 5105, a file missing, a directory or one an online database holds Msg 5120 with operating system error 2, 5 or 32, a file holding no database Msg 5172, one of another database Msg 5173 and a second primary file Msg 1830, each but the first two followed by Msg 1802.
  Files it doesn't list it looks for where the database left them; a missing log is rebuilt there (Msgs 5111 and 5182, 63 pages growing 10 percent with no ceiling), as `FOR ATTACH_REBUILD_LOG` does too, while a log the list names must exist.
  Sizes in the list change nothing.
  `WITH ENABLE_BROKER` / `NEW_BROKER` / `ERROR_BROKER_CONVERSATIONS` (one of them) and `RESTRICTED_USER` follow `FOR ATTACH` alone; anything else is Msg 102 at it.
- **The attached database** comes back online under the smallest free `database_id`, owned by the attaching login, with its data, objects, compatibility level and recovery model as they were, and with `TRUSTWORTHY`, `DB_CHAINING`, `READ_ONLY`, `AUTO_CLOSE` and its broker off and `MULTI_USER` but for what the `WITH` list says — under any name, its files' logical names unchanged.
  Dropping it deletes its files; dropping an `OFFLINE` database leaves them, attachable.
- **`sp_attach_db @dbname, @filename1 [, … @filename16]`** and **`sp_attach_single_file_db @dbname, @physname`** refuse a NULL name or first file with Msg 15004 from line 27 or 17, then run `CREATE DATABASE … FOR ATTACH` over the files as a dynamic batch, whose refusals come at line 1 naming no procedure and return 1.
- **The error log** gets what real writes: a startup line for an attach (a restoring database's included, ahead of its Msg 1824), Msg 17204's `FCB::Open failed: Could not open file … for file number 0.  OS error: …` for a file that wouldn't open, Msg 5105 and Msg 5173, and for a rebuilt log Msg 17207's `FileMgr::StartLogFiles: …` and `New log file '…' was created.`; a detach writes nothing.
  A `CREATE DATABASE` over a taken path logs Msg 5170 at state 1 and then 4, and a file the disk holds — a detached database's, a backup — is a taken path too.
- **The files** list on the disk's surfaces (`sys.dm_os_file_exists`, `xp_fileexist`, `xp_dirtree`); `xp_delete_file` refuses one as no backup (Msg 22049 class 15).

## Differential and log backups

`BACKUP DATABASE … WITH DIFFERENTIAL` and `BACKUP LOG` write sets beside full ones, each with its LSNs and the database's [`DatabaseBackupChain`](../../src/SqlServerSimulator/Storage/Backup/DatabaseBackupChain.cs) moving as real's does (probed 2026-10-16 against SQL Server 2025).
A full backup that isn't `COPY_ONLY` becomes the differential base, and outside `SIMPLE` recovery a full or differential backup starts the log chain when none runs; a log backup that isn't `COPY_ONLY` continues it from where the last left off, the first after a chain starts `begins_log_chain`.
Switching to `SIMPLE` recovery breaks the chain, so a log backup is Msg 4214 until a full or differential backup starts the next; a differential before any full backup is Msg 3035, of `master` Msg 3024.
A recovered database's last full set is its differential base and, outside `SIMPLE` recovery, the log chain continues from the last set's last LSN — or, recovered at a stop inside a log set, from that set's first.
`sys.database_recovery_status.last_log_backup_lsn` (NULL while restoring), `sys.master_files`' differential base columns, `sys.dm_db_log_stats`' log backup columns and `sys.dm_db_file_space_usage.modified_extent_page_count` report the chain.
Every set carries the database as it stood, and a log set the transactions committed since the log backup before it as well (see [The log a log backup carries](#the-log-a-log-backup-carries)).

`BACKUP LOG … WITH NORECOVERY` takes the tail of the log and leaves the database `RESTORING`, its own restore sequence standing at the set's end, so its full, differential and log sets restore over it without `REPLACE` (probed 2026-10-16 against SQL Server 2025); the set's `Flags` adds 256 and `is_force_offline` / `IsForceOffline` is 1, and it needs the database to itself as a restore does.
`NO_TRUNCATE` of an online database backs its log up as `COPY_ONLY` does (`Flags` 1536), the chain left where it was; of a restoring one it is Msg 927 state 4, where every other backup of it is state 2, both then Msg 3013.
`ALTER DATABASE` of a restoring database is Msg 5052, then 5069.

## msdb's history

Each backup writes real's rows (probed 2026-10-16 against SQL Server 2025): `backupmediaset` and `backupmediafamily` once per media set, which `INIT` keeps and `FORMAT` replaces, so every set appended to a device shares one; then `backupset`, a `backupfile` row per database file and a `backupfilegroup` row per filegroup.
A restore writes `restorehistory` (`restore_type` D for a full set, I for a differential, L for a log), `restorefile` and `restorefilegroup`, and `RESTORE VERIFYONLY … WITH LOADHISTORY` a `restorehistory` row of type V; a set msdb has no row for gets its backup rows first.
`recovery` is 1 only on the restore that recovered, `stop_at` holds a `STOPAT`'s time (with or without recovery) or the time a found mark was placed, and `stop_at_mark_name` / `stop_before` a mark option's name and which it was, the time NULL when the set lacked the mark (probed 2026-10-16 against SQL Server 2025); a `RESTORE … WITH RECOVERY` alone writes no row.
A committed transaction `BEGIN TRAN name WITH MARK ['description']` began writes a `logmarkhistory` row for each database it wrote rows in — name, description, login, a stand-in LSN and the time the mark was placed; one that rolled back or wrote nothing writes none.

## Reading a device without restoring

`RESTORE VERIFYONLY` (Msg 3262 for the set it reads, `FILE = 1` unless named), `HEADERONLY` (every set, or the one `FILE` names), `FILELISTONLY` and `LABELONLY` return real's columns and types (probed 2026-10-16 against SQL Server 2025).
An option that would change a database is Msg 3032 state 2 with no Msg 3013 after it, `STATS` one for all but `VERIFYONLY`; a missing set is Msg 4038, and only `VERIFYONLY` refuses a transaction.

## What a backup carries

A [`DatabaseImage`](../../src/SqlServerSimulator/Storage/Backup/DatabaseImage.cs): the database's settings, files, filegroups, schemas, principals, permissions, role memberships, extended properties, keys and plan guides as values; every schema object as the T-SQL that recreates it, with the object ids it allocates; the state that DDL can't set (column and index ids, constraint trust and disabled flags, system-named marks, index options, identity and sequence positions, owners, `EXECUTE AS` principals, trigger switches and order, modification dates); and every table's rows.
Modules replay their stored definition verbatim, under the `QUOTED_IDENTIFIER` and `ANSI_NULLS` they were created with and in their own schema.

Partition functions and schemes come back as values under their own ids, and each table's, key's and index's placement on a scheme is stamped on the recreated object, since a partition is never stored apart.
Recreating objects through the engine's DDL, rather than copying the object graph, is what rebinds every parsed expression — a default calling a sequence, a computed column calling a function, a view's bound base tables — to the restored database's own objects.
A copy would leave those pointing into the source database, which a deep clone of this graph can't detect.

Ids come back as they were backed up: each replayed statement draws its ids from a queue the step carries (`Database.RestoredObjectIds`), the creation clock is pinned to the object's own instant (`SimulatedDbConnection.PinnedUtcNow`), so `object_id`, `create_date` and the index and column ids all match.
A statement that draws more ids than its objects keep — a DML trigger binds its `inserted` / `deleted` tables, a module binds a table-valued parameter's constraints — runs in a transaction the replay rolls back and repeats with the extra draws served first.
Fix-ups look objects up by name, so an id that still came back different costs fidelity, not the restore.

Service Broker's state travels as one value beside the steps, since no statement rebuilds a conversation: the broker id, the message types, contracts, queues (with their settings and messages), services, routes, bindings and priorities under their own ids, and the database's conversation endpoints, groups and held transmission-queue messages, a dialog between two databases relinked when the far side still names the endpoint it replaces.
A restore puts it in place of whatever the database held, in place or rebuilt, and leaves the broker disabled unless a switch says otherwise (see [`service-broker.md`](service-broker.md#backup-and-restore)).

A database holding something the image can't rebuild yet makes `BACKUP` raise `NotSupportedException` naming it (see Not modeled yet), so a backup never silently drops part of a database.

## How a restore runs

Restoring over a database whose definitions are still exactly the backup's — one database restored again and again from one backup, as a test harness does before each fixture — happens **in place**: its rows are replaced table by table (as `TRUNCATE TABLE` empties a heap, then the backed-up rows load straight into it), every value the image carries is stamped again, and no object is rebuilt.
"Exactly" is decided by capturing the live database's definitions and comparing them with the image's, statement for statement, statistics aside, since queries create statistics as they compile; the restored database keeps the image's statistics either way.
Anything else — a new name, a dropped database, a definition changed since — rebuilds the database: a new one registers under the name (and the replaced database's `database_id`) in the `RESTORING` state, which turns away every other session (Msg 927, a login naming it Msg 4060), then the replay runs, the rows load, and the state goes `ONLINE`.

Measured 2026-10-11 on a Configured Commerce API database (`ApiReal.bacpac`: 301 tables, 26,332 rows, 115 modules, 3,088 defaults, 435 foreign keys), Release build, in process: a backup takes about 0.23 s, an in-place restore 0.17–0.27 s, a rebuild about 0.9 s; over TDS, an in-place restore 0.22–0.28 s and a rebuild 1.3 s.

A restore needs the database to itself: it waits about 20 seconds for every other session using it — or holding its single-user slot — to leave, going ahead the moment they do, and refuses with Msg 3101 after that, as real does.
A harness's `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` from `master` makes its own session the single user, so the application connection pools that reconnect between that statement and the `RESTORE` are refused (a login naming the database Msg 4060, a `USE` or three-part reference Msg 924) rather than taking the database back; see [`database-options.md`](database-options.md).
When that statement ended any session, it first holds the database in transition for about three seconds, refusing logins at once with a lone Msg 18456; the pools that reconnect then fail fast and sit out SqlClient's blocking period instead of meeting the restore, whose refusal SqlClient retries ten seconds later, the pool opening no other connection meanwhile (see [`database-options.md`](database-options.md#settling-the-sessions-a-rollback-ended)).

A restore brings back the backed-up recovery model and user access mode, as real does — a harness that sets `SINGLE_USER` and `RECOVERY SIMPLE` before restoring finds the database `MULTI_USER` and in `FULL` recovery after it.

## Restore sequences

A full set restored `WITH NORECOVERY` leaves the database `RESTORING` — Msg 927 to every session, the restoring one's included — in a restore sequence (`Database.PendingRestore`) standing at the set's last LSN; `WITH STANDBY = file` leaves it online and read-only (`is_in_standby`, `DATABASEPROPERTYEX(…, 'IsInStandBy')`, Msg 3906 to a write) with the undo file on the server's disk until recovery deletes it.
The sequence takes, in LSN order, differential sets of the full set it began from and log sets reaching the LSN it stands at, as SQL Server 2025 was probed taking them (2026-10-16):

- A differential or log set onto a database no sequence holds is Msg 3117, onto a missing database Msg 3118, and one of another database Msg 3154 state 4 — which a full set over a restoring database without `REPLACE` is too, though it needs no tail-log backup there.
- A log set beginning past the LSN the sequence needs is Msg 4305 and one ending before it Msg 4326, each naming both LSNs; one ending exactly there applies, to no effect, so the last log restored can be restored again.
- A differential of another base is Msg 3136 state 1, one older than the logs restored since its base state 3.
- `RESTORE DATABASE` of a log set restores it as `RESTORE LOG` does and says so in its messages; `RESTORE LOG` of a full or differential set is refused by the same LSN rules.
- A log set reports no page of each data file and its own of the log file.
- `RESTORE { DATABASE | LOG } name WITH RECOVERY` alone recovers the database where the sequence stands, with Msg 3014 of no pages; of an online database it is Msg 3153, of a missing one Msg 3118.
- `RECOVERY`, `NORECOVERY` and `STANDBY` conflict pairwise, as `STOPAT`, `STOPATMARK` and `STOPBEFOREMARK` do, with real's Msg 3031 wording (`'stopatmark' conflicts with option(s) 'stopbeforemark, stopbeforemark'` included).

### Stopping inside a log

`STOPAT = time` stops a log set's restore after the last transaction committed by then, and `STOPATMARK = 'name' [AFTER time]` / `STOPBEFOREMARK` after or before the first marked transaction of that name; with `NORECOVERY` the database stays restoring at that point, and the same log set can be restored again to a later one.
Recovery there forks the database's recovery path: `sys.database_recovery_status` reports a new `recovery_fork_guid`, the old one as `family_guid` and `first_recovery_fork_guid`, and a `fork_point_lsn`.
What real does with a stop point that doesn't fall inside the set (probed 2026-10-16 against SQL Server 2025):

- A log set ending before its `STOPAT` restores whole and leaves the database restoring for the next, with Msg 4322 state 1; one without the mark with Msg 4329.
- A `STOPAT` earlier than where the sequence already stands, in a log set that begins before that point, is Msg 4335 after the set's page messages; one earlier than all of a set that begins where the sequence stands restores none of it.
- On a full or differential set, a `STOPAT` before the backup is Msg 4338; one after it leaves the database restoring — quietly with `NORECOVERY`, and otherwise as Msg 3013 followed by Msg 4322 state 2.
- A full restore over the database without `REPLACE` takes a `STOPAT` the log backups reach in place of a tail-log backup; one past the last log backup is Msg 3173.
- A `STOPAT` of NULL or an empty string is Msg 3217 state 2, of any other string that isn't a time state 1, both before the database is looked at; a number is Msg 206.

### The log a log backup carries

Real reaches a stop point by redoing its log; the simulator keeps, per database, the transactions committed since the last log backup ([`DatabaseLog`](../../src/SqlServerSimulator/Storage/Backup/DatabaseLog.cs)) — each one's commit time, mark, and the rows it deleted and inserted in write order — and a log backup carries them beside its end image.
The heap records each visible row write into the writing transaction's undo log (`Heap.LogWrite`), so a rollback drops it, and the commit publishes one record per database; a row is kept as its stored bytes when no cell lies off-row, else as its values, since an off-row cell's LOB chain belongs to the heap.
`TRUNCATE TABLE` records the delete of every row it removes.

Recording runs only while the database keeps a log (`Database.KeepsLog`): a log backup chain runs, which a full or differential backup outside `SIMPLE` recovery starts.
A database that never takes one pays a field test per write; measured 2026-10-16 (Release, in process, 20,000 single-row inserts then twenty 20,000-row updates of a narrow table) against the build before recording existed, it ran 117 ms / 645 ms against 118 ms / 594–675 ms, while one keeping its log ran 140 ms / 973 ms and held the row images in memory.
Between two log backups the log keeps at most 32 MB of row images, then stops recording — and costing — until the next log backup; a stop point inside the span it didn't record is refused rather than restored wrong.

A stop replays rows ([`LogReplay`](../../src/SqlServerSimulator/Storage/Backup/LogReplay.cs)) matching each by its whole image, since a restore loads rows afresh at new addresses: forward from where the sequence stands up to the stop, or — when a definition changed before the stop — back from the set's end image over the transactions after it.
A definition change is noticed wherever a statement moves the plan cache's schema version (`Simulation.BumpSchemaVersion`), which compares each logging database's definitions with those its last commit stood on, so a change made by any statement, in any database, reaches the log of the one it changed; one a transaction rolled back is noted too, which can only refuse a stop.
A stop with a definition change on both sides of it, inside one log set, isn't replayed (see Not modeled yet).

## The error log

A backup, a restore and their refusals write real's lines to the error log, the LSNs and page counts standing in for real's as [the error log](catalog-views.md#the-sql-server-error-log) records; the backup set keeps its stand-in first LSN and the database's last good `CHECKDB` time, which a restore brings back (probed 2026-10-16 against SQL Server 2025).
A full or differential set starts the database up and logs it marked `RESTORING`; a recovery starts it up again and logs `Restore is complete on database '…'.  The database is now available.`; then come the set's own line — `Database was restored: …`, `Database changes were restored. …` or `Log was restored. …` — and, for `RESTORE DATABASE` and a recovery alone only, the Msg 3014 summary; a `STOPAT` too early logs its Msg 4335 text under the session.
Real's lines between them — parallel redo starting and stopping, the transactions rolled forward and back, the checkpoint recovery writes — are left out.

## The maintenance solution

Ola Hallengren's SQL Server Maintenance Solution (version 2026-09-12 15:02:38) installs into a user database and runs here as on SQL Server 2025 (probed 2026-10-16), over user databases holding fragmented indexes, a heap, standalone statistics, a LOB column and a partitioned table:
`IndexOptimize` with every fragmentation group, `@UpdateStatistics = 'ALL'`, `@OnlyModifiedStatistics = 'Y'` and `@LogToTable = 'Y'`, executing or not; `DatabaseIntegrityCheck` with `CHECKDB`, `CHECKALLOC`, `CHECKTABLE`, `CHECKCATALOG` and `CHECKFILEGROUP`; and `DatabaseBackup` of each `@BackupType` with `@Verify`, `@CheckSum` and `@CleanupTime`.
Each generates real's commands and `CommandLog` rows and sends real's messages, but for the figures real's storage decides: page counts and fragmentation (see [`indexes.md`](indexes.md)), the LSNs and the allocated and modified extent counts, the system tables `CHECKDB` and `CHECKALLOC` list (see [`dbcc.md`](dbcc.md)), and a statistic real leaves unbuilt that a query here built.
Real's `DatabaseBackup` checks that the root `@Directory` exists and relies on `BACKUP` creating the directories beneath it.

## Divergences

- **A backup takes a shared lock on every table while it reads.**
  Real copies pages and the log beside running writers, then rolls their uncommitted work back on restore; here a backup waits for open write transactions on the database to end and holds writers off while it reads (about 0.2 s for the database above).
  It waits with nothing held and starts its sweep again, so it never joins a deadlock.
- **Page counts** in Msgs 4035 / 3014 are a new database's 384 plus the tables' own heap and LOB pages for the primary data file, 8 for any other data file and 2 for each log file; a differential reports 96 for the primary data file, and a log backup a page per 16 blocks of log it spans.
- **LSNs** are the simulator's stand-ins in the active virtual log file `DBCC LOGINFO` reports: only how one set's relate to another's matches real, each logged commit moving the log's end eight blocks.
- **The message order of a stop past a full or differential set** is SqlClient's: the exception lists Msg 3013 first and the page messages and Msg 4322 after it, where real's stream sends the page messages, then 3013, then 4322.
- **A stop point is matched against commit times**, which `STOPAT`'s `datetime` rounds to a 300th of a second; a stop replayed back from a log set's end leaves identity positions as the end left them.
- **`modified_extent_page_count`** is real's 72 after a differential base plus each table written since, in whole extents, and `log_since_last_log_backup_mb` counts 512 bytes a log block; both stand in for figures real's storage decides.
- **`xp_dirtree` lists names in order**, where real lists each directory in its file system's order.
- **With `OpenBackupFile` set**, the directories are the simulation's own, `xp_delete_file` deletes nothing (the delegate has no delete) and `xp_dirtree` lists only the files the simulation knows.
- **`STATS` steps** are the exact multiples, sent once the work is done, where real sends the first progress point at or past each multiple as it goes; real also closes each message with a `DONE_MORE` token of its own.
- **Statistics come back without the histograms they held**, as though never built; `UPDATE STATISTICS` or an automatic refresh builds them over the restored rows.
- **Query Store** comes back with its options and no captured data.
- **A different simulator build refuses a backup** (Msg 3169) in either direction, where real restores an older version's backup.
- **A session already using a database it detaches keeps its old copy.**
  Real lets another session in the database go on, its writes reaching the files, and a re-attach under the same `database_id` puts it back in the database; here the session keeps working in the detached copy, whose writes no file holds.
- **`@skipchecks = 'false'`'s `sp_updatestats`** lists the database's user tables alone, where real's also lists some forty of its system tables.
- **A detached database's file is refused as no database file** (Msg 5172) by a different simulator build, where real attaches an older version's.
- **Real takes about a second** to refuse an attach of a file an online database holds; here the refusal is immediate.

## Not modeled yet

- File and filegroup backups.
- `REWINDONLY`, `PARTIAL` and piecemeal restores, `STOPATMARK = 'lsn:…'`, and `VERIFYONLY`'s permission check.
- A stop inside a log set with a definition change on both sides of it, or inside a span its database's log stopped recording; both raise `NotSupportedException`.
- `RESTORE … WITH RECOVERY` alone combined with `NORECOVERY`, `STANDBY` or a stop point, `RESTORE LOG` of a full or differential set its LSNs don't refuse, and `BACKUP DATABASE … WITH NORECOVERY` / `NO_TRUNCATE`.
- Striped media (more than one device), `TAPE` and `URL` devices written as such (a registered tape device is refused as real's Linux server refuses it), `MIRROR TO`, encryption, `EXPIREDATE` / `RETAINDAYS`, `MEDIANAME` on `RESTORE`.
- Backing up a database holding CLR assemblies or modules, full-text catalogs, change tracking, security policies, `CREATE DEFAULT` / `CREATE RULE` objects or bindings, memory-optimized tables or table types, temporal or graph tables, XML / spatial / JSON / vector / columnstore / hash / hypothetical indexes, masked or `GENERATED ALWAYS` columns, edge constraints, encrypted or numbered modules, or a DDL trigger with a firing order.
- Restoring `model` or `msdb`.
- Detaching or attaching a database holding what a backup can't yet carry (above); `@keepfulltextindexfile` has nothing to keep.
- Attaching a database whose log was lost without a clean shutdown, which no simulator detach leaves.
