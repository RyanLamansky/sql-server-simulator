# Memory-optimized tables

In-Memory OLTP's surface: the `MEMORY_OPTIMIZED_DATA` filegroup and its containers, `CREATE TABLE … WITH (MEMORY_OPTIMIZED = ON)` with its hash and range indexes, memory-optimized table types, the DDL, hints and isolation levels such a table refuses, the write conflicts that replace waiting, and natively compiled modules.
Everything here was probed 2026-10-02 against SQL Server 2025 unless it says otherwise.

The rows live on the ordinary heap (`HeapTable.IsMemoryOptimized` marks the table).
What the feature changes is modeled — the rules, the catalog, and how a session reaches the rows — and what it is for is not: there is no lock-free engine, no checkpoint file pair, and no restart for `DURABILITY` to be observed through.

## The filegroup and its containers

`ALTER DATABASE … ADD FILEGROUP name CONTAINS MEMORY_OPTIMIZED_DATA`, or a `FILEGROUP … CONTAINS MEMORY_OPTIMIZED_DATA` group of `CREATE DATABASE`'s file list, records the filegroup on `Database.MemoryOptimizedFilegroupId`; a second one is Msg 10797.
`sys.filegroups` and `sys.data_spaces` report it as `FX` / `MEMORY_OPTIMIZED_DATA_FILEGROUP` with `is_default` 1 beside the real default, so `MODIFY FILEGROUP … DEFAULT` says the property is already set (Msg 5045 state 3), `READ_ONLY` / `READ_WRITE` is Msg 41361, and `REMOVE FILEGROUP` is Msg 5042 state 8 — the filegroup goes only with its database.

A file added to it is a container (`DatabaseFile.IsContainer`): `sys.database_files` type 2 `FILESTREAM`, ids from 65537, size 0, unlimited, no growth.
`SIZE` or `FILEGROWTH` on one is Msg 5509 and a bounded `MAXSIZE` Msg 41873 then 5009.
A container's id is past `smallint`, so `FILE_ID` answers NULL and `FILE_IDEX` the id; `sp_helpfile` and `FILEPROPERTY` leave containers out, and `sp_helpfilegroup` counts none.

`CREATE TABLE … WITH (MEMORY_OPTIMIZED = ON)` in a database without the filegroup is Msg 41337 state 100, and with the filegroup but no container state 1.
This is what EF Core's migrations script checks for: it joins `sys.filegroups` (type `FX`) to `sys.database_files` (type 2) before adding either.

