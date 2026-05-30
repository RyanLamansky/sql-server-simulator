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

The acceleration structure is `HeapSeekCache` (`Parser/HeapSeekCache.cs`) — a per-`Heap` index attached through a `ConditionalWeakTable<Heap, HeapSeekCache>` and reached via `HeapSeekCache.For(heap)`. It's a storage-level accelerator, not a query-planner detail: **the consumers share it** — the query planner (`Selection`'s equality / range / ORDER BY / keyset seeks over `SELECT`), mutation targets (`UPDATE` / `DELETE`'s `WHERE` scan via `Selection.SeekMutationTarget`, and the inverted `MERGE` per-source target seek via `Selection.TryPrepareMergeTargetSeek` — see [UPDATE / DELETE target seeking](#update--delete-target-seeking) and [MERGE target seeking](#merge-target-seeking-loop-inversion)), and constraint enforcement (`Simulation`'s foreign-key parent-existence and cascade lookups; see [`foreign-keys.md`](foreign-keys.md)). The first two lean on a residual filter — the query planner keeps the matched conjuncts in WHERE, the mutation loop re-runs the full predicate per row — to discard the cache's stale-entry false-positives; the FK path has no residual filter, so it re-verifies each candidate against live bytes (`HeapSeekCache.AnyRowMatches` / `MatchingRows`). `SqlValueKey` (the bucket key) is likewise a shared top-level type, not nested in `Selection`.

Although there's no B-tree, an index (or PK / UNIQUE key) **does** accelerate reads via an equality seek on its **leading key-column prefix**. `Selection.Execution.IndexSeek.cs` collects every top-level WHERE conjunct of the shape `indexedColumn = <stable value>` — where the value side is a literal, a variable, an outer/correlated column reference, an arithmetic node (`TwoSidedExpression`) whose operands are all themselves stable (which is how a negative literal arrives: the parser builds `-1` as `0 - 1`, so `col = -1` and `col = @v + 1` stay sargable — a deterministic operator over row-invariant operands is row-invariant), or a `CAST` / `CONVERT` / parenthesization peeling down to one of those (via `Expression.PureConversionOperand`) — then rewrites a single-base-table scan into a hash-index lookup keyed on the longest leading prefix of some index/key whose columns are all covered by those conjuncts. So `WHERE a = x AND b = y` against an index on `(a, b, …)` keys on the two-column tuple, not just `a`; conjunct order is irrelevant (columns map to the prefix by ordinal). The longest usable prefix across all keys/indexes wins, which is what keeps a **non-selective leading column** (a bit flag, a low-cardinality FK) from dragging its whole bucket through the residual filter — the full matched prefix is keyed precisely. Peeling pure conversions matches real SQL Server keeping `col = CAST(<const> AS …)` sargable (probe-confirmed 2026-05-25: integer / decimal widenings of a constant or variable still Index Seek; the cross-type case that does *not* seek there is `varchar` column vs `nvarchar` value, which the simulator's `Collation.Resolve` guard already declines). Every matched conjunct stays in WHERE as a residual filter, so the seek can only narrow the row source, never change results; the per-component key promotion / collation rules mirror the equi-join hash path exactly (`SqlType.Promote` + `CoerceTo` + collation-coercibility `Resolve` guard, applied column-by-column into a multi-value `SqlValueKey`).

A WHERE conjunct of the shape `col IN (v1, v2, …)` — or its equivalent OR-of-equalities `col = v1 OR col = v2 OR …`, which EF Core emits for `Contains(...)` against a small list — decomposes through the same path via `BooleanExpression.TryGetEqualityFamily`. Every candidate must be a stable value and they must all anchor on the same column (mixed-column ORs like `a = 1 OR b = 2` fall through to scan); a single column's binding then becomes a **multi-value** equality. Surviving probes are unioned into one promoted type (pairwise `SqlType.Promote` across candidates), NULL candidates are silently dropped (never equal under `=`), and the seek expands across the **cartesian product** of per-column probe arrays — so `a IN (1, 2) AND b = 3` fires two composite-prefix probes against the (a, b) cache and `a IN (1, 2) AND b IN (3, 4)` fires four. Each tuple is one hash lookup against the same per-Heap cache; a row can only sit in one bucket per probe column, so the unioned candidate stream contains no duplicates. `NOT IN` is an AND-of-inequalities (not a positive equality family) and declines.

