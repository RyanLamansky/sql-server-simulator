# Foreign Keys

`FOREIGN KEY` constraints ship: inline + table-level grammar with optional `[CONSTRAINT name]`, full `ON DELETE` / `ON UPDATE` action set (`NO ACTION` / `CASCADE` / `SET NULL` / `SET DEFAULT`), runtime enforcement on `INSERT` / `UPDATE` / `DELETE` / `MERGE`, cascade chains, `DROP TABLE` protection, and the `sys.foreign_keys` + `sys.foreign_key_columns` catalog surface. Probe-confirmed against SQL Server 2025 on 2026-05-13.

## Grammar

Two equivalent declaration shapes accepted in `CREATE TABLE`:

```sql
-- Inline (single-column FK on the column being declared):
CREATE TABLE c (
  id      int NOT NULL PRIMARY KEY,
  p_id    int NOT NULL [CONSTRAINT fk_c_to_p] REFERENCES p (id) [ON DELETE action] [ON UPDATE action]
);

-- Table-level (single or multi-column FK):
CREATE TABLE c (
  id      int NOT NULL PRIMARY KEY,
  ra      int NOT NULL,
  rb      int NOT NULL,
  [CONSTRAINT fk_c_to_p] FOREIGN KEY (ra, rb) REFERENCES p (a, b) [ON DELETE action] [ON UPDATE action]
);
```

The referenced column list defaults to the parent table's PRIMARY KEY columns when omitted: `REFERENCES p` ≡ `REFERENCES p(<pk-columns>)`.

Action variants:

| Token form | `ReferentialAction` | `sys.foreign_keys.*_referential_action` |
|------------|---------------------|------------------------------------------|
| `NO ACTION` (default) | `NoAction` | 0 |
| `CASCADE` | `Cascade` | 1 |
| `SET NULL` | `SetNull` | 2 |
| `SET DEFAULT` | `SetDefault` | 3 |

`ALTER TABLE … ADD CONSTRAINT … FOREIGN KEY …` + `ALTER TABLE … DROP CONSTRAINT …` ship through the same FK pipeline; see [`alter-table.md`](alter-table.md) for the ALTER-side validation rules, `WITH NOCHECK` plumbing, and atomic multi-drop semantics.

## Storage

`HeapTable` carries two mutable lists:

- `OutgoingForeignKeys` — populated on the child (referring) side as each `CREATE TABLE` finishes.
- `IncomingForeignKeys` — populated on the parent (referenced) side at the same time.

`ForeignKey` itself is in `Storage/ForeignKey.cs`: name, object id, both tables, child + referenced full-ordinal arrays, the two `ReferentialAction` values, and an `IsSystemNamed` flag. Full ordinals (not storage ordinals) because the enforcement loop materializes whole rows.

The two lists wire up symmetrically during the CREATE pass via `Simulation.ResolveForeignKeys`. Self-referencing FKs are supported (the parent table is already in its schema dict by the time the FK resolves).

## Validation at CREATE

1. **Referenced table must exist** — `MultiPartName` lookup against the live schema dict; missing → Msg 208.
2. **Referenced column set must form a PRIMARY KEY or UNIQUE** — multiset compare against `referencedTable.KeyConstraints`. Mismatch → Msg 1776.
3. **Cascade-cycle / multiple-path check** — when the new FK declares any non-NO_ACTION action, the resolver walks the existing FK graph (plus the FKs already queued in this same `CREATE TABLE` statement) looking for either a self-reference or a path from `newFk.ReferencedTable` back to `newFk.ChildTable`. Either condition → Msg 1785.

The validation runs across the full pending FK list *before* mutating either table's `OutgoingForeignKeys` / `IncomingForeignKeys`. A failure unwinds the partial `CREATE TABLE` by removing the new table from its dict.

## Enforcement

### Child side (INSERT / UPDATE / MERGE-INSERT / MERGE-UPDATE)

After per-row check + key validation, `EnforceOutgoingForeignKeys` walks `table.OutgoingForeignKeys`. For each FK:

- A NULL in any child FK column **skips the check** (probe-confirmed — applies to partial NULL in composite FKs too).
- A non-NULL tuple that doesn't match any row of the parent on the FK's referenced columns → Msg 547 with the FK name and the parent's qualified table reference. Single-column FK appends `, column 'X'`; composite FK omits the column phrase. Self-referencing FK substitutes `FOREIGN KEY SAME TABLE` for `FOREIGN KEY`.

### Parent side (DELETE / UPDATE / MERGE-DELETE / MERGE-UPDATE)

After parent-side mutations, `EnforceIncomingForeignKeys` (for DELETE) and `EnforceIncomingFkOnUpdate` (for UPDATE) walk `table.IncomingForeignKeys`. For each FK:

- **UPDATE-side filter**: rows whose referenced-column tuple didn't change are skipped (a non-PK update on the parent is a no-op for the FK).
- For each parent row whose values were affected, scan the child table's heap for rows whose FK tuple matches the parent's old value.
- If matches exist, dispatch on the FK's `DeleteAction` (for DELETE) or `UpdateAction` (for UPDATE):

| Action | Behavior |
|--------|----------|
| `NO ACTION` | Raise Msg 547 with `REFERENCE constraint` wording, naming the child table + column. |
| `CASCADE` (DELETE) | Recursively delete the matching child rows (themselves potentially parents — recursion guarded by `MaxCascadeDepth = 32`). |
| `CASCADE` (UPDATE) | Rewrite each child row's FK columns to the parent's new value. |
| `SET NULL` | Rewrite each child row's FK columns to NULL. |
| `SET DEFAULT` | Rewrite each child row's FK columns to each column's `DEFAULT` expression (NULL if no default). |

