# Foreign Keys

`FOREIGN KEY` constraints ship: inline + table-level grammar with optional `[CONSTRAINT name]`, full `ON DELETE` / `ON UPDATE` action set (`NO ACTION` / `CASCADE` / `SET NULL` / `SET DEFAULT`), runtime enforcement on `INSERT` / `UPDATE` / `DELETE` / `MERGE`, cascade chains, `DROP TABLE` protection, and the `sys.foreign_keys` + `sys.foreign_key_columns` catalog surface.
Probe-confirmed against SQL Server 2025.

## Grammar

Two equivalent declaration shapes accepted in `CREATE TABLE`:

```sql
-- Inline (single-column FK on the column being declared):
CREATE TABLE c (
  id      int NOT NULL PRIMARY KEY,
  p_id    int NOT NULL [CONSTRAINT fk_c_to_p] [FOREIGN KEY] REFERENCES p (id) [ON DELETE action] [ON UPDATE action]
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
`FOREIGN KEY` ahead of the inline form's `REFERENCES` is a noise phrase real accepts and the simulator consumes (`ConsumeOptionalForeignKeyNoisePhrase`); the FK is single-column either way, since the inline form's column is the one being declared.

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

`ForeignKey` itself is in `Storage/ForeignKey.cs`: name, object id, both tables, child + referenced full-ordinal arrays, the two `ReferentialAction` values, and an `IsSystemNamed` flag.
Full ordinals (not storage ordinals) because the enforcement loop materializes whole rows.

The two lists wire up symmetrically during the CREATE pass via `Simulation.ResolveForeignKeys`.
Self-referencing FKs are supported (the parent table is already in its schema dict by the time the FK resolves).

## Validation at CREATE

1. **Referenced table must exist** — `MultiPartName` lookup against the live schema dict; missing → Msg 208.
2. **Referenced column set must form a PRIMARY KEY or UNIQUE** — multiset compare against `referencedTable.KeyConstraints`.
   Mismatch → Msg 1776.
3. **Cascade-cycle / multiple-path check** — when the new FK declares any non-NO_ACTION action, the resolver walks the existing FK graph (plus the FKs already queued in this same `CREATE TABLE` statement) looking for either a self-reference or a path from `newFk.ReferencedTable` back to `newFk.ChildTable`.
   Either condition → Msg 1785.

The validation runs across the full pending FK list *before* mutating either table's `OutgoingForeignKeys` / `IncomingForeignKeys`.
A failure unwinds the partial `CREATE TABLE` by removing the new table from its dict.

## Enforcement

### Child side (INSERT / UPDATE / MERGE-INSERT / MERGE-UPDATE)

After per-row check + key validation, `EnforceOutgoingForeignKeys` walks `table.OutgoingForeignKeys`.
For each FK:

- A NULL in any child FK column **skips the check** (probe-confirmed — applies to partial NULL in composite FKs too).
- A non-NULL tuple that doesn't match any row of the parent on the FK's referenced columns → Msg 547 with the FK name and the parent's qualified table reference.
  Single-column FK appends `, column 'X'`; composite FK omits the column phrase.
  Self-referencing FK substitutes `FOREIGN KEY SAME TABLE` for `FOREIGN KEY`.

`ReferencedRowExists` **seeks** the parent rather than scanning it: the referenced columns are always a PK/UNIQUE key, so it probes the parent's per-`Heap` [`HeapSeekCache`](indexes.md) on those columns (the parent's own index, incrementally maintained) and verifies each candidate against live bytes — there's no residual WHERE to discard the cache's stale-entry false-positives, so the verify is mandatory.
Bulk child inserts against a large parent drop from O(children × parent) to one parent-index build plus O(1) per insert (measured ~67× faster for 2 000 inserts against a 20 000-row parent, and the ratio grows with parent size).
The full→storage ordinal map is `HeapTable.StorageOrdinals[fullOrdinal]`.
Every column an FK can legally name is stored — a PERSISTED computed column has a storage slot and a non-persisted one is rejected at declaration (see [Computed columns in a foreign key](#computed-columns-in-a-foreign-key)) — so the seek path always applies and the scan fallback beside it is a guard on the storage layout rather than a live branch.

### Parent side (DELETE / UPDATE / MERGE-DELETE / MERGE-UPDATE)

After parent-side mutations, `EnforceIncomingForeignKeys` (for DELETE) and `EnforceIncomingFkOnUpdate` (for UPDATE) walk `table.IncomingForeignKeys`.
For each FK:

- **UPDATE-side filter**: rows whose referenced-column tuple didn't change are skipped (a non-PK update on the parent is a no-op for the FK).
- For each parent row whose values were affected, find the child rows whose FK tuple matches the parent's old value via `MatchChildRowsToParents`: it **seeks** the child's `HeapSeekCache` on the FK columns once per affected parent key (verifying each candidate against live bytes, de-duplicating by address), falling back to a single full child scan only when an FK column isn't stored.
  Unlike real SQL Server — where an un-indexed FK child column forces a table scan on every parent delete (the classic "always index your FKs" pitfall) — the simulator builds the FK-column index on first touch and amortizes it, so cascades stay fast regardless of declared indexes (a performance divergence, not an observable-behavior one; the result set is identical).
- If matches exist, dispatch on the FK's `DeleteAction` (for DELETE) or `UpdateAction` (for UPDATE):

| Action | Behavior |
|--------|----------|
| `NO ACTION` | Raise Msg 547 with `REFERENCE constraint` wording, naming the child table + column. |
| `CASCADE` (DELETE) | Recursively delete the matching child rows (themselves potentially parents — recursion guarded by `MaxCascadeDepth = 32`). |
| `CASCADE` (UPDATE) | Rewrite each child row's FK columns to the parent's new value. |
| `SET NULL` | Rewrite each child row's FK columns to NULL. |
| `SET DEFAULT` | Rewrite each child row's FK columns to each column's `DEFAULT` expression (NULL if no default). |

Statement-level atomicity continues to apply: a Msg 547 raised mid-cascade unwinds via the undo log, leaving the entire statement's mutations reverted.
Cascade chains recurse up to `MaxCascadeDepth` (32) and then raise `NotSupportedException`.

## Computed columns in a foreign key

CHECK constraints over computed columns follow the same 8183-vs-1764 split — see [`constraints.md`](constraints.md#computed-columns-in-a-check-constraint).

A **PERSISTED** computed column is a legal referencing column, in all three declaration forms — the inline column tail (`cc AS base + 1 PERSISTED [CONSTRAINT n] [FOREIGN KEY] REFERENCES p(id)`), the table-level list, and `ALTER TABLE … ADD CONSTRAINT`.
It has a storage slot, so enforcement reads its stored value like any other column and takes the same seek path: an INSERT or an UPDATE of the columns the expression reads re-evaluates it and raises Msg 547 against the parent (the UPDATE need never name the FK column), a NO ACTION parent DELETE raises Msg 547 naming the computed column, and `ON DELETE CASCADE` removes the whole child row.

It is also a legal *referenced* column when it carries the PRIMARY KEY / UNIQUE the FK needs — the parent-side seek probes its stored value, and Msg 547 names it.

A **non-persisted** computed column is rejected at declaration, with the message depending on which form declared it (probe-confirmed split):

| Form | Error |
|------|-------|
| Inline column tail | **Msg 8183** — `Only UNIQUE or PRIMARY KEY constraints can be created on computed columns, while CHECK, FOREIGN KEY, and NOT NULL constraints require that computed columns be persisted.` (real rejects at parse, before the constraint reaches resolution) |
| Table-level list, `ALTER TABLE … ADD CONSTRAINT` | **Msg 1764** — `Computed Column '<col>' in table '<table>' is invalid for use in 'FOREIGN KEY CONSTRAINT' because it is not persisted.` (note real's capitalized "Computed Column") |

The referential actions are then restricted to the ones that never *write* the computed column:

| Declared action | Result |
|-----------------|--------|
| `ON DELETE NO ACTION` / `ON DELETE CASCADE` | Accepted — CASCADE removes the whole row rather than writing the column. |
| `ON DELETE SET NULL` / `ON DELETE SET DEFAULT` | **Msg 1765** — `Foreign key '<fk>' creation failed. Only NO ACTION and CASCADE referential delete actions are allowed for referencing computed column '<col>'.` |
| `ON UPDATE NO ACTION` | Accepted. |
| `ON UPDATE CASCADE` / `SET NULL` / `SET DEFAULT` | **Msg 1715** — `Foreign key '<fk>' creation failed. Only NO ACTION referential update action is allowed for referencing computed column '<col>'.` (CASCADE is rejected here where the ON DELETE side accepts it, because an update cascade has to write the column) |

Probed precedence among the four: **Msg 1776** (no matching parent key) beats **1764**, which beats **1765**, which beats **1715**.
`ResolveForeignKeys` applies the three computed-column gates in that order, after the referenced-key check and before the SET DEFAULT / cascade-cycle ones.
Placing them there covers `CREATE TABLE`'s table-level form, `ALTER TABLE … ADD CONSTRAINT`, and the `ALTER TABLE DROP COLUMN` ordinal-shift re-resolution from one site; the inline form's Msg 8183 fires earlier, in `ParseComputedColumnInlineConstraint`.
As with Msg 1776, real's trailing informational **Msg 1750** (`Could not create constraint or index. See previous errors.`) is collapsed away.

Real also rejects a *non-persisted* computed **referenced** column with **Msg 1784**, which the simulator never reaches: `UNIQUE` on a non-persisted computed column is itself unbuilt (`NotSupportedException`), so the parent key that FK would need can't exist.

## DROP TABLE protection

A table with `IncomingForeignKeys.Count > 0` cannot be dropped — Msg 3726 (`Could not drop object '<name>' because it is referenced by a FOREIGN KEY constraint.`).
Drop the child first, or drop the FK via `ALTER TABLE … DROP CONSTRAINT` (deferred).

On a successful `DROP TABLE`, the dropped table's `OutgoingForeignKeys` are detached from each parent's `IncomingForeignKeys` list so subsequent DROPs on the parent see the up-to-date reference count.

## Catalog surface

### `sys.objects`

Each FK emits a `'F '` / `FOREIGN_KEY_CONSTRAINT` row interleaved after its child table's row (matching the probe-confirmed `sys.objects` shape).
`parent_object_id` is the child table's id.

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
| `create_date` / `modify_date` | `ForeignKey.CreateDate` / `.ModifyDate` — the declaring statement's instant (see [`alter-table.md`](alter-table.md#per-constraint-dates)) |
| `is_ms_shipped` / `is_published` / `is_schema_published` | 0 |
| `referenced_object_id` | parent table's object id |
| `key_index_id` | the referenced table's index that backs the FK, resolved through `HeapTable.IndexIdentities()` |
| `is_disabled` / `is_not_for_replication` / `is_not_trusted` | 0 |
| `delete_referential_action` | 0/1/2/3 |
| `delete_referential_action_desc` | `NO_ACTION` / `CASCADE` / `SET_NULL` / `SET_DEFAULT` |
| `update_referential_action` | 0/1/2/3 |
| `update_referential_action_desc` | matching string |
| `is_system_named` | `ForeignKey.IsSystemNamed` |

`key_index_id` resolves through the same `HeapTable.IndexIdentities()` allocation authority `sys.key_constraints.unique_index_id` reads: the referenced table's PRIMARY KEY / UNIQUE constraint (or unique index) whose key columns are exactly the columns the FK targets reports its own `sys.indexes.index_id`.
So an FK pointing at a NONCLUSTERED PK reports whatever id that PK landed on rather than 1 — probe-confirmed: with a clustered index holding id 1, real reported 3 for the PK-referencing FK and 2 for one referencing a UNIQUE constraint.

### `sys.foreign_key_columns` — 6 columns

One row per (FK, column-pair).
Composite FKs emit one row per participating column with `constraint_column_id` starting at 1.

| Column | Source |
|--------|--------|
| `constraint_object_id` | FK object id |
| `constraint_column_id` | 1-based position in the FK's column list |
| `parent_object_id` | child table's object id |
| `parent_column_id` | stable `sys.columns.column_id` of the child's FK column |
| `referenced_object_id` | parent table's object id |
| `referenced_column_id` | stable `sys.columns.column_id` of the parent's referenced column |

### `OBJECT_ID(name, 'F')`

Not modeled.
The simulator's `OBJECT_ID` only recognizes `U` / `FN` / `IF` / `TF` / `V` / `P` filters today; adding `'F'` is straightforward but no application probed uses it.

## Auto-generated FK name

Same FNV-1a hash scheme as PK / UQ / CHECK constraint naming, with a different prefix:

- Single-column FK: `FK__<child-table-first-8>__<column-first-8>__<8 hex>`
- Composite FK: `FK__<child-table-first-8>__<8 hex>`

The 8-hex suffix is deterministic across runs (FNV-1a over table name + column names + declaration index), so test assertions on the auto-name shape are stable.

## EF Core integration

`HasOne` / `WithMany` / `HasForeignKey` end-to-end.
EF Core's SqlServer provider emits inline + table-level FK shapes during `EnsureCreated`, but `EnsureCreated` itself runs through `sys.extended_properties` which the simulator doesn't model — so the canonical pattern is to **bootstrap tables with raw `CREATE TABLE` containing the FK**, then exercise the LINQ surface against the schema (same convention as `EFCoreHiLo`).
Once tables exist:

- Child INSERT through `SaveChanges` validates the FK; violations surface as `DbUpdateException` wrapping the simulator's Msg 547.
- `OnDelete(DeleteBehavior.Cascade)` matches the SQL `ON DELETE CASCADE` clause — server-side cascade applies through raw SQL DELETE on the connection.

## Fidelity gaps

- *(the referenced-column order gap is closed — `ReferencedColumnsFormKey` matches in declared order, so `REFERENCES p(y, x)` against `UNIQUE (x, y)` raises **Msg 1776 State 1** as real does; probe-confirmed)*
- **`OBJECT_ID(name, 'F')`** — Returns NULL.
  The handful of `F`-filter callers in the wild can use `select object_id from sys.foreign_keys where name = …` instead.
- *(the `SET DEFAULT` gap is closed — a NOT NULL referencing column with no DEFAULT raises **Msg 1762** at declaration, matching real. The earlier note claimed Msg 1789; the probed number is 1762, and its text names the constraint in double quotes where Msg 1776 beside it uses single. A **nullable** referencing column without a default is accepted, since NULL is then the value SET DEFAULT sets.)*
