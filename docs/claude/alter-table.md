# ALTER TABLE

`ALTER TABLE` ships three modeled shapes: `SET (SYSTEM_VERSIONING = OFF)` (see [`temporal-tables.md`](temporal-tables.md)), `[WITH CHECK | WITH NOCHECK] ADD [CONSTRAINT name] (PRIMARY KEY | UNIQUE | FOREIGN KEY | CHECK | DEFAULT) …`, and `DROP CONSTRAINT [IF EXISTS] name [, …]`. ADD / DROP COLUMN, ALTER COLUMN, REBUILD, SWITCH PARTITION, and the SET-versioning-on direction raise `NotSupportedException`. Probe-confirmed against SQL Server 2025 on 2026-05-13.

## Grammar

```sql
ALTER TABLE [schema.]table
    [WITH CHECK | WITH NOCHECK]
    ADD [CONSTRAINT name] <body>

ALTER TABLE [schema.]table
    DROP CONSTRAINT [IF EXISTS] name [, name ...]
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
- `ForeignKey.IsNotTrusted` / `CheckConstraint.IsNotTrusted` are mutable bool fields, false on CREATE-time inline / true on WITH-NOCHECK ALTER ADD.
- `CheckConstraint.IsSystemNamed` flags auto-named CHECKs; `KeyConstraint` infers the same from its name prefix (no explicit flag).
- `HeapColumn.Default` is now mutable (ALTER ADD DEFAULT sets, ALTER DROP CONSTRAINT clears).
- `HeapColumn.DefaultConstraint` is the named metadata wrapper alongside `Default` — populated at inline DEFAULT (auto-named, `IsSystemNamed = true`) and named ALTER ADD DEFAULT (explicit name, `IsSystemNamed = false`).

## Catalog views

Three new views ship with this bundle:

- **`sys.check_constraints`** — one row per CHECK constraint, with `is_not_trusted`, `is_system_named`, `parent_column_id` (1-based ordinal for inline column-level; `0` for table-level), and `definition` (currently NULL — see fidelity gaps).
- **`sys.key_constraints`** — one row per PRIMARY KEY / UNIQUE constraint, with `type` = `PK` / `UQ`, `type_desc` = `PRIMARY_KEY_CONSTRAINT` / `UNIQUE_CONSTRAINT`, and `is_system_named` inferred from the auto-name prefix.
- **`sys.default_constraints`** — one row per named DEFAULT (inline + ALTER ADD), with `parent_column_id` and `is_system_named`.

`sys.foreign_keys.is_not_trusted` now reads from `ForeignKey.IsNotTrusted` (previously hardcoded `0`).

## Fidelity gaps

- **Single primary error instead of error pair** — real SQL Server emits Msg X + trailing Msg 1750 / 3727 (`"Could not create constraint or index"` / `"Could not drop constraint"`); the simulator emits only Msg X. Test code asserting on the primary error number works unchanged.
- **`sys.check_constraints.definition` / `sys.default_constraints.definition` return NULL** — the simulator stores parsed Expression trees, not source text. Real SQL Server reformats predicates as e.g. `([qty]>(0))`. Adding source-text capture is straightforward (slice the parser's source between balanced parens) but no probed application reads these columns.
- **`KeyConstraint.IsSystemNamed` is inferred from the name prefix** — `PK__` / `UQ__` → system-named. Custom names matching the prefix would report `is_system_named = true` incorrectly. Real SQL Server tracks the flag explicitly; the simulator inherits a no-flag pre-bundle storage layout and infers rather than adding a column-mutating change.
- **`WITH NOCHECK` is one-way** — once `IsNotTrusted = true`, there's no `WITH CHECK CHECK CONSTRAINT name` to flip it back. Probe-confirmed wording (`ALTER TABLE t WITH CHECK CHECK CONSTRAINT fk_x`) raises `NotSupportedException` at parse.
- **Multi-constraint ADD in one statement** — `ALTER TABLE t ADD CONSTRAINT pk1 PRIMARY KEY (id), CONSTRAINT fk1 FOREIGN KEY (p_id) REFERENCES p(id)` raises `NotSupportedException`. Real SQL Server supports it; EF Migrations doesn't emit it.
- **`ALTER TABLE … DROP CONSTRAINT name1, , name2`** (empty middle element) — accepted by real SQL Server; the simulator's grammar rejects with Msg 102.
- **Defaults' parent_column_id for inline-DEFAULT-on-computed-column** — the simulator allows inline DEFAULT on computed columns (which real SQL Server rejects with Msg 8183). Edge case unlikely in practice.

## EF Core integration

EF Migrations emit FK adds via `ALTER TABLE` heavily (separate from `CREATE TABLE`). The simulator accepts that emit shape, but no EFCore-specific test ships in this bundle — once the FK is in place (whether declared inline at CREATE TABLE or added via ALTER), EF Core sees the same database state either way, so the `EFCoreForeignKey` test already covers the LINQ surface. The simulator-side `AlterTableConstraintTests` covers the parser / validation / catalog surface for ALTER directly.
