# ALTER TABLE

`ALTER TABLE` ships seven modeled shapes: `SET (SYSTEM_VERSIONING = OFF | ON (HISTORY_TABLE = name [, DATA_CONSISTENCY_CHECK = ON|OFF]))` (see [`temporal-tables.md`](temporal-tables.md)), `[WITH CHECK | WITH NOCHECK] ADD [CONSTRAINT name] (PRIMARY KEY | UNIQUE | FOREIGN KEY | CHECK | DEFAULT) …`, `DROP CONSTRAINT [IF EXISTS] name [, …]`, `[WITH CHECK | WITH NOCHECK] (CHECK | NOCHECK) CONSTRAINT (ALL | name [, …])` (trust toggling), `ADD [COLUMN] col TYPE [, …]` (multi-column add — see [Column ops](#column-ops)), `DROP COLUMN [IF EXISTS] col [, …]` (multi-column drop with dependency rejection), and `ALTER COLUMN col TYPE[(prec[,scale])] [COLLATE coll] [NULL|NOT NULL]` (single-column type / nullability change — see [ALTER COLUMN](#alter-column)). REBUILD, SWITCH PARTITION, and the `ALTER COLUMN col ADD/DROP {PERSISTED|MASKED|ROWGUIDCOL|SPARSE}` sub-clause forms raise `NotSupportedException`. Probe-confirmed against SQL Server 2025 on 2026-05-14.

## Grammar

```sql
ALTER TABLE [schema.]table
    [WITH CHECK | WITH NOCHECK]
    ADD [CONSTRAINT name] <body>

ALTER TABLE [schema.]table
    DROP CONSTRAINT [IF EXISTS] name [, name ...]

ALTER TABLE [schema.]table
    [WITH CHECK | WITH NOCHECK]
    (CHECK | NOCHECK) CONSTRAINT (ALL | name [, name ...])
```

`<body>` is one of:

```sql
PRIMARY KEY (col [ASC|DESC] [, ...])
UNIQUE      (col [ASC|DESC] [, ...])
FOREIGN KEY (col [, ...]) REFERENCES parent [(col [, ...])]
            [ON DELETE NO ACTION | CASCADE | SET NULL | SET DEFAULT]
            [ON UPDATE NO ACTION | CASCADE | SET NULL | SET DEFAULT]
CHECK (predicate)
DEFAULT (expression) FOR column
```

Single constraint per `ADD` — comma-separated multi-constraint ADD raises `NotSupportedException`. Anonymous ADD (no `CONSTRAINT name`) auto-generates a name with the same FNV-1a-based scheme as CREATE TABLE inline: `PK__<t8>__<hex>` / `UQ__<t8>__<hex>` / `FK__<t8>__<col8>__<hex>` / `CK__<t8>__<hex>` / `DF__<t8>__<col8>__<hex>`. `is_system_named` reflects the auto-name path on FK / CHECK / DEFAULT (KeyConstraint infers from the prefix — `PK__` / `UQ__` — since the existing storage doesn't carry an explicit flag).

## WITH CHECK / WITH NOCHECK

`WITH NOCHECK` applies only to FK and CHECK adds. It bypasses the existing-row validation pass and sets `IsNotTrusted = true` on the new constraint. `WITH CHECK` (the default) runs the validation pass. PK / UQ / DEFAULT ignore the modifier — the grammar accepts it but validation is unconditional (PK / UQ always scan for duplicates; DEFAULT has no data to validate against).

`sys.foreign_keys.is_not_trusted` and `sys.check_constraints.is_not_trusted` reflect the flag. Re-trusting via `WITH CHECK CHECK CONSTRAINT name` isn't modeled.

## Existing-data validation

Default (`WITH CHECK`) scans the live heap before mutating:

| Family | Check | Error |
|--------|-------|-------|
| `PRIMARY KEY` | column declared NOT NULL | Msg 8111 |
| `PRIMARY KEY` | table doesn't already have one | Msg 1779 |
| `PRIMARY KEY` / `UNIQUE` | column exists | Msg 1911 |
| `PRIMARY KEY` / `UNIQUE` | no duplicate key tuples in existing rows | Msg 1505 (`CREATE UNIQUE INDEX statement terminated …`) |
| `FOREIGN KEY` | child column exists | Msg 1769 |
| `FOREIGN KEY` | referenced columns form PK / UQ on parent | Msg 1776 |
| `FOREIGN KEY` | cascade graph doesn't form a cycle / multiple paths | Msg 1785 |
| `FOREIGN KEY` | every non-NULL existing FK tuple matches a parent row | Msg 547 with `"ALTER TABLE statement"` prefix |
| `CHECK` | every existing row passes (UNKNOWN passes) | Msg 547 with `"ALTER TABLE statement"` prefix |
| `DEFAULT` | column exists | Msg 1752 |
| `DEFAULT` | column doesn't already have a DEFAULT | Msg 1781 |
| any | constraint name unique across all schemas' tables | Msg 2714 |

Real SQL Server emits a trailing Msg 1750 / 1753 after the primary failure (`"Could not create constraint or index. See previous errors."`); the simulator emits only the primary error — same end state, single-error stream. Documented quirk.

The Msg 547 verb difference is the only wording variance between INSERT-time CHECK / FK violations and ALTER-time existing-data violations. The constraint name, table reference, and column suffix follow the same format.

## Trust toggling — bulk-import recipe

`NOCHECK CONSTRAINT name` disables enforcement on a specific FK / CHECK and sets both `IsDisabled = true` and `IsNotTrusted = true`. While disabled:

- INSERT / UPDATE / MERGE skip the FK / CHECK validation.
- DELETE / UPDATE on the parent skips both the NO-ACTION reject **and** any CASCADE / SET NULL / SET DEFAULT action (probe-confirmed: disabled CASCADE FK leaves children orphaned when the parent is deleted).

`CHECK CONSTRAINT name` (bare, no `WITH CHECK` prefix) re-enables enforcement on subsequent rows but does **not** re-validate existing data — `IsDisabled = false` and `IsNotTrusted` stays `true`. Common gotcha.

`WITH CHECK CHECK CONSTRAINT name` re-enables enforcement **and** re-validates existing data — raises Msg 547 with the `"ALTER TABLE statement"` prefix on the first conflicting row; on success, `IsDisabled = false` and `IsNotTrusted = false`.

`ALL` targets every FK + CHECK on the table at once. The same toggle action applies uniformly to every constraint on the target.

| Shape | IsDisabled | IsNotTrusted | Revalidate existing? |
|-------|------------|--------------|----------------------|
| `NOCHECK CONSTRAINT name` | → true | → true | No |
| `CHECK CONSTRAINT name` | → false | unchanged | No |
| `WITH CHECK CHECK CONSTRAINT name` | → false | → false (on success) | Yes |
| `WITH NOCHECK CHECK CONSTRAINT name` | → false | unchanged | No |

Probe-confirmed error paths:

| Condition | Behavior |
|-----------|----------|
| Constraint name not found | Msg 4917 (`Constraint 'name' does not exist.`) |
| Multi-name with one missing | Atomic — all names resolved first; Msg 4917 prevents all mutations |
| Trailing comma | Msg 102 |
| Revalidation failure (WITH CHECK CHECK) | Msg 547 with `"ALTER TABLE statement"` prefix |

The bulk-import recipe:

```sql
ALTER TABLE Orders NOCHECK CONSTRAINT ALL;
-- Push large batch; FK + CHECK ignored, even if rows would have violated.
ALTER TABLE Orders CHECK CONSTRAINT ALL;
-- Enforcement back on for subsequent DML; existing data marked is_not_trusted.
-- Optional: ALTER TABLE Orders WITH CHECK CHECK CONSTRAINT ALL;
-- if you want the optimizer to trust the data (and accept Msg 547 if any row
-- violates).
```

## DROP CONSTRAINT

```sql
ALTER TABLE t DROP CONSTRAINT name [, name ...]
ALTER TABLE t DROP CONSTRAINT IF EXISTS name
```

Name lookup walks all four families on the target table in order:

1. `KeyConstraints` (PK / UQ)
2. `CheckConstraints`
3. `OutgoingForeignKeys`
4. Each column's `DefaultConstraint`

First hit wins (collation-insensitive). Probe-confirmed shapes:

| Condition | Behavior |
|-----------|----------|
| Name resolves | Remove from the matching container; FK additionally detaches from `parent.IncomingForeignKeys` |
| Name not found, no `IF EXISTS` | Msg 3728 (`'name' is not a constraint.`) |
| Name not found, `IF EXISTS` | Silent no-op |
| PK / UQ referenced by an incoming FK | Msg 3725 (`The constraint 'X' is being referenced by table 'Y', foreign key constraint 'Z'.`) |
| Trailing comma | Msg 102 (probe-confirmed) |

**Multi-drop is atomic** — all names resolve and validate first; any failure (Msg 3728 / 3725) leaves the table's constraint state unchanged. Probe-confirmed.

## Storage

- `HeapTable.KeyConstraints` / `CheckConstraints` are `List<>` (the reference is `readonly`, contents mutable) so ADD / DROP can append / remove in place. Inline at CREATE TABLE still goes through the same lists.
- `HeapTable.OutgoingForeignKeys` / `IncomingForeignKeys` already lists pre-existing (introduced with the FK bundle).
- `ForeignKey.IsNotTrusted` / `CheckConstraint.IsNotTrusted` are mutable bool fields, false on CREATE-time inline / true on WITH-NOCHECK ALTER ADD / true after `NOCHECK CONSTRAINT`. Cleared by `WITH CHECK CHECK CONSTRAINT` on successful revalidation.
- `ForeignKey.IsDisabled` / `CheckConstraint.IsDisabled` are independent mutable bools — true after `NOCHECK CONSTRAINT`, false after either `CHECK CONSTRAINT` form. The enforcement loops (`EnforceCheckConstraints`, `EnforceOutgoingForeignKeys`, `EnforceIncomingForeignKeys`, `EnforceIncomingFkOnUpdate`) skip when `IsDisabled` — including suppressing cascade actions.
- `CheckConstraint.IsSystemNamed` flags auto-named CHECKs; `KeyConstraint` infers the same from its name prefix (no explicit flag).
- `HeapColumn.Default` is now mutable (ALTER ADD DEFAULT sets, ALTER DROP CONSTRAINT clears).
- `HeapColumn.DefaultConstraint` is the named metadata wrapper alongside `Default` — populated at inline DEFAULT (auto-named, `IsSystemNamed = true`) and named ALTER ADD DEFAULT (explicit name, `IsSystemNamed = false`).

## Catalog views

Three new views ship with this bundle:

- **`sys.check_constraints`** — one row per CHECK constraint, with `is_not_trusted`, `is_system_named`, `parent_column_id` (1-based ordinal for inline column-level; `0` for table-level), and `definition` (currently NULL — see fidelity gaps).
- **`sys.key_constraints`** — one row per PRIMARY KEY / UNIQUE constraint, with `type` = `PK` / `UQ`, `type_desc` = `PRIMARY_KEY_CONSTRAINT` / `UNIQUE_CONSTRAINT`, and `is_system_named` inferred from the auto-name prefix.
- **`sys.default_constraints`** — one row per named DEFAULT (inline + ALTER ADD), with `parent_column_id` and `is_system_named`.

`sys.foreign_keys.is_not_trusted` / `is_disabled` now read from `ForeignKey.IsNotTrusted` / `IsDisabled`; `sys.check_constraints.is_not_trusted` / `is_disabled` read from the corresponding `CheckConstraint` flags.

## Fidelity gaps

- **Single primary error instead of error pair** — real SQL Server emits Msg X + trailing Msg 1750 / 3727 (`"Could not create constraint or index"` / `"Could not drop constraint"`); the simulator emits only Msg X. Test code asserting on the primary error number works unchanged.
- **`sys.check_constraints.definition` / `sys.default_constraints.definition` return NULL** — the simulator stores parsed Expression trees, not source text. Real SQL Server reformats predicates as e.g. `([qty]>(0))`. Adding source-text capture is straightforward (slice the parser's source between balanced parens) but no probed application reads these columns.
- **`KeyConstraint.IsSystemNamed` is inferred from the name prefix** — `PK__` / `UQ__` → system-named. Custom names matching the prefix would report `is_system_named = true` incorrectly. Real SQL Server tracks the flag explicitly; the simulator inherits a no-flag pre-bundle storage layout and infers rather than adding a column-mutating change.
- **Multi-constraint ADD in one statement** — `ALTER TABLE t ADD CONSTRAINT pk1 PRIMARY KEY (id), CONSTRAINT fk1 FOREIGN KEY (p_id) REFERENCES p(id)` raises `NotSupportedException`. Real SQL Server supports it; EF Migrations doesn't emit it.
- **`ALTER TABLE … DROP CONSTRAINT name1, , name2`** (empty middle element) — accepted by real SQL Server; the simulator's grammar rejects with Msg 102.
- **Defaults' parent_column_id for inline-DEFAULT-on-computed-column** — the simulator allows inline DEFAULT on computed columns (which real SQL Server rejects with Msg 8183). Edge case unlikely in practice.

## EF Core integration

EF Migrations emit FK adds via `ALTER TABLE` heavily (separate from `CREATE TABLE`). The simulator accepts that emit shape, but no EFCore-specific test ships in this bundle — once the FK is in place (whether declared inline at CREATE TABLE or added via ALTER), EF Core sees the same database state either way, so the `EFCoreForeignKey` test already covers the LINQ surface. The simulator-side `AlterTableConstraintTests` covers the parser / validation / catalog surface for ALTER directly.

## Column ops

`ALTER TABLE … ADD [COLUMN] col TYPE [, …]` and `ALTER TABLE … DROP COLUMN [IF EXISTS] col [, …]` ship as part of the EF Migrations parity workstream. Probe-confirmed against SQL Server 2025 on 2026-05-14.

### Grammar — ADD COLUMN

```sql
ALTER TABLE [schema.]table ADD [COLUMN] col TYPE [(N | MAX [, scale])]
    [NULL | NOT NULL]
    [DEFAULT expr]
    [IDENTITY [(seed, increment)]]
    [CONSTRAINT name (CHECK (predicate) | UNIQUE | PRIMARY KEY | REFERENCES parent(cols))]
    [, col2 TYPE …]
```

Inline column-level constraints (CHECK / UNIQUE / PRIMARY KEY / REFERENCES, with or without `CONSTRAINT name`) all parse through the shared `ParseOneColumnIntoLists` helper that backs CREATE TABLE. Computed columns via `col AS expr [PERSISTED [NOT NULL]]` are supported and resolve against the combined (existing + new) column view.

The optional `COLUMN` keyword between `ADD` and the column name is accepted (probe-confirmed real SQL Server accepts both shapes); the simulator's grammar recognizes `COLUMN` as a reserved keyword here.

### Backfill semantic

Existing rows are re-encoded against the new schema. Per-column backfill values:

| Column kind | Backfill for existing rows |
|-------------|----------------------------|
| Nullable (regardless of DEFAULT) | NULL — DEFAULT only applies to future INSERTs |
| NOT NULL with DEFAULT | DEFAULT expression evaluated once at ALTER time, snapshotted to every row |
| NOT NULL IDENTITY | Sequential allocation: seed, seed+increment, seed+2·increment, … in heap-scan order |
| NOT NULL ROWVERSION / TIMESTAMP | Per-row from the database-scoped rowversion counter |
| NOT NULL without DEFAULT/IDENTITY/ROWVERSION on non-empty table | Msg 4901 (probe-confirmed) |
| Computed (non-persisted) | No backfill — evaluated on read |

The DEFAULT-evaluated-once rule is a probe-confirmed SQL Server quirk: `ALTER TABLE t ADD created datetime NOT NULL DEFAULT GETUTCDATE()` produces a single timestamp for every existing row, not a per-row evaluation. The simulator matches.

### Error paths — ADD COLUMN

| Msg | Trigger |
|-----|---------|
| **2744** | Adding a second IDENTITY column to a table that already has one (existing-identity count tracked from `HeapTable.IdentityOrdinal`). |
| **2705** | Duplicate column name — against any existing column or another column in the same multi-column ADD. |
| **4901** | NOT NULL without DEFAULT / IDENTITY / ROWVERSION on a non-empty table. |
| **8111** | Existing PrimaryKeyOnNullableColumn for inline `PRIMARY KEY` on an explicit-`NULL` column (inherited from CREATE TABLE shared parser). |
| **1505** | Inline `UNIQUE` constraint when existing rows have duplicate values — the resolver runs the existing-data validation on the combined column set. |
| **547** | Inline FK / CHECK rejecting existing rows during the post-mutation enforcement scan. |

### Grammar — DROP COLUMN

```sql
ALTER TABLE [schema.]table DROP COLUMN [IF EXISTS] col [, col2, …]
```

Two-pass apply: every name is resolved + dependency-checked before any mutation, so a single Msg 5074 or Msg 4924 leaves the table unchanged.

### Dependency rejection — DROP COLUMN

Probe-confirmed: dropping a column referenced by ANY of the following raises **Msg 5074** with one line per blocker:

- `PRIMARY KEY` / `UNIQUE` constraint (`KeyConstraint` storage ordinals)
- Outgoing `FOREIGN KEY` (child side — the FK's child column references the to-be-dropped column)
- Incoming `FOREIGN KEY` (parent side — another table's FK references this column)
- `CHECK` constraint (inline `InlineColumn` match OR table-level predicate walked structurally for column refs by name)
- `DEFAULT` constraint attached to the column
- `INDEX` (`CREATE INDEX`-declared — either KEY column or INCLUDE column)

Each blocker emits its line with the appropriate prefix: `The object 'X' is dependent on column 'col'.` for constraints, `The index 'X' is dependent on column 'col'.` for indexes. Multiple blockers on one column emit one line each.

`IF EXISTS` suppresses Msg 4924 (column doesn't exist) but does NOT suppress Msg 5074 (dependencies block) — matches real SQL Server.

### Storage rewrite

DROP COLUMN walks every surviving `KeyConstraint` / `Index` / `ForeignKey` (outgoing + incoming) and in-place remaps their storage / full ordinals through an `oldStorageToNew[]` / `oldFullToNew[]` map. The mutation patterns:

- `KeyConstraint.StorageOrdinals[i]` — array element reassignment
- `Index.KeyColumns[i]` — slot replacement with new `IndexKeyColumn(newOrdinal, oldDescending)`
- `Index.IncludedColumns[i]` — array element reassignment
- `ForeignKey.ChildColumnOrdinals[i]` — array element reassignment (outgoing)
- `ForeignKey.ReferencedColumnOrdinals[i]` — array element reassignment (incoming, since this table is the referenced side)

The heap is re-encoded: each row is decoded under the old `StoredColumns` layout, projected through the surviving ordinals, and re-encoded against the new `StoredColumns`. The old `Heap` is replaced wholesale (via the now-mutable `HeapTable.Heap` field).

### Fidelity gaps — Column ops

- **Eager row rewrite vs metadata-only**: Real SQL Server 2012+ optimizes many ADD COLUMN cases (nullable adds, NOT NULL constant-default adds) to metadata-only — no physical row updates. The simulator always rewrites every row. Behavior is identical; performance differs (acceptable for simulator workload sizes).
- **DROP COLUMN inside transaction**: Real SQL Server makes column-level DDL transactional. The simulator's regular-DDL non-logging pattern (see existing CREATE/DROP TABLE quirk) extends here: ALTER TABLE ADD / DROP COLUMN doesn't participate in the undo log, so a `BEGIN TRAN` / `ROLLBACK` won't undo a column mutation. Matches the existing CREATE/DROP TABLE asymmetry.
- **Table variable column ops**: `DECLARE @t TABLE` then `ALTER TABLE @t ADD …` raises Msg 102 at parse — real SQL Server's grammar also doesn't allow ALTER on table variables.
- **Single primary error**: Real SQL Server's Msg 5074 path may pair with a trailing Msg 4922 informational; the simulator emits only the primary Msg 5074.

## ALTER COLUMN

### Grammar — ALTER COLUMN

```sql
ALTER TABLE [schema.]table
    ALTER COLUMN col TYPE[(precision[, scale])] [COLLATE collation] [NULL | NOT NULL]
```

Single-column shape only (real SQL Server's grammar doesn't accept comma-separated multi-column ALTER COLUMN). Routed from `TryParseAlterTable` via `Keyword.Alter` into `TryParseAlterTableAlterColumn`. The trailing `NULL`/`NOT NULL` keyword is optional — omitting it preserves the column's existing nullability (probe-confirmed). `COLLATE` is parse-accepted and ignored (the simulator has a single default collation).

The `ALTER COLUMN col ADD/DROP {PERSISTED|MASKED|ROWGUIDCOL|SPARSE}` sub-clause forms aren't modeled — `Keyword.Add` / `Keyword.Drop` after the column name raises `NotSupportedException`.

### Conversion fidelity

Type and length changes flow per-row through `SqlValue.CoerceTo`, which is the same conversion path CAST / CONVERT use. Real SQL Server's error codes surface verbatim:

| Path | Trigger | Code |
|------|---------|------|
| Integer narrowing overflow | `int → tinyint` with value 500 | Msg 220 (`Arithmetic overflow error for data type tinyint, value = 500.`) |
| String → integer with non-numeric data | `varchar → int` with `'hello'` | Msg 245 (`Conversion failed when converting the varchar value 'hello' to data type int.`) |
| String → date/time with bad format | `varchar → date` with `'not-a-date'` | Msg 241 (`Conversion failed when converting date and/or time from character string.`) |
| Decimal precision narrow | `decimal(10,2) → decimal(4,2)` with 999.99 | Msg 8115 (`Arithmetic overflow error converting expression to data type numeric.`) |
| Bounded-string narrow | `varchar(50) → varchar(10)` with 30-char value | Msg 2628 (`String or binary data would be truncated…`) |
| `NULL → NOT NULL` with existing NULL | `varchar(10) null → varchar(10) not null` on a row with NULL | Msg 515 (`Cannot insert the value NULL into column 'X', table 'Y'; column does not allow nulls.`) |

Widening within the same family (`varchar(50) → varchar(100)`, `int → bigint`, `tinyint → smallint`) always succeeds; bounded-string narrowings succeed when every existing value fits the new length.

### Blockers (Msg 5074)

`CollectAlterColumnBlockers` walks the same constraint surface as DROP COLUMN, except CHECK and DEFAULT don't block (probe-confirmed: ALTER COLUMN under a CHECK constraint succeeds and the constraint stays in force against future inserts):

| Source | Blocks ALTER COLUMN? | Prefix in Msg 5074 |
|--------|----------------------|--------------------|
| PRIMARY KEY on this column | Always | `The object 'X' is dependent…` |
| UNIQUE constraint on this column | Always | `The object 'X' is dependent…` |
| Outgoing FOREIGN KEY using this column as a child column | Always | `The object 'X' is dependent…` |
| Incoming FOREIGN KEY referencing this column as a parent column | Always | `The object 'X' is dependent…` |
| Computed column that references this column in its expression | Always | `The column 'X' is dependent…` |
| Index whose key or include columns reference this column | Only when the `SqlType` subclass changes — length widening within the same family (`varchar(50) → varchar(100)`) passes | `The index 'X' is dependent…` |
| CHECK constraint that references this column | Never (constraint survives the type change and continues to enforce against future inserts) | — |
| DEFAULT constraint on this column | Never (default expression + constraint name survive the type change) | — |

Multi-blocker enumeration follows the existing `DropColumnHasDependenciesMixed` pattern: one line per blocker, all surfaced in one Msg 5074 raise. Blocker order: PK / UQ → outgoing FK → incoming FK → computed-column refs → indexes (when applicable).

### Rejection paths (other than Msg 5074)

| Condition | Code | Notes |
|-----------|------|-------|
| Column doesn't exist on the table | Msg 4924 | Shares the code with DROP COLUMN's missing-column path, distinct wording (`ALTER TABLE ALTER COLUMN failed because column 'X' does not exist…`). |
| Column is a computed column | Msg 4928 | Phrasing: `Cannot alter column 'X' because it is 'COMPUTED'.` |
| Column is rowversion / timestamp | Msg 4928 | Phrasing: `Cannot alter column 'X' because it is 'timestamp'.` |
| Column is `GENERATED ALWAYS AS ROW START/END` | `NotSupportedException` | Real SQL Server has its own grammar rule here; not exercised by current EF Migrations emissions. |
| ALTER COLUMN of an IDENTITY column to a non-integer type | `NotSupportedException` | Adding / removing identity itself is a parser-level Msg 156 in real SQL Server (the grammar excludes IDENTITY from the ALTER COLUMN clause). |

### Preservation through the column instance swap

`HeapColumn` instances are immutable for most fields; ALTER COLUMN constructs a fresh `HeapColumn` for the target ordinal and inherits identity / default / generated-as / hidden state from the prior instance. Specifically:

- **Identity counter**: `existingCol.Identity` (the `IdentityState` reference) carries over verbatim. The high-water mark survives, so the next INSERT after `int identity` → `bigint not null` keeps incrementing from where it left off.
- **DEFAULT expression + constraint name**: `existingCol.Default` and `existingCol.DefaultConstraint` both carry over. Probe-confirmed: a named DEFAULT keeps its name through the alter; sys.default_constraints shows the same entry post-ALTER.
- **`is_hidden` / `GeneratedAs`**: Inherited but currently rejected up front (see table above).
- **Inline CHECK constraints**: live on `HeapTable.CheckConstraints` keyed by `InlineColumn` name, not on the HeapColumn — so they remain wired to the column by name and continue to enforce after the rebuild.

### Storage rewrite

`RewriteHeapForAlterColumn` walks every row, decoding the target column under the pre-alter `HeapColumn` and re-encoding the whole row against the post-alter `StoredColumns`. The non-altered columns are decoded then re-encoded as-is (no coercion). Strategy: build the candidate post-alter Columns array, swap it onto the table (so `StoredColumns` / `Schema` reflect the new shape before re-encoding writes), walk rows; on any failure restore the original Columns + recompute. The Heap field replaces wholesale at the end.

Like ADD / DROP COLUMN, the rewrite is unconditional — even pure length widening (`varchar(50) → varchar(100)`) walks every row through decode + re-encode, because the singleton `SqlType` reference differs between lengths and the StoredColumns / Schema arrays must mirror that. Storage cost is negligible at simulator workload sizes.

### Fidelity gaps — ALTER COLUMN

- **Eager rewrite even when bytes are identical**: As above — pure length widening within the same family rewrites every row, even though the encoded bytes are byte-for-byte identical between varchar(50) and varchar(100). Performance only; behavior matches.
- **Index protection nuance**: Real SQL Server allows length widening AND length narrowing (when data fits) under an index — both pass with the same SqlType base. The simulator allows both only when the `SqlType` subclass matches; decimal precision narrowing under an index (same `DecimalSqlType` subclass) would pass in the simulator but is probably blocked in real SQL Server (not probed; EF Migrations drops indexes before significant type changes anyway, so the gap is application-unreachable through EF).
- **No `ALTER COLUMN ADD/DROP` sub-clause**: PERSISTED, MASKED, ROWGUIDCOL, SPARSE sub-grammar forms raise `NotSupportedException` — none of these features are modeled at the simulator level.
- **Non-transactional column DDL**: Same as ADD / DROP COLUMN — ALTER COLUMN bypasses the undo log; `BEGIN TRAN` / `ROLLBACK` doesn't undo the type change.
