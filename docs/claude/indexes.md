# Indexes

`CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX` + `DROP INDEX` ship, with full grammar coverage for column ordering (ASC / DESC), INCLUDE columns, WHERE filter, and the WITH (options) clause.
The `sys.indexes` + `sys.index_columns` catalog views project rows for PRIMARY KEY constraints, UNIQUE constraints, and CREATE INDEX-declared entries.
Probe-confirmed against SQL Server 2025.

## Grammar

```sql
CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX name
    ON table (col [ASC | DESC] [, …])
    [INCLUDE (col [, …])]
    [WHERE filter]
    [WITH (option = value [, …])]
    [ON <filegroup>];

DROP INDEX [IF EXISTS] name ON table [, name ON table [, …]];
DROP INDEX [IF EXISTS] table.name [, table.name [, …]];   -- deprecated two-part form
```

The deprecated `DROP INDEX table.index` form (also `schema.table.index`) is accepted: the rightmost segment names the index, the remaining left segments name the table.
A missing index still raises **Msg 3701** through the same path as the `name ON table` form; the parser branches on whether an `ON` follows the first parsed object name.

**One clustered index per table**: `CREATE CLUSTERED INDEX` on a table that already carries a clustered index — a clustered PRIMARY KEY / UNIQUE constraint (a default PK is clustered) or a prior clustered index — raises **Msg 1902** (`Cannot create more than one clustered index on table 't'. Drop the existing clustered index '…' before creating another.`), naming the existing clustered index.

The simulator has no B-tree storage, so an index never constrains inserts (UNIQUE aside) and isn't a stored ordered structure.
UNIQUE indexes participate in INSERT / UPDATE / MERGE enforcement alongside `KeyConstraint`.
The `WITH (...)` clause is parsed parens-balanced and discarded — none of `FILLFACTOR` / `PAD_INDEX` / `IGNORE_DUP_KEY` / `ONLINE` / `SORT_IN_TEMPDB` / etc. alter behavior.
The trailing `ON <filegroup>` placement clause (e.g. `ON [PRIMARY]`) is also parsed and discarded — no filegroup model.
The same two trailers are accepted on inline `CONSTRAINT … PRIMARY KEY | UNIQUE` clauses inside CREATE TABLE and on `ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY | UNIQUE (cols)`, plus `) ON [PRIMARY] [TEXTIMAGE_ON [PRIMARY]]` at the end of CREATE TABLE — the full SSMS-scripting verbosity surface.

### Inline indexes in CREATE TABLE

Both inline-index forms are accepted inside CREATE TABLE (probe-confirmed against SQL Server 2025):

```sql
CREATE TABLE t (id int, INDEX ix (id));                             -- table-level
CREATE TABLE t (id int, name varchar(10) INDEX ix NONCLUSTERED);    -- column-level (single column)
CREATE TABLE t (id int PRIMARY KEY NONCLUSTERED, a int INDEX ixa);  -- alongside a PK
```

The parser collects each into a `PendingInlineIndex` (name, `CLUSTERED`/`NONCLUSTERED`, key columns) — the table-level form in `ParseColumnList` (`ParseTableLevelInlineIndex`), the column-level form as an `INDEX` case in the per-column constraint loop.
After the `HeapTable` is built, `AddInlineIndexes` (`Simulation.CreateIndex.cs`) resolves the columns and appends the same `Index` a standalone CREATE INDEX would (catalog metadata + seek acceleration; no UNIQUE / INCLUDE / filter — the inline grammar exposes none).
Column resolution, name-collision (Msg 2714 / 1911 wording via `IndexAlreadyExists` / `IndexColumnMissing`), and one-clustered-per-table (Msg 1902) run inside the CREATE TABLE atomic block, so a bad inline index rolls the table back.
Inline indexes are **CREATE TABLE only** — table variables / table types leave the `INDEX` keyword to the column path, which rejects it.

## Equality-seek acceleration

The acceleration structure is `HeapSeekCache` (`Parser/HeapSeekCache.cs`) — a per-`Heap` index attached through a `ConditionalWeakTable<Heap, HeapSeekCache>` and reached via `HeapSeekCache.For(heap)`.
It's a storage-level accelerator, not a query-planner detail: **the consumers share it** — the query planner (`Selection`'s equality / range / ORDER BY / keyset seeks over `SELECT`), mutation targets (`UPDATE` / `DELETE`'s `WHERE` scan via `Selection.SeekMutationTarget`, and the inverted `MERGE` per-source target seek via `Selection.TryPrepareMergeTargetSeek` — see [UPDATE / DELETE target seeking](#update--delete-target-seeking) and [MERGE target seeking](#merge-target-seeking-loop-inversion)), and constraint enforcement (`Simulation`'s foreign-key parent-existence and cascade lookups; see [`foreign-keys.md`](foreign-keys.md)).
The first two lean on a residual filter — the query planner keeps the matched conjuncts in WHERE, the mutation loop re-runs the full predicate per row — to discard the cache's stale-entry false-positives; the FK path has no residual filter, so it re-verifies each candidate against live bytes (`HeapSeekCache.AnyRowMatches` / `MatchingRows`).
`SqlValueKey` (the bucket key) is likewise a shared top-level type, not nested in `Selection`.