Statement-level atomicity continues to apply: a Msg 547 raised mid-cascade unwinds via the undo log, leaving the entire statement's mutations reverted. Cascade chains recurse up to `MaxCascadeDepth` (32) and then raise `NotSupportedException`.

## DROP TABLE protection

A table with `IncomingForeignKeys.Count > 0` cannot be dropped — Msg 3726 (`Could not drop object '<name>' because it is referenced by a FOREIGN KEY constraint.`). Drop the child first, or drop the FK via `ALTER TABLE … DROP CONSTRAINT` (deferred).

On a successful `DROP TABLE`, the dropped table's `OutgoingForeignKeys` are detached from each parent's `IncomingForeignKeys` list so subsequent DROPs on the parent see the up-to-date reference count.

## Catalog surface

### `sys.objects`

Each FK emits a `'F '` / `FOREIGN_KEY_CONSTRAINT` row interleaved after its child table's row (matching the probe-confirmed `sys.objects` shape). `parent_object_id` is the child table's id.

### `sys.foreign_keys` — 22 columns

| Column | Source |
|--------|--------|
| `name` | `ForeignKey.Name` |
| `object_id` | `ForeignKey.ObjectId` |
| `principal_id` | NULL |
| `schema_id` | child table's schema id |
| `parent_object_id` | child table's object id |
| `type` | `'F '` |
| `type_desc` | `FOREIGN_KEY_CONSTRAINT` |
| `create_date` / `modify_date` | child table's create date |
| `is_ms_shipped` / `is_published` / `is_schema_published` | 0 |
| `referenced_object_id` | parent table's object id |
| `key_index_id` | `1` (the simulator doesn't model indexes — see [Fidelity gaps](#fidelity-gaps)) |
| `is_disabled` / `is_not_for_replication` / `is_not_trusted` | 0 |
| `delete_referential_action` | 0/1/2/3 |
| `delete_referential_action_desc` | `NO_ACTION` / `CASCADE` / `SET_NULL` / `SET_DEFAULT` |
| `update_referential_action` | 0/1/2/3 |
| `update_referential_action_desc` | matching string |
| `is_system_named` | `ForeignKey.IsSystemNamed` |

### `sys.foreign_key_columns` — 6 columns

One row per (FK, column-pair). Composite FKs emit one row per participating column with `constraint_column_id` starting at 1.

| Column | Source |
|--------|--------|
| `constraint_object_id` | FK object id |
| `constraint_column_id` | 1-based position in the FK's column list |
| `parent_object_id` | child table's object id |
| `parent_column_id` | 1-based ordinal of the child's FK column |
| `referenced_object_id` | parent table's object id |
| `referenced_column_id` | 1-based ordinal of the parent's referenced column |

### `OBJECT_ID(name, 'F')`

Not modeled. The simulator's `OBJECT_ID` only recognizes `U` / `FN` / `IF` / `TF` / `V` / `P` filters today; adding `'F'` is straightforward but no application probed uses it.

## Auto-generated FK name

Same FNV-1a hash scheme as PK / UQ / CHECK constraint naming, with a different prefix:

- Single-column FK: `FK__<child-table-first-8>__<column-first-8>__<8 hex>`
- Composite FK: `FK__<child-table-first-8>__<8 hex>`

The 8-hex suffix is deterministic across runs (FNV-1a over table name + column names + declaration index), so test assertions on the auto-name shape are stable.

## EF Core integration

`HasOne` / `WithMany` / `HasForeignKey` end-to-end. EF Core's SqlServer provider emits inline + table-level FK shapes during `EnsureCreated`, but `EnsureCreated` itself runs through `sys.extended_properties` which the simulator doesn't model — so the canonical pattern is to **bootstrap tables with raw `CREATE TABLE` containing the FK**, then exercise the LINQ surface against the schema (same convention as `EFCoreHiLo`). Once tables exist:

- Child INSERT through `SaveChanges` validates the FK; violations surface as `DbUpdateException` wrapping the simulator's Msg 547.
- `OnDelete(DeleteBehavior.Cascade)` matches the SQL `ON DELETE CASCADE` clause — server-side cascade applies through raw SQL DELETE on the connection.

## Fidelity gaps

- **`key_index_id` in `sys.foreign_keys`** — Always reports `1`. Real SQL Server reports the index id on the parent table that backs the FK's referenced columns; the simulator has no index storage, so 1 is the canonical "the FK is backed by the parent's PK / first UQ" answer.
- **Composite FK that references a multi-column UNIQUE where the column order differs from the FK column order** — accepted by `ReferencedColumnsFormKey`'s set-equality check; real SQL Server matches the column order as declared. Probe didn't surface this case; the simulator's matching rule is slightly looser.
- **`OBJECT_ID(name, 'F')`** — Returns NULL. The handful of `F`-filter callers in the wild can use `select object_id from sys.foreign_keys where name = …` instead.
- **`SET DEFAULT` when the column has no `DEFAULT` clause** — Real SQL Server raises Msg 1789 at CREATE TABLE if the FK column's `SET DEFAULT` would resolve to NULL on a NOT NULL column. The simulator defers the check to runtime, where the resulting NULL fails Msg 515 instead. Same end state (the statement fails); different error code.
