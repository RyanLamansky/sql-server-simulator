# `ALTER DATABASE` SET-option surface

Closed accept-list parser (`RecognizedDatabaseOptions` in `Simulation.Alter.cs`) covering every database-scope toggle SqlPackage emits from a bacpac's `SqlDatabaseOptions` element.
Most options parse-and-discard; only the seven "load-bearing" toggles (`COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`, `RECURSIVE_TRIGGERS`, `TRUSTWORTHY`, `DB_CHAINING`, `READ_ONLY` / `READ_WRITE`) drive actual behavior.
`RECOVERY` is tracked without driving anything — the simulator has no transaction log, but `sys.databases.recovery_model` / `recovery_model_desc` report it, and a bacpac carries the source database's value, so an imported database describes itself the way the original did.
Real ships `master` / `tempdb` / `msdb` SIMPLE and `model` FULL, which every new user database inherits (probe-confirmed).

## Target database

`ALTER DATABASE <name>` lands on **that** database, not the session's — `CURRENT` names the session's (`ResolveAlterDatabaseTarget`).
So a per-database flag is settable from anywhere, which is what lets one batch stage two databases' versioning options.
A name the `Simulation` doesn't host raises **Msg 5011** sev 14 state 5 (`User does not have permission to alter database '<n>', the database does not exist, or the database is not in a state that allows access checks.`); real follows it with a trailing Msg 5069 (`ALTER DATABASE statement failed.`), which the simulator omits the way it omits the Msg 297 that trails a DMV denial.
The name also governs the `COLLATE` clause below.

## Recognized options by value shape

**`OnOff`** (`SET <name> {ON | OFF}`):
- `ANSI_NULLS` / `ANSI_PADDING` / `ANSI_WARNINGS` / `ARITHABORT` / `CONCAT_NULL_YIELDS_NULL` / `NUMERIC_ROUNDABORT` / `QUOTED_IDENTIFIER` / `TORN_PAGE_DETECTION` / `TEMPORAL_HISTORY_RETENTION`

**`EnumIdent`** (`SET <name> <bareIdent>`):
- `RECOVERY`: `FULL` / `BULK_LOGGED` / `SIMPLE`
- `PAGE_VERIFY`: `CHECKSUM` / `TORN_PAGE_DETECTION` / `NONE`
- `CURSOR_DEFAULT`: `GLOBAL` / `LOCAL`

**`EqualsOnOff`** (`SET <name> = {ON | OFF}` — `=` required per probe):
- `ACCELERATED_DATABASE_RECOVERY`
- `OPTIMIZED_LOCKING`

**`IntegerWithUnit`** (`SET <name> = N SECONDS|MINUTES` — unit required per probe):
- `TARGET_RECOVERY_TIME`