A prefix is bounded at the first key column that either has no equality conjunct or whose value side can't anchor a seek (a `NULL` probe — never equal under `=`; a cross-collation string compare; a promotion/evaluation that throws). The seek then keys on the shorter usable prefix and the residual WHERE handles the rest: `WHERE a = 1 AND b = @null` seeks on `a` alone, then the residual `b = @null` correctly excludes every candidate. The per-`Heap` cache is keyed by the leading ordinal and remembers the exact prefix (ordinals + promoted types) it was built for, rebuilding from a scan if a later query requests a different prefix sharing that lead.

This is the path that collapses a **correlated `EXISTS` / `IN` / scalar subquery** (and an unindexed-inner `APPLY`) from O(outer × inner) toward linear: the inner re-executes per outer row, but a per-table cache (keyed on the `Heap`, built lazily on first seek) persists across those calls. Measured: a correlated `EXISTS` over a 4000 × 16000-row pair dropped from ~1480 ms to ~5 ms, on par with live SQL Server.

The cache is **incrementally maintained**, not rebuilt on every write. The first seek against a heap builds its buckets from a full scan and activates the heap's bounded *mutation journal* (`Heap.ActivateSeekJournal`); thereafter a write appends a visible-row event (insert / delete / update, carrying the before/after row image) rather than invalidating the cache, and the next seek applies that delta (`Heap.SnapshotSeekJournalSince` → `CacheEntry.Apply`, traced as `CacheReplay`) instead of re-scanning. This kills the per-mutation warm-up: a *mutate-then-seek* loop over a 20 000-row table dropped from ~5.6 ms/iter to ~0.8 ms/iter (the gap widens with table size — a rebuild is O(rows), a replay is O(delta)). A never-seeked (write-only) table never activates the journal, so it pays nothing, and the write path's row locking / lock-escalation accounting stay untouched — journaling is a side-append under its own lock, not a change to the mutation itself.

