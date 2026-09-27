# Change tracking

Read this when working on `ALTER DATABASE … SET CHANGE_TRACKING`, `ALTER TABLE … { ENABLE | DISABLE } CHANGE_TRACKING`, `CHANGETABLE(CHANGES …)` / `CHANGETABLE(VERSION …)`, `WITH CHANGE_TRACKING_CONTEXT`, the three `CHANGE_TRACKING_*` scalars, or `sys.change_tracking_databases` / `sys.change_tracking_tables`.
Everything below was probed against SQL Server 2025 on 2026-09-27; version numbers are per database and start at 0 in each fresh probe database, which is what made them comparable.

## Where the state lives

- **Database**: `Database.ChangeTracking` (a `DatabaseChangeTracking`, null while off) holds retention and auto-cleanup; the version counter sits beside it on `Database` itself, because real's counter survives an `OFF` / `ON` cycle.
- **Table**: `HeapTable.ChangeTracking` (a `TableChangeTracking`, null while untracked) holds `TRACK_COLUMNS_UPDATED`, the minimum valid version, and the committed history keyed by primary-key value.
  A write to an untracked table pays one null check per statement or row; nothing else changes on the DML paths.
- **Pending writes** ride the undo log as `PendingRowChange` entries.
  Rollback (whole, to a savepoint, or of a failed statement) drops them with the rest of the log; `UndoLog.Commit` draws **one version per database whose tracked tables the transaction changed** and publishes the entries under it, in log order.
  So an autocommit statement, its triggers and its cascades share one version, a transaction touching only untracked tables draws none, and a statement that matches no row draws none.
- `ENABLE` / `DISABLE` ride the `ALTER TABLE` table snapshot, so both roll back with their transaction; a re-enable starts an empty history at the current version.

## The database and table switches

`SET CHANGE_TRACKING = ON [( CHANGE_RETENTION = n { DAYS | HOURS | MINUTES } [, AUTO_CLEANUP = { ON | OFF }] )]`, `= OFF`, and `SET CHANGE_TRACKING ( … )` to change the options of a tracking database.
A fresh `ON` keeps two days with cleanup on; the options block changes only what it names.
The clause must stand alone in its `SET` list (Msg 22114).
The grammar's refusals raise while parsing: an unknown unit (singular spellings included) is Msg 155 state 2, a repeated option Msg 5091, a retention of zero or past `int` minutes Msg 5092, and the malformed shapes Msg 102 / 156 at the token real names — inside a `(` after `OFF` that is the first token past the parenthesis, and a missing unit or an out-of-`int` literal is reported at the option's name.
The state's refusals raise when it runs, each followed by Msg 5069: a system database, `msdb` included (5090), `ON` twice (5088), `OFF` or an options change while off (5089 at states 1 and 2), and `OFF` while a table still tracks (22115).

`ENABLE CHANGE_TRACKING [WITH (TRACK_COLUMNS_UPDATED = { ON | OFF })]` needs the database tracking (Msg 1718, which names `tempdb` for a `#temp` table) and then a primary key (4997); a second `ENABLE` is 4996, a `DISABLE` of an untracked table 4998.
While tracked, the primary key can't be dropped (Msg 3735 then 3727); other `ALTER TABLE` actions, `sp_rename` and `DROP TABLE` proceed, and a dropped column's id never reappears in a mask.

## Net changes

A row's history is one entry per transaction: the first and last operation it performed, whether it inserted the row, the columns its updates set, and the context of its last statement to touch the row.
`CHANGETABLE(CHANGES t, v)` folds the entries with a version above `v` (all of them for `NULL`; a negative or future `v` needs no special case):

- `SYS_CHANGE_VERSION` is the last entry's version.
- `SYS_CHANGE_OPERATION` is `D` when the last entry ends in a delete, else `I` when the first in-range entry starts with an insert, else `U` — so a delete then a re-insert nets to `U`, and an insert then a delete still reports `D`.
- `SYS_CHANGE_CREATION_VERSION` is the latest in-range insert's version, NULL when none is in range; a `D` row carries it too.
- `SYS_CHANGE_CONTEXT` is the last entry's context.
- Rows come back in primary-key order.

