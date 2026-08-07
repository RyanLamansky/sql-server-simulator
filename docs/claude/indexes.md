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

**No INCLUDE on a clustered index**: a clustered index's leaf *is* the table row, so real refuses the list with **Msg 10601** class 16 state 1, `Cannot specify included columns for a clustered index.` — naming neither the index nor the table, because it is a statement-shape check.
It fires ahead of every name-resolution error (a missing table and a missing INCLUDE column alike) and ahead of Msg 1916, all probe-confirmed, so it sits beside the `IGNORE_DUP_KEY` shape check in `TryParseCreateIndex` and covers the indexed-view path with it.

The simulator has no B-tree storage, so an index never constrains inserts (UNIQUE aside) and isn't a stored ordered structure.
UNIQUE indexes participate in INSERT / UPDATE / MERGE enforcement alongside `KeyConstraint`.
The `WITH (...)` clause is scanned for `IGNORE_DUP_KEY` — the one option with a semantic, see [`constraints.md`](constraints.md#ignore_dup_key) — and otherwise parsed parens-balanced and discarded: none of `FILLFACTOR` / `PAD_INDEX` / `ONLINE` / `SORT_IN_TEMPDB` / etc. alter behavior.
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

## Disabled indexes (`ALTER INDEX … DISABLE` / `… REBUILD`)

`DISABLE` takes an index out of service and `REBUILD` puts it back; both live in `Simulation.AlterIndex.cs` alongside the `SET` form, and the state is `Index.IsDisabled` / `KeyConstraint.IsDisabled` (real allows disabling a constraint's backing index even though it refuses to change that constraint's `IGNORE_DUP_KEY` — Msg 1979).
Probed against SQL Server 2025 throughout.

While an index is disabled:

- **Its uniqueness isn't enforced at all** — duplicates insert freely, and for an `IGNORE_DUP_KEY` index they're *stored* rather than skipped, so no Msg 3604 either.
  The three enforcement loops skip disabled entries.
- **A nonclustered one leaves the table fully usable**, reads and writes alike.
- **A clustered one locks the whole table**: every query and every DML raises **Msg 8655** naming the index, because on real the clustered index *is* the table's storage.
  `RejectDisabledClusteredIndex` is the gate, called from the base-table query row source (`Selection`) and the four DML targets (`ProcessHeapInsert`, UPDATE, DELETE, MERGE).
  DDL is deliberately **not** gated — `ALTER INDEX … REBUILD` and `DROP INDEX` keep working on a locked table, which is how real recovers it, so the gate sits at those entry points rather than at table resolution.
  Since a `PRIMARY KEY` defaults clustered, `ALTER INDEX ALL … DISABLE` is the usual way a table ends up locked, and `ALL … REBUILD` is the way back.
- **`SET (…)` against it raises Msg 1973**.
- It can still be dropped.

`REBUILD` re-validates the rows that accumulated while the index was out, exactly as a fresh `CREATE UNIQUE INDEX` would: **Msg 1505** if any duplicate got in, naming the index (or constraint) whose key collided.
Once rebuilt, enforcement returns with the wording of whatever declared it — Msg 2601 for an index, **Msg 2627** for a constraint.
A `REBUILD` of an index that was never disabled is a no-op success, and the form accepts `PARTITION = ALL` plus its own `WITH (…)` block, both discarded.
`sys.indexes.is_disabled` projects the flag.

**A divergence worth naming**: the seek acceleration below does *not* consult `IsDisabled`.
Real's optimizer ignores a disabled index, but here the seek is a pure accelerator keyed on heap + column ordinals with a residual filter, never an index object, so skipping it couldn't change a result — only a plan, which the simulator doesn't expose.
The observable surface (enforcement, the lockout, the catalog) is what's modeled.

`DisabledIndexTests` is the regression suite.

## REORGANIZE, and the resumable trio

`ALTER INDEX … REORGANIZE` compacts a B-tree's pages in place.
A flat page list has nothing to compact, so what the statement does here is validate and succeed — probe-confirmed that real leaves the rows identical either way.
The grammar is `REORGANIZE [PARTITION = { ALL | <n> }] [WITH ( option = ON | OFF [, …] )]`, and the validation is where the fidelity sits:

- The `WITH` block takes `LOB_COMPACTION` and `COMPRESS_ALL_ROW_GROUPS` — real accepts the columnstore-shaped second option on a rowstore index without complaint, so neither name is gated on the index kind.
  Anything else is **Msg 155** in REORGANIZE's own wording (`'X' is not a recognized ALTER INDEX REORGANIZE option.`, distinct from the `SET` block's plain text), a non-`ON`/`OFF` value is **Msg 153**, and an empty list is Msg 102 on the closing paren.
