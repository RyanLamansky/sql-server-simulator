# `ALTER DATABASE` SET-option surface

Closed accept-list parser (`RecognizedDatabaseOptions` in `Simulation.Alter.cs`) covering every database-scope toggle SqlPackage emits from a bacpac's `SqlDatabaseOptions` element.
Most options parse-and-discard; only the three "load-bearing" toggles (`COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`) drive actual behavior.

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