An `UPDATE` that moves a row's key is a delete of the old key and an insert of the new one; a statement records all its deletes before its inserts, so `SET id = id + 1` nets the middle rows to `U`.
`last_sync_version` takes `NULL`, an integer literal with an optional minus, or a variable (a string one converts to `bigint`, Msg 8114 when it can't; a date one is Msg 257); anything else is a syntax error.
A version below the table's minimum valid version is not refused — the client is expected to compare against `CHANGE_TRACKING_MIN_VALID_VERSION` itself.
The source must be aliased (Msg 22104), may carry a column-alias list, and names a user table: a view, catalog view, function or `#temp` table is Msg 22107 as written, an untracked table 22105.
Msg 22105 and 22104 bind while the batch compiles, so an untracked table refuses the whole batch, from a branch never taken too, and refuses a procedure, function or view at `CREATE`.
`CHANGETABLE` reads committed history only, so a transaction doesn't see its own uncommitted changes there.

## The columns mask

With `TRACK_COLUMNS_UPDATED = ON`, an update records the column ids its `SET` list names, in that order, then any `rowversion` column the write stamped; later updates in range append ids not yet present.
The mask is NULL — "every column" — for inserts and deletes, for any range containing one, for an update that sets a key column (even to itself), for an update whose recorded columns cover every non-key column, and for a cascade's rewrite of the only non-key column.
`SYS_CHANGE_COLUMNS` encodes it as four zero bytes and one little-endian `int` per id; `CHANGE_TRACKING_IS_COLUMN_IN_MASK` reads ids from offset 4 while a whole one fits, answers 1 for a NULL mask, and refuses a mask shorter than eight bytes (Msg 22101).

## `CHANGETABLE(VERSION …)`

`VERSION t, (key columns), (values)` is a lookup in the table itself: each listed column is compared with its value as `=` would (so `('2', 5)` against an `int` / `varchar` key reaches Msg 245 on the second column), and a matching row reports its latest change's version and context, or NULLs when it hasn't changed since tracking began.
A deleted or missing row yields nothing.
Inside the writing transaction a row with an uncommitted change reports NULLs.
Each column must be a key column (22111), then the list must match the key's length (22110), then the values the columns' (22103); the values may correlate to an `APPLY`'s left side.

## `WITH CHANGE_TRACKING_CONTEXT`

Leads an `INSERT` / `UPDATE` / `DELETE` / `MERGE` (a `SELECT` accepts it and ignores it), and may be followed by a comma and a CTE list; past first position the word is Msg 102 at itself.
The value is a variable or a binary literal — `NULL`, a string or any expression is a syntax error — and a variable must be `varbinary` or `binary` of at most 128 bytes (Msg 22109).
It is recorded on `StatementContext`, so a trigger's own statements don't inherit it.

## Scalars and catalog

- `CHANGE_TRACKING_CURRENT_VERSION()` — the current database's last committed version, NULL while it doesn't track.
- `CHANGE_TRACKING_MIN_VALID_VERSION(object_id)` — NULL for anything but a tracked table of the current database; the argument must be an `int` (Msg 8116, a bare `NULL` included, which is refused while the batch compiles).
- `sys.change_tracking_databases` is server-wide, like `sys.databases`; `max_cleanup_version` is NULL.
- `sys.change_tracking_tables` reports `begin_version` and `min_valid_version` alike; `cleanup_version` is NULL.
- `DATABASEPROPERTYEX(db, 'IsChangeTrackingEnabled')` is a `tinyint` 1 / 0; `OBJECTPROPERTY(…, 'TableHasChangeTracking')` is NULL on real too.
- `TRUNCATE TABLE` empties the history and moves the minimum valid version to the current version without drawing one.

## Divergences

- **Nothing is ever cleaned up.**
  Retention and `AUTO_CLEANUP` are recorded and reported but never run, so the minimum valid version moves only on enable and truncation, and the cleanup columns stay NULL.
- **A `MERGE` records the union of its `WHEN MATCHED THEN UPDATE` clauses' columns** for every row it updates, rather than the columns of the clause that fired.
- **`WITH CHANGE_TRACKING_CONTEXT (…) WITH cte …`** reports real's Msg 156 without the Msg 319 real sends after it.

## Not modeled yet

- How `SNAPSHOT` isolation shapes `CHANGETABLE` hasn't been probed; the simulator reads the latest committed history under every isolation level.
- `sys.internal_tables`' `change_tracking_<object_id>` row per tracked table.
- The `VIEW CHANGE TRACKING` permission `CHANGETABLE` requires beside `SELECT`.
- BACPAC import of the database's and tables' change tracking settings.
- `TRUNCATE TABLE … WITH (PARTITIONS …)` and `ALTER TABLE … SWITCH` on a tracked table: neither records changes nor moves the minimum valid version.