- A **disabled** index refuses REORGANIZE with **Msg 1973**, but `ALTER INDEX ALL` steps past a disabled index rather than aborting on it — the opposite of the `SET (IGNORE_DUP_KEY …)` fan-out, and probe-confirmed both ways.
- `PARTITION = ALL` is accepted; a partition *number* is refused, and real splits the refusal by what it can name.
  A named index reports **Msg 7729** State 1 (`… as the index 'ix' is not partitioned.`), while `ALL` reports **Msg 7735** naming the first index the statement would have touched — index_id order, so a key constraint's clustered index first — or the table when it carries no index at all.
- A table carrying no index at all takes `ALL … REORGANIZE` as a no-op success.

`RESUME` / `PAUSE` / `ABORT` address a *paused resumable* index build.
The simulator never starts one — every index is built in place — so the whole model is real's own refusal, raised after the table and the index have resolved and caring nothing about the disabled flag:

- A named index: **Msg 10638**, `ALTER INDEX 'RESUME' failed. There is no pending resumable index operation for the index 'ix' on 't'.` — the table named unqualified, at State **1** for `RESUME` and State **2** for `PAUSE` and `ABORT`.
- `ALL`: **Msg 10680** at Level 11 (not 16), `ALTER INDEX ALL 'RESUME' failed. There is no pending resumable index operation on 't'.`, at State 1 for all three forms and raised without looking at an individual index — a bare heap reports it too.
- `RESUME` alone takes a `WITH (…)` block (`MAX_DURATION = <n> [MINUTES]` / `MAXDOP` / `WAIT_AT_LOW_PRIORITY (…)`), validated by name and discarded; an unrecognized name there is the *plain* Msg 155.
  `PAUSE` and `ABORT` take no block, which real reports as Msg 319, and none of the three takes a `PARTITION` clause.

`AlterIndexMaintenanceTests` is the regression suite.

## Equality-seek acceleration