A full rebuild (traced `CacheBuild`) still happens when the requested prefix differs from the cached one, the journal overflowed its cap so the delta can't be replayed (a large bulk mutation trimmed events the cache needed — `MaxSeekJournalEvents` = 512), or the journal was invalidated. Invalidation is the **rollback / TRUNCATE safety valve**: a rollback rewinds heap state by mutating pages directly (no reversing journal events) and `TRUNCATE` swaps the page list wholesale, so both call `Heap.InvalidateSeekJournal` to force a rebuild from the rewound state (`UndoLog.RollbackTo` invalidates each affected heap once via `UndoEntry.AffectedHeap`). ALTER TABLE replaces the heap instance, so the new heap starts journal-free. Correctness rests on the heap staying the single source of truth: every matched equality conjunct remains a residual WHERE filter, so a stale bucket membership is only ever a harmless false-positive (the residual drops it; the materializer also de-dups and skips tombstoned slots), while the add side — insert / update-new-key — recomputes from live row bytes and so never drops a live candidate (the one thing that would change results). The seek isn't a free pass around concurrency control: under a lock-based plan it routes each seeked candidate row through the same `BatchContext.TouchRowForRead` lock / conflict pipeline the full scan uses, so it acquires locks on only the rows it touches (matching a real index seek's footprint). It **declines** — falling back to the full scan — for:

- **tx-scoped row-lock plans** (`REPEATABLE READ` / `SERIALIZABLE` / `UPDLOCK` / `HOLDLOCK` …), where the scan deliberately locks every row it reads to end of transaction;
- non-base-table sources (derived tables, table variables, `FOR SYSTEM_TIME`), a WHERE with no equality conjunct on any index's leading column (a range-only predicate is handled separately — see [Range seeks](#range-seeks)), and `NULL` / non-resolvable-collation value sides.

### Snapshot / RCSI seeks (version-aware materialization)

A SNAPSHOT / RCSI reader sees the version visible at its snapshot, which can carry a *different key* than the live heap row — so a live-key-only bucket lookup could miss a row whose key was changed out from under the snapshot. Rather than declining table-wide whenever the version store is non-empty (which under a busy RCSI workload meant nearly every point lookup full-scanned — a measured ~90–235× regression from a single live version chain), the seek runs and materializes through the version store (`MaterializeSnapshotCandidates`):

- **Bucket candidates** (live rows whose *current* key matched the probe) each resolve to their snapshot-visible version via `VersionStore.ResolveVisibleVersion`, dropping out when not visible.
- **A version-chain sweep** over `HeapTable.RowVersions` adds every slot carrying a chain — those are exactly the rows whose snapshot-visible key can differ from their live key, plus tombstoned slots a pre-delete snapshot still sees (`ResolveTombstonedSlotForSnapshot`). Deduplicated by slot against the bucket candidates.

The matched equality conjuncts stay in the residual WHERE, so any candidate whose *resolved* version doesn't actually match the probe (e.g. a live-bucket row whose snapshot-visible version carries the old key) is filtered there — no false positives, and the sweep eliminates false negatives. The extra cost is O(|`RowVersions`|), which a read-mostly RCSI workload keeps small (the version-store GC trims versions no open snapshot needs); the whole-table scan this replaces *already* walks `RowVersions` in its own second pass, so the version-aware seek is never more expensive than the scan it supplants. `ResolveSnapshotXidForRead` is called unconditionally regardless of whether the read seeks or scans, for its snapshot-pinning side effects.

### Range seeks

A WHERE with no usable equality seek but a **range bound on the leading key column** of some index or key narrows to a range seek instead of a full scan (`TrySeekByRange`, traced `RangeSeek(table)`). Recognized shapes (`BooleanExpression.TryGetRangeOperands` / `TryGetBetweenOperands`): `col > v` / `>=` / `<` / `<=` in either operand order (the operator flips when the column is on the right — `v < col` ≡ `col > v`), and `col BETWEEN lo AND hi` (two-sided inclusive). Two one-sided bounds on the same column combine (`col > 1 AND col < 9`); first-writer-wins per side, so a redundant looser bound stays a residual filter. The bound value(s) must be stable (the same literal / variable / parameter / correlated-ref / pure-conversion shapes the equality path accepts), so correlated range subqueries re-evaluate the bound per outer row off the shared cache.

The seek reuses the per-`Heap` cache: the leading column's bucket entry gains a **lazily-built, then incrementally-maintained** ordered view (`SortedSet<SqlValueKey>`, single-component), so `GetViewBetween` returns the in-range keys in O(log k + matches). The sorted view is built on the first range seek for that column and thereafter maintained in lockstep with the hash buckets by the same journal replay (a key joins / leaves it exactly when its bucket appears / empties) — so range seeks inherit the no-warm-up property. Measured: 2000 selective `BETWEEN` (11-row) point-ranges over a 100 000-row table dropped from ~12.0 ms/query (full scan) to ~0.07 ms/query (~180×).

Bounds promote against the column type the same way equality probes do (upward, so an out-of-domain bound like `int_col < 9999999999` is exact, not truncated); a NULL bound makes every comparison UNKNOWN, so the range matches nothing — a valid empty seek, not a scan. A promotion / collation-conflict failure declines to the scan. Scope (v1): **single leading-column range only**. An equality-prefix continued by a range (`a = 1 AND b > 5` on an index `(a, b)`) takes the equality seek on `a` and leaves the `b` range residual; a range on a non-leading column declines.

All three single-table projectors — non-aggregate (`ProjectSqlRows`), aggregate (`BuildAggregateProjectionRows`), and window (`ProjectWindowedRows`) — narrow a single-base-table source through the seek, so `SELECT COUNT(*) … WHERE indexedcol = x` and `SELECT … OVER (…) FROM t WHERE indexedcol = x` both seek like their non-aggregate counterpart (without this the window projector silently full-scanned even when its WHERE was perfectly sargable, the regression that made running-total-per-parent EF queries scan the table). They also push single-source WHERE equality predicates onto the **leftmost** source of a multi-source FROM before the join (`NarrowLeftmostJoinSource` — the leftmost is always preserved, so this never changes outer-join semantics), which shrinks the join's driving rowset. The INNER / LEFT equi-join operator then **seeks the inner side per outer row** when that rowset is small and the inner is indexed on the join key — see [`joins.md`](joins.md).

### ORDER BY elimination

A `SELECT` from a single base table whose ORDER BY matches the **key order of some index / key** skips the buffer-and-sort entirely: `TryApplyOrderedScan` (traced `OrderedScan(table)`) enumerates the matching ordered view (the same `SortedSet<SqlValueKey>` the equality / range seeks build, ascending or — for an all-DESC order — reversed) and routes to the streaming projector. The residual WHERE and projection preserve order, so the sort (and the full-result buffer) is gone, and `TOP` / a shallow `OFFSET` then materialize only the rows they need. Three shapes qualify, all reusing one composite ordered view:

- **Single NOT-NULL leading key column** (`ORDER BY id`), optionally same-column range-narrowed (`WHERE id > 100 ORDER BY id` stays narrowed *and* ordered). `SELECT TOP 50 … ORDER BY id` over 100 000 rows dropped from ~65 ms/query (materialize + sort the whole table) to ~7 ms/query — ~8.7×.
- **Multi-column leading prefix** (`ORDER BY a, b` against a key on `(a, b, …)`), every order column NOT NULL. `SELECT TOP 50 … ORDER BY a, b` over 100 000 composite-key rows dropped from ~111 ms/query (full buffered sort — the previous single-column-only path declined this outright) to ~12 ms/query — ~9.3×.
- **Equality prefix continued by the order columns** (`WHERE a = @x ORDER BY b` against `(a, b)`): columns pinned to a single stable equality value anchor the seek *and* drop out of the sort (they're constant within the result), so the scan positions on `a = @x` via `GetViewBetween(prefix, prefix)` — using a ragged-arity tuple comparer where a short prefix key sorts equal to every full key sharing it — and the trailing key columns emerge already ordered. Touches only the matching group, not the whole table. A folded range on the first order column narrows it further (`WHERE a = @x AND b > 5 ORDER BY b`).
- **Keyset (seek-method) pagination** (`WHERE a > @x OR (a = @x AND b > @y) ORDER BY a, b`, traced `KeysetSeek(table)` alongside `OrderedScan`): the canonical OR-of-AND staircase is recognized (`TryMatchKeyset`, general N columns, `>` for ASC and `<` for a DESC order) as a lexicographic composite cursor and positions the ordered scan just past it via a single exclusive `GetViewBetween` lower bound (upper, for a descending order — the ascending in-range list is then reversed). Recognition reconciles every term's value for a column to one agreed value and bails on any mismatch / NULL / non-stable operand, so the cursor is exactly `(a, …) > (@x, …)` with nothing dropped that the predicate keeps; the OR also stays in the residual WHERE. A deep page measured **~18× faster than the equivalent `OFFSET … FETCH`** (1.2 vs 22.5 ms/query at ~90 % depth over 30 000 rows) — the gap grows with depth, since `OFFSET` streams through every skipped row while the cursor seeks straight to its position. Only matched without a pinned prefix or a same-column range fold (those are the other ways the leading column gets bounded); an equality-pinned tenant prefix plus a keyset cursor is left for later.

The ordered candidate list is still built eagerly (O(rows) addresses) for the non-keyset shapes, so their win is "no sort + materialize only what's taken," not sublinear; a deep `OFFSET` over them streams past the skipped rows too, so its win is just the eliminated sort. The keyset seek is the one that turns deep pagination sublinear in the cursor position.

Every shape rides any leading-prefix index — a PK / UNIQUE key *or* a secondary `CREATE INDEX` — identically: the ordered view is built from the live heap-row bytes keyed by whatever ordinal list the index provides, so there's no clustered-vs-nonclustered distinction (the seek / range / order paths all enumerate `KeyConstraints` then `Indexes`).

ORDER BY elimination is the one index optimization that's **observable if wrong** (it's the only place row order is guaranteed), so the bar to apply it is deliberately high — it declines (keeping the buffered sort) for: a nullable order column (its NULL-key rows aren't in the ordered view, which excludes any tuple with a NULL component), a **mixed-direction** multi-column order (the single ascending-by-value view serves only all-ASC forward or all-DESC reversed — per-column index direction isn't modeled), an order whose column sequence isn't a leading prefix of any key, an expression / ordinal sort key, `DISTINCT`, a SNAPSHOT / RCSI read (the version-chain sweep can't stay ordered), a tx-scoped row-lock plan, or a competing equality / IN / range seek on a leading key column the chosen prefix doesn't consume — including an IN-list on the first order column, where the composite equality seek (`a = @x AND b IN (…) ORDER BY b`) pins one column further than the ordered prefix and so wins.

