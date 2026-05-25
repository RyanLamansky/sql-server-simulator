# Indexes

`CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX` + `DROP INDEX` ship, with full grammar coverage for column ordering (ASC / DESC), INCLUDE columns, WHERE filter, and the WITH (options) clause. The `sys.indexes` + `sys.index_columns` catalog views project rows for PRIMARY KEY constraints, UNIQUE constraints, and CREATE INDEX-declared entries. Probe-confirmed against SQL Server 2025 on 2026-05-14.

## Grammar

```sql
CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX name
    ON table (col [ASC | DESC] [, …])
    [INCLUDE (col [, …])]
    [WHERE filter]
    [WITH (option = value [, …])]
    [ON <filegroup>];

DROP INDEX [IF EXISTS] name ON table [, name ON table [, …]];
```

The simulator has no B-tree storage, so an index never constrains inserts (UNIQUE aside) and isn't a stored ordered structure. UNIQUE indexes participate in INSERT / UPDATE / MERGE enforcement alongside `KeyConstraint`. The `WITH (...)` clause is parsed parens-balanced and discarded — none of `FILLFACTOR` / `PAD_INDEX` / `IGNORE_DUP_KEY` / `ONLINE` / `SORT_IN_TEMPDB` / etc. alter behavior. The trailing `ON <filegroup>` placement clause (e.g. `ON [PRIMARY]`) is also parsed and discarded — no filegroup model. The same two trailers are accepted on inline `CONSTRAINT … PRIMARY KEY | UNIQUE` clauses inside CREATE TABLE and on `ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY | UNIQUE (cols)`, plus `) ON [PRIMARY] [TEXTIMAGE_ON [PRIMARY]]` at the end of CREATE TABLE — the full SSMS-scripting verbosity surface.

## Equality-seek acceleration

Although there's no B-tree, an index (or PK / UNIQUE key) **does** accelerate reads via an equality seek on its **leading key-column prefix**. `Selection.Execution.IndexSeek.cs` collects every top-level WHERE conjunct of the shape `indexedColumn = <stable value>` — where the value side is a literal, a variable, an outer/correlated column reference, or a `CAST` / `CONVERT` / parenthesization peeling down to one of those (via `Expression.PureConversionOperand`) — then rewrites a single-base-table scan into a hash-index lookup keyed on the longest leading prefix of some index/key whose columns are all covered by those conjuncts. So `WHERE a = x AND b = y` against an index on `(a, b, …)` keys on the two-column tuple, not just `a`; conjunct order is irrelevant (columns map to the prefix by ordinal). The longest usable prefix across all keys/indexes wins, which is what keeps a **non-selective leading column** (a bit flag, a low-cardinality FK) from dragging its whole bucket through the residual filter — the full matched prefix is keyed precisely. Peeling pure conversions matches real SQL Server keeping `col = CAST(<const> AS …)` sargable (probe-confirmed 2026-05-25: integer / decimal widenings of a constant or variable still Index Seek; the cross-type case that does *not* seek there is `varchar` column vs `nvarchar` value, which the simulator's `Collation.Resolve` guard already declines). Every matched conjunct stays in WHERE as a residual filter, so the seek can only narrow the row source, never change results; the per-component key promotion / collation rules mirror the equi-join hash path exactly (`SqlType.Promote` + `CoerceTo` + collation-coercibility `Resolve` guard, applied column-by-column into a multi-value `SqlValueKey`).

A prefix is bounded at the first key column that either has no equality conjunct or whose value side can't anchor a seek (a `NULL` probe — never equal under `=`; a cross-collation string compare; a promotion/evaluation that throws). The seek then keys on the shorter usable prefix and the residual WHERE handles the rest: `WHERE a = 1 AND b = @null` seeks on `a` alone, then the residual `b = @null` correctly excludes every candidate. The per-`Heap` cache is keyed by the leading ordinal and remembers the exact prefix (ordinals + promoted types) it was built for, rebuilding from a scan if a later query requests a different prefix sharing that lead.

This is the path that collapses a **correlated `EXISTS` / `IN` / scalar subquery** (and an unindexed-inner `APPLY`) from O(outer × inner) toward linear: the inner re-executes per outer row, but a per-table cache (keyed on the `Heap`, built lazily on first seek) persists across those calls. Measured: a correlated `EXISTS` over a 4000 × 16000-row pair dropped from ~1480 ms to ~5 ms, on par with live SQL Server.

