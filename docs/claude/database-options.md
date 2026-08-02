# `ALTER DATABASE` SET-option surface

Closed accept-list parser (`RecognizedDatabaseOptions` in `Simulation.Alter.cs`) covering every database-scope toggle SqlPackage emits from a bacpac's `SqlDatabaseOptions` element.
Most options parse-and-discard; only the six "load-bearing" toggles (`COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`, `RECURSIVE_TRIGGERS`, `TRUSTWORTHY`, `DB_CHAINING`) drive actual behavior.

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

Both cross-database toggles take the bare `ON` / `OFF` shape (`SET TRUSTWORTHY = ON` is Msg 102, probe-confirmed), and each refuses a set of system databases whatever the value asked for:

| Statement | Refused on | Error |
|---|---|---|
| `SET TRUSTWORTHY` | `model` / `tempdb` | **Msg 15309** class 16 state 1 — `Cannot alter the trustworthy state of the model or tempdb databases.` |
| `SET DB_CHAINING` | `master` / `model` / `tempdb` | **Msg 5600** class 16 state 2 — `The Cross Database Chaining option cannot be set to the specified value on the specified database.` |

`msdb` is the one system database real lets either flag move on.
The **shipped defaults** match real (probe-confirmed): `master` / `tempdb` chained, `msdb` chained *and* trustworthy, `model` and every user database neither.
Neither flag is inherited from `model` — a new database starts with both off, which real enforces structurally by refusing to set them on `model` at all.

## `COLLATE` clause

`ALTER DATABASE name COLLATE <name>` — separate top-level grammar, not under `SET`.
Validates against `Collation.Recognized` (12 entries — see [`collations.md`](collations.md)).
Stores on `Database.CollationName`.
Unrecognized names raise `NotSupportedException` rather than silently accepting (silent acceptance would mean the bacpac loader silently mis-loads collation-sensitive data on non-default-collation models).

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