### UPDATE / DELETE target seeking

A single-table `UPDATE t SET … WHERE …` / `DELETE FROM t WHERE …` narrows its target scan through the same seek cache rather than walking the whole heap. `Selection.SeekMutationTarget(table, where, batch)` builds a minimal single-source view of the base table, runs the **equality** (longest-prefix, IN-list / OR-family, composite) and **single-column range** analysis the `SELECT` path uses, and returns the seek-narrowed `(page, slot, bytes)` candidates — or `null` when the WHERE carries nothing seekable, so the caller keeps its `Heap.EnumerateRowsWithAddress()` full scan. The candidate cores (`TryComputeEqualityCandidates` / `TryComputeRangeCandidates`) are factored out of the query path's `TrySeekByLongestPrefix` / `TrySeekByRange`, which now wrap them with the read-path lock / snapshot materializer; the mutation path wraps the same cores with `MaterializeMutationCandidates` (dedup + tombstone-skip, yielding heap addresses to rewrite).

Two properties make this a pure narrowing with no fidelity cost:

- **Residual predicate.** The mutation loop already re-runs the full WHERE per row (`UPDATE`'s `ResolveOriginal` / `DELETE`'s per-row `where.Run`), so a stale bucket entry or a partly-seekable WHERE (`id = @x AND f(val) = …`) is correct — the seek only narrows which rows the predicate is evaluated against. This is the query path's residual-WHERE contract reused, so no live-key re-verify (unlike the FK path).
- **Unchanged lock footprint.** The mutation acquires its table-level lock once up front and X-locks **only the rows it commits** (`AcquireRowLockTxScoped` at commit), never the rows the scan merely reads. Seeking touches strictly fewer rows but the *committed* set is identical, so the lock footprint is unchanged — no `RowTxScoped` / snapshot gate is needed (the loop reads live addresses, exactly as the scan it replaces does, and the separate snapshot-conflict pass over the version chain is untouched). Positioned mutations (`WHERE CURRENT OF`) leave `where` null and so keep the scan — the cursor already fixed a single row.