Although there's no B-tree, an index (or PK / UNIQUE key) **does** accelerate reads via an equality seek on its **leading key-column prefix**.
`Selection.Execution.IndexSeek.cs` collects every top-level WHERE conjunct of the shape `indexedColumn = <stable value>` — where the value side is a literal, a variable, an outer/correlated column reference, an arithmetic node (`TwoSidedExpression`) whose operands are all themselves stable (which is how a negative literal arrives: the parser builds `-1` as `0 - 1`, so `col = -1` and `col = @v + 1` stay sargable — a deterministic operator over row-invariant operands is row-invariant), or a `CAST` / `CONVERT` / parenthesization peeling down to one of those (via `Expression.PureConversionOperand`) — then rewrites a single-base-table scan into a hash-index lookup keyed on the longest leading prefix of some index/key whose columns are all covered by those conjuncts.
So `WHERE a = x AND b = y` against an index on `(a, b, …)` keys on the two-column tuple, not just `a`; conjunct order is irrelevant (columns map to the prefix by ordinal).
The longest usable prefix across all keys/indexes wins, which is what keeps a **non-selective leading column** (a bit flag, a low-cardinality FK) from dragging its whole bucket through the residual filter — the full matched prefix is keyed precisely.
Peeling pure conversions matches real SQL Server keeping `col = CAST(<const> AS …)` sargable (probe-confirmed: integer / decimal widenings of a constant or variable still Index Seek; the cross-type case that does *not* seek there is `varchar` column vs `nvarchar` value, which the simulator's `Collation.Resolve` guard already declines).
Every matched conjunct stays in WHERE as a residual filter, so the seek can only narrow the row source, never change results; the per-component key promotion / collation rules mirror the equi-join hash path exactly (`SqlType.Promote` + `CoerceTo` + collation-coercibility `Resolve` guard, applied column-by-column into a multi-value `SqlValueKey`).

A WHERE conjunct of the shape `col IN (v1, v2, …)` — or its equivalent OR-of-equalities `col = v1 OR col = v2 OR …`, which EF Core emits for `Contains(...)` against a small list — decomposes through the same path via `BooleanExpression.TryGetEqualityFamily`.
Every candidate must be a stable value and they must all anchor on the same column (mixed-column ORs like `a = 1 OR b = 2` fall through to scan); a single column's binding then becomes a **multi-value** equality.
Surviving probes are unioned into one promoted type (pairwise `SqlType.Promote` across candidates), NULL candidates are silently dropped (never equal under `=`), and the seek expands across the **cartesian product** of per-column probe arrays — so `a IN (1, 2) AND b = 3` fires two composite-prefix probes against the (a, b) cache and `a IN (1, 2) AND b IN (3, 4)` fires four.
Each tuple is one hash lookup against the same per-Heap cache; a row can only sit in one bucket per probe column, so the unioned candidate stream contains no duplicates.
`NOT IN` is an AND-of-inequalities (not a positive equality family) and declines.

A prefix is bounded at the first key column that either has no equality conjunct or whose value side can't anchor a seek (a `NULL` probe — never equal under `=`; a cross-collation string compare; a promotion/evaluation that throws).
The seek then keys on the shorter usable prefix and the residual WHERE handles the rest: `WHERE a = 1 AND b = @null` seeks on `a` alone, then the residual `b = @null` correctly excludes every candidate.
A range bound on the key column just past the prefix extends the seek one column further — see [Equality-prefix + range continuation](#equality-prefix--range-continuation).
The per-`Heap` cache is keyed by the leading ordinal and remembers the prefix (ordinals + promoted types) it was built for, serving any request that prefix **covers** (leading-prefix match; shorter-arity probes read the ordered view) and rebuilding from a scan only when a request reaches wider or diverges.

This is the path that collapses a **correlated `EXISTS` / `IN` / scalar subquery** (and an unindexed-inner `APPLY`) from O(outer × inner) toward linear: the inner re-executes per outer row, but a per-table cache (keyed on the `Heap`, built lazily on first seek) persists across those calls.
Measured: a correlated `EXISTS` over a 4000 × 16000-row pair dropped from ~1480 ms to ~5 ms, on par with live SQL Server.

The cache is **incrementally maintained**, not rebuilt on every write.
The first seek against a heap builds its buckets from a full scan and activates the heap's bounded *mutation journal* (`Heap.ActivateSeekJournal`); thereafter a write appends a visible-row event (insert / delete / update, carrying the before/after row image) rather than invalidating the cache, and the next seek applies that delta (`Heap.SnapshotSeekJournalSince` → `CacheEntry.Apply`, traced as `CacheReplay`) instead of re-scanning.
This kills the per-mutation warm-up: a *mutate-then-seek* loop over a 20 000-row table dropped from ~5.6 ms/iter to ~0.8 ms/iter (the gap widens with table size — a rebuild is O(rows), a replay is O(delta)).
A never-seeked (write-only) table never activates the journal, so it pays nothing, and the write path's row locking / lock-escalation accounting stay untouched — journaling is a side-append under its own lock, not a change to the mutation itself.

A full rebuild (traced `CacheBuild`) still happens when the requested prefix differs from the cached one, the journal overflowed its cap so the delta can't be replayed (a large bulk mutation trimmed events the cache needed — `MaxSeekJournalEvents` = 512), or the journal was invalidated.
Invalidation is the **rollback / TRUNCATE safety valve**: a rollback rewinds heap state by mutating pages directly (no reversing journal events) and `TRUNCATE` swaps the page list wholesale, so both call `Heap.InvalidateSeekJournal` to force a rebuild from the rewound state (`UndoLog.RollbackTo` invalidates each affected heap once via `UndoEntry.AffectedHeap`).
ALTER TABLE replaces the heap instance, so the new heap starts journal-free.
Correctness rests on the heap staying the single source of truth: every matched equality conjunct remains a residual WHERE filter, so a stale bucket membership is only ever a harmless false-positive (the residual drops it; the materializer also de-dups and skips tombstoned slots), while the add side — insert / update-new-key — recomputes from live row bytes and so never drops a live candidate (the one thing that would change results).
The seek isn't a free pass around concurrency control: under a lock-based plan it routes each seeked candidate row through the same `BatchContext.TouchRowForRead` lock / conflict pipeline the full scan uses, so it acquires locks on only the rows it touches (matching a real index seek's footprint).
It **declines** — falling back to the full scan — for:

- **tx-scoped row-lock plans** (`REPEATABLE READ` / `SERIALIZABLE` / `UPDLOCK` / `HOLDLOCK` …), where the scan deliberately locks every row it reads to end of transaction;
- non-base-table sources (derived tables, table variables, `FOR SYSTEM_TIME`), a WHERE with no equality conjunct on any index's leading column (a range-only predicate is handled separately — see [Range seeks](#range-seeks)), and `NULL` / non-resolvable-collation value sides.

### Snapshot / RCSI seeks (version-aware materialization)

A SNAPSHOT / RCSI reader sees the version visible at its snapshot, which can carry a *different key* than the live heap row — so a live-key-only bucket lookup could miss a row whose key was changed out from under the snapshot.
Rather than declining table-wide whenever the version store is non-empty (which under a busy RCSI workload meant nearly every point lookup full-scanned — a measured ~90–235× regression from a single live version chain), the seek runs and materializes through the version store (`MaterializeSnapshotCandidates`):

- **Bucket candidates** (live rows whose *current* key matched the probe) each resolve to their snapshot-visible version via `VersionStore.ResolveVisibleVersion`, dropping out when not visible.
- **A version-chain sweep** over `HeapTable.RowVersions` adds every slot carrying a chain — those are exactly the rows whose snapshot-visible key can differ from their live key, plus tombstoned slots a pre-delete snapshot still sees (`ResolveTombstonedSlotForSnapshot`).
  Deduplicated by slot against the bucket candidates.

The matched equality conjuncts stay in the residual WHERE, so any candidate whose *resolved* version doesn't actually match the probe (e.g. a live-bucket row whose snapshot-visible version carries the old key) is filtered there — no false positives, and the sweep eliminates false negatives.
The extra cost is O(|`RowVersions`|), which a read-mostly RCSI workload keeps small (the version-store GC trims versions no open snapshot needs); the whole-table scan this replaces *already* walks `RowVersions` in its own second pass, so the version-aware seek is never more expensive than the scan it supplants.
`ResolveSnapshotXidForRead` is called unconditionally regardless of whether the read seeks or scans, for its snapshot-pinning side effects.

### Range seeks

A WHERE with no usable equality seek but a **range bound on the leading key column** of some index or key narrows to a range seek instead of a full scan (`TrySeekByRange`, traced `RangeSeek(table)`).
Recognized shapes (`BooleanExpression.TryGetRangeOperands` / `TryGetBetweenOperands`): `col > v` / `>=` / `<` / `<=` in either operand order (the operator flips when the column is on the right — `v < col` ≡ `col > v`), and `col BETWEEN lo AND hi` (two-sided inclusive).
Two one-sided bounds on the same column combine (`col > 1 AND col < 9`); first-writer-wins per side, so a redundant looser bound stays a residual filter.
The bound value(s) must be stable (the same literal / variable / parameter / correlated-ref / pure-conversion shapes the equality path accepts), so correlated range subqueries re-evaluate the bound per outer row off the shared cache.

The seek reuses the per-`Heap` cache: the leading column's bucket entry gains a **lazily-built, then incrementally-maintained** ordered view (`SortedSet<SqlValueKey>`, single-component), so `GetViewBetween` returns the in-range keys in O(log k + matches).
The sorted view is built on the first range seek for that column and thereafter maintained in lockstep with the hash buckets by the same journal replay (a key joins / leaves it exactly when its bucket appears / empties) — so range seeks inherit the no-warm-up property.
Measured: 2000 selective `BETWEEN` (11-row) point-ranges over a 100 000-row table dropped from ~12.0 ms/query (full scan) to ~0.07 ms/query (~180×).

Bounds promote against the column type the same way equality probes do (upward, so an out-of-domain bound like `int_col < 9999999999` is exact, not truncated); a NULL bound makes every comparison UNKNOWN, so the range matches nothing — a valid empty seek, not a scan.
A promotion / collation-conflict failure declines to the scan.
This leading-column form is the no-equality fallback; an equality-prefix continued by a range takes the extended equality seek instead (next section).
A range on a column that is neither a leading key column nor the continuation column stays residual.

### Equality-prefix + range continuation

A stable range bound on the key column **immediately after** the matched equality prefix extends the seek predicate one column further: `WHERE a = @x AND b BETWEEN @lo AND @hi` against a key on `(a, b, …)` seeks the in-range slice of `a = @x`'s group rather than dragging the whole group through the residual filter.
This mirrors a real index seek's predicate shape exactly (probe-confirmed against SQL Server 2025 plan XML): an equality prefix, then **at most one** range column, everything deeper residual — `a = @x AND c > 5` on `(a, b, c)` seeks width-1 on `a` and leaves the `c` bound residual, because a seek predicate can't skip a key column.
(The live server's other composite trick — folding a *leading-column range plus later-column equality* into tightened lexicographic endpoints, `a BETWEEN @1 AND @2 AND b = @3` → `Start (a,b) ≥ (@1,@3), End (a,b) ≤ (@2,@3)` with `b = @3` still residual — trims only the first and last groups of the scanned range, so the simulator's plain leading-range seek plus residual has identical coverage and doesn't mirror it.)

Mechanically the extension is a composite `OrderedSeek` over the same per-`Heap` cache entry the ORDER BY path builds: the prefix probe tuple plus the bound value form ragged-arity `GetViewBetween` bounds (a missing side falls back to the prefix tuple alone, which sorts equal to every key sharing it).
It composes with IN-list / OR-family prefixes (each cartesian probe tuple fires its own bounded slice), works correlated (the bound may be an outer reference, re-evaluated per outer row — so `EXISTS (… WHERE c.pid = p.id AND c.num > p.threshold)` seeks the slice per outer row), rides the same snapshot / RCSI version-store materializer and lock-check wrapper as the pure equality seek, and serves the **mutation paths** through the shared candidate core (`SeekMutationTarget` for UPDATE / DELETE, `TryPrepareMergeTargetSeek` for MERGE).
Index choice: the longest equality prefix still wins; a range continuation only breaks ties between same-width prefixes (a bucket-size-informed cost model remains future work).
A bound that fails to evaluate (promotion / collation conflict) falls back to the pure equality seek on the same prefix; a NULL bound seeks to empty (the bound conjunct is UNKNOWN for every row and stays residual).
Traced as `PrefixRangeSeek(table)` alongside `Seek` / `SeekWidth` (width still counts only the equality columns).

Because the extended seek requests a wider cache prefix (`(a, b)`) than the pure equality lookup (`(a)`), the cache reuses by **covering prefix** rather than exact match: an entry serves any request whose ordinals + promoted types are a leading prefix of its own.
Entries therefore only ever widen — alternating `a = @x` and `a = @x AND b > @y` workloads share one entry instead of rebuilding per query, and a journal-overflow rebuild keeps the entry's own (widest) prefix.
A shorter-arity equality probe is answered from a lazily-built per-arity **narrow hash view** (prefix key → unioned rids, maintained in lockstep with the buckets by the same replay / add / remove hooks — answering from the ordered view instead measured ~1.5× slower on a 500-row group lookup), so narrow probes stay O(1); the FK enforcement path inherits the same view (then live-byte verifies as before).

The slice is **cost-gated by group size** (`HeapSeekCache.RangeSliceMinGroupRids` = 256): enumerating a `SortedSet` view pays per-node comparer calls, which for string keys costs about as much as the residual's per-row filter — a 211-rid `nvarchar` group with a 144-key slice measured ~1.3× *slower* sliced than plainly group-seeked.
So a group at or under the threshold returns whole (`PrefixRangeGroup` trace; the residual WHERE applies the range — exactly the pre-continuation behavior), and only a larger group takes the ordered slice (`PrefixRangeSlice`).
Both shapes over-approximate at worst, so the threshold is pure cost policy.

Measured — synthetic (100 000 rows, PK `(cust, seq)`, 200 groups × 500 rows): `cust = @c AND seq BETWEEN @lo AND @lo+10` dropped from ~0.31 ms/query (equality seek on `cust` + 500-row residual filter) to ~0.03 ms/query (~10×, growing with group size).
Real data (WWI `Sales.OrderLines`, index `(StockItemID, PickingCompletedWhen)`, hot item ≈ 5 000 rows): a six-month picking window dropped ~6.5 → ~1.2 ms/query (~5.6×).
The pure `cust = @c` group lookup holds parity before/after the entry widens, and the AW `Person.Person` name-browse (`LastName = @l AND FirstName >= @a AND FirstName < @b`, 211-row group under RCSI) holds baseline parity via the group fallback.

All three single-table projectors — non-aggregate (`ProjectSqlRows`), aggregate (`BuildAggregateProjectionRows`), and window (`ProjectWindowedRows`) — narrow a single-base-table source through the seek, so `SELECT COUNT(*) … WHERE indexedcol = x` and `SELECT … OVER (…) FROM t WHERE indexedcol = x` both seek like their non-aggregate counterpart (without this the window projector silently full-scanned even when its WHERE was perfectly sargable, the regression that made running-total-per-parent EF queries scan the table).
They also push single-source WHERE equality predicates onto the **leftmost** source of a multi-source FROM before the join (`NarrowLeftmostJoinSource` — the leftmost is always preserved, so this never changes outer-join semantics), which shrinks the join's driving rowset.
The INNER / LEFT equi-join operator then **seeks the inner side per outer row** when that rowset is small and the inner is indexed on the join key — see [`joins.md`](joins.md).

### ORDER BY elimination

A `SELECT` from a single base table whose ORDER BY matches the **key order of some index / key** skips the buffer-and-sort entirely: `TryApplyOrderedScan` (traced `OrderedScan(table)`) enumerates the matching ordered view (the same `SortedSet<SqlValueKey>` the equality / range seeks build, ascending or — for an all-DESC order — reversed) and routes to the streaming projector.
The residual WHERE and projection preserve order, so the sort (and the full-result buffer) is gone, and `TOP` / a shallow `OFFSET` then materialize only the rows they need.
Three shapes qualify, all reusing one composite ordered view:

- **Single NOT-NULL leading key column** (`ORDER BY id`), optionally same-column range-narrowed (`WHERE id > 100 ORDER BY id` stays narrowed *and* ordered).
  `SELECT TOP 50 … ORDER BY id` over 100 000 rows dropped from ~65 ms/query (materialize + sort the whole table) to ~7 ms/query — ~8.7×.
- **Multi-column leading prefix** (`ORDER BY a, b` against a key on `(a, b, …)`), every order column NOT NULL.
  `SELECT TOP 50 … ORDER BY a, b` over 100 000 composite-key rows dropped from ~111 ms/query (full buffered sort — the previous single-column-only path declined this outright) to ~12 ms/query — ~9.3×.
- **Equality prefix continued by the order columns** (`WHERE a = @x ORDER BY b` against `(a, b)`): columns pinned to a single stable equality value anchor the seek *and* drop out of the sort (they're constant within the result), so the scan positions on `a = @x` via `GetViewBetween(prefix, prefix)` — using a ragged-arity tuple comparer where a short prefix key sorts equal to every full key sharing it — and the trailing key columns emerge already ordered.
  Touches only the matching group, not the whole table.
  A folded range on the first order column narrows it further (`WHERE a = @x AND b > 5 ORDER BY b`).
- **Keyset (seek-method) pagination** (`WHERE a > @x OR (a = @x AND b > @y) ORDER BY a, b`, traced `KeysetSeek(table)` alongside `OrderedScan`): the canonical OR-of-AND staircase is recognized (`TryMatchKeyset`, general N columns, `>` for ASC and `<` for a DESC order) as a lexicographic composite cursor and positions the ordered scan just past it via a single exclusive `GetViewBetween` lower bound (upper, for a descending order — the ascending in-range list is then reversed).
  Recognition reconciles every term's value for a column to one agreed value and bails on any mismatch / NULL / non-stable operand, so the cursor is exactly `(a, …) > (@x, …)` with nothing dropped that the predicate keeps; the OR also stays in the residual WHERE.
  A deep page measured **~18× faster than the equivalent `OFFSET … FETCH`** (1.2 vs 22.5 ms/query at ~90 % depth over 30 000 rows) — the gap grows with depth, since `OFFSET` streams through every skipped row while the cursor seeks straight to its position.
  Only matched without a pinned prefix or a same-column range fold (those are the other ways the leading column gets bounded); an equality-pinned tenant prefix plus a keyset cursor is left for later.

The ordered candidate list is still built eagerly (O(rows) addresses) for the non-keyset shapes, so their win is "no sort + materialize only what's taken," not sublinear; a deep `OFFSET` over them streams past the skipped rows too, so its win is just the eliminated sort.
The keyset seek is the one that turns deep pagination sublinear in the cursor position.

Every shape rides any leading-prefix index — a PK / UNIQUE key *or* a secondary `CREATE INDEX` — identically: the ordered view is built from the live heap-row bytes keyed by whatever ordinal list the index provides, so there's no clustered-vs-nonclustered distinction (the seek / range / order paths all enumerate `KeyConstraints` then `Indexes`).

ORDER BY elimination is the one index optimization that's **observable if wrong** (it's the only place row order is guaranteed), so the bar to apply it is deliberately high — it declines (keeping the buffered sort) for: a nullable order column (its NULL-key rows aren't in the ordered view, which excludes any tuple with a NULL component), a **mixed-direction** multi-column order (the single ascending-by-value view serves only all-ASC forward or all-DESC reversed — per-column index direction isn't modeled), an order whose column sequence isn't a leading prefix of any key, an expression / ordinal sort key, `DISTINCT`, a SNAPSHOT / RCSI read (the version-chain sweep can't stay ordered), a tx-scoped row-lock plan, or a competing equality / IN / range seek on a leading key column the chosen prefix doesn't consume — including an IN-list on the first order column, where the composite equality seek (`a = @x AND b IN (…) ORDER BY b`) pins one column further than the ordered prefix and so wins.

### UPDATE / DELETE target seeking

A single-table `UPDATE t SET … WHERE …` / `DELETE FROM t WHERE …` narrows its target scan through the same seek cache rather than walking the whole heap.
`Selection.SeekMutationTarget(table, where, batch)` builds a minimal single-source view of the base table, runs the **equality** (longest-prefix, IN-list / OR-family, composite) and **single-column range** analysis the `SELECT` path uses, and returns the seek-narrowed `(page, slot, bytes)` candidates — or `null` when the WHERE carries nothing seekable, so the caller keeps its `Heap.EnumerateRowsWithAddress()` full scan.
The candidate cores (`TryComputeEqualityCandidates` / `TryComputeRangeCandidates`) are factored out of the query path's `TrySeekByLongestPrefix` / `TrySeekByRange`, which wrap them with the read-path lock / snapshot materializer; the mutation path wraps the same cores with `MaterializeMutationCandidates` (dedup + tombstone-skip, yielding heap addresses to rewrite).

Two properties make this a pure narrowing with no fidelity cost:

- **Residual predicate.**
  The mutation loop already re-runs the full WHERE per row (`UPDATE`'s `ResolveOriginal` / `DELETE`'s per-row `where.Run`), so a stale bucket entry or a partly-seekable WHERE (`id = @x AND f(val) = …`) is correct — the seek only narrows which rows the predicate is evaluated against.
  This is the query path's residual-WHERE contract reused, so no live-key re-verify (unlike the FK path).
- **Unchanged lock footprint.**
  The mutation acquires its table-level lock once up front and X-locks **only the rows it commits** (`AcquireRowLockTxScoped` at commit), never the rows the scan merely reads.
  Seeking touches strictly fewer rows but the *committed* set is identical, so the lock footprint is unchanged — no `RowTxScoped` / snapshot gate is needed (the loop reads live addresses, exactly as the scan it replaces does, and the separate snapshot-conflict pass over the version chain is untouched).
  Positioned mutations (`WHERE CURRENT OF`) leave `where` null and so keep the scan — the cursor already fixed a single row.

Measured: 2 000 point `UPDATE`s by PK over a 20 000-row table ran ~5.4× faster than the same updates filtered on an unindexed column (0.85 vs 4.54 ms/op); the ratio grows with table size, since the scan is O(rows) and the seek amortizes to ~O(1).

### MERGE target seeking (loop inversion)

MERGE's Phase A is `target × source`: for each target row it scans every source row evaluating the `ON` predicate.
Its `ON` correlates the target to the *source* row (`t.k = s.k`), so the target has no constant predicate to narrow on — but `t.k` *is* a target column and `s.k` is stable *for a given source row*, which is exactly the **correlated-seek** shape the SELECT path already uses (`allowCorrelatedColumnValue: true`).
So when the conditions hold, the loop **inverts** into a match phase: for each source row, `Selection.TryPrepareMergeTargetSeek` seeks the matching target rows (one correlated equality seek per source row, the probe bound through an `outerResolver` over that source row) and groups them by target address into `matchedByTarget` (first-source-wins via source-index order).
The full `ON` predicate is re-run per seeked candidate (residual filter: the seek keys on the equality prefix only, so an extra `ON` term or a stale cache entry is dropped there); `sourceMatched` is set here for Phase B.
This turns the match work from O(target × source) into O(source × log target).

The apply phase then splits on whether a `WHEN NOT MATCHED BY SOURCE` clause is present:

- **No BY-SOURCE clause** — only matched targets do anything, so iterate `matchedByTarget` alone (sorted by `(page, slot)` to restore heap order) and apply the `WHEN MATCHED` action.
  Sublinear in target when matches are sparse.
- **BY-SOURCE clause present** — it has to act on every *un*matched target, so one heap pass (`EnumerateRowsWithAddress`) walks all targets in order: a target in `matchedByTarget` takes its precomputed source list (`WHEN MATCHED`), the rest fall to `WHEN NOT MATCHED BY SOURCE`.
  The inner per-target source loop is gone (the matches were precomputed by seeking), so this is O(target) + the O(source × log target) match phase rather than O(target × source); heap-order interleaving reproduces the scan path's discovery order exactly.
  `ApplyMergeMatched` is the shared apply helper for both passes (first-source-wins + the Msg 8672 multi-match guard).

Phase B (`WHEN NOT MATCHED BY TARGET` → insert unmatched source rows) is unchanged.
Inversion declines (keeping the `target × source` scan) when **either** the target is a **view** (its column names don't map to the base heap the seek source is built from) or the `ON` has no equality on a target leading key / index column — each a safe fallback to identical behavior.
Measured: a 5-row upsert batch (`WHEN MATCHED UPDATE … WHEN NOT MATCHED INSERT`) into a 20 000-row PK target ran **~9.1× faster** than the same MERGE against a no-key target that falls back to the scan (4.4 vs 40.0 ms/merge).
The BY-SOURCE-clause path wins by the source-row count (the factor the inner loop is dropped): a 3 900-row source reconciled against a 4 000-row PK target with a `WHEN NOT MATCHED BY SOURCE UPDATE` ran **~11.6× faster** than the same against a non-seekable `ON` (617 vs 7 171 ms; the equal PK-revalidation of the updated rows is paid on both sides, so the delta is the match phase alone).

## Storage

`HeapTable.Indexes` is a mutable `List<Index>`, populated by CREATE INDEX and trimmed by DROP INDEX.
The `Index` record carries:

- `Name`, `ObjectId` (allocated from the per-database counter).
- `IsUnique` — drives enforcement.
- `IsClustered` — doesn't alter storage (every table is a flat heap regardless), but **drives index-id allocation**: a clustered index takes `index_id = 1` / `type_desc = CLUSTERED` and suppresses the HEAP row (see [Index-id allocation](#index-id-allocation)).
  `KeyConstraint.IsClustered` is the constraint-side equivalent (PK defaults clustered, UNIQUE defaults nonclustered).
- `KeyColumns[]` — each entry pairs a storage ordinal with the ASC / DESC flag.
- `IncludedColumns[]` — storage ordinals for INCLUDE columns; catalog-only.
- `Filter` (BooleanExpression?) — only honored on UNIQUE indexes.
- `FilterDefinition` (string?) — normalized predicate text for `sys.indexes.filter_definition`, rendered at CREATE INDEX time by `BooleanExpression.RenderFilterDefinition` (see [Filtered-index `filter_definition`](#filtered-index-filter_definition)).

PRIMARY KEY / UNIQUE constraints stay in `HeapTable.KeyConstraints`; sys.indexes synthesizes rows for them alongside the user indexes.

## Enforcement

### INSERT side

`EnforceUniqueIndexes` runs after `EnforceKeyConstraints` in the INSERT path:

1. Skip entirely when no UNIQUE entry exists on the table.
2. For each UNIQUE entry: if the entry has a `Filter`, evaluate it against the new row's full-column values; skip the check entirely when the filter doesn't evaluate `true` (false or UNKNOWN both skip — mirrors SQL Server's filtered-unique-index semantic).
3. Linear-scan the heap.
   For each existing row:
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

A filtered UNIQUE index only constrains rows where `filter` evaluates `true`.
Rows where the filter is `false` or `UNKNOWN` (any NULL operand in the predicate, by three-valued logic) bypass the check entirely.
This is the standard SQL Server behavior — load-bearing for application patterns like "unique among non-archived rows" or "unique when status is active."

### Filtered-index `filter_definition`

At CREATE INDEX time the parsed `WHERE` predicate is rendered into SQL Server's normalized `sys.indexes.filter_definition` form by `BooleanExpression.RenderFilterDefinition(batch)` (the column was always wired to `Index.FilterDefinition`; before, that field was never populated).
The renderer is a `private protected virtual TryAppendFilterDefinition` on `BooleanExpression`, overridden on the renderable filtered-predicate grammar only — `AND`, the six comparisons (`= <> > >= < <=` via a `FilterOperator` token), `IS [NOT] NULL`, and positive `IN`.
The canonical form (probe-confirmed verbatim against SQL Server 2025):

- whole predicate wrapped in one outer paren pair; columns bracketed (`[status]`); comparison operators space-free (`[status]=(1)`); `AND` / `IS [NOT] NULL` / `IN` uppercase-spaced;
- numeric constants parenthesized in invariant culture preserving the **literal's** scale (`amount > 10.5` → `([amount]>(10.5))`, `nm = 0.10` → `([nm]=(0.10))` — the literal's own type, not the column's);
- string constants quoted, N-prefixed when the literal is national (`uname = N'abc'` → `([uname]=N'abc')`);
- a negative literal folds correctly — the parser builds `-1` as `0 - 1`, and operand rendering constant-folds the value side via `Run`, so `x = -1` → `([x]=(-1))`;
- `IN` renders each element parenthesized inside the list (`status in (1,2,3)` → `([status] IN ((1), (2), (3)))`).

Any node outside that grammar returns `false`, so `RenderFilterDefinition` yields NULL and the catalog reports NULL with `has_filter` still set — those shapes (`OR`, `NOT`, `BETWEEN`, function calls) are exactly the ones a real server rejects at CREATE for a filtered index, so a NULL there never hides a definition a real server would have stored.
(Two rare literal-typing corners still diverge — see [Fidelity gaps](#fidelity-gaps).)

## Existing-data validation at CREATE

`ValidateExistingRowsForUniqueIndex` linear-scans the heap building a set of (filter-included) key tuples, raising Msg 1505 on the first duplicate.
Non-UNIQUE CREATE INDEX skips this entirely.

## DROP INDEX

`DROP INDEX name ON table` resolves the target table, then walks `HeapTable.Indexes` for a name match.
Three rejection paths:

- Missing parent table: Msg 3701 State 6 (`Cannot drop the index 'dbo.t.ix', because it does not exist or you do not have permission.`).
  `IF EXISTS` suppresses.
- Index name matches a PRIMARY KEY or UNIQUE constraint: Msg 3723 (`An explicit DROP INDEX is not allowed on index 'dbo.t.ix'. It is being used for PRIMARY KEY constraint enforcement.`).
  The PK/UQ kind word is interpolated (`PRIMARY KEY` or `UNIQUE`).
  `IF EXISTS` does NOT suppress — real SQL Server's behavior matches.
- Index not found on the resolved table: Msg 3701 State 7.
  `IF EXISTS` suppresses.

Multi-target `DROP INDEX ix1 ON t1, ix2 ON t2` resolves each entry independently in declaration order; any error short-circuits with whatever drops already happened persisted.
(Real SQL Server has the same behavior — DROP INDEX is not atomic across the comma list.)

## Indexed views

`CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED] INDEX … ON <view>` records an indexed (materialized) view.
`CREATE INDEX` resolves the target as a table first; a table miss retries as a view (`CreateIndexOnView`, `Simulation/Simulation.IndexedViews.cs`).
The simulator never materializes the view — it always expands it at read time (real SQL Server's Enterprise auto-matching of a query to an indexed view isn't modeled either, and results are identical because the view is always expanded), so an indexed view is catalog surface + **live DML uniqueness enforcement** rather than stored rows.

### Storage shape

View indexes reuse `Storage.Index` on a new `View.Indexes` list.
A view has no heap and no storage ordinals, so a view index's key / INCLUDE `IndexKeyColumn` ordinals are **view OUTPUT-column ordinals** — the view row bytes are encoded in `View.OutputColumns` order, so the output ordinal doubles as the storage ordinal (for enforcement decode) and the column ordinal (for `sys.index_columns.column_id = ordinal + 1`, matching `sys.columns` of the view).
`View.IndexIdentities()` is the view analog of `HeapTable.IndexIdentities()`: a view is never a heap (no synthetic index_id-0 row — probe-confirmed: an ordinary view has zero `sys.indexes` rows), the clustered index takes index_id 1 / CLUSTERED, others take 2..N in object-id order.

### Create-time gates (probe-confirmed order, SQL Server 2025)

Applied in this exact order:

1. **Msg 1939** — view not `WITH SCHEMABINDING` (`Cannot create index on view '<leaf>' because the view is not schema bound.`; uses the view's **leaf** name, unlike the two below which schema-qualify).
   `View.IsSchemaBound` is captured at CREATE VIEW time.
2. **Msg 1941** — a **non-unique CLUSTERED** index (`Cannot create nonunique clustered index on view '<schema.view>' … only unique clustered indexes are allowed.`).
   Fires before 1940.
3. **Msg 1940** — the view has no unique clustered index yet **and** this index isn't unique-clustered (`Cannot create index on view '<schema.view>'. It does not have a unique clustered index.`) — i.e. the first index on a view must be UNIQUE CLUSTERED.
   A unique *nonclustered* first index also hits 1940.

A key column not in the view's output → **Msg 1911** (shared "table, index or view" wording).
At CREATE the current view rows are evaluated once and checked for duplicates → **Msg 1505** on a collision (same factory / rendering as the heap-table create-time path).

### DML enforcement (Msg 2601)

Each base table the view references gets the view registered on its `HeapTable.DependentIndexedViews` (collected at CREATE INDEX time by re-parsing the body under a `BatchContext.DependencySink` that records every resolved base table + nested schema-bound view).
After an INSERT or UPDATE applies its heap writes, `EnforceIndexedViews(mutatedTable, batch)` re-evaluates each dependent view (full re-evaluation per statement — the accepted cost) and checks every UNIQUE index for a duplicate key, raising **Msg 2601** naming the schema-qualified view + index and rendering the key (`Cannot insert duplicate key row in object 'schema.view' with unique index 'ix' …` — same text on INSERT and UPDATE).
The violation throws inside the mutation body, so `RunMutation`'s undo log rolls the statement back (statement atomicity).
The hook is zero-cost (`DependentIndexedViews.Count == 0` guard) for the overwhelmingly common no-indexed-view case.

The hook is wired on the INSERT and UPDATE paths.
**MERGE** into an indexed-view base table isn't hooked (a niche shape — AW's indexed-view bases are never MERGE targets); it would need the same post-apply call in `Simulation.Merge.cs`.

**DELETE is deliberately not enforced** (verified): a valid indexed view is an inner-join / aggregate projection, so removing base rows can only remove or reduce view rows — never create a new duplicate key.
(The simulator doesn't enforce real's determinism / `COUNT_BIG(*)` / GROUP BY battery, so a user could in principle build a shape where this reasoning fails; AW needs none of it — see Fidelity gaps.)

`FROM <view> WITH (NOEXPAND)` is accepted (it's in the table-hint accept-list — see [`query-hints.md`](query-hints.md)); results are identical since the simulator always expands.

### Catalog surface

View indexes surface through `sys.indexes` (index_id 1 / CLUSTERED / is_unique = 1 / is_primary_key = 0 / is_unique_constraint = 0, no HEAP row), `sys.index_columns` (key columns keyed on the view output ordinal), `sys.stats` + `sys.stats_columns` (one index-backed stat per view index, stats_id = index_id).
`is_schema_bound` surfaces through `sys.sql_modules` / `sys.all_sql_modules` and `OBJECTPROPERTY(id, 'IsSchemaBound')` / `OBJECTPROPERTYEX`.
`sys.partitions` / `sys.allocation_units` / `sys.dm_db_partition_stats` are **not** extended to view indexes (those read heap page counts; real reports a partitions row carrying the materialized view row count, which the simulator doesn't store) — DacFx's index export doesn't need them, and AW re-imports cleanly without them.

## Catalog surface

### `sys.indexes` — 24-column probe-confirmed shape

One row per (table, index), with ids allocated by the single authority described in [Index-id allocation](#index-id-allocation):

- **Clustered entry** (clustered PK / UNIQUE constraint or `CREATE CLUSTERED INDEX`) at `index_id = 1`, `type = 1`, `type_desc = CLUSTERED`.
  `is_primary_key` / `is_unique` / `is_unique_constraint` reflect the backing object (a non-unique `CREATE CLUSTERED INDEX` is `is_unique = 0`).
- **HEAP row** (only when the table has no clustered index) at `index_id = 0`, `type = 0`, `type_desc = HEAP`, `name = NULL`.
  Matches SQL Server's "the table itself is the heap" semantic.
- **UNIQUE constraints** (nonclustered) at `index_id ≥ 2`, `type_desc = NONCLUSTERED`, `is_unique = 1`, `is_unique_constraint = 1`.
- **CREATE UNIQUE INDEX** at `index_id ≥ 2`, `type_desc = NONCLUSTERED`, `is_unique = 1`, `is_unique_constraint = 0`.
- **Non-UNIQUE CREATE INDEX** and a **NONCLUSTERED PRIMARY KEY** at `index_id ≥ 2`, `type_desc = NONCLUSTERED`.

### Index-id allocation

`HeapTable.IndexIdentities()` is the **single source of truth** every index-id consumer reads — `sys.indexes` / `sys.index_columns` / `sys.stats` / `sys.stats_columns` / `sys.partitions` / `sys.allocation_units` / `sys.dm_db_partition_stats` (through the shared `EnumerateTableIndexIdentities` flattening), `sys.key_constraints.unique_index_id`, and `INDEX_COL` / `INDEXKEY_PROPERTY` / `STATS_DATE` (through `IndexLookup.ResolveByIndexId`).
It returns the table's canonical `IndexIdentity` rows — `(index_id, type, name, KeyConstraint? Constraint, Index? Index)` — with SQL-Server-exact allocation (probe-confirmed against SQL Server 2025):

- The single **clustered** entry — a clustered PK / UNIQUE constraint (`KeyConstraint.IsClustered`) or a `CREATE CLUSTERED INDEX` (`Index.IsClustered`), whichever has the lowest object id — takes `index_id = 1`, `type = 1`, and **suppresses the HEAP row**.
  The clustered index is always id 1 regardless of creation order (a `CREATE CLUSTERED INDEX` added after nonclustered indexes still lands at 1; those keep their ids).
- With no clustered entry the table is a **heap**: one synthetic row at `index_id = 0`, `type = 0`, no backing object.
  Nonclustered ids on a heap still start at 2 — index_id 1 (the clustered slot) is never reused.
- Every remaining (nonclustered) constraint / index — including a NONCLUSTERED PK — takes `index_id = 2..N`, `type = 2`, in object-id (declaration) order (the simulator's `AllocateObjectId` is monotonic, so this matches SQL Server's declaration-order behavior).

A PK defaults **clustered** (unless declared `NONCLUSTERED`); a UNIQUE constraint defaults **nonclustered** (unless declared `CLUSTERED`) — captured at parse time (`ParseInlineKeyKindAndModifiers` → `KeyConstraint.IsClustered`) across inline column constraints, table-level constraints, and `ALTER TABLE ADD CONSTRAINT` (the shape the bacpac loader emits).
At most one clustered index exists per table, enforced on every path: `CREATE INDEX` and `ALTER TABLE ADD CONSTRAINT … CLUSTERED` raise **Msg 1902 State 3** naming the existing clustered index, and two CLUSTERED constraints in one CREATE TABLE raise **Msg 8112** instead (real's distinct message for the case where neither entry exists yet to name). Two PRIMARY KEYs — both clustered by default — report Msg 8110 ahead of either, probe-confirmed.

`compression_delay` is **NULL** on every row: it carries a minute-delay only for columnstore indexes (unmodeled), and is NULL for every rowstore index (probe-confirmed).
SMO's index-scripting query reads it as `CAST(i.compression_delay AS int)` with no `ISNULL` wrapper.

### `sys.index_columns` — 10-column probe-confirmed shape

One row per (index, column):

- **KEY columns**: `key_ordinal = 1..N`, `index_column_id = 1..N`, `is_included_column = 0`.
- **INCLUDE columns**: `key_ordinal = 0`, `index_column_id = N+1..`, `is_included_column = 1`.
- HEAP entries (index_id = 0) don't appear — real SQL Server's catalog omits them.

`is_descending_key` reflects the per-column DESC flag from CREATE INDEX.
`column_id` is the 1-based full-column ordinal from `sys.columns` (mapped back from the storage ordinal stored on the index).

## `DBCC SHOW_STATISTICS(<table>, <stat>) WITH HISTOGRAM`

DacFx's `sqlpackage /Action:Export` runs one `dbcc show_statistics(N'[schema].[table]', N'<index-or-stat-name>') with histogram` per table (using the PK / clustered-index statistic name) before bulk-reading it, to chunk the table into extraction ranges — so the DATA phase of a bacpac export needs this parsed.
`TryParseShowStatistics` (`Simulation/Simulation.Dbcc.cs`) peeks past `DBCC`, restoring the cursor on any other subcommand.
Both argument forms real accepts are handled: a `N'...'` string literal whose content is a 1- / 2-part bracketed name (DacFx's form, parsed with the same `ObjectId.TryParseObjectName` seam `OBJECT_ID` uses) and a bare dotted / bracketed identifier (`BatchContext.ParseObjectName`).
The statement parses mid-batch (DacFx precedes it with a `SELECT TOP 1` probe in the same batch).

The named statistic resolves against the table's index-backed stats via the canonical [`IndexIdentities()`](#index-id-allocation) allocator (a heap identity — null name — can't match); the leading key column is the first `KeyConstraint.StorageOrdinals` / `Index.KeyColumns` ordinal.
The result set is the probe-confirmed 5 columns: `RANGE_HI_KEY` (typed as the **leading key column's own type** — `int` for an int PK, `datetime2` / `nvarchar` for those keys, reaching real SqlClient through the standard TDS codecs), `RANGE_ROWS` `real`, `EQ_ROWS` `real`, `DISTINCT_RANGE_ROWS` `bigint`, `AVG_RANGE_ROWS` `real`.

**Histogram content**: the simulator scans the heap once (`Heap.EnumerateRows` + the array-typed `RowDecoder.DecodeColumn` fast path over `StoredColumns`), groups by distinct non-null leading-key value, sorts, and emits one step per distinct value up to 200 steps — beyond that, 200 boundary steps evenly spaced over the sorted distinct values, with `RANGE_ROWS` / `DISTINCT_RANGE_ROWS` folded from the skipped values between adjacent boundaries and `AVG_RANGE_ROWS` = `RANGE_ROWS / DISTINCT_RANGE_ROWS` (**1** when there are no range rows, matching real's convention — probe-confirmed).
The **MIN value is always the first step and MAX the last**, matching real's histogram envelope — load-bearing for DacFx, whose bacpac-export chunker interpolates boundary parameters between adjacent steps and overflows its arithmetic client-side (`Double` → `Int32` conversion failure) when the MIN anchor is missing.
Step *placement* still diverges from real's sampled max-diff algorithm; the values are honest and self-consistent with `COUNT(*)` / `MIN` / `MAX`.
An empty table yields a 0-row result set.
Errors mirror real: unresolvable table → Msg 2501, unknown statistic → Msg 2767, NULL / unparseable argument → Msg 2560 (all probe-confirmed class/state).
Only `WITH HISTOGRAM` is modeled — the no-`WITH` three-result-set form and every other option (`STAT_HEADER` / `DENSITY_VECTOR` / `STATS_STREAM` / `NO_INFOMSGS` combinations) raise `NotSupportedException` naming the option.
`STATS_STREAM` (the serialized histogram blob SMO's `Statistic.Stream` reads) remains a deferred gap — see [`backlog.md`](backlog.md).

## EF Migrations integration

EF Core's SqlServer provider emits `CREATE INDEX` (and `CREATE UNIQUE INDEX` for `HasIndex().IsUnique()`) during `EnsureCreated` and during migrations' `Up()` methods.
With the simulator:

- Index creation parses + emits a `sys.indexes` row — `EnsureCreated`'s introspection (which reads sys.indexes back) sees the expected shape.
- `HasIndex().HasFilter("...")` emits a `WHERE` clause — the predicate is captured and honored for UNIQUE indexes (filter-aware uniqueness).
- Non-UNIQUE indexes are no-ops at the storage layer (no query acceleration), but their presence in `sys.indexes` keeps EF Migrations introspection happy.

## Indexed-view qualifying battery

Real SQL Server accepts *any* view body at CREATE VIEW and applies the qualifying rules at **CREATE INDEX** (probe-confirmed: every shape below creates as a view without complaint and fails only when indexed).
The simulator matches that placement — `Simulation.IndexedViews.cs`'s `EnforceIndexedViewQualifies` runs after the 1939 / 1941 / 1940 gates and re-parses the view's stored body with an `IndexedViewShape` collector installed (`ParserContext.IndexedViewShapeCollector`, the same collector pattern aggregates / windows / sequences use, so the recording sites are null checks on the normal path).

| Shape | Msg | State |
|---|---|---|
| `DISTINCT` | 10100 | 1 |
| `TOP` / `OFFSET` / `FETCH` | 10101 | 1 |
| LEFT / RIGHT / FULL join | 10113 | 1 |
| `UNION` / `INTERSECT` / `EXCEPT` | 10116 | 1 |
| `AVG` / `MIN` / `MAX` / `STDEV*` / `VAR*` (named in the text) | 10125 | 1 |
| Subquery at any depth | 10127 | 1 |
| `COUNT(*)` | 10136 | 1 |
| `GROUP BY` without `COUNT_BIG(*)` | 10138 | 1 |
| `SUM` over a nullable expression | 8662 | **0** |
| Nondeterministic built-in | 1949 | 1 |
| Self-join | 1947 | 1 |

Wording is verbatim, including real's **inconsistent quoting**: 10116 / 10138 / 1949 single-quote the view name where the rest use double quotes, and 8662 alone names the *index* as well as the view and carries State 0.
The view is database-qualified (`db.schema.view`) throughout, and Msg 1949 lower-cases the function name regardless of how it was written.

Nondeterminism is a closed set of built-ins (`GETDATE` / `GETUTCDATE` / `SYSDATETIME` / `SYSUTCDATETIME` / `SYSDATETIMEOFFSET` / `NEWID` / `NEWSEQUENTIALID` / `RAND`) recorded at `ResolveBuiltIn`, so a reference at any nesting depth is caught.
Aggregates outside the disallowed set (`STRING_AGG` and friends) are **left alone** rather than guessed at — an unprobed rejection would be the over-restrictive direction.
`SUM` nullability reuses `Expression.ResultIsNullable`, the same rule that drives result-metadata nullability, with an unresolvable column treated as nullable.

Shapes that keep indexing cleanly: inner-join projections (the form AdventureWorks' two indexed views take), `SUM` + `COUNT_BIG` grouped views over NOT NULL columns, and filtered projections.

## Fidelity gaps

- **`filter_definition` edge cases**: the column is rendered (see [Filtered-index `filter_definition`](#filtered-index-filter_definition)) and byte-matches SQL Server across the common filtered grammar, but two literal-typing corners diverge: an integer literal larger than `int` range renders `(5000000000)` where SQL Server types it as `numeric` and renders `(5000000000.)` (trailing dot), and a scale-0 decimal literal likewise omits the trailing dot.
  Both are rare in filtered predicates.
  A predicate the simulator accepts but can't render canonically (an `OR`, `NOT`, `BETWEEN`, or function call — all of which a real server *rejects* at CREATE for a filtered index) reports `filter_definition` NULL with `has_filter` still set.
- **CLUSTERED keyword drives allocation, not storage**: `CREATE CLUSTERED INDEX` (and a clustered PK / `UNIQUE CLUSTERED` constraint) correctly reports `index_id = 1` / `type_desc = CLUSTERED` and suppresses the HEAP row (see [Index-id allocation](#index-id-allocation)), but there's no row-ordered storage behind it — clustering never changes scan/seek behavior.
- *(the one-clustered-per-table rule now covers every path — see [One clustered index per table](#grammar). The constraint paths raise **Msg 1902 State 3** naming the existing clustered index, except an all-inline CREATE TABLE pair, which real gives its own **Msg 8112** since neither entry exists yet to name; the multiple-PRIMARY-KEY check (Msg 8110) outranks both.)*
- **WITH options ignored**: `FILLFACTOR`, `IGNORE_DUP_KEY`, `ONLINE`, `MAXDOP`, etc. all parse but have no behavior.
  `IGNORE_DUP_KEY = ON` is the one with observable fallout: real skips the duplicate row and continues (probe-confirmed — the surviving rows insert, `@@ROWCOUNT` counts only those, and a severity-10 Msg 3604 `Duplicate key was ignored.` rides the info stream once per statement), while the simulator raises Msg 2601 and fails the INSERT.
  The flag isn't stored either, so `sys.indexes.ignore_dup_key` reports 0 for a declared-ON index.
  Tracked in [`backlog.md`](backlog.md).
- **No partition-aware index storage**: `partition_ordinal` always 0, `data_space_id` always 1 (PRIMARY).
- **DROP INDEX comma list not atomic**: each entry resolves independently.
  Real SQL Server rolls back all on any failure.
- **Indexed-view battery gate order**: each rejection below was probed in isolation, so real's precedence when one view violates several at once isn't pinned — a body with both DISTINCT and TOP may name the other one on real.
  The simulator's order is fixed and documented in `Simulation.IndexedViews.cs`.
- **Indexed-view `sys.partitions` row**: real reports a `sys.partitions` / `sys.dm_db_partition_stats` row for a view index carrying the materialized row count; the simulator (which never materializes) omits view indexes from those page-count views.
  `sys.indexes` / `sys.index_columns` / `sys.stats` are populated.
- **Index hints (`SELECT … WITH (INDEX = name)`)**: not modeled — query planner is single-strategy (full scan) regardless.