**`AccessMode`** (bare state, no `=`, with an optional termination clause): `SET {SINGLE_USER | MULTI_USER | RESTRICTED_USER} [WITH ROLLBACK IMMEDIATE | WITH ROLLBACK AFTER n [SECONDS] | WITH NO_WAIT]`.
`READ_ONLY` / `READ_WRITE` take the same shape and the same termination clause but are load-bearing — see [Read-only databases](#read-only-databases).
The state and the termination clause are both parse-and-discarded — the simulator has no connection-count access model, so it never actually restricts, and `WITH ROLLBACK …` never evicts.
Load-bearing for `DROP DATABASE`: every ORM/app test-teardown runs `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` immediately before the drop (Django/mssql-django).
Parsed explicitly (`ConsumeAccessModeTail`) rather than scanned to a boundary, because `ROLLBACK` is itself a statement-starting keyword — only `WITH`/`ROLLBACK` tokenize as keywords, `IMMEDIATE`/`AFTER`/`SECONDS`/`NO_WAIT` are matched by text.

**`QueryStore`** (dedicated sub-grammar): `SET QUERY_STORE = ON [( … )] | = OFF | CLEAR [ALL]`.
The sub-options block is itself a closed accept-list (`RecognizedQueryStoreSubOptions`):
- `OPERATION_MODE` / `CLEANUP_POLICY` / `DATA_FLUSH_INTERVAL_SECONDS` / `MAX_STORAGE_SIZE_MB` / `INTERVAL_LENGTH_MINUTES` / `SIZE_BASED_CLEANUP_MODE` / `QUERY_CAPTURE_MODE` / `MAX_PLANS_PER_QUERY` / `WAIT_STATS_CAPTURE_MODE` / `QUERY_CAPTURE_POLICY`

The two nested-block sub-options (`CLEANUP_POLICY`, `QUERY_CAPTURE_POLICY`) eat balanced parens via `SkipBalancedParens` without enforcing inner-block sub-option names.

## Load-bearing options (behavior wired)

These dispatch to dedicated helpers rather than falling into the parse-and-discard accept-list:

- **`COMPATIBILITY_LEVEL`** — stored on `Database.CompatibilityLevel`.
- **`ALLOW_SNAPSHOT_ISOLATION`** — toggles `Database.AllowSnapshotIsolation`; required for `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`.
  See [`locking.md`](locking.md).
- **`READ_COMMITTED_SNAPSHOT`** — toggles `Database.ReadCommittedSnapshot`; switches RCSI behavior for the default READ COMMITTED isolation level.
  See [`locking.md`](locking.md).
- **`RECURSIVE_TRIGGERS`** — toggles `Database.RecursiveTriggers`; lets an AFTER trigger's own DML re-fire that trigger, and surfaces as `sys.databases.is_recursive_triggers_on`.
  See [`triggers.md`](triggers.md#nesting-and-recursion-options).
- **`TRUSTWORTHY`** — toggles `Database.Trustworthy`; lets a database-scoped identity established here (an `EXECUTE AS USER` frame, a module's `WITH EXECUTE AS <user>` frame, an activated application role) reach another database, where its own login then answers.
  Surfaces as `sys.databases.is_trustworthy_on`.
  See [`permissions.md`](permissions.md#cross-database-references).
- **`DB_CHAINING`** — toggles `Database.CrossDatabaseChaining`; an ownership chain crosses the database boundary only when *both* databases have it on.
  Surfaces as `sys.databases.is_db_chaining_on`.
  See [`permissions.md`](permissions.md#cross-database-references).
- **`READ_ONLY` / `READ_WRITE`** — toggles `Database.IsReadOnly`, which refuses every write to that database.
  See [Read-only databases](#read-only-databases).

Both cross-database toggles take the bare `ON` / `OFF` shape (`SET TRUSTWORTHY = ON` is Msg 102, probe-confirmed), and each refuses a set of system databases whatever the value asked for:

| Statement | Refused on | Error |
|---|---|---|
| `SET TRUSTWORTHY` | `model` / `tempdb` | **Msg 15309** class 16 state 1 — `Cannot alter the trustworthy state of the model or tempdb databases.` |
| `SET DB_CHAINING` | `master` / `model` / `tempdb` | **Msg 5600** class 16 state 2 — `The Cross Database Chaining option cannot be set to the specified value on the specified database.` |

`msdb` is the one system database real lets either flag move on.
The **shipped defaults** match real (probe-confirmed): `master` / `tempdb` chained, `msdb` chained *and* trustworthy, `model` and every user database neither.
Neither flag is inherited from `model` — a new database starts with both off, which real enforces structurally by refusing to set them on `model` at all.

## Read-only databases

`ALTER DATABASE <name> SET { READ_ONLY | READ_WRITE }` moves `Database.IsReadOnly`, projected by `sys.databases.is_read_only` and by `DATABASEPROPERTYEX(name, 'Updateability')` (`READ_ONLY` / `READ_WRITE`).
Every write to a read-only database is **Msg 3906** class 16 — `Failed to update database "<n>" because the database is read-only.` — the identical wording for DML and DDL, probe-confirmed against SQL Server 2025 (2026-08-04, the states and the catalog-writing statements re-probed 2026-08-08).
The state is **1** everywhere but `ALTER TABLE`, whose every sub-action (ADD / DROP / ALTER COLUMN, ADD / DROP CONSTRAINT, CHECK / NOCHECK, REBUILD) reports **12**.
The error names the database that *would have been written*, so a three-part write out of another session database reports the target's name, the same rule the rowversion counter and trigger dispatch follow.

**The check happens where the write happens**, which is what reproduces real's laziness.
An `UPDATE` or `DELETE` matching no row, an `INSERT … SELECT` producing none, and a `MERGE` whose actions all decline complete quietly; an `INSERT … VALUES`, a `TRUNCATE` of an already-empty table, and every DDL statement raise.
Writes to a table belonging to no database — a `#temp` table, a `##global` table, a table variable, a table-valued parameter — are unaffected however the session's own database is set, matching real's separate `tempdb`; `SELECT … INTO #t` reading a read-only table is legal, while `SELECT … INTO <permanent>` is not.

Enforced at two kinds of seam: the per-row DML writes (INSERT / UPDATE / DELETE / MERGE / bulk load, keyed on `HeapTable.OwningDatabase`), and the DDL statements' own target resolution — the module `CREATE` / `ALTER` family through `ResolveModuleSchema`, plus `CREATE TABLE`, `SELECT … INTO`, `TRUNCATE`, `ALTER TABLE`, `CREATE INDEX`, `ALTER SEQUENCE`, the `CREATE` / `DROP` pairs for sequences, types and synonyms, and every `DROP` of a table, view, procedure, function, sequence, type or trigger.

The catalog-writing statements carry it too: `GRANT` / `REVOKE` / `DENY`, `sp_rename` (object, column and index forms), `sp_addextendedproperty` and its update / drop siblings, `ALTER SCHEMA … TRANSFER`, `CREATE SCHEMA`, `CREATE` / `ALTER` / `DROP INDEX`, `CREATE STATISTICS`, `CREATE` / `DROP ASSEMBLY`, and the database-scoped principal DDL (`CREATE` / `DROP USER`, `CREATE` / `DROP ROLE`, `ALTER ROLE … ADD | DROP MEMBER`, the application-role trio).
Login and server-role DDL don't: those write `master`, which can never be read-only.

**Where the refusal sits relative to name resolution differs per statement**, and real's order is what each gate follows (all probe-confirmed):

| Statement | Refused before or after resolution |
|---|---|
| `GRANT` / `REVOKE` / `DENY` | before — a missing object *or* principal still reports Msg 3906 |
| `ALTER SCHEMA … TRANSFER` | before — a missing object still reports Msg 3906 |
| `CREATE USER` / `CREATE ROLE` | before — an existing name still reports Msg 3906 |
| `DROP ASSEMBLY` | before — a name no assembly holds still reports Msg 3906 |
| `ALTER INDEX` | after the *table* (a missing one is Msg 1088), before the index |
| `DROP INDEX` | after both — a missing table or index reports its own Msg 3701 |
| `sp_rename` | after the target resolves (Msg 15225 / 15248 otherwise) |
| `sp_addextendedproperty` | after the target resolves (Msg 15135 otherwise) |
| `ALTER TABLE` | after the table resolves (Msg 4902 otherwise) |
| every `DROP` of an object | after — real checks existence before the access mode |

The **full-text** statements report the subsystem's own **Msg 7690** instead (`Full-text operation failed because database is read only.`), at severity 16 and a state per statement: `CREATE FULLTEXT CATALOG` 100, `DROP FULLTEXT CATALOG` 102, `CREATE FULLTEXT INDEX` 103, `DROP FULLTEXT INDEX` 105.
`CREATE SCHEMA` raises Msg 3906 and then real's own trailing Msg 2759, of which the simulator reports the 3906.

`master` and `tempdb` **pin** the option and raise **Msg 5058** class 16 for either value asked for — `Option '<READ_ONLY|READ_WRITE>' cannot be set in database '<n>'.` — at their own states, **5** for `master` and **4** for `tempdb`.
`model` and `msdb` both accept it.

`COMPATIBILITY_LEVEL` is the one `SET` option a read-only database itself refuses (Msg 3906, which real trails with a Msg 5069 the simulator omits like every other ALTER DATABASE failure).
`ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`, `RECURSIVE_TRIGGERS`, `ANSI_NULLS`, `RECOVERY` — and `READ_WRITE` itself — all move freely on one, probe-confirmed by reading the flags back.

**A bacpac import lands writable.**
DacFx omits the access mode from `SqlDatabaseOptions` even when exporting a read-only database (verified against the WideWorldImporters and AdventureWorks models, neither of which carries the property), and an `IsReadOnly` property is deliberately not translated: the element is read in phase 1, before the schema and data load, so a `READ_ONLY` set there would refuse the rest of its own import.
Carrying it wants a post-load hook.

### Not modeled yet

- The database-level `OFFLINE` / `EMERGENCY` / `RESTRICTED_USER` states real also refuses writes in stay parse-and-discard.

## `COLLATE` clause

`ALTER DATABASE name COLLATE <name>` — separate top-level grammar, not under `SET`.
Validates against `Collation.Recognized` (12 entries — see [`collations.md`](collations.md)).
Stores on `Database.CollationName`.
An unrecognized *collation* name raises `NotSupportedException` rather than silently accepting — silent acceptance would mean the bacpac loader silently mis-loads collation-sensitive data on a non-default-collation model.
(An unrecognized `SET` **option** name is a different path and never reaches an accept-list: the grammar has no production for it, so it is Msg 102 at the name.)

`sys.databases.collation_name` and `DATABASEPROPERTYEX(db, 'Collation')` surface the declared name.

Per-column declarations, the postfix `expr COLLATE name` operator, coercibility resolution, Msg 468 / 457 cross-collation enforcement, and `#temp` collation inheritance are documented in [`collations.md`](collations.md).

## `IsFullTextEnabled`

Not handled here — emitted by SqlPackage as `EXEC sp_fulltext_database 'enable|disable'`, a system sproc the simulator doesn't model.
See [`full-text.md`](full-text.md) for the broader full-text deferral.

## Error paths

All raise Msg 102 — matching probed real SQL Server wording:
- `SET RECOVERY = FULL` (EnumIdent options reject `=`)
- `SET ACCELERATED_DATABASE_RECOVERY ON` (EqualsOnOff options require `=`)
- `SET TARGET_RECOVERY_TIME = 60` (IntegerWithUnit options require the unit)

## Bacpac loader context

`EmitDatabaseOptions` in `ModelXmlReader.cs` translates each `SqlDatabaseOptions` property to its `ALTER DATABASE … SET …` form.
Options that fall outside the accept-list (e.g. unrecognized future toggles) record on `BacpacLoadResult.Warnings` and the load continues — graceful degradation per the load-best-effort contract.
See [`bacpac-loader.md`](bacpac-loader.md).