The cache is a **read-only acceleration structure, not a maintained index**: buckets are tagged with the heap's `MutationGeneration` (bumped by every `Heap.Insert` / `DeleteAt`) and rebuilt when it moves, so the heap stays the single source of truth and the write path — including its row locking and lock-escalation accounting — is completely untouched. The seek isn't a free pass around concurrency control: it routes each seeked candidate row through the same `BatchContext.TouchRowForRead` lock / conflict pipeline the full scan uses, so it acquires locks on only the rows it touches (matching a real index seek's footprint), and it **declines** — falling back to the full scan — for:

- **snapshot / RCSI reads**, where the version visible to the reader can carry a different key value than the live heap row (a current-heap index would miss it);
- **tx-scoped row-lock plans** (`REPEATABLE READ` / `SERIALIZABLE` / `UPDLOCK` / `HOLDLOCK` …), where the scan deliberately locks every row it reads to end of transaction;
- non-base-table sources (derived tables, table variables, `FOR SYSTEM_TIME`), a WHERE with no equality conjunct on any index's leading column, range predicates (`>` / `<` / `BETWEEN`), and `NULL` / non-resolvable-collation value sides.

Not modeled: range seeks, ORDER BY elimination, and seek acceleration of the aggregate projection path (`SELECT COUNT(*) … WHERE indexedcol = x` still scans — only the non-aggregate `ProjectSqlRows` path seeks). Equi-joins are unaffected: they already take the O(L+R) hash path.

## Storage

`HeapTable.Indexes` is a mutable `List<Index>`, populated by CREATE INDEX and trimmed by DROP INDEX. The `Index` record carries:

- `Name`, `ObjectId` (allocated from the per-database counter).
- `IsUnique` — drives enforcement.
- `IsClustered` — captured for fidelity but doesn't alter storage; every table is a flat heap regardless.
- `KeyColumns[]` — each entry pairs a storage ordinal with the ASC / DESC flag.
- `IncludedColumns[]` — storage ordinals for INCLUDE columns; catalog-only.
- `Filter` (BooleanExpression?) — only honored on UNIQUE indexes.
- `FilterDefinition` (string?) — text form for `sys.indexes.filter_definition`. Always NULL today (see [Fidelity gaps](#fidelity-gaps)).

PRIMARY KEY / UNIQUE constraints stay in `HeapTable.KeyConstraints`; sys.indexes synthesizes rows for them alongside the user indexes.

## Enforcement

### INSERT side

`EnforceUniqueIndexes` runs after `EnforceKeyConstraints` in the INSERT path:

1. Skip entirely when no UNIQUE entry exists on the table.
2. For each UNIQUE entry: if the entry has a `Filter`, evaluate it against the new row's full-column values; skip the check entirely when the filter doesn't evaluate `true` (false or UNKNOWN both skip — mirrors SQL Server's filtered-unique-index semantic).
3. Linear-scan the heap. For each existing row:
   - If the index has a filter, evaluate against the existing row too; skip when not `true`.
   - Decode the key tuple via storage ordinals and compare to the new row's stored values via `SqlValue.Equals` (which already handles SQL Server's NULLs-equal-for-UNIQUE rule).
   - Raise Msg 2601 on the first collision, naming the qualified table and index.

### UPDATE / MERGE side

`EnforceUniqueIndexesForUpdate` mirrors `EnforceKeyConstraintsForUpdate`:

1. Cross-check every pair of affected rows for collisions within the batch (filter-aware on both sides).
2. For each affected row, walk the non-affected heap looking for collisions (filter-aware).
3. Raise Msg 2601 on the first match.

Filter evaluation reuses the same `EvaluateIndexFilter` helper — a closure-based resolver that maps `MultiPartName` references to the row's column slot by case-insensitive name compare (same shape `EnforceCheckConstraints` uses).

### Filter-aware uniqueness — concrete semantic

A filtered UNIQUE index only constrains rows where `filter` evaluates `true`. Rows where the filter is `false` or `UNKNOWN` (any NULL operand in the predicate, by three-valued logic) bypass the check entirely. This is the standard SQL Server behavior — load-bearing for application patterns like "unique among non-archived rows" or "unique when status is active."

## Existing-data validation at CREATE

`ValidateExistingRowsForUniqueIndex` linear-scans the heap building a set of (filter-included) key tuples, raising Msg 1505 on the first duplicate. Non-UNIQUE CREATE INDEX skips this entirely.

## DROP INDEX

`DROP INDEX name ON table` resolves the target table, then walks `HeapTable.Indexes` for a name match. Three rejection paths:

- Missing parent table: Msg 3701 State 6 (`Cannot drop the index 'dbo.t.ix', because it does not exist or you do not have permission.`). `IF EXISTS` suppresses.
- Index name matches a PRIMARY KEY or UNIQUE constraint: Msg 3723 (`An explicit DROP INDEX is not allowed on index 'dbo.t.ix'. It is being used for PRIMARY KEY constraint enforcement.`). The PK/UQ kind word is interpolated (`PRIMARY KEY` or `UNIQUE`). `IF EXISTS` does NOT suppress — real SQL Server's behavior matches.
- Index not found on the resolved table: Msg 3701 State 7. `IF EXISTS` suppresses.

Multi-target `DROP INDEX ix1 ON t1, ix2 ON t2` resolves each entry independently in declaration order; any error short-circuits with whatever drops already happened persisted. (Real SQL Server has the same behavior — DROP INDEX is not atomic across the comma list.)

## Catalog surface

### `sys.indexes` — 24-column probe-confirmed shape

One row per (table, index):

- **PK** (when present) at `index_id = 1`, `type = 1`, `type_desc = CLUSTERED`, `is_primary_key = 1`, `is_unique = 1`.
- **HEAP row** (when no PK) at `index_id = 0`, `type = 0`, `type_desc = HEAP`, `name = NULL`. Matches SQL Server's "the table itself is the heap" semantic.
- **UNIQUE constraints** at `index_id ≥ 2`, `type_desc = NONCLUSTERED`, `is_unique = 1`, `is_unique_constraint = 1`.
- **CREATE UNIQUE INDEX** at `index_id ≥ 2`, `type_desc = NONCLUSTERED`, `is_unique = 1`, `is_unique_constraint = 0`.
- **Non-UNIQUE CREATE INDEX** at `index_id ≥ 2`, `type_desc = NONCLUSTERED`, `is_unique = 0`.

`index_id` assignment among non-PK entries follows allocation order (the simulator's `AllocateObjectId` is monotonic), matching SQL Server's declaration-order behavior.

### `sys.index_columns` — 10-column probe-confirmed shape

One row per (index, column):

- **KEY columns**: `key_ordinal = 1..N`, `index_column_id = 1..N`, `is_included_column = 0`.
- **INCLUDE columns**: `key_ordinal = 0`, `index_column_id = N+1..`, `is_included_column = 1`.
- HEAP entries (index_id = 0) don't appear — real SQL Server's catalog omits them.

`is_descending_key` reflects the per-column DESC flag from CREATE INDEX. `column_id` is the 1-based full-column ordinal from `sys.columns` (mapped back from the storage ordinal stored on the index).

## EF Migrations integration

EF Core's SqlServer provider emits `CREATE INDEX` (and `CREATE UNIQUE INDEX` for `HasIndex().IsUnique()`) during `EnsureCreated` and during migrations' `Up()` methods. With the simulator:

- Index creation parses + emits a `sys.indexes` row — `EnsureCreated`'s introspection (which reads sys.indexes back) sees the expected shape.
- `HasIndex().HasFilter("...")` emits a `WHERE` clause — the predicate is captured and honored for UNIQUE indexes (filter-aware uniqueness).
- Non-UNIQUE indexes are no-ops at the storage layer (no query acceleration), but their presence in `sys.indexes` keeps EF Migrations introspection happy.

## Fidelity gaps

- **`filter_definition` always NULL**: real SQL Server stores the parenthesized predicate text. The simulator's `BooleanExpression.Parse` consumes its source text without retaining the span, so the column reports NULL even when `has_filter` is true. Round-trip migrations that diff this column will see a spurious change.
- **CLUSTERED keyword is decorative**: `CREATE CLUSTERED INDEX` is accepted but the resulting index reports `type_desc = NONCLUSTERED` in `sys.indexes`. Real SQL Server tracks one clustered index per table (replacing the heap); the simulator has no row-ordered storage to differentiate.
- **Multiple clustered indexes**: real SQL Server raises Msg 1902 (`Cannot create more than one clustered index on table`). The simulator silently accepts a second CLUSTERED INDEX since clustering is decorative.
- **WITH options ignored**: `FILLFACTOR`, `IGNORE_DUP_KEY`, `ONLINE`, `MAXDOP`, etc. all parse but have no behavior. `IGNORE_DUP_KEY = ON` notably should downgrade Msg 2601 to a warning + skip — not modeled.
- **No partition-aware index storage**: `partition_ordinal` always 0, `data_space_id` always 1 (PRIMARY).
- **DROP INDEX comma list not atomic**: each entry resolves independently. Real SQL Server rolls back all on any failure.
- **`CREATE INDEX … ON view(col)` for indexed views**: not modeled. Real SQL Server requires SCHEMABINDING + WITH CHECK OPTION on the view. The simulator's CREATE INDEX requires the target to be a HeapTable.
- **Index hints (`SELECT … WITH (INDEX = name)`)**: not modeled — query planner is single-strategy (full scan) regardless.
