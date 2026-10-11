# BACKUP and RESTORE

`BACKUP DATABASE … TO DISK` and `RESTORE DATABASE … FROM DISK`, full backups only.
Behavior was probed 2026-10-11 against SQL Server 2022 (16.0.1200) on Windows.
The progress and summary messages, the refusals for a missing set or device, a new name over existing files, the restoring session's own database and a transaction, and the whole single-user admission sequence (the holder's `ROLLBACK IMMEDIATE`, Msg 924 on `USE` and on a three-part name, Msg 4060 at login, Msg 5064 to another session's `MULTI_USER`, the holder's restore, the first session in once the holder leaves) were re-checked the same day against SQL Server 2025 on Linux and matched.
SQL Server 2025 on Linux creates a missing directory on a backup's path, nested ones included, refusing only where it can't (operating system error 5); with `OpenBackupFile`, whether a path can be written is the host's answer, and the in-memory store writes any.

Code: [`Simulation.BackupRestore.cs`](../../src/SqlServerSimulator/Simulation/Simulation.BackupRestore.cs) (the statements), [`Storage/Backup/`](../../src/SqlServerSimulator/Storage/Backup/) (the format, capture and replay), [`SimulatedSqlException.BackupErrors.cs`](../../src/SqlServerSimulator/Errors/SimulatedSqlException.BackupErrors.cs).

## Devices

A backup device is a file the statement names with `DISK = 'path'` (a variable works too).
Following [`bulk-and-adhoc.md`](bulk-and-adhoc.md#file-access)'s settled rule, the simulator never reaches the host's files on its own: with no `Simulation.OpenBackupFile` delegate, devices live in memory for the life of the `Simulation`, keyed by the path as spelled, which is the simulated server's own disk.
A host that needs real files — a test harness checking `File.Exists` on the backup it asked for — supplies the delegate, which opens the path for reading (null for a missing file) or for writing the device's whole new content (null for a path that can't be written).

The file holds the simulator's own format ([`BackupMedia`](../../src/SqlServerSimulator/Storage/Backup/BackupMedia.cs)), not Microsoft's Tape Format: a real `.bak` is refused as an incorrectly formed media family, which is what real says of a file that isn't one.
Row values are stored in the engine's own storage encodings, so the header records the writing build (the assembly's informational version, commit included) and any other build refuses the backup.

## What a backup carries

A [`DatabaseImage`](../../src/SqlServerSimulator/Storage/Backup/DatabaseImage.cs): the database's settings, files, filegroups, schemas, principals, permissions, role memberships and extended properties as values; every schema object as the T-SQL that recreates it, with the object ids it allocates; the state that DDL can't set (column and index ids, constraint trust and disabled flags, system-named marks, index options, identity and sequence positions, owners, `EXECUTE AS` principals, trigger switches and order, modification dates); and every table's rows.
Modules replay their stored definition verbatim, under the `QUOTED_IDENTIFIER` and `ANSI_NULLS` they were created with and in their own schema.

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

A restore brings back the backed-up recovery model and user access mode, as real does — a harness that sets `SINGLE_USER` and `RECOVERY SIMPLE` before restoring finds the database `MULTI_USER` and in `FULL` recovery after it.

## Divergences

- **A backup takes a shared lock on every table while it reads.**
  Real copies pages and the log beside running writers, then rolls their uncommitted work back on restore; here a backup waits for open write transactions on the database to end and holds writers off while it reads (about 0.2 s for the database above).
  It waits with nothing held and starts its sweep again, so it never joins a deadlock.
- **Page counts** in Msgs 4035 / 3014 are a new database's 384 plus the tables' own heap and LOB pages for the primary data file, 8 for any other data file and 2 for each log file.
- **`STATS` steps** are the exact multiples, sent once the work is done, where real sends the first progress point at or past each multiple as it goes; real also closes each message with a `DONE_MORE` token of its own.
- **Restoring without `REPLACE` over the same database outside `SIMPLE` recovery is always Msg 3159**; real refuses only when the log holds work since the last log backup, which for a database that has seen writes is always.
- **Statistics come back without the histograms they held**, as though never built; `UPDATE STATISTICS` or an automatic refresh builds them over the restored rows.
- **Query Store** comes back with its options and no captured data.
- **A different simulator build refuses a backup** (Msg 3169) in either direction, where real restores an older version's backup.

## Not modeled yet

- `BACKUP LOG` (Msg 4208 under `SIMPLE` recovery is modeled), differential and file backups, `COPY_ONLY`'s effect on a log chain (accepted, nothing else to affect).
- `RESTORE` with `NORECOVERY` / `STANDBY`, `RESTORE LOG`, `HEADERONLY`, `FILELISTONLY`, `LABELONLY`, `VERIFYONLY`, `REWINDONLY`, point-in-time options.
- Striped media (more than one device), `TAPE` and `URL` devices, `MIRROR TO`, encryption, `EXPIREDATE` / `RETAINDAYS`, `MEDIANAME` on `RESTORE`.
- Backing up a database holding CLR assemblies or modules, partitioning, full-text catalogs, change tracking, security policies, `CREATE DEFAULT` / `CREATE RULE` objects or bindings, memory-optimized tables or table types, temporal or graph tables, XML / spatial / JSON / vector / columnstore / hash / hypothetical indexes, masked or `GENERATED ALWAYS` columns, edge constraints, encrypted or numbered modules, or a DDL trigger with a firing order.
- `msdb`'s backup history (`backupset`, `restorehistory`), and restoring `model` or `msdb`.