`ALTER DATABASE … SET MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT { ON | OFF }` is recorded (`DatabaseSwitches.MemoryOptimizedElevateToSnapshot`) and reported by `sys.databases` and `DATABASEPROPERTYEX`; it lifts Msg 41368 (see [Isolation](#isolation)).

## Declaring a table

`WITH (MEMORY_OPTIMIZED = ON [, DURABILITY = SCHEMA_AND_DATA | SCHEMA_ONLY])`.
`DURABILITY` without `MEMORY_OPTIMIZED = ON` is Msg 10779 naming the value lower-cased, an `ON filegroup` before the list is Msg 10794 ("The feature 'ON'"), `DATA_COMPRESSION` Msg 10794 state 2, and a temporary table Msg 12322.

What the table may hold, in the order the checks run (`Simulation.ValidateTableDeclaration`):

- **Column types**: `xml`, `text`, `ntext`, `image`, `sql_variant`, `timestamp`, the CLR system types (named `sys.geography`, `sys.hierarchyid` …), `datetimeoffset(n)`, `vector(n)`, `json` and CLR user types are Msg 10794 state 80.
  The MAX types are taken.
- `SPARSE` (state 4), `ROWGUIDCOL` (state 5), and an identity seed or increment other than 1 (Msg 12339).
- **No clustered rowstore key or index**: the default clustered primary key is Msg 12317, so every key is written `NONCLUSTERED`; a clustered columnstore index is taken.
- No filtered index (Msg 10794 "The feature 'WHERE'"), no `INCLUDE` (Msg 10664 then 1750), and no `FILLFACTOR` / `PAD_INDEX` (Msg 10794 state 81).
- **A durable table needs a primary key** (Msg 41321) and a `SCHEMA_ONLY` one at least one index (Msg 41327), each followed by Msg 1750.

A foreign key joins two memory-optimized tables or none (Msg 10778 then 1750, either direction), takes no referential action (Msg 10794 state 134 naming it) and no `NOT FOR REPLICATION` (state 128), and must reference the primary key itself, not a unique key (Msg 10780 then 1750).
A trigger must be natively compiled (Msg 10777).

## Hash and range indexes

A key or inline index written `HASH` — `PRIMARY KEY NONCLUSTERED HASH WITH (BUCKET_COUNT = n)`, `INDEX ix [UNIQUE] HASH (cols) WITH (…)`, the column-level `INDEX ix HASH WITH (…)` — is a hash index; anything else is a range (`NONCLUSTERED`) index.
`BUCKET_COUNT` is required (Msg 10789, naming an unnamed key's index as `''`), must lie in 1 to 2^30 (Msg 41303, both raised compiling the batch) and is kept rounded up to a power of two (100 keeps 128); a range index refuses it (Msg 10790).
On a disk-based table `HASH` is Msg 10791 then 1750.
`sys.indexes` reports a hash index as type 7 `NONCLUSTERED HASH`, and `sys.hash_indexes` lists the same rows with their `bucket_count`.

Index ids follow the declaration in reverse, the rule every table's declaration already follows ([`indexes.md`](indexes.md#index-id-allocation)), and every row a memory-optimized table reports in `sys.indexes` — the heap row included — has `data_space_id` 0 and allows no row or page locks.

## What a memory-optimized table refuses

The index DDL moves into `ALTER TABLE`: `ADD INDEX ix [UNIQUE] [NONCLUSTERED | HASH] (cols) [WITH (BUCKET_COUNT = n)]`, `DROP INDEX ix [, INDEX ix2]` and `ALTER INDEX ix REBUILD WITH (BUCKET_COUNT = n)` — the `WITH` list required, Msg 102 without — are what a memory-optimized table takes (Msg 10785 then 1750 on a disk-based one, and a missing index Msg 3701 state 21 / 22).
An added unique index checks the rows already there.

Msg 10794 for the rest, each with its own state and noun: `CREATE INDEX` (state 7, either form), `DROP INDEX` (106), `ALTER INDEX` (8), `TRUNCATE TABLE` (88, "The statement"), `ALTER TABLE SWITCH` (125) and `REBUILD` (126), `SET (LOCK_ESCALATION = …)` (127, "The option"), change tracking (124, "The feature"), and a `MERGE` target (94, refused compiling the batch).
An `UPDATE` setting a primary key column is Msg 12302, also refused compiling.
`ALTER COLUMN` on a column an index keys on is refused even when it only grows a variable-length type, which a disk-based table allows; this is why EF Core drops and re-adds the index around an altered column.

**Table hints**: `NOLOCK`, the isolation hints `SNAPSHOT`, `REPEATABLEREAD` and `SERIALIZABLE`, and the index hints are taken; `HOLDLOCK`, `NOEXPAND`, `NOWAIT`, `PAGLOCK`, `READCOMMITTED`, `READCOMMITTEDLOCK`, `READPAST`, `READUNCOMMITTED`, `ROWLOCK`, `TABLOCK`, `TABLOCKX`, `UPDLOCK` and `XLOCK` are Msg 10794 state 82 naming the hint lower-cased, raised compiling the batch.
`SNAPSHOT` on a disk-based table is Msg 367.

## Isolation

A memory-optimized table is reached under rules of its own (`BatchContext.CheckMemoryOptimizedIsolation`), each error ending the batch and rolling back as under `XACT_ABORT`, or dooming the transaction for a `TRY`:

- A session at SNAPSHOT is refused outright (Msg 41332), with a hint too, and one at READ UNCOMMITTED likewise (Msg 10794 state 76).
- A session at REPEATABLE READ or SERIALIZABLE needs the `SNAPSHOT` hint (Msg 41333), in or out of a transaction.
- At READ COMMITTED, autocommit reads at snapshot; inside an explicit or implicit transaction a read needs an isolation hint or the database's `MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT` (Msg 41368).
  An `INSERT`'s target reads nothing and needs neither; an `UPDATE`, `DELETE` or `INSERT … SELECT` from the table does.

Every read of the table is a snapshot read (`BatchContext.ResolveSnapshotXidForRead`): the statement's outside a transaction, the transaction's — taken at its first read — inside one, through the same version store SNAPSHOT isolation uses ([`locking.md`](locking.md#snapshot-isolation--mvcc)), which versions every write of the table whatever the database's flags.

## Concurrency: no waiting, write conflicts

A memory-optimized table never makes a session wait.
Its row locks are taken with no timeout and serve only to find a conflicting writer (`BatchContext.AcquireOnTable`), and a writer's target read waits for nothing (`AwaitTargetRow`).
Writing a row another transaction has written — still in flight, or committed since the writer's snapshot (`VersionStore.CheckSnapshotUpdateConflict`) — is Msg 41302 at once, state 110 for an update and 111 for a delete.
It ends the batch; an open transaction is doomed rather than rolled back, so the batch's end rolls it back with Msg 3998, and a `TRY` catches it with `XACT_STATE()` -1 (`SimulatedSqlException.DoomsWhenUncaught`).
A memory-optimized table's row locks never escalate.

A duplicate-key message names the table alone (`'m'`, not `'dbo.m'`).

## Table types and table variables

`CREATE TYPE … AS TABLE (…) WITH (MEMORY_OPTIMIZED = ON)` makes a memory-optimized table type: it needs an index or key (Msg 41327 then 1750, naming the type), refuses a clustered key (Msg 12317) and the column types a table refuses, and takes no `DURABILITY` (Msg 10788, compiling).
`sys.table_types.is_memory_optimized` reports it.
Its variables behave as any table variable: a rollback leaves their rows, and any isolation level reads them.

## Natively compiled modules

`WITH NATIVE_COMPILATION, SCHEMABINDING` on a procedure, scalar function or trigger: the body must be one `BEGIN ATOMIC` block (Msg 10783), whose `WITH` list names a `TRANSACTION ISOLATION LEVEL` and a `LANGUAGE` (Msg 10784 for either missing) and refuses READ COMMITTED (Msg 10794 state 77); `DATEFORMAT`, `DATEFIRST` and `DELAYED_DURABILITY` parse and are discarded.
The body is schema-bound — a select-list star is Msg 1054 and a one-part name Msg 4512 — and reaches memory-optimized tables only (Msg 10775, naming the table as written).
A natively compiled trigger is an `AFTER` trigger (Msg 10794 state 130).
`sys.sql_modules` reports each `uses_native_compilation` and `is_schema_bound` (`SchemaObject.IsNativelyCompiled`).

Running, the atomic block is one transaction: its own when none is open, committed when the block ends, else a savepoint in the caller's.
An error inside rolls the block back and, uncaught, ends the batch, leaving the caller's transaction committable — `@@TRANCOUNT` and `XACT_STATE()` stay 1, a row the caller wrote stays — and a caller's `TRY` catches it with the transaction still committable.
Inside the block a memory-optimized table is reached at the block's own level, so the session's rules don't apply (`SimulatedDbConnection.AtomicBlockDepth`); a SNAPSHOT session may call a natively compiled procedure.

## Catalog

`sys.tables.is_memory_optimized`, `durability` and `durability_desc`; `OBJECTPROPERTY(…, 'TableIsMemoryOptimized')`; `sys.hash_indexes`; `sp_help`'s `Data_located_on_filegroup` reads `not applicable` and `sp_helpindex` describes each index `located in MEMORY ` — a trailing space included — a hash one as `nonclustered hash`.
`sys.dm_db_xtp_table_memory_stats` lists each memory-optimized table with the figures real reports for an empty one: 64 KB allocated for the table and for each index (the heap row's included), none used.

## Divergences

- **Commit-time validation**: real lets two transactions insert the same key and fails the later committer with Msg 41325, and validates a `REPEATABLEREAD` or `SERIALIZABLE` hinted read at commit (Msg 41305 / 41325); here a second writer of a key still in flight meets Msg 41302 at once, and hinted reads are never revalidated.
- **Native compilation's surface**: a natively compiled body runs the whole interpreted T-SQL surface, where real refuses much of it compiling (a subquery outside a `SELECT`, `@@TRANCOUNT`, `XACT_STATE()` — Msg 12311 / 10794 state 85 among others).
- `sys.dm_db_xtp_table_memory_stats` doesn't grow with the rows.
- `ERROR_STATE()` reads a caught Msg 41368's state 1, where real's reads 0.

## Not modeled yet

- The other `sys.dm_db_xtp_*` views (`index_stats`, `hash_index_stats`, `object_stats`, `memory_optimized_tables_internal_attributes`).
- `REMOVE FILE` of a container, which real lets wait on its checkpoint files.
- A natively compiled inline table-valued function.
- `sys.allocation_units` placing a memory-optimized table's units on the `FX` filegroup.