The acceleration structure is `HeapSeekCache` (`Parser/HeapSeekCache.cs`) — a per-`Heap` index attached through a `ConditionalWeakTable<Heap, HeapSeekCache>` and reached via `HeapSeekCache.For(heap)`.
It's a storage-level accelerator, not a query-planner detail: **the consumers share it** — the query planner (`Selection`'s equality / range / ORDER BY / keyset seeks over `SELECT`), mutation targets (`UPDATE` / `DELETE`'s `WHERE` scan via `Selection.SeekMutationTarget`, and the inverted `MERGE` per-source target seek via `Selection.TryPrepareMergeTargetSeek` — see [UPDATE / DELETE target seeking](#update--delete-target-seeking) and [MERGE target seeking](#merge-target-seeking-loop-inversion)), and constraint enforcement (`Simulation`'s foreign-key parent-existence and cascade lookups, see [`foreign-keys.md`](foreign-keys.md); and PK / UNIQUE / unique-index duplicate detection on INSERT, UPDATE and MERGE, see [`constraints.md`](constraints.md#key-uniqueness-enforcement-seeks-rather-than-scans)).
The first two lean on a residual filter — the query planner keeps the matched conjuncts in WHERE, the mutation loop re-runs the full predicate per row — to discard the cache's stale-entry false-positives; the two enforcement paths have no residual filter, so they re-verify each candidate against live bytes (`HeapSeekCache.AnyRowMatches` / `MatchingRows`).
`SqlValueKey` (the bucket key) is likewise a shared top-level type, not nested in `Selection`.

Although there's no B-tree, an index (or PK / UNIQUE key) **does** accelerate reads via an equality seek on its **leading key-column prefix**.
`Selection.Execution.IndexSeek.cs` collects every top-level WHERE conjunct of the shape `indexedColumn = <stable value>` — where the value side is a literal, a variable, an outer/correlated column reference, an arithmetic node (`TwoSidedExpression`) whose operands are all themselves stable (which is how a negative literal arrives: the parser builds `-1` as `0 - 1`, so `col = -1` and `col = @v + 1` stay sargable — a deterministic operator over row-invariant operands is row-invariant), or a `CAST` / `CONVERT` / parenthesization peeling down to one of those (via `Expression.PureConversionOperand`) — then rewrites a single-base-table scan into a hash-index lookup keyed on the longest leading prefix of some index/key whose columns are all covered by those conjuncts.
So `WHERE a = x AND b = y` against an index on `(a, b, …)` keys on the two-column tuple, not just `a`; conjunct order is irrelevant (columns map to the prefix by ordinal).
The longest usable prefix across all keys/indexes wins, which is what keeps a **non-selective leading column** (a bit flag, a low-cardinality FK) from dragging its whole bucket through the residual filter — the full matched prefix is keyed precisely.
Peeling pure conversions matches real SQL Server keeping `col = CAST(<const> AS …)` sargable (probe-confirmed: integer / decimal widenings of a constant or variable still Index Seek; the cross-type case that does *not* seek there is `varchar` column vs `nvarchar` value, which the simulator's `Collation.Resolve` guard already declines).
Every matched conjunct stays in WHERE as a residual filter, so the seek can only narrow the row source, never change results; the per-component key promotion / collation rules mirror the equi-join hash path exactly (`SqlType.Promote` + `CoerceTo` + collation-coercibility `Resolve` guard, applied column-by-column into a multi-value `SqlValueKey`).

A WHERE conjunct of the shape `col IN (v1, v2, …)` — or its equivalent OR-of-equalities `col = v1 OR col = v2 OR …`, which EF Core emits for `Contains(...)` against a small list — decomposes through the same path via `BooleanExpression.TryGetEqualityFamily`.
Every candidate must be a stable value and they must all anchor on the same column; a single column's binding then becomes a **multi-value** equality.
A mixed-column OR (`a = 1 OR b = 2`) is not a family one column can hold and takes the [union of seeks](#union-of-seeks-a-cross-column-or) instead.
Surviving probes are unioned into one promoted type (pairwise `SqlType.Promote` across candidates), NULL candidates are silently dropped (never equal under `=`), and the seek expands across the **cartesian product** of per-column probe arrays — so `a IN (1, 2) AND b = 3` fires two composite-prefix probes against the (a, b) cache and `a IN (1, 2) AND b IN (3, 4)` fires four.
Each tuple is one hash lookup against the same per-Heap cache; a row can only sit in one bucket per probe column, so the unioned candidate stream contains no duplicates.
`NOT IN` is an AND-of-inequalities (not a positive equality family) and declines.

A prefix is bounded at the first key column that either has no equality conjunct or whose value side can't anchor a seek (a `NULL` probe — never equal under `=`; a cross-collation string compare; a promotion/evaluation that throws).
The seek then keys on the shorter usable prefix and the residual WHERE handles the rest: `WHERE a = 1 AND b = @null` seeks on `a` alone, then the residual `b = @null` correctly excludes every candidate.
A range bound on the key column just past the prefix extends the seek one column further — see [Equality-prefix + range continuation](#equality-prefix--range-continuation).
The per-`Heap` cache is keyed by the leading ordinal and remembers the prefix (ordinals + promoted types) it was built for, serving any request that prefix **covers** (leading-prefix match; shorter-arity probes read the ordered view) and rebuilding from a scan only when a request reaches wider or diverges.

This is the path that collapses a **correlated `EXISTS` / `IN` / scalar subquery** (and an unindexed-inner `APPLY`) from O(outer × inner) toward linear: the inner re-executes per outer row, but a per-table cache (keyed on the `Heap`, built lazily on first seek) persists across those calls.
Measured: a correlated `EXISTS` over a 4000 × 16000-row pair dropped from ~1480 ms to ~5 ms, on par with live SQL Server.
An inner reading through a **join** narrows the same way — the enclosing-scope value is stable for the inner plan's whole execution whether that plan has one source or several, which is a classification the multi-source pushdown makes rather than a second mechanism (see [`joins.md`](joins.md#where-pushdown-into-every-base-table-source)).
A scan sitting under a **view or derived table** is reached the other way round: the enclosing WHERE's conjunct is pushed into the body first, and the seek here then sees it as the body's own (see [`joins.md`](joins.md#where-pushdown-into-a-view--derived-table-body)).
A scan under a **grouped** body is reached the same way by the join above it, whose partner keys arrive as an `IN` the seek probes once per key (see [`joins.md`](joins.md#join-key-reduction-of-a-grouped-body)).

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

#### The span gate

A range seek only pays for itself while the interval is **selective**.
It trades a sequential page walk for a per-address `ReadSlotBytes` plus an ordered-view walk whose per-key comparer calls the scan doesn't make, so past some share of the table those costs dominate: a `> 100` over WWI's 231 000-row `Sales.OrderLines` (99.9% of the rows) measured **~55 ms seeked against ~35 ms scanned**, and 16 MB more allocated.

So the walk carries a candidate cap: `rowCount / RangeSpanGateDivisor` (4 — a quarter of the table), applied only once the heap holds `RangeSpanGateMinRows` (1024) or more.
`HeapSeekCache.RangeScan` abandons the walk and returns null the moment the collected rid count passes it, and `TryComputeRangeCandidates` reports `RangeSpanTooWide(table)` and declines to the scan.
Aborting mid-walk rather than counting afterwards is what keeps the fallback cheap — the discarded work is bounded by the cap instead of the whole table (the same `> 100` measured ~28 ms gated, level with the scan).
The gate lives in the address-only core, so the UPDATE / DELETE path takes it too.

The floor exists because under it the candidate list is small however wide the interval, so the seek can't lose by enough to matter, and leaving the gate off keeps the access path predictable for small tables.
The cache entry and its ordered view are already built when the gate declines, which is deliberate: that build is shared with every other seek against the column and is what makes the *next* narrow range on it free.
The gate is pure cost policy — the bound conjuncts are residual either way, so the rows are identical whichever path runs.

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

### Union of seeks: a cross-column `OR`

A top-level conjunct that is an `OR` whose **every disjunct seeks on its own** narrows to a union of those seeks (`TryComputeUnionCandidates`, traced `UnionSeek(table,n)` alongside `Seek`, `n` being the disjunct count) rather than falling to a scan.
`WHERE o.CustomerID = 90 OR o.SalespersonPersonID = 16` is the shape: both columns are indexed, but no single column's equality family can express the disjunction, so without the union pass the read scans however well indexed both sides are.
Real unions two Index Seeks under a Concatenation and dedupes; this does the same by **row address** — each disjunct fires its own probe set through the same per-`Heap` cache a lone equality would, and the `(page, slot)` candidates union through a `HashSet`, so a row several disjuncts match is read (and locked) exactly once.
Dedup by address rather than by value is what makes that hold when two disjuncts match one row on two different columns.

Semantics are exact by construction, on the seek's own standing contract: the whole original WHERE — the OR included — stays as the residual filter, and each disjunct's probe set selects a **superset** of the rows that disjunct matches, so their union is a superset of the OR's match set and the residual drops the rest.
NULLs need nothing special: a NULL probe value is skipped as anywhere else, and a row NULL in every disjunct's column matches no probe and reads UNKNOWN in the residual.

A disjunct's conjuncts are collected exactly as a WHERE's are, so an `AND` group inside the OR (`a = 1 OR (b = 2 AND c = 3)`) is **not** a decline — the group seeks on whatever prefix its own terms cover (composite prefix and range continuation included) and its remaining terms stay residual like everything else.
Mixed same/cross-column (`a = 1 OR a = 2 OR b = 3`) is just three disjunct probes.

It declines — silently, to the range seek or the scan — when any disjunct records no stable-value equality on a seekable non-LOB column of *this* source: a bare range or `IS NULL` / `NOT` disjunct, an expression-wrapped column (`ABS(b) = 2`), a non-indexed column, a column of another source, a value side reading a sibling of the same FROM.
That is what keeps the **catch-all** shape (`@p IS NULL OR col = @p`) a scan, as it is on real.
It also declines over a **probe cap** of 64 across the whole disjunction — counted structurally (the product of a disjunct's per-column probe counts, over every column it records) before any probe evaluates, since past that the one-pass scan beats dozens of bucket lookups plus a dedup set.

Two ordering rules make the choice deterministic rather than value-dependent:

- The claim is **exclusive with the IN-family path**: an OR that anchors on one column *is* the IN list `CollectColumnEqualities` already records as a multi-value equality, and is skipped here by a structural test (`IsSingleColumnEqualityFamily`) that doesn't ask which seek actually ran.
- The union is tried **after** the equality-prefix seek and **before** the range seek, and takes the first structurally eligible OR conjunct in written order.
  So a WHERE that seeks on its own conjunction keeps that single access path (the OR stays purely residual), a WHERE offering both an OR and a range bound takes the OR's point probes, and a correlated inner re-planned per outer row keeps one access path whatever the outer values are.
  If the claimed conjunct's probes then fail to anchor (a NULL or cross-collation value side collapsing some disjunct's prefix), the read scans rather than passing the claim on — a declined *probe* doesn't mean the disjunct matches nothing, so its contribution can't be treated as empty.

Everything downstream composes because this is just another way a source gets seeked: it rides the same snapshot / RCSI materializer and per-row lock pipeline, sits behind the same tx-scoped-row-lock decline, reports its deduped candidate count so a union on a **non-leftmost** joined source drives the join reorder like any other narrowing, and serves the **mutation** path through the same candidate core (`SeekMutationTarget`, so an UPDATE / DELETE whose WHERE is a cross-column OR seeks its target).
MERGE's per-source target seek doesn't take it yet — `TryPrepareMergeTargetSeek` settles its structural question once for the whole statement (`HasSeekableLeadingPrefix` over the `ON` conjuncts) and would need the union's own plan hoisted out of the per-source delegate.
The SERIALIZABLE phantom fence is settled from the *top-level* conjuncts before any candidate address is read and a disjunction pins no interval on any one key, so a fenced reader keeps the whole-table S while its read still narrows — narrowing which rows a read touches never narrows what it fences (see [`locking.md`](locking.md#what-the-reader-takes)).

Measured (WWI, `Sales.Orders`, `CustomerID = 90 OR SalespersonPersonID = 16`): ~39 ms → ~5.2 ms, against ~6.5 ms on live SQL Server.

All three single-table projectors — non-aggregate (`ProjectSqlRows`), aggregate (`BuildAggregateProjectionRows`), and window (`ProjectWindowedRows`) — narrow a single-base-table source through the seek, so `SELECT COUNT(*) … WHERE indexedcol = x` and `SELECT … OVER (…) FROM t WHERE indexedcol = x` both seek like their non-aggregate counterpart (without this the window projector silently full-scanned even when its WHERE was perfectly sargable, the regression that made running-total-per-parent EF queries scan the table).
They also push single-source WHERE equality / range predicates onto **every** base-table source of a multi-source FROM before the join (`NarrowJoinSources` — the matched conjuncts stay in the residual WHERE, so narrowing any one source is semantics-preserving for every join kind), and reorder a pure INNER equi-join chain to drive from the source the pushdown narrowed hardest.
The INNER / LEFT equi-join operator then **seeks the inner side per outer row** when that rowset is small relative to the inner and the inner is indexed on the join key — see [`joins.md`](joins.md).

### The scan prefilter: a join source no key can seek

A source whose sargable conjunct lands on a column **no key or index leads** can't seek — but the predicate can still shrink what the join sees.
`TryPrefilterJoinSource` (traced `ScanPrefilter(table,n)`, `n` being the conjuncts pushed) is that fallback: when `NarrowJoinSources`' seek attempt declines for a source, the source's row stream is wrapped in a filter evaluating the WHERE conjuncts that read only that source, and the join reads through it.
It runs only for a multi-source FROM — with one source the residual WHERE already is the scan's filter, so there is nothing to save.

The pushed shapes are the same sargable whitelist the seek's own intake uses: a comparison (`=` / `>` / `>=` / `<` / `<=`, either operand order) or a `BETWEEN` whose column side is a bare reference into this source (`TryIdentifyIndexableColumn`) and whose value side is row-invariant for this execution (`IsStableValueSide` — a literal, a variable, a pure conversion or arithmetic over those, or an enclosing-scope column; a **sibling** of the same FROM declines, since it isn't readable before the join runs).
That structural whitelist is what makes the push provably source-local: both operand shapes are enumerated node by node, so unlike an `Expression.VisitColumnReferences` walk it can't miss a reference buried in a container the walk doesn't descend into.
Every name a pushed conjunct can read is therefore either this source's own column or one the enclosing resolver answers, and the filter resolves through a one-slot tuple over that single source with the enclosing resolver behind it.

Correctness rests on the same residual invariant the seek narrowing does — **the pushed conjunct stays in the enclosing WHERE** — which makes the pass a pure narrowing that can only drop rows the residual would have rejected:

- **Every join kind** is safe, the NULL-extendable side included. All the pushed shapes are NULL-rejecting on the source's own column, so a tuple an outer join NULL-extends because this side lost a row reads UNKNOWN for the very conjunct that dropped it and is excluded — exactly as the matched-but-failing tuple was.
- **`TOP` / `OFFSET`** are unaffected: the row cap applies after the residual WHERE, so the same output rows come out of the same underlying rows, which is also what leaves the scan's **lock footprint** unchanged (the underlying enumeration is untouched; a filtered row was still read and locked).
- A conjunct that **raises** while being prefiltered (a divide-by-zero bound, say) keeps its row rather than dropping it, and lets the residual decide. Dropping it would be the one way the narrowing could change results: the join might never have produced a tuple from that row, in which case the enclosing statement never evaluated the conjunct at all.

Cost is one predicate evaluation per source row against whatever the join saves, so a predicate that drops almost nothing is pure overhead.
Past `PrefilterProbeRows` (4096) rows a pass rate above one half switches the filter off for the rest of the enumeration — sound at any point, since removing no rows is a correct outcome for a pure narrowing.

The pass is the seek's fallback, not its peer: a source the seek narrowed never takes it, and the join **reorder** ignores prefiltered sources (it picks its driver by seeked candidate count, which a lazy filtered stream doesn't have — with nothing seeked the written order stands).
What it buys is the join's own strategy switch: a driving table cut to a handful of rows lands under `EquiJoinSeekOrHash`'s outer cap and takes the per-outer-row seek instead of hashing the whole inner.

Measured (WWI, `Sales.Orders JOIN Sales.OrderLines` with a one-week `BETWEEN` on the unindexed `OrderDate` — the shape real also has no index for): **77.3 ms → 26.6 ms** (~2.9×) and 59.2 MB → 8.4 MB allocated, against ~64 ms on live SQL Server.
A year-wide range on the same shape (38% of the table, so the filter keeps filtering but the join still hashes) went 92.0 → 74.4 ms.

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
`Selection.SeekMutationTarget(table, where, batch)` builds a minimal single-source view of the base table, runs the **equality** (longest-prefix, IN-list / OR-family, composite), **cross-column `OR`** (the [union of seeks](#union-of-seeks-a-cross-column-or), in the same order the query path tries it) and **single-column range** analysis the `SELECT` path uses, and returns the seek-narrowed `(page, slot, bytes)` candidates — or `null` when the WHERE carries nothing seekable, so the caller keeps its `Heap.EnumerateRowsWithAddress()` full scan.
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

The **joined** (`… FROM <sources>`) form takes a different route: its target keeps the enumeration the write pipeline's address side-channel is keyed to, while its *other* sources go through the read path's own WHERE pushdown — see [`dml.md`](dml.md#joined-row-sources).

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
Inversion declines when **either** the target is a **view** (its column names don't map to the base heap the seek source is built from) or the `ON` has no equality on a target leading key / index column.
It declines to the heap walk, which hashes the source by the `ON`'s equality keys rather than scanning `target × source` — see [`dml.md`](dml.md#match-strategies) — and only an `ON` with no `target = source` equality at all reaches the quadratic scan.
Measured against the `target × source` scan, which is what a no-key target took before it hashed: a 5-row upsert batch (`WHEN MATCHED UPDATE … WHEN NOT MATCHED INSERT`) into a 20 000-row PK target ran **~9.1× faster** than the same MERGE against a no-key target (4.4 vs 40.0 ms/merge), and a 3 900-row source reconciled against a 4 000-row PK target with a `WHEN NOT MATCHED BY SOURCE UPDATE` ran **~11.6× faster** than the same against a non-seekable `ON` (617 vs 7 171 ms; the equal PK-revalidation of the updated rows is paid on both sides, so the delta is the match phase alone).
Both controls now hash instead, so the remaining gap is the seek's — not the quadratic walk's.

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
Four rejection paths:

- Missing parent table: Msg 3701 State 6 (`Cannot drop the index 'dbo.t.ix', because it does not exist or you do not have permission.`).
  `IF EXISTS` suppresses.
- Index name matches a PRIMARY KEY or UNIQUE constraint: Msg 3723 (`An explicit DROP INDEX is not allowed on index 'dbo.t.ix'. It is being used for PRIMARY KEY constraint enforcement.`).
  The PK/UQ kind word is interpolated (`PRIMARY KEY` or `UNIQUE`).
  `IF EXISTS` does NOT suppress — real SQL Server's behavior matches.
- The index is a **history table's clustered index** and its base is on a finite `HISTORY_RETENTION_PERIOD`: Msg 13766 — the index real's aged-data cleanup seeks through, released as soon as the base returns to INFINITE retention or versioning is turned off.
  See [`temporal-tables.md`](temporal-tables.md#the-history-cleanup-index).
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

An indexed view is schema bound by requirement, so its base tables also carry the [schema-binding dependency gate](programmable.md#schema-binding-with-schemabinding) — `DROP TABLE` on a base is **Msg 3729**, independently of the `DependentIndexedViews` wiring below (that one is about DML re-validation, this one about DDL).

### DML enforcement (Msg 2601)

Each base table the view references gets the view registered on its `HeapTable.DependentIndexedViews` (collected at CREATE INDEX time by re-parsing the body under a `BatchContext.DependencySink` that records every resolved base table + nested schema-bound view).
After an INSERT or UPDATE applies its heap writes, `EnforceIndexedViews(mutatedTable, batch)` re-evaluates each dependent view (full re-evaluation per statement — the accepted cost) and checks every UNIQUE index for a duplicate key, raising **Msg 2601** naming the schema-qualified view + index and rendering the key (`Cannot insert duplicate key row in object 'schema.view' with unique index 'ix' …` — same text on INSERT and UPDATE).
The violation throws inside the mutation body, so `RunMutation`'s undo log rolls the statement back (statement atomicity).
The hook is zero-cost (`DependentIndexedViews.Count == 0` guard) for the overwhelmingly common no-indexed-view case.

`CREATE INDEX` on a view re-parses the body three times — the qualifying-battery shape scan, the create-time duplicate-key materialization, and this dependency collection — each in a child `BatchContext` that executes outside the dispatch loop.
Each therefore **releases its own statement schema locks** (`ReleaseStatementSchemaLocks` in the `finally`) rather than relying on the loop's release: without that, the Sch-S the body took on each base table outlives the statement and the connection, and the next connection's Sch-M on that table — a `DROP TABLE` / `ALTER TABLE` — waits forever.

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

### `sys.indexes` — 23-column probe-confirmed shape

Real's column order, leading `object_id, name, index_id, type, type_desc, …` and ending `suppress_dup_key_messages, auto_created, optimize_for_sequential_key`.
There is no `statistics_incremental` column — selecting it raises Msg 207 on real too.

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
- Every remaining (nonclustered) constraint / index — including a NONCLUSTERED PK — takes `index_id = 2..N`, `type = 2`, in **object-id order**.

Object-id order is not declaration order for the key constraints of a single declaration: real allocates the clustered one's id first and the rest in **reverse** declaration order (probe-confirmed for `CREATE TABLE` — inline and table-level alike — and for an `ALTER TABLE ADD` of several constrained columns), which `Simulation.ResolveKeyConstraints` mirrors.
So `create table t (id int primary key nonclustered, u int unique)` answers UNIQUE 2 / PRIMARY KEY 3, and `create table t (a int unique, b int primary key clustered, c int unique)` answers the clustered PK 1, `c`'s UNIQUE 2, `a`'s 3.
A later `ALTER TABLE ADD CONSTRAINT` takes the next id up, since its own id is allocated after everything already there.

A PK defaults **clustered** (unless declared `NONCLUSTERED`); a UNIQUE constraint defaults **nonclustered** (unless declared `CLUSTERED`) — captured at parse time (`ParseInlineKeyKindAndModifiers` → `KeyConstraint.IsClustered`) across inline column constraints, table-level constraints, and `ALTER TABLE ADD CONSTRAINT` (the shape the bacpac loader emits).
At most one clustered index exists per table, enforced on every path: `CREATE INDEX` and `ALTER TABLE ADD CONSTRAINT … CLUSTERED` raise **Msg 1902 State 3** naming the existing clustered index, and two CLUSTERED constraints in one CREATE TABLE raise **Msg 8112** instead (real's distinct message for the case where neither entry exists yet to name). Two PRIMARY KEYs — both clustered by default — report Msg 8110 ahead of either, probe-confirmed.

`compression_delay` is **NULL** on every row: it carries a minute-delay only for columnstore indexes (unmodeled), and is NULL for every rowstore index (probe-confirmed).
SMO's index-scripting query reads it as `CAST(i.compression_delay AS int)` with no `ISNULL` wrapper.

### `sys.index_columns` — 10-column probe-confirmed shape

One row per (index, column):

- **KEY columns**: `key_ordinal = 1..N`, `is_included_column = 0`, and `index_column_id = 1..N` in an order that splits by index kind —
  a **nonclustered** index numbers them in key order (so `index_column_id = key_ordinal`), while a **clustered** one numbers them in **table column order**, ascending `column_id`.
  `create clustered index ix on t(b, a)` therefore reports `a` at `index_column_id` 1 / `key_ordinal` 2 (probe-confirmed, and the same for a clustered PRIMARY KEY and an indexed view's clustered index); `key_ordinal` carries the key order either way.
- **INCLUDE columns**: `key_ordinal = 0`, `index_column_id = N+1..`, `is_included_column = 1`.
  Only a nonclustered index has any, since a clustered one can't declare an INCLUDE list at all (Msg 10601).
- HEAP entries (index_id = 0) don't appear — real SQL Server's catalog omits them.

`sys.stats_columns.stats_column_id` tracks the sibling `index_column_id` through the same rule (`KeyIndexColumnIds`), so DacFx's join of the two views on `(stats_column_id, column_id)` pairs up for a clustered index too.

`is_descending_key` reflects the per-column DESC flag from CREATE INDEX.
`column_id` is the 1-based full-column ordinal from `sys.columns` (mapped back from the storage ordinal stored on the index).

## `CREATE STATISTICS` / `DROP STATISTICS`

`CREATE STATISTICS <name> ON <table> (<column> [, …]) [WITH <option> [, …]]` records a standalone statistics object on the table; `DROP STATISTICS <table>.<name> [, …]` removes one (each entry addressing its own table, so the leaf is the statistic and everything before it the table).
What's modeled is the **declaration**, not a histogram — the simulator makes no cardinality estimates, so a statistic changes nothing about how a query runs.
What it does carry is catalog identity: `sys.stats` rows with `user_created = 1` and `sys.stats_columns` rows in the declared column order, which is what DacFx re-exports, SSMS scripts, and a bacpac's `SqlStatistic` elements round-trip through.

`stats_id` is drawn from the **same per-table sequence the index ids use**, continuing past the highest one in use — an index-backed statistic shares its index's id, so a table whose PK takes index_id 1 gives its first standalone statistic stats_id 2.

Of the WITH options only `NORECOMPUTE` is observable (`sys.stats.no_recompute`); the sampling family (`FULLSCAN`, `SAMPLE n {PERCENT | ROWS}`, `PERSIST_SAMPLE_PERCENT`, `INCREMENTAL`, `MAXDOP`, `AUTO_DROP`) describes how real would scan the data to build a histogram there isn't one of here, so those parse and discard.

Diagnostics, probe-confirmed against SQL Server 2025:

| Case | Error |
|---|---|
| Name the table already carries — **including an index's name**, since statistics and indexes share one per-table name space | **Msg 1927** sev 16 state 2, `There are already statistics on table '<table>' named '<name>'.` |
| Missing table | **Msg 1088** state 12 (shared with `CREATE INDEX`) |
| Missing column | **Msg 1911** |
| `DROP STATISTICS` of one the table doesn't carry | **Msg 3701** sev **11** state 7, `Cannot drop the statistics '<written name>', because it does not exist or you do not have permission.` |

Auto-created column statistics (the `_WA_Sys_*` rows real materializes on first predicate use) still aren't modeled — see [`catalog-views.md`](catalog-views.md).

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
| A CTE prefix on the body | 10137 | 1 |
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

Gate order is the simulator's own except for Msg 10137, whose precedence is probe-confirmed: a CTE-bearing body reports it ahead of the DISTINCT, subquery and nondeterministic-function rejections it also violates, so the check runs first.
10137 embeds a CTE name, and real names the **first the body declares** — not the one the body's SELECT reads, and even when nothing reads it — so `ParseCteBindings` records the first name it registers into the shape collector.
CTE-bodied views themselves ship; only indexing one is refused → [`ctes.md`](ctes.md#where-a-prefix-may-appear).

Nondeterminism is a closed set of built-ins (`GETDATE` / `GETUTCDATE` / `SYSDATETIME` / `SYSUTCDATETIME` / `SYSDATETIMEOFFSET` / `NEWID` / `NEWSEQUENTIALID` / `RAND`) recorded at `ResolveBuiltIn`, so a reference at any nesting depth is caught.
Aggregates outside the disallowed set (`STRING_AGG` and friends) are **left alone** rather than guessed at — an unprobed rejection would be the over-restrictive direction.
`SUM` nullability reuses `Expression.ResultIsNullable`, the same rule that drives result-metadata nullability, with an unresolvable column treated as nullable.

Shapes that keep indexing cleanly: inner-join projections (the form AdventureWorks' two indexed views take), `SUM` + `COUNT_BIG` grouped views over NOT NULL columns, and filtered projections.

## Computed columns as index keys

A **non-persisted** computed column is a legal key for an index, a UNIQUE constraint and statistics — AdventureWorks' `AK_SalesOrderHeader_SalesOrderNumber` is the shape — and real puts two preconditions on it, both of which the simulator applies at every declaration site (`CREATE INDEX`, inline and table-level `UNIQUE` at `CREATE TABLE`, `ALTER TABLE … ADD CONSTRAINT`, `CREATE STATISTICS`):

- **Msg 2729** — the expression must be **deterministic**, off the same walk `OBJECTPROPERTY`'s `IsDeterministic` and PERSISTED's own Msg 4936 use.
- **Msg 2799** — it must be **precise**, meaning no `float` / `real` anywhere in it.
  That reaches past the column's own resolved type: `CAST(f AS int)`, `CAST(SQRT(i) AS int)`, `CONVERT(int, CONVERT(float, i))` and `i + CAST(1.5e0 AS int)` are all imprecise even though each lands on `int`, so the check reads the definition's tokens for an approximate column, an explicit `float` / `real` conversion target, a float-returning built-in and a scientific-notation literal (`ComputedColumnPrecision`).
  A **persisted** column skips this one — its value is stored, so nothing is re-evaluated.

Both gates fire for a *non-unique* index and for `CREATE STATISTICS` as readily as for a unique one, and both append real's `Could not create constraint or index. See previous errors.` when the failure arrived through a constraint rather than a bare `CREATE INDEX` — the same suffix Msg 1711 carries when a `PRIMARY KEY` names a non-persisted computed column (which stays refused: a PK needs the value stored and non-nullable).

### How the key is read

Those two gates are what make the key well defined: the value is then a reproducible function of the row's stored columns, so it can be evaluated per row rather than stored.
The enforcement paths carry a **full ordinal** beside each key column's storage ordinal (`Index.KeyFullOrdinals`, `KeyConstraint.FullOrdinals`) — a non-persisted computed column's storage entry is `-1` — and read such a key off an evaluated full row instead of out of row bytes.
NULLs collide as they do for a stored key, and Msg 2601 / 2627 / 1505 quote the computed value.

The existing-row side can't use the per-`Heap` seek cache, which indexes stored bytes.
So it scans **once per statement** into a hash set of key tuples (`StatementContext.ComputedUniqueKeys`), and each row the statement admits is added as it goes — which is what makes the rows of one multi-row INSERT collide with each other, and keeps a K-row statement at one scan rather than K.
Measured on AdventureWorks' 31 465-row `Sales.SalesOrderHeader`: inserting 10 rows and inserting 200 cost the same, where the per-row scan they replace would have been 20× apart.
The UPDATE path builds the same set once per statement, excluding the rows it is itself rewriting, and compares those against each other separately; a row whose computed key stands still skips its own check exactly as a stored key does.

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
