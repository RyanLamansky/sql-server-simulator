# CHECK / PRIMARY KEY / UNIQUE constraint enforcement

Sibling deep-dives: [`foreign-keys.md`](foreign-keys.md) (the FK family in full), [`indexes.md`](indexes.md) (seek acceleration mechanics), [`alter-table.md`](alter-table.md) (ADD/DROP CONSTRAINT incl. trust toggling).

- `CHECK`: inline single-column + table-level; Msg 547 per row on definitely-false predicate (UNKNOWN passes — opposite of WHERE).
  Inline column-level CHECK may only reference its owning column — peer refs raise **Msg 8141** at CREATE TABLE (probe-confirmed).
  The walker is structural (`Expression.VisitColumnReferences` + `BooleanExpression.VisitOperandExpressions`); coverage spans the common containers (`Reference`, `Parenthesized`, `TwoSidedExpression`, `Cast`, `Length`) — peer refs in rarer ones (`DATEPART`, `SUBSTRING`, nested `CASE`) escape the CREATE check and surface at INSERT.
  Table-level CHECK has no peer restriction.
  A predicate over a computed column has its own persistence rules — [below](#computed-columns-in-a-check-constraint).
- `PRIMARY KEY` / `UNIQUE` / secondary `CREATE INDEX`: no B-tree; reads, `UPDATE`/`DELETE`/`MERGE` target scans, **and key-uniqueness enforcement itself** go through the **incrementally-maintained** per-`Heap` seek acceleration (equality / IN / leading-column range / equality-prefix+range continuation / ORDER BY elimination / keyset).
  Seek shapes, mutation/MERGE seeking, journal mechanics, decline rules, residual-WHERE invariant in [`indexes.md`](indexes.md); the enforcement seek's own decline rules are [below](#key-uniqueness-enforcement-seeks-rather-than-scans).
  Violations: PK/UNIQUE *constraints* raise Msg 2627; unique *indexes* raise Msg 2601.
  UNIQUE treats NULLs as equal (the signature SQL Server divergence from ANSI).
- `FOREIGN KEY`: inline / table-level / named forms; all four referential actions on `ON DELETE`/`ON UPDATE`; enforced at INSERT/UPDATE/DELETE/MERGE; full `sys.foreign_keys` / `sys.foreign_key_columns`.
  Enforcement **seeks the shared `HeapSeekCache`** (live-byte verified, no residual WHERE).
  Referential-action, cascade-cycle, PK/UNIQUE-target, NULL-skip rules + Msg numbers in [`foreign-keys.md`](foreign-keys.md).

## One inline constraint of each kind per column (Msg 8148 / 8151)

A single column definition admits at most one inline constraint of each kind.
A second raises **Msg 8148** `More than one column <kind> constraint specified for column 'b', table 't'.` — `CHECK`, `DEFAULT`, `UNIQUE` and `PRIMARY KEY` each echoing their own keyword, whether the pair is named, unnamed, or one of each.
An inline `PRIMARY KEY` beside an inline `UNIQUE` is **Msg 8151** (`Both a PRIMARY KEY and UNIQUE constraint have been defined for column 'b', table 't'. Only one is allowed.`) in either order instead.
Both fire wherever a column definition is parsed: CREATE TABLE, `DECLARE @t TABLE` (the message names `@t`), `CREATE TYPE … AS TABLE`, `ALTER TABLE … ADD <column>`, and the inline tail of a **persisted computed column**.

The restriction is on the column *definition* only — a table-level `CHECK` over a column that already carries an inline one is legal, as is a later `ALTER TABLE … ADD CHECK`, and a `DEFAULT` pairs with a `CHECK` freely.
All probe-confirmed against SQL Server 2025.

**Divergence**: the message names the table by the leaf the parser carries, where real echoes the name as written (`table 'dbo.t'` for a schema-qualified CREATE) — shared with the sibling column-definition errors (Msg 8141 / 8147).

## Constraint naming metadata

`sys.check_constraints.is_system_named` is 1 for every server-generated name and 0 for a `CONSTRAINT name` one, on both declaration paths — CREATE TABLE (inline column tail and table-level list) and `ALTER TABLE … ADD` — matching real (probe-confirmed against SQL Server 2025, which reports the same split for `sys.default_constraints` and `sys.key_constraints`).
The auto-name shapes themselves are in [`alter-table.md`](alter-table.md); they are deterministic but don't byte-match real's object-id-derived hex.

## Computed columns in a CHECK constraint

A **PERSISTED** computed column carries a CHECK in every form: the inline column tail (`cc AS a + 1 PERSISTED [CONSTRAINT n] CHECK (cc > 0)`), the table-level list, `ALTER TABLE … ADD CONSTRAINT … CHECK`, and the inline tail of an `ALTER TABLE … ADD` of the computed column itself.
Enforcement reads the stored value like any other column, so an INSERT out of range raises Msg 547 and so does an UPDATE of the underlying column that drives the expression out of range without ever naming the computed column.
An unnamed inline CHECK auto-names as the column-level shape `CK__<table>__<column>__<hex>`, the same as a regular column's.

A computed column takes several inline constraints in any order — `PERSISTED PRIMARY KEY CHECK (cc > 0)`, the reverse, and the doubly-named `CONSTRAINT ck CHECK (…) CONSTRAINT uq UNIQUE` all parse — so `ParseComputedColumnInlineConstraint` loops rather than reading one constraint.
A PRIMARY KEY naming the computed column promotes it to NOT NULL, inline and table-level alike, exactly as it does a regular column: the promotion happens where the computed `HeapColumn` is materialized, since `ParseColumnList`'s promotion loop walks the column list while that slot is still an unresolved placeholder.

A **non-persisted** computed column is rejected, with the message depending on how the predicate reaches it (probe-confirmed split, the same shape the FK family has):

| Form | Error |
|------|-------|
| CHECK inline on the non-persisted column itself | **Msg 8183** — `Only UNIQUE or PRIMARY KEY constraints can be created on computed columns, while CHECK, FOREIGN KEY, and NOT NULL constraints require that computed columns be persisted.` (real rejects at parse, before the constraint reaches resolution) |
| Table-level list, `ALTER TABLE … ADD CONSTRAINT`, or an inline CHECK on a *persisted* column reaching a non-persisted computed peer | **Msg 1764** — `Computed Column '<col>' in table '<table>' is invalid for use in 'CHECK CONSTRAINT' because it is not persisted.` (note real's capitalized "Computed Column") |

Msg 1764 **beats Msg 8141**: an inline CHECK reaching a non-persisted computed peer reports the persistence failure, not the peer-reference one, so `RejectChecksOverNonPersistedComputedColumns` runs ahead of the peer-reference walk at every site.
The peer-reference gate still wins when the peer is persisted or regular.
`WITH NOCHECK` doesn't excuse Msg 1764 — real rejects the declaration itself, and the option only skips the existing-row scan.
As with the FK family's Msg 1764, real's trailing informational **Msg 1750** (`Could not create constraint or index. See previous errors.`) is collapsed away.

Both gates reach `DECLARE @t TABLE` and `CREATE TYPE … AS TABLE` through the shared column parser, naming the variable (`'@t'`) or the type in the Msg 1764 text.
The Msg 1764 walk shares `Expression.VisitColumnReferences` with the Msg 8141 gate and so inherits its container-coverage limits (see the bullet above): a reference buried in a rarer container escapes the declaration-time check.

## `IGNORE_DUP_KEY`

Declared on a UNIQUE index or a PRIMARY KEY / UNIQUE constraint, the option turns a duplicate row into a *skip*: the INSERT drops that row, keeps going with the rest, and reports success.
Every behavior below was probed against SQL Server 2025.

**Where it may be declared** — a unique index (`CREATE UNIQUE INDEX … WITH (IGNORE_DUP_KEY = ON)`), a table-level `PRIMARY KEY (…)` / `UNIQUE (…) WITH (…)` constraint, the same constraints inline on a column (`id int primary key with (…)`), and `ALTER TABLE … ADD CONSTRAINT … WITH (…)`.
It stores on `Index.IgnoreDupKey` / `KeyConstraint.IgnoreDupKey` and surfaces as `sys.indexes.ignore_dup_key`.
`ParseOptionalIndexWithClause` reads it out of the option list while still skipping every other option unexamined, so scripted DDL keeps flowing.

**What the skip does.** The row isn't written, doesn't reach `OUTPUT`, doesn't reach a trigger's `INSERTED`, and doesn't count: rows-affected and `@@ROWCOUNT` report only the rows that landed, and `@@ERROR` stays 0.
A severity-**0** Msg 3604 (`Duplicate key was ignored.`) rides the info-message stream **once per statement** however many rows were dropped, and not at all when none were — latched on `StatementContext.ReportedIgnoredDuplicate`, which the dispatch loop clears per statement.
An identity value is still consumed by a row that gets dropped.
The option is per key, not per table: a row duplicating a lenient index's key is skipped while one duplicating a strict index's key on the same table still raises.
A duplicate *within* one `VALUES` list is skipped the same way as one against an existing row, and `INSERT … SELECT` behaves identically.

Enforcement signals the skip by returning `RowKeyVerdict.SkipDuplicate` rather than throwing, so which callers honor it is visible at the call sites: the INSERT statement and BCP bulk load act on it, while **UPDATE and MERGE never ask** — real keeps raising Msg 2601 / 2627 on both, including MERGE's `WHEN NOT MATCHED THEN INSERT`, so the update-path enforcers are unchanged.

**Where real refuses it** (all faithful rejections, so the simulator raises them too):

| declaration | error |
| --- | --- |
| non-unique `CREATE INDEX` | **Msg 1916**, `"CREATE INDEX options nonunique and ignore_dup_key are mutually exclusive."` — a statement-shape check, probe-confirmed to fire ahead of table, column and duplicate-name resolution, so it names nothing |
| filtered unique index | **Msg 10618**, `"Cannot create filtered index … "` — names the table, so it can only raise once the target has bound |
| index on a view | **Msg 1990** |
| `ALTER INDEX … SET` on a non-unique index | **Msg 1915** — a different number *and* wording from CREATE's 1916 |
| `ALTER INDEX … SET` on a filtered index | **Msg 10618** with the verb `alter` in place of `create` |
| `ALTER INDEX … SET` on a constraint-backed index | **Msg 1979** — real accepts the option in a constraint's own declaration but refuses to change it afterwards |

Because filtered unique indexes reject the option outright, there is no filtered-plus-`IGNORE_DUP_KEY` interaction to model anywhere.

### `ALTER INDEX … SET`

`Simulation.AlterIndex.cs` implements `ALTER INDEX { name | ALL } ON <table> SET ( option [, …] )`.
`IGNORE_DUP_KEY` is honored; `ALLOW_ROW_LOCKS` / `ALLOW_PAGE_LOCKS` / `OPTIMIZE_FOR_SEQUENTIAL_KEY` / `STATISTICS_NORECOMPUTE` / `COMPRESSION_DELAY` / `FILLFACTOR` are recognized by name and discarded.
The list is validated **strictly** here, unlike CREATE INDEX's tolerant `WITH (…)`, because real is strict too: an unknown name raises **Msg 155**, and a value that isn't `ON` / `OFF` (or a numeric where one belongs) is **Msg 102**, as is an empty list.
A named target resolves against the table's indexes *and* its key constraints — that's what makes Msg 1979 reachable.
`ALL` fans out over every index and aborts on the first refusal, so a table carrying any key constraint can't have the option set table-wide; a SET that never mentions `IGNORE_DUP_KEY` has nothing to refuse and sweeps cleanly.
Missing index → **Msg 2727** (Level 11); missing table → **Msg 1088** (State 9, the object name in double quotes).

`FILLFACTOR` is a reserved keyword where every other option name is an ordinary identifier, so the option name is read off `Token.Source` rather than as an identifier token.

The `DISABLE` / `REBUILD` forms ship too — see [`indexes.md`](indexes.md#disabled-indexes-alter-index--disable--rebuild); `REORGANIZE` / `RESUME` / `PAUSE` / `ABORT` raise `NotSupportedException`.
A disabled index isn't enforced at all, so an `ALTER INDEX … DISABLE` earlier in a script silently stops later duplicate checks — worth knowing when reading probe transcripts, since it is exactly what made one of this feature's own probes look like a divergence.

`IgnoreDupKeyTests` is the regression suite.

## Key-uniqueness enforcement seeks rather than scans

Four enforcement paths ask the same question — *does a live row already carry this key tuple?* — and all four answer it by seeking the shared per-`Heap` cache, the way foreign-key parent-existence already did:
`EnforceKeyConstraints` / `EnforceUniqueIndexes` (`Simulation.Coerce.cs`, reached from INSERT, the TVP row materializer, and BCP bulk load) and `EnforceKeyConstraintsForUpdate` / `EnforceUniqueIndexesForUpdate` (`Simulation.Update.cs`, reached from UPDATE and MERGE).

`TryPrepareKeySeek` is the shared gate.
It resolves the per-component promoted types the seek entry keys on — each key column's own stored type, the same convention `TryMapFkColumnsToStorage` uses — and builds the probe through `TryBuildSeekProbe`, which both families share.
A seek hit *is* the duplicate: `HeapSeekCache.AnyRowMatches` / `MatchingRows` verify every candidate against live bytes and skip tombstoned slots, so unlike the query path there's no residual WHERE to lean on.
Correctness rests on `SqlValueKey`'s per-component equality being the same comparison the scan made — `SqlValue.Equals`, including its collation-aware ANSI-padded string path — and on `SqlValue.GetHashCode` folding case and trailing spaces to agree with it, so a case-insensitive or trailing-space duplicate lands in the bucket its collision needs.

Two conditions decline the seek and fall back to the full scan, which stays the oracle:

- **A NULL key component.**
  The cache drops NULL keys at build time (they can never satisfy `=`), so its buckets can't express UNIQUE's NULLs-collide rule — a NULL probe has to scan or a second NULL would insert silently.
  Non-NULL probes are unaffected: an existing NULL row can't collide with them anyway, so its absence from the buckets is free.
- **A key column with no storage slot**, which the seek can't decode.

Size is deliberately *not* a third condition.
A minimum-heap-size gate was built, measured and dropped: it won nowhere — 500 keyed tables seeded 1 / 3 / 10 / 50 rows each landed within run-to-run noise with and without it, since building a bucket entry over a heap that small is nearly free — and it cost 26% at 200 rows per table and up to 1.9× per insert on a few-hundred-row narrow table, whose rows all still fit inside the single page it exempted.
The memory argument for it doesn't survive either: a table big enough for its bucket index to matter is past any such threshold by definition, so the gate only ever exempted indexes that were trivially small, while making enforcement allocate *more* (every scan comparison decodes a value — 224 MiB against 177 MiB over a 300-table × 100-row fixture).
The whole-suite timing can't see the difference in either direction; it sits under the ±3% run-to-run noise.

A filtered unique index seeks like any other: the key narrows the candidates, then the filter is evaluated on each candidate's own decoded row (`DecodeFullRow`), so only filter-passing rows on both sides participate.

### The UPDATE path compares within the statement, then against the heap

An affected row's new key has to clear two comparisons: against the other affected rows' new keys, and against the rows the statement isn't touching.
The second is the seek described above, with `affectedAddrs` excluding the statement's own rows.
The first goes through `AffectedKeyIndex` — the affected rows' new key tuples plus an occurrence count per distinct tuple, so "does another affected row carry this key?" is a hash probe.
It is built at most once per constraint or index — on that constraint's first moved row, and not at all for a constraint no row moved.

Two details keep the index faithful to the pairwise walk it replaces.
`SqlValueKey` compares per component through `SqlValue.Equals` and folds two NULLs together — UNIQUE's NULLs-collide rule — and hashes to agree, so unlike the heap-side seek (whose buckets drop NULL keys) this index carries them and a NULL-bearing key still finds its duplicate inside the statement (`Update_TwoMovedRowsOntoTheSameNullKey_Raises`).
And a filtered index counts only the affected rows inside its set, which `FilterMembership` decides once per row rather than once per pair as the walk did — so a collision among rows that all sit *outside* the filter is correctly not a violation (`Update_TwoMovedRowsOntoTheSameKeyOutsideFilteredIndex_Succeeds`).

The row-major loop order is unchanged, so which of several simultaneous violations gets reported is the same as the walk's.

#### Rows whose key stood still skip their own check

`KeyTupleMoved` drops an affected row's own uniqueness check when the UPDATE didn't move its key tuple: the row was unique before the statement, non-affected rows don't change, and a collision with a row whose key *did* move is caught when that row is checked, since every affected row stays a comparison target either way.
Without it a bulk UPDATE built the key index and seeked for every row even when it never touched the key.

The pre-update key comes from the captured old row when there is one (OUTPUT clause, trigger present, MERGE's matched updates) and otherwise straight off the row's heap slot — validation runs before the rewrite phase, so the slot still holds the old bytes and only the key columns need decoding.
`FullOld` is null on the plain UPDATE path, which is exactly the shape that matters, so reading the slot is what makes the skip fire at all rather than a no-op.
A sentinel `(-1, i)` address (MERGE's pending inserts, which have no pre-update state) and an unreadable slot both count as moved and take the full check.

The skip is taken for an **unfiltered** index only: a filter can read columns outside the key, so a row whose key stood still can still have moved into the filtered set and collided there (`Update_MovingIntoFilteredSetWithStandingKey_Raises`).

`Index.KeyStorageOrdinals` is the projection the seek keys on, materialized at index construction.
`ALTER TABLE … DROP COLUMN` shifts every later storage slot down and remaps it in the same loop that rewrites `Index.KeyColumns`, so the two can't drift — a stale copy there decodes the wrong column, which is how it first went wrong.

**Cost.** Enforcement was O(N) per row, so loading N rows into a keyed table was quadratic: 50 000 single-row inserts into a `PRIMARY KEY` table took 48.9 s, rising through 0.20 ms/insert at 5 000 rows to 1.93 ms at 50 000.
Seeking makes it flat — the same load is ~1.2 s (**~40×**), and per-insert cost holds at ~0.005–0.014 ms across 1 000 → 50 000 rows, matching a keyless heap's.
A bulk UPDATE on 20 000 rows went from 2 163 ms to ~50–75 ms when it leaves the key alone, and from 2 582 ms to ~90–185 ms when it moves the key on every row — the second being the affected-vs-affected comparison, quadratic until `AffectedKeyIndex` replaced the walk.
Both now grow linearly: the key-moving statement lands at ~165 ms on 50 000 rows, where the quadratic shape predicted ~16 s.
The price is the seek entry's memory (a bucket per distinct key, rid lists) and journal maintenance on any keyed table — the same structure a point-lookup query would have built anyway — plus, per key-moving UPDATE, one `SqlValueKey` per affected row.

Because enforcement now seeks per row, a bulk INSERT into a keyed table replays the mutation journal as it goes and leaves the cache current, so such a table no longer reaches the journal cap that would force a rebuild (`IndexSeekTests.BulkInsertBeyondJournalCap_FallsBackToRebuild` uses a non-unique index for exactly that reason).

`KeyUniquenessSeekTests` is the regression suite, running the semantics on tables large enough to span pages: Msg 2627 / 2601 wording, composite full-vs-partial keys, NULLs-collide, case-insensitive and trailing-space duplicates, filtered indexes on both sides, re-insert after DELETE and after ROLLBACK, UPDATE into an existing key, a non-key UPDATE not colliding with itself, mass key shift (`SET id = id + 1`) succeeding while one into untouched rows raises, two moved rows landing on one key (plain, NULL, composite, and inside vs outside a filtered index), a moved key landing on a standing affected row, MERGE, and the DROP COLUMN ordinal shift.