Measured: 2 000 point `UPDATE`s by PK over a 20 000-row table ran ~5.4× faster than the same updates filtered on an unindexed column (0.85 vs 4.54 ms/op); the ratio grows with table size, since the scan is O(rows) and the seek amortizes to ~O(1).

### MERGE target seeking (loop inversion)

MERGE's Phase A is `target × source`: for each target row it scans every source row evaluating the `ON` predicate. Its `ON` correlates the target to the *source* row (`t.k = s.k`), so the target has no constant predicate to narrow on — but `t.k` *is* a target column and `s.k` is stable *for a given source row*, which is exactly the **correlated-seek** shape the SELECT path already uses (`allowCorrelatedColumnValue: true`). So when the conditions hold, the loop **inverts** into a match phase: for each source row, `Selection.TryPrepareMergeTargetSeek` seeks the matching target rows (one correlated equality seek per source row, the probe bound through an `outerResolver` over that source row) and groups them by target address into `matchedByTarget` (first-source-wins via source-index order). The full `ON` predicate is re-run per seeked candidate (residual filter: the seek keys on the equality prefix only, so an extra `ON` term or a stale cache entry is dropped there); `sourceMatched` is set here for Phase B. This turns the match work from O(target × source) into O(source × log target).

The apply phase then splits on whether a `WHEN NOT MATCHED BY SOURCE` clause is present:

- **No BY-SOURCE clause** — only matched targets do anything, so iterate `matchedByTarget` alone (sorted by `(page, slot)` to restore heap order) and apply the `WHEN MATCHED` action. Sublinear in target when matches are sparse.
- **BY-SOURCE clause present** — it has to act on every *un*matched target, so one heap pass (`EnumerateRowsWithAddress`) walks all targets in order: a target in `matchedByTarget` takes its precomputed source list (`WHEN MATCHED`), the rest fall to `WHEN NOT MATCHED BY SOURCE`. The inner per-target source loop is gone (the matches were precomputed by seeking), so this is O(target) + the O(source × log target) match phase rather than O(target × source); heap-order interleaving reproduces the scan path's discovery order exactly. `ApplyMergeMatched` is the shared apply helper for both passes (first-source-wins + the Msg 8672 multi-match guard).

Phase B (`WHEN NOT MATCHED BY TARGET` → insert unmatched source rows) is unchanged. Inversion declines (keeping the `target × source` scan) when **either** the target is a **view** (its column names don't map to the base heap the seek source is built from) or the `ON` has no equality on a target leading key / index column — each a safe fallback to identical behavior. Measured: a 5-row upsert batch (`WHEN MATCHED UPDATE … WHEN NOT MATCHED INSERT`) into a 20 000-row PK target ran **~9.1× faster** than the same MERGE against a no-key target that falls back to the scan (4.4 vs 40.0 ms/merge). The BY-SOURCE-clause path wins by the source-row count (the factor the inner loop is dropped): a 3 900-row source reconciled against a 4 000-row PK target with a `WHEN NOT MATCHED BY SOURCE UPDATE` ran **~11.6× faster** than the same against a non-seekable `ON` (617 vs 7 171 ms; the equal PK-revalidation of the updated rows is paid on both sides, so the delta is the match phase alone).

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
