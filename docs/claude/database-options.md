# `ALTER DATABASE` SET-option surface

Closed accept-list parser (`RecognizedDatabaseOptions` in `Simulation.Alter.cs`) covering every database-scope toggle SqlPackage emits from a bacpac's `SqlDatabaseOptions` element. Most options parse-and-discard; only the three "load-bearing" toggles (`COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`) drive actual behavior.

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

**`QueryStore`** (dedicated sub-grammar): `SET QUERY_STORE = ON [( … )] | = OFF | CLEAR [ALL]`. The sub-options block is itself a closed accept-list (`RecognizedQueryStoreSubOptions`):
- `OPERATION_MODE` / `CLEANUP_POLICY` / `DATA_FLUSH_INTERVAL_SECONDS` / `MAX_STORAGE_SIZE_MB` / `INTERVAL_LENGTH_MINUTES` / `SIZE_BASED_CLEANUP_MODE` / `QUERY_CAPTURE_MODE` / `MAX_PLANS_PER_QUERY` / `WAIT_STATS_CAPTURE_MODE` / `QUERY_CAPTURE_POLICY`

The two nested-block sub-options (`CLEANUP_POLICY`, `QUERY_CAPTURE_POLICY`) eat balanced parens via `SkipBalancedParens` without enforcing inner-block sub-option names.

## Load-bearing options (behavior wired)

These dispatch to dedicated helpers rather than falling into the parse-and-discard accept-list:

- **`COMPATIBILITY_LEVEL`** — stored on `Database.CompatibilityLevel`.
- **`ALLOW_SNAPSHOT_ISOLATION`** — toggles `Database.AllowSnapshotIsolation`; required for `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`. See [`locking.md`](locking.md).
- **`READ_COMMITTED_SNAPSHOT`** — toggles `Database.ReadCommittedSnapshot`; switches RCSI behavior for the default READ COMMITTED isolation level. See [`locking.md`](locking.md).

## `COLLATE` clause

`ALTER DATABASE name COLLATE <name>` — separate top-level grammar, not under `SET`. Validates against `Collation.Recognized` (`SQL_Latin1_General_CP1_CI_AS` + `Latin1_General_100_CI_AS` + `Latin1_General_CI_AS` + `Latin1_General_CS_AS` + `Latin1_General_BIN` + `Latin1_General_BIN2`). Stores on `Database.CollationName`. Unrecognized names raise `NotSupportedException` rather than silently accepting (silent acceptance would mean the bacpac loader silently mis-loads collation-sensitive data on non-default-collation models).

**Postfix `expr COLLATE name`** parses as a general string-expression operator (`CollateExpression` in `Parser/Expressions/`). Validation against `Collation.ByName` happens at parse time (Msg 448 on unknown). The wrapper passes through `Run` / `GetSqlType` and exposes the resolved collation for callers that consult it. Chained COLLATE (`expr COLLATE A COLLATE B`) is rejected with Msg 156, matching probed real SQL Server. `COLLATE` on a non-string operand surfaces Msg 447 at runtime (real SQL Server raises at bind time — same Msg + same wording, just earlier).

**LIKE consults the override**: `LikeExpression.Run` peels `CollateExpression` (through `Parenthesized`) on either operand and reads `Collation.CaseSensitive` to flip `RegexOptions.IgnoreCase`. `_CS_` and `_BIN` produce case-sensitive matching; `_CI_` stays case-insensitive (matches the default). Both operands carrying *different* explicit COLLATEs raises Msg 468 (`Cannot resolve the collation conflict between "X" and "Y" in the like operation.`). Accent-insensitive variants aren't recognized yet — the regex backend can't fold accents without giving up the bracket-class / wildcard machinery.

**Important caveat**: comparison / sort / `=` semantics outside `LIKE` are *not* extended per declared collation — every other string op still routes through `Collation.Default` (`SQL_Latin1_General_CP1_CI_AS` rules). `LIKE` is the one site that honors an explicit `COLLATE` postfix. Visible divergence is in `ORDER BY` of accented `varchar` strings + the small set of `LIKE` rules around Unicode expansion (e.g. German `ß ↔ ss`).

`sys.databases.collation_name` and `DATABASEPROPERTYEX(db, 'Collation')` surface the declared name.

## `IsFullTextEnabled`

Not handled here — emitted by SqlPackage as `EXEC sp_fulltext_database 'enable|disable'`, a system sproc the simulator doesn't model. See [`full-text.md`](full-text.md) for the broader full-text deferral.

## Error paths

All raise Msg 102 — matching probed real SQL Server wording:
- `SET RECOVERY = FULL` (EnumIdent options reject `=`)
- `SET ACCELERATED_DATABASE_RECOVERY ON` (EqualsOnOff options require `=`)
- `SET TARGET_RECOVERY_TIME = 60` (IntegerWithUnit options require the unit)

## Bacpac loader context

`EmitDatabaseOptions` in `ModelXmlReader.cs` translates each `SqlDatabaseOptions` property to its `ALTER DATABASE … SET …` form. Options that fall outside the accept-list (e.g. unrecognized future toggles) record on `BacpacLoadResult.Warnings` and the load continues — graceful degradation per the load-best-effort contract. See [`bacpac-loader.md`](bacpac-loader.md).
