# BACKUP and RESTORE

`BACKUP DATABASE` (full and differential) and `BACKUP LOG` to `DISK`, `RESTORE DATABASE … FROM DISK` of a full backup set, the `RESTORE` forms that only read a device, msdb's history of both, and the server's disk the backup tooling reads and tidies.
Behavior was probed 2026-10-11 against SQL Server 2022 (16.0.1200) on Windows.
The progress and summary messages, the refusals for a missing set or device, a new name over existing files, the restoring session's own database and a transaction, and the whole single-user admission sequence (the holder's `ROLLBACK IMMEDIATE`, Msg 924 on `USE` and on a three-part name, Msg 4060 at login, Msg 5064 to another session's `MULTI_USER`, the holder's restore, the first session in once the holder leaves) were re-checked the same day against SQL Server 2025 on Linux and matched.
SQL Server 2025 on Linux creates a missing directory on a backup's path, nested ones included, refusing only where it can't (operating system error 5); with `OpenBackupFile`, whether a path can be written is the host's answer, and the in-memory store writes any.

Code: [`Simulation.BackupRestore.cs`](../../src/SqlServerSimulator/Simulation/Simulation.BackupRestore.cs) (the statements), [`Simulation.RestoreInformation.cs`](../../src/SqlServerSimulator/Simulation/Simulation.RestoreInformation.cs) (the forms that only read a device), [`Simulation.BackupHistory.cs`](../../src/SqlServerSimulator/Simulation/Simulation.BackupHistory.cs) (msdb), [`Simulation.ServerFiles.cs`](../../src/SqlServerSimulator/Simulation/Simulation.ServerFiles.cs) and [`Simulation.ServerFileProcedures.cs`](../../src/SqlServerSimulator/Simulation/Simulation.ServerFileProcedures.cs) (the disk), [`Storage/Backup/`](../../src/SqlServerSimulator/Storage/Backup/) (the format, capture and replay, and `DatabaseBackupChain`), [`SimulatedSqlException.BackupErrors.cs`](../../src/SqlServerSimulator/Errors/SimulatedSqlException.BackupErrors.cs).

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

## Differential and log backups

`BACKUP DATABASE … WITH DIFFERENTIAL` and `BACKUP LOG` write sets beside full ones, each with its LSNs and the database's [`DatabaseBackupChain`](../../src/SqlServerSimulator/Storage/Backup/DatabaseBackupChain.cs) moving as real's does (probed 2026-10-16 against SQL Server 2025).
A full backup that isn't `COPY_ONLY` becomes the differential base, and outside `SIMPLE` recovery a full or differential backup starts the log chain when none runs; a log backup that isn't `COPY_ONLY` continues it from where the last left off, the first after a chain starts `begins_log_chain`.
Switching to `SIMPLE` recovery breaks the chain, so a log backup is Msg 4214 until a full or differential backup starts the next; a differential before any full backup is Msg 3035, of `master` Msg 3024.
A restored database's set is its differential base and, outside `SIMPLE` recovery, the log chain continues from the set's last LSN.
`sys.database_recovery_status.last_log_backup_lsn`, `sys.master_files`' differential base columns, `sys.dm_db_log_stats`' log backup columns and `sys.dm_db_file_space_usage.modified_extent_page_count` report the chain.
Every set carries the database as it stood, so restoring a differential or log set could rebuild it, but only a full set restores (see Not modeled yet).

## msdb's history

Each backup writes real's rows (probed 2026-10-16 against SQL Server 2025): `backupmediaset` and `backupmediafamily` once per media set, which `INIT` keeps and `FORMAT` replaces, so every set appended to a device shares one; then `backupset`, a `backupfile` row per database file and a `backupfilegroup` row per filegroup.
A restore writes `restorehistory` (`restore_type` D), `restorefile` and `restorefilegroup`, and `RESTORE VERIFYONLY … WITH LOADHISTORY` a `restorehistory` row of type V; a set msdb has no row for gets its backup rows first.

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

## The error log

A backup, a restore and their refusals write real's lines to the error log, the LSNs and page counts standing in for real's as [the error log](catalog-views.md#the-sql-server-error-log) records; the backup set keeps its stand-in first LSN and the database's last good `CHECKDB` time, which a restore brings back (probed 2026-10-16 against SQL Server 2025).

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
- **LSNs** are the simulator's stand-ins in the active virtual log file `DBCC LOGINFO` reports: only how one set's relate to another's matches real.
- **`modified_extent_page_count`** is real's 72 after a differential base plus each table written since, in whole extents, and `log_since_last_log_backup_mb` counts 512 bytes a log block; both stand in for figures real's storage decides.
- **`xp_dirtree` lists names in order**, where real lists each directory in its file system's order.
- **With `OpenBackupFile` set**, the directories are the simulation's own, `xp_delete_file` deletes nothing (the delegate has no delete) and `xp_dirtree` lists only the files the simulation knows.
- **`STATS` steps** are the exact multiples, sent once the work is done, where real sends the first progress point at or past each multiple as it goes; real also closes each message with a `DONE_MORE` token of its own.
- **Restoring without `REPLACE` over the same database outside `SIMPLE` recovery is always Msg 3159**; real refuses only when the log holds work since the last log backup, which for a database that has seen writes is always.
- **Statistics come back without the histograms they held**, as though never built; `UPDATE STATISTICS` or an automatic refresh builds them over the restored rows.
- **Query Store** comes back with its options and no captured data.
- **A different simulator build refuses a backup** (Msg 3169) in either direction, where real restores an older version's backup.

## Not modeled yet

- File and filegroup backups.
- `RESTORE` with `NORECOVERY` / `STANDBY`, restoring a differential or log set (`RESTORE LOG`, or `RESTORE DATABASE` from either, raises `NotSupportedException` over an existing database; Msg 3118 for a missing one is modeled), `REWINDONLY`, point-in-time options, and `VERIFYONLY`'s permission check.
- Striped media (more than one device), `TAPE` and `URL` devices written as such (a registered tape device is refused as real's Linux server refuses it), `MIRROR TO`, encryption, `EXPIREDATE` / `RETAINDAYS`, `MEDIANAME` on `RESTORE`.
- Backing up a database holding CLR assemblies or modules, full-text catalogs, change tracking, security policies, `CREATE DEFAULT` / `CREATE RULE` objects or bindings, memory-optimized tables or table types, temporal or graph tables, XML / spatial / JSON / vector / columnstore / hash / hypothetical indexes, masked or `GENERATED ALWAYS` columns, edge constraints, encrypted or numbered modules, or a DDL trigger with a firing order.
- Restoring `model` or `msdb`.
