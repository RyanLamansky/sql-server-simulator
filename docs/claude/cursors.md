# Cursors

T-SQL server-side cursors: `DECLARE … CURSOR`, `OPEN`, `FETCH`, `CLOSE`, `DEALLOCATE`, the `STATIC` / `KEYSET` / `DYNAMIC` sensitivity model, scroll fetches, the `@@FETCH_STATUS` / `@@CURSOR_ROWS` / `CURSOR_STATUS` status surface, and positioned `WHERE CURRENT OF` UPDATE / DELETE.
Behavior probed against SQL Server 2025.

## Layout

- **`Cursor.cs`** (root) — the session-scoped runtime cursor: effective sensitivity, scrollability, read-only flag, the participating base tables, and per-sensitivity position state (the per-source addresses it currently sits on).
  `Open` / `Fetch` / `Close` live here.
- **`SimulatedDbConnection.Cursors`** — per-session `Dictionary<string, Cursor>` (case-insensitive, names are identifiers not `@`-prefixed).
  Plus `LastFetchStatus` (`@@FETCH_STATUS`) and `LastCursorRows` (`@@CURSOR_ROWS`).
  Cleared on `Dispose` (cursors auto-deallocate at session close).
- **`Simulation.Cursor.cs`** — the `DECLARE CURSOR` grammar (SQL-92 + T-SQL extended), `OPEN` / `FETCH` / `CLOSE` / `DEALLOCATE` dispatch, the `FETCH` direction parser, and the `WHERE CURRENT OF` helpers (`ParseWhereCurrentOf` / `CursorRowMatches`) shared by UPDATE / DELETE.
- **`Selection.Cursor.cs`** — `CursorShape` (the parse-time capture of a SELECT's cursor-navigability), `CursorSourcePlan` / `CursorSlot` (the resolved plan and its per-slot backing), `TryBuildCursorPlan` (the DECLARE-time resolution that follows deferred slots down to base tables), the ORDER BY index-coverage rule `CursorSourcePlan.OrderBySuppliedByIndex` and the row-locator rule `CursorSourcePlan.SupportsKeyset`, both resolved at construction, `EnumerateForCursor` (live enumeration over the base heaps, folding the JOIN chain and reusing `ResolveAcrossTuple` + `ComputeOrderKeys`), and the `CursorRow` / identity-comparison helpers, kept inside `Selection` where the private projection / ORDER BY machinery lives.
- **`Simulation.InvokeView.cs`'s `TryParseViewBodyPlan`** — the parse-only view-body seam cursor planning looks through a view with; mirrors `InvokeViewCore`'s child-batch setup but stops at parse and returns null rather than propagating a body error.
- **`Parser/Expressions/CursorScalars.cs`** — `@@FETCH_STATUS`, `@@CURSOR_ROWS`, `CURSOR_STATUS(scope, name)`.
- **`Errors/SimulatedSqlException.CursorErrors.cs`** — Msg 16905 / 16911 / 16915 / 16916 / 16917 / 16924 / 16925 / 16929 / 16931 / 16932 (FOR UPDATE OF) / 16933 (target not one of the cursor's tables) / 16947+3621 (nothing to mutate) / 16947+16934+3621 (OPTIMISTIC conflict chain) / 16950 (unallocated cursor variable) — all probe-confirmed verbatim.
  Msg 16916 and 16950 report, from `OPEN` and `FETCH`, the line of the statement that ran before — 0 when none did, a bare `BEGIN` and a `DECLARE` that initializes nothing not counting, and the statement's own line right after a failed one — and from `CLOSE` and `DEALLOCATE` their own line (probed 2026-09-29 against SQL Server 2025; `BatchContext.PriorStatementLine`).
- **`Simulation.DescribeCursor.cs`** — `sp_cursor_list` and the `sp_describe_cursor` family; see [Describing cursors](#describing-cursors).
- **A fetch's result** names its columns' base tables and columns in the TDS browse tokens real sends with every fetch — see [`tds-endpoint.md`](tds-endpoint.md#per-statement-done-tokens).
  TYPE_WARNING's Msg 16956 and the self-join Msg 16961 ride the `BatchContext.AppendInfoError` info pipeline, not this factory set.

The dispatch routes `Keyword.Declare` to cursor handling when the token after `DECLARE` isn't `@`-prefixed (cursor names are bare identifiers; that's the only non-`@` DECLARE form).
`Keyword.Open` / `Fetch` / `Close` / `Deallocate` get their own dispatch cases and are in `IsStatementBoundary`.
The query after `FOR` parses through the shared body seam, so it may carry a `WITH cte AS (…)` prefix (the bindings are captured into the stored plan at DECLARE, and OPEN re-executes it) → [`ctes.md`](ctes.md#where-a-prefix-may-appear).

**API server cursors** (the `sp_cursor*` TDS RPC family SSMS's grid editor and legacy ODBC / OLE DB apps drive) reuse this engine surface from the wire layer: `Network/TdsSession.Cursors.cs` synthesizes a `DECLARE … CURSOR … FOR <stmt>; OPEN` batch, pulls the engine `Cursor` out of `SimulatedDbConnection.Cursors`, drives `Cursor.Fetch` per row, and runs `UPDATE/DELETE … WHERE CURRENT OF` for positioned edits.
Handle→cursor mapping, the scrollopt/ccopt option translation, and the probed wire contract live in [`tds-endpoint.md`](tds-endpoint.md).

## Sensitivity model (probe-confirmed)

The effective type is resolved at DECLARE from the requested keywords **and** whether the SELECT is navigable — a query whose FROM doesn't reach base tables the cursor can re-fold is forced to STATIC, matching SQL Server's silent conversion.
With a navigable query: explicit `STATIC` / `INSENSITIVE` → STATIC; `KEYSET` → KEYSET; `DYNAMIC` → DYNAMIC; unspecified → KEYSET when `SCROLL` was asked for, DYNAMIC for the forward-only default; `FAST_FORWARD` reads live or settles at OPEN by the shape — see [FAST_FORWARD](#fast_forward).
Two shapes cap the result at KEYSET: a **row limit** anywhere in the shape (see [Row-limited cursors](#row-limited-cursors)) and an **ORDER BY no index delivers** (see [Index-delivered ORDER BY](#index-delivered-order-by)).
Sensitivity and scrollability are separate: naming any of the three implies `SCROLL`, while a cursor that names none stays forward-only *whatever it resolved to* — probe-confirmed that a bare cursor converted to a snapshot (DISTINCT) and one converted to KEYSET (`TOP`) both report Msg 16911 for a scrolling direction, so the test is on the requested keyword, never the effective sensitivity.
A third gate converts KEYSET all the way to a read-only snapshot: every participating base table has to carry a **keyset row locator** (see [The keyset row locator](#the-keyset-row-locator)).

| Type | Membership | Column values | `@@CURSOR_ROWS` | Updatable |
|------|-----------|---------------|-----------------|-----------|
| **STATIC** | frozen snapshot at OPEN | frozen | row count | no (read-only) |
| **KEYSET** | frozen at OPEN (identity set) | re-read live per FETCH | row count | yes |
| **DYNAMIC** | live (inserts appear, deletes vanish) | re-read live per FETCH | `-1` | yes |

- **STATIC** snapshots projected rows once (`Selection.Execute` → decoded `SqlValue[]`); immune to later changes; covers every non-navigable query.
- **KEYSET** snapshots an ordered list of members at OPEN.
  Each FETCH re-enumerates the live base tables (`EnumerateForCursor`) and matches the snapshotted member by the key each base table is identified by — see [What a keyset member keys on](#what-a-keyset-member-keys-on).
  A value change to non-identity columns shows through (status 0); a deleted-or-key-changed member yields `@@FETCH_STATUS = -2`.
- **DYNAMIC** stores no list; it tracks the last-emitted `(ORDER BY key, identity)` and re-enumerates live each FETCH to find the next/prior row by that total order.
  Deletes ahead are silently skipped; inserts ahead appear.

A cursor's *position* rides the row's stable `(page, slot)` heap address — one per base table the plan reads, however many layers of view / derived table / CTE sit above it.
`Heap.UpdateAt` (the in-place / forwarding-pointer machinery in `Storage/Heap.cs`) preserves that address through value updates: a fits-in-place rewrite overwrites the slot's bytes; an oversize rewrite appends the new row elsewhere and installs a single-level forwarding pointer at the original slot.
Either way the row's visible address is unchanged, so positioned `WHERE CURRENT OF` DML reaches the row the cursor sits on whatever key it has.

### The keyset row locator

A T-SQL KEYSET keys on a row locator every participating base table has to carry, and a table with none converts the cursor to **`Snapshot | Read Only`** — positioned DML through it is then **Msg 16929**.
The gate sits where sensitivity resolves (`Simulation.Cursor.cs`), so it catches every route to KEYSET, and it reads `CursorSourcePlan.SupportsKeyset`, which walks the plan's flattened `IdentityTables` — so a deferred body's base tables and both sides of a join are covered.
The converted cursor drops its plan and takes the same STATIC path a non-navigable query takes: membership *and* values freeze at OPEN, `@@CURSOR_ROWS` is the row count, and the API path reports the conversion in its `scrollopt` / `ccopt`.

What counts as a locator, probe-confirmed against `sys.dm_exec_cursors(@@SPID).properties`:

| Table carries | Real | Simulator |
|---------------|------|-----------|
| a PRIMARY KEY, clustered or nonclustered | Keyset | KEYSET |
| a UNIQUE constraint | Keyset | KEYSET |
| a unique nonclustered index, or a unique clustered one | Keyset | KEYSET |
| a **disabled** unique index | Keyset | KEYSET |
| a **non-unique clustered** index | Keyset | KEYSET |
| a unique index on a PERSISTED computed column | Keyset | KEYSET |
| nothing (a bare heap, permanent or `#temp`) | Snapshot / Read Only | STATIC |
| only a non-unique nonclustered index | Snapshot / Read Only | STATIC |
| only a **filtered** unique index | Snapshot / Read Only | STATIC |
| an IDENTITY column and no key | Snapshot / Read Only | STATIC |
| a `rowversion` column and no key | Snapshot / Read Only | STATIC |

Two shapes of the rule read oddly and are probe-confirmed both times.
A *disabled* unique index qualifies — real reads the index's presence in metadata rather than its usability, and the cursor takes positioned DML through it.
A *non-unique clustered* index qualifies because SQL Server's uniquifier makes the clustered key fix one row.
Mere existence anywhere on the table is the whole test: the locator's columns need not be projected, or referenced at all.

Every route to KEYSET takes the gate — an explicit `KEYSET`, the KEYSET a plain `SCROLL` implies, the [row-limit](#row-limited-cursors) cap, the [ORDER BY](#index-delivered-order-by) cap, and the `SET @c = CURSOR KEYSET FOR …` cursor-variable form.
DYNAMIC needs no locator and is untouched: an explicit `DYNAMIC` and the bare forward-only default both stay DYNAMIC over a keyless table.
Across a join, *every* participating table must qualify — one keyless side converts the whole cursor.
`TYPE_WARNING` reports the conversion with **Msg 16956** like any other downgrade, for the implied request as much as a spelled-out one.

**API server cursors are exempt.**
The `sp_cursoropen` family keeps KEYSET over a keyless table and takes positioned `sp_cursor` DML through it, keying on the row address the way this engine always does — probe-confirmed, and the split `sys.dm_exec_cursors` itself reports as `API | Keyset` against the T-SQL cursor's `TSQL | Snapshot | Read Only`.
`SimulatedDbCommand.ApiServerCursor` → `BatchContext.ApiServerCursor` carries the origin from the TDS endpoint's synthesized `DECLARE` / `OPEN` pair to the gate.

### What a keyset member keys on

Each base table's rows are identified by one key, resolved per plan as `CursorIdentityKey` and probed row by row on 2026-09-29 against SQL Server 2025:

| Table carries | A member is | Status after the change |
|---------------|-------------|-------------------------|
| a clustered index, unique (PRIMARY KEY, UNIQUE CLUSTERED, or a unique clustered index) | its key's values | `-2` when an UPDATE changes them, `0` when it assigns them their own value |
| a non-unique clustered index | its key's values plus the uniquifier | `-2` whenever an UPDATE *assigns* a key column, even to its own value, and after a delete and re-insert of the same key |
| a heap with a PRIMARY KEY | the PRIMARY KEY's values | `-2` when an UPDATE changes them |
| a heap with only UNIQUE constraints and unique indexes | the one created first — a disabled unique index counts | `-2` when an UPDATE changes it |
| a heap with none (an API server cursor only — a T-SQL one converts) | its address | `-2` after a delete |

A nonclustered PRIMARY KEY beside a clustered index is not what the member keys on: updating the key moves nothing, while updating the clustered key does.
Collation equality decides a key match (`'b'` → `'B'` keeps a case-insensitive unique member), where the uniquifier makes any assignment a new row.

The uniquifier is `Heap.Uniquifiers`: an UPDATE, MERGE, or ON UPDATE CASCADE that assigns a column of a non-unique clustered key draws the row a fresh value from a per-heap counter (`ClusteredScan.NoteKeyAssignment`), and the undo log restores the old one on rollback — real's rolled-back key update leaves the member whole.
A row never so updated reads 0, so the member matches on its key, its uniquifier and its address together.

A cursor walks a clustered table in its key order — NULLs first under an ascending column, reversed under a descending one — then by uniquifier, and a heap in write order (`CompareCursorRows`), for KEYSET membership and DYNAMIC navigation alike; an ORDER BY's ties fall to the same order, and a join nests each source's.
So a DYNAMIC cursor meets a row whose key an UPDATE moved ahead of it again, a row moved behind it never, and duplicates of a non-unique key in the order their uniquifiers were drawn — insertion order, with a row an UPDATE moved onto the key after the ones already there.

### Which shapes are navigable

Navigability resolves in two passes.
`ComputeCursorShape` runs at SELECT-parse time (beside the view-updatability capture in `Selection.Execution.cs`) and rejects the statement-level constructs no source set can rescue: DISTINCT, an aggregate / GROUP BY / HAVING, a window function, a set-op chain, a parenthesized join group, and any join kind outside INNER / CROSS / LEFT / RIGHT / FULL / CROSS APPLY / OUTER APPLY.
A `TOP` / `OFFSET` / `FETCH` limit is *not* a rejection — it rides along as `CursorShape.RowLimit`, unresolved so its operands re-evaluate against the batch that OPENs.
`TryBuildCursorPlan` then runs at **DECLARE CURSOR** time and resolves each FROM slot, which is what makes a cursor KEYSET / DYNAMIC-eligible.
Deferring the second pass is load-bearing: a view slot's body has to be parsed to see what it reads, and DECLARE is both cheap enough to afford that and the point real SQL Server fixes the cursor's plan at.

A slot resolves when it is a direct base-table scan, or a **deferred body the cursor can follow** — a derived table, a CTE reference, an APPLY right side, or a view — whose own shape resolves in turn, to any depth.
A slot whose rows a *generator* produces (a TVF, a catalog view, `VALUES`, `OPENJSON`, PIVOT, `.nodes()`, a linked server) never resolves, nor does a `FOR SYSTEM_TIME` source; one unresolved slot forces the whole cursor to STATIC.
Both of those match real, which reports the same shapes as read-only snapshots.

Probed against SQL Server 2025 with `sys.dm_exec_cursors(@@SPID).properties`, which reports the effective type:

| Shape | Real | Simulator |
|-------|------|-----------|
| single base table | Dynamic | DYNAMIC |
| 2-, 3-table JOIN; LEFT / RIGHT / FULL / CROSS; comma FROM; self-join | Dynamic | DYNAMIC |
| JOIN + WHERE, JOIN + ORDER BY on an indexed column | Dynamic | DYNAMIC |
| CROSS / OUTER APPLY | Dynamic | DYNAMIC |
| derived table (with WHERE, over a join, nested), CTE | Dynamic | DYNAMIC |
| view over one table, view over a join, view over a view, derived table over a view, view joined to a base table | Dynamic | DYNAMIC |
| TVF (`STRING_SPLIT`), `OPENJSON`, `VALUES` constructor | Snapshot / Read Only | STATIC |
| `FOR SYSTEM_TIME` (`AS OF` / `ALL` / `BETWEEN` / `FROM…TO` / `CONTAINED IN`, with or without `SCROLL`) | Snapshot / Read Only | STATIC |
| `TOP n` / `TOP n PERCENT` / `TOP n WITH TIES` / `OFFSET…FETCH` (also inside a derived table, CTE or view body) | Keyset | KEYSET |
| ORDER BY a column no index delivers | Keyset | KEYSET |
| DISTINCT, GROUP BY, set op (also inside a derived table or view body) | Snapshot / Read Only | STATIC |
| any KEYSET row over a table carrying no [keyset row locator](#the-keyset-row-locator) | Snapshot / Read Only | STATIC |

The conversion boundary follows the deferred body's own constructs: a view whose body carries DISTINCT or GROUP BY is a read-only snapshot on both, a view whose body carries TOP is Keyset on both, and `DECLARE … DYNAMIC TYPE_WARNING` fires Msg 16956 for exactly those and stays silent for a plain view (probe-confirmed).

### Row-limited cursors

A `TOP n` / `TOP n PERCENT` / `TOP n WITH TIES` / `OFFSET … FETCH` limit stays navigable and **caps sensitivity at KEYSET**: the limit chooses which rows are members at OPEN, so there is no live set left for DYNAMIC to walk.
Probe-confirmed against `sys.dm_exec_cursors` — real reports `Keyset` with the limited row count for every one of those forms, whether the limit sits on the cursor's own statement or inside a derived table, a CTE or a view body, and whether the cursor asked for `SCROLL` or took the bare forward-only default.
`CursorSourcePlan.HasRowLimit` (this plan's own limit or any nested slot's) is what performs the cap, so `DYNAMIC` → KEYSET and the bare default → KEYSET; `DECLARE … DYNAMIC TYPE_WARNING` then fires Msg 16956 and `KEYSET TYPE_WARNING` stays silent, matching real.
The cap does **not** make the cursor scrollable: a bare row-limited cursor is still forward-only, and a scrolling direction there is Msg 16911 (including `ABSOLUTE`, since 16925 is dynamic-sensitivity only).

`EnumerateForCursor` takes an `applyRowLimit` flag, true only at OPEN, and applies the limit after the ORDER BY sort through the same `ComputeTopCap` the read path uses — so the rows admitted are exactly the rows the equivalent SELECT returns, `PERCENT`'s ceiling and `WITH TIES`'s boundary extension included.
Per FETCH the flag is false, which is the probed semantic: **membership is frozen**, so a member pushed out of the window by a mid-loop insert still fetches with status 0 and its live values, and only a genuinely deleted (or key-changed) member reports `@@FETCH_STATUS = -2`.

One exception, probe-confirmed: a limit written inside a **view** body re-evaluates on every FETCH, so a member the view no longer returns fetches as `-2` even though its base row still exists.
A derived table's or CTE's limit doesn't (real inlines those, landing the limit on the statement).
`AppendCursorSlotRows` therefore re-raises the flag for a slot with a `ThroughView`, which also makes it compose outward — a TOP view read through a derived table is re-evaluated too.

Everything else about a row-limited cursor is ordinary KEYSET: values re-read live, positioned `WHERE CURRENT OF` UPDATE / DELETE reach the base row, and `SCROLL` makes `ABSOLUTE` position within the limited membership.
Covered by `CursorRowLimitTests`.

### Index-delivered ORDER BY

An ORDER BY an index delivers leaves the cursor DYNAMIC; one the plan would have to sort for **caps sensitivity at KEYSET**, since a sorted result is a materialized one.
Probe-confirmed against `sys.dm_exec_cursors`: `ORDER BY` an indexed column reports `Dynamic` with `@@CURSOR_ROWS = -1`, an unindexed one `Keyset` with the row count, and the converted cursor then behaves as an ordinary KEYSET — membership frozen at OPEN, values re-read live, a deleted member `@@FETCH_STATUS = -2`, positioned DML reaching the base row.
`CursorSourcePlan.OrderBySuppliedByIndex` performs the cap alongside `HasRowLimit`, so `DYNAMIC` → KEYSET and the bare default → KEYSET, and neither makes the cursor scrollable.

Each ORDER BY item resolves down to the base-table column it reads — through a positional ordinal, an output alias, a view's or derived table's projection, however deep — and the items are then grouped into maximal consecutive runs per base table.
A run is delivered when some key on its table matches it as a **leading prefix in a consistent direction** (all forward, or all reversed — the backward scan real does for `ORDER BY … DESC`); a run that items from another table follow must additionally be **unique**, since only a key that fixes one row leaves the next source free to decide the rest.
Items past a unique prefix of the same table are redundant and drop out, and an item reading no column at all (`ORDER BY (SELECT NULL)`) constrains no order — real drops it too.
The candidate keys are the table's PRIMARY KEY / UNIQUE constraints and its enabled unfiltered indexes; a non-unique index over a table with a clustered key carries that key's columns after its own (real appends the clustering key as the row locator) and becomes unique with them.

Probe-confirmed row by row, all covered by `CursorOrderCoverageTests`:

| ORDER BY | Real | Simulator |
|----------|------|-----------|
| the clustered PK, ASC or DESC; a nonclustered index's column; a `DESC` index's column either way | Dynamic | DYNAMIC |
| a unique prefix followed by anything of the same table (`id, v`) | Dynamic | DYNAMIC |
| a nonclustered index's column then the clustering key (`v, id`) | Dynamic | DYNAMIC |
| a composite index's prefix (`a` / `a, b`), uniformly reversed (`a DESC, b DESC`) | Dynamic | DYNAMIC |
| a positional ordinal, an output alias, or a view's renamed column resolving to any of the above | Dynamic | DYNAMIC |
| across a join, one indexed run per source, each but the last unique (`a.id, b.id`) | Dynamic | DYNAMIC |
| an unindexed column, on the statement or through a view / derived table / CTE | Keyset | KEYSET |
| a composite out of order (`b, a`), past its length (`a, b, c`), or with mixed directions (`a ASC, b DESC`) | Keyset | KEYSET |
| a filtered index's column (with no matching WHERE), a disabled index's column | Keyset | KEYSET |
| a keyless heap's unindexed column | Snapshot / Read Only | STATIC |
| an order-preserving expression over an indexed column (`v + 1`, `v * 2`, `CAST(v AS bigint)`) | Dynamic | *KEYSET* |
| a filtered index's column with the WHERE the filter matches | Dynamic | *KEYSET* |
| a unique run whose following run is decided by the join rather than an index (1:1 `a.id, b.c`) | Dynamic | *KEYSET* |

The keyless-heap row lands on a snapshot because the KEYSET the cap produces then meets the [row-locator gate](#the-keyset-row-locator); the two conversions compose.
The italicised rows are the residuals — see [Divergences](#divergences-from-sql-server-documented-not-byte-identical).
Real decides this on the finished plan (it converts exactly when the plan carries a Sort), so its reasoning reaches order-preserving expressions and functional dependencies the prefix rule doesn't; each residual converts where real wouldn't, which is the direction the conversion already runs in.

### Multi-source navigation

A cursor's position is the **flattened tuple of stable addresses** of every base table the plan reads — one entry per base-table scan anywhere in the tree, depth-first in slot order, with a null entry on the NULL-extended side of an outer join or an empty OUTER APPLY.
`CursorSourcePlan.SlotIdentityOffset` / `SlotIdentityWidth` locate each FROM slot's contiguous span, so a slot backed by a view over a join contributes two entries and a base-table slot one.
`Cursor.CurrentRids` and `CursorRow.Rids` are arrays of that width, which is what lets positioned DML reach a base table nested arbitrarily deep without the cursor knowing how it got there.

`EnumerateForCursor` scans each slot into a `CursorSlotScan` (bytes + the slot's identity span + its unique keys), folds the JOIN chain left-deep into row-index tuples (`FoldCursorTuples`), then runs the WHERE excluders and projections through the shared hoisted resolver — so the whole shape is re-derived on every FETCH and mid-loop changes anywhere in it show.
A base-table slot walks its heap directly; a deferred slot re-enters the enumeration on its own nested plan and **re-encodes** the body's projected values into the bytes the outer plan's column resolution decodes (the source's declared `StoredSchema` — the same layout the deferred source's row stream carries on the ordinary read path), carrying the body's own addresses through unchanged.
That re-encode is the whole cost of the nesting: one row encode per deferred row per FETCH, on a path that already re-scans every participating heap per FETCH.
An APPLY right side correlates with the left, so it has no up-front scan — the fold appends its rows to the shared slot scan per left tuple, with `OUTER APPLY` null-filling an empty result.

Probe-confirmed consequences, all covered by `CursorMultiSourceTests` and `CursorDeferredSourceTests`:

- An UPDATE to a column on either side between FETCHes is visible on the next FETCH.
- A row inserted mid-loop appears — including a row inserted into the *inner* side of a CROSS JOIN, against an outer row the cursor hasn't reached yet.
- A row whose partner is deleted mid-loop silently vanishes from a DYNAMIC cursor and yields `@@FETCH_STATUS = -2` on a KEYSET one.
- `@@CURSOR_ROWS` is `-1` for the forward-only default and the row count for `SCROLL` (which resolves to KEYSET, exactly as on a single table — probe-confirmed for a view and a derived table too), and `FETCH ABSOLUTE` on a `SCROLL DYNAMIC` join cursor is Msg 16925 while `RELATIVE` walks.
- A view's own WHERE bounds cursor membership, and an APPLY cursor walks exactly the correlated pairs — the deferred body's predicate runs inside the fold, not as a post-filter.

The fold is a plain nested loop: the equi-join hash / seek strategies of the read path ([`joins.md`](joins.md#joindriver)) don't apply, because every intermediate row must keep its per-source address and the cursor re-folds per FETCH regardless.
Cursors are the row-at-a-time slow path, and this matches the single-source design, which already re-scans the base heap on every FETCH.

## FAST_FORWARD

A `FAST_FORWARD` cursor — named so, or one that is forward-only and read-only (`READ_ONLY` or `FOR READ ONLY`, with neither `SCROLL` nor a sensitivity keyword), which `sys.dm_exec_cursors` reports as `Fast_Forward` — runs its plan as the fetches go (probed 2026-09-29 against SQL Server 2025).
Over a navigable shape whose ORDER BY an index delivers it reads live rows: an insert ahead appears, an update shows, a delete ahead vanishes, and a row whose clustered key moved ahead comes round again — `CursorSensitivity.Dynamic` with `Cursor.FastForward` set.
A plan that has to finish a sort or a row limit first, and a non-navigable shape, settle every row and value at OPEN — `CursorSensitivity.Static` — and neither needs a row locator.
Either way `@@CURSOR_ROWS` reads -1 and `CURSOR_STATUS` 1 while open, an empty cursor included, TYPE_WARNING never fires, and `ABSOLUTE` is the forward-only Msg 16911 rather than the dynamic Msg 16925.
An API server cursor's FORWARD_ONLY READ_ONLY request keeps its own negotiation and isn't read as FAST_FORWARD — see [`tds-endpoint.md`](tds-endpoint.md#api-server-cursors-sp_cursor-rpc-family).

## FETCH

`FETCH [NEXT|PRIOR|FIRST|LAST|ABSOLUTE n|RELATIVE n] [FROM] <cursor> [INTO @v,…]` — a direction takes the `FROM`, `FETCH NEXT c` being Msg 102 near the direction (probed 2026-10-02).

- **Scrollability**: naming a sensitivity implies `SCROLL` — `STATIC`, `KEYSET` *and* `DYNAMIC` all scroll unless `FORWARD_ONLY` / `FAST_FORWARD` says otherwise (probe-confirmed).
  A cursor that names none is forward-only and allows only `NEXT`.
- **`ABSOLUTE` on a dynamic-sensitivity cursor** other than a FAST_FORWARD one → **Msg 16925** (`"The fetch type Absolute cannot be used with dynamic cursors."`, direction title-cased).
  Real checks this *before* scrollability, so a bare `FORWARD_ONLY` cursor — which defaults to dynamic sensitivity — reports 16925 for `ABSOLUTE` and 16911 for everything else.
- **Any other non-`NEXT` direction on a non-scrollable cursor** → **Msg 16911** (`"fetch: The fetch type prior cannot be used with forward only cursors."`), whose direction name is **lower-cased**, unlike 16925's.
- **`RELATIVE` is legal on a scrollable DYNAMIC cursor** and walks the live set one row at a time, since there's no stable ordinal to jump to; a zero offset re-reads the current row.
  `ABSOLUTE` is the only direction dynamic sensitivity rejects.
- **INTO** assigns the projected columns to the variables (coerced to each declared type).
  A count mismatch raises **Msg 16924** regardless of whether the FETCH lands on a row.
  On a successful fetch the variables are written; on `-1` (past end) they retain their prior value (probe-confirmed).
- **Without INTO** every FETCH yields a result set — one row when it lands, empty past either end — whose columns end in a hidden `ROWSTAT` int (`VisibleFieldCount` excludes it, and so does `GetValues`), matching what SqlClient reports against SQL Server 2025 (probed 2026-09-25).
  ROWSTAT is 1 for a fetched row and 2 for a deleted keyset member.
- **A deleted keyset member (`-2`) still lands on a row**, which reads as NULL in each column the projection reports nullable and as the type's zero in the rest — 0, the empty GUID, 1900-01-01 for `datetime`, 0001-01-01 for the newer dates, a bounded string or binary filled to its declared length with spaces or zero bytes, an empty `max` value.
  That row goes into INTO variables as well as into the result set (probed 2026-09-25).
- `@@FETCH_STATUS`: `0` success, `-1` past end / no row, `-2` keyset member deleted; a fetch that fails — a direction refused, a row whose projection raises — reads `-1` (probed 2026-10-02).
- `@@CURSOR_ROWS` reads 0 once a `CLOSE` or `DEALLOCATE` ran (probed 2026-10-02).
- **A table redefined since the cursor opened** — a column added or dropped, an index created — fails a KEYSET, DYNAMIC or FAST_FORWARD cursor's next fetch with **Msg 16943** state 4, while a STATIC cursor reads on from the rows it copied (probed 2026-10-03 against SQL Server 2025).
  The cursor notes each table's `HeapTable.DefinitionVersion` as it opens; a fetch once decoded rows in the new layout with the old one and failed outside any SQL error.

## WHERE CURRENT OF

`UPDATE t SET … WHERE CURRENT OF c` / `DELETE FROM t WHERE CURRENT OF c` target exactly the row the cursor is positioned on, found by matching the address the cursor recorded for the identity slot the target resolved to (`PositionedCursorTarget` + `CursorRowMatches`).
The UPDATE / DELETE parsers branch in their WHERE clause: `Keyword.Current` → `ParseWhereCurrentOf`, otherwise a normal boolean WHERE.
The SI tombstone pre-flight is skipped for positioned DML (the cursor already fixed a single live row).
A positioned write that fails on the cursor — no row fetched (Msg 16931), a column outside `FOR UPDATE OF` (Msg 16932) — is followed by Msg 3621, as an ordinary write's error is (probed 2026-10-02).

**The target names a table, not a cursor alias**: `UPDATE a SET …` where `a` is only the cursor's alias is Msg 208 (`Invalid object name 'a'`) from ordinary name resolution, matching real.

### Reference provenance

The target is matched by the reference **as written**, not by the base table behind it — real resolves positioned DML against the reference the cursor's own FROM used, and the simulator carries that as `CursorSourcePlan.IdentityViews` (the view stamping each identity entry, or null).
`ParseWhereCurrentOf` receives the view the statement named alongside the base table the UPDATE / DELETE parser already resolved it to, and both must agree.
Probe-confirmed:

| Cursor reads | Statement must name | Naming anything else |
|--------------|--------------------|----------------------|
| base table `t` | `t` | Msg 16933 |
| view `v` (over one table, over a join) | `v` | Msg 16933 — including the base table under it |
| view `vv` over view `v` | `vv` | Msg 16933 — including the inner `v` *and* the base table |
| derived table / CTE / APPLY body over `t` | `t` | Msg 208 for the alias / CTE name, Msg 16933 for an unrelated table |
| derived table / CTE over view `v` | `v` | Msg 16933 for the base table |

A view is opaque and a query body is transparent, so the two compose: `(SELECT … FROM v) d` is addressed by `v`, and a view whose body reads a derived table is addressed by the view.
Everything the view's own DML path enforces then applies to the positioned write — a `WITH CHECK OPTION` view raises **Msg 550** when the new value would leave the view (probe-confirmed), a DELETE through the same view is unaffected, and a view with a plain WHERE accepts a write that pushes the row out of range.
A view over a JOIN carries its own rule with it: a positioned UPDATE whose SET list lands in one base table writes through, one spanning both is **Msg 4405**, and a positioned DELETE is **Msg 4405** — see [`programmable.md`](programmable.md#dml-through-a-join-view).

`ParseWhereCurrentOf` validates, in this order:

| Condition | Error |
|-----------|-------|
| cursor is read-only (STATIC / FAST_FORWARD / `FOR READ ONLY`) | **Msg 16929** `The cursor is READ ONLY.` + **Msg 3621** (probed 2026-09-29) |
| the reference isn't one the cursor reads, or a `FOR UPDATE OF` list names none of the slot's surface columns | **Msg 16933** `The cursor does not include the table being modified or the table is not updatable through the cursor.` + **Msg 3621** (probed 2026-10-01) |
| the reference reaches more than one identity slot (self-join, including a self-joined view) | **Msg 16961**, severity 0 info — binds the *first* instance and continues |
| cursor isn't positioned on a row (before first FETCH, past the end) | **Msg 16931** `There are no rows in the current fetch buffer.` |
| the target's slot is the NULL-extended side of an outer join or an empty OUTER APPLY, **or** the cursor sits on a keyset hole (last FETCH reported `-2`) | **Msg 16947** + **Msg 3621** `No rows were updated or deleted.` |
| a positioned UPDATE assigns a column outside the `FOR UPDATE OF` list | **Msg 16932** |
| OPTIMISTIC cursor whose row changed out-of-band | **Msg 16947** + **16934** + **3621** |

All probe-confirmed, including the split real makes between 16933 and 16931: naming an unrelated table is 16933 even when the cursor *is* positioned, while naming a correct table before any FETCH is 16931.
The off-a-row cases split by cause, also probe-confirmed: before the first FETCH and past the end are 16931, while a keyset hole is 16947 — the member is gone, so there is nothing to update rather than nothing in the buffer (`Cursor.OnKeysetHole`, set from the FETCH status).
Msg 16947 without the descriptive 16934 is the NULL-extended / keyset-hole case; the OPTIMISTIC conflict adds 16934 — and that detection reaches a base row behind a view, since it reads the flattened address the cursor recorded.
A cursor reading a partitioned view is a read-only snapshot, and a positioned write naming the partitioned view while one reads it ends the session — see [`programmable.md`](programmable.md#partitioned-views).

## Scope: GLOBAL vs LOCAL

Two independent cursor namespaces, both probe-confirmed against SQL Server 2025:

- **GLOBAL** cursors live on `SimulatedDbConnection.Cursors` and persist for the connection (visible across GO-separated batches).
- **LOCAL** cursors live on `BatchContext.LocalCursors` and are implicitly deallocated when the frame (batch / procedure / trigger body) exits — `DeclareCursorInScope` picks the map, `TeardownFrameCursors` (called in the batch `finally` and after proc invocation) releases them.

Default scope is the database's `CURSOR_DEFAULT` option — **GLOBAL** as installed (`is_local_cursor_default = 0` for every system and freshly-created database), LOCAL once `ALTER DATABASE … SET CURSOR_DEFAULT LOCAL` sets it (probed 2026-10-02 against SQL Server 2025).
A name may exist in **both** scopes at once.
Resolution at a use site (`OPEN` / `CLOSE` / `DEALLOCATE` / `FETCH … FROM` / `WHERE CURRENT OF`):

- Unqualified name → **LOCAL first, then GLOBAL** (probe-confirmed: an unqualified `OPEN c` / `FETCH c` binds the LOCAL `c` when both exist).
- `GLOBAL name` → the global map only.
- Unqualified `DEALLOCATE` removes from LOCAL first (probe-confirmed).

`ResolveCursor` / `ReadCursorReference` in `Simulation.Cursor.cs` centralize this; the use-site parsers all route through them.
`CURSOR_STATUS(scope, name)` is scope-aware: `'local'` / `'global'` consult the respective named map, `'variable'` consults the cursor-variable namespace, and asking the wrong scope returns `-3`.
A NULL or unknown source, or a NULL or empty name, raises Msg 16902 (the source judged first), so the function never answers NULL.

## Cursor variables

`DECLARE @c CURSOR` registers an unallocated slot in `BatchContext.CursorVariables` (a namespace parallel to scalar `Variables` and `TableVariables`; `DECLARE @c CURSOR` routes through `TryParseDeclare`'s CURSOR case).
A cursor variable is a **refcounted reference** to a shared `Cursor` object, so multiple variables share one cursor — **including position** (a fetch on either advances the same cursor).
Probe-confirmed matrix:

| Operation | Effect |
|-----------|--------|
| `DECLARE @c CURSOR` | slot = null; `CURSOR_STATUS('variable','@c')` = **-2** |
| `SET @c = CURSOR [opts] FOR <select>` | builds an unnamed cursor (`IsUnnamed`, refcount 1); status **-1** until OPEN |
| `SET @c2 = @c` / `SET @c = named_cursor` | shares the referenced cursor (refcount++) |
| `OPEN`/`FETCH`/`CLOSE`/`DEALLOCATE @c` | operate on the referenced cursor |
| `DEALLOCATE @c` | drops this variable's reference, returns the slot to -2; the cursor is destroyed only when the last reference goes (`ReleaseVariableReference`) |
| `FETCH … FROM @c` on an unallocated slot | **Msg 16950** (`"The variable '@c' does not currently have a cursor allocated to it."`, class 16 state 2) |

`SET @c = CURSOR …` reuses the shared `BuildCursorDefinition` parser (the same one `DECLARE name CURSOR` uses).
Refcount changes flow through `RebindCursorVariable` (release old, increment new) and `ReleaseVariableReference` (decrement, destroy unnamed-at-zero).

**Cursor OUTPUT parameters** (`CREATE PROC p @c CURSOR VARYING OUTPUT AS …`): the parameter parses as `IsCursor` (output-only), seeds an unallocated cursor variable in the proc's child frame, and — after the body `SET`s + `OPEN`s a cursor on it — the invocation binds that cursor back into the caller's cursor variable (refcounted, so it survives the proc frame's `TeardownFrameCursors`).
The EXEC `@c OUTPUT` argument carries the caller's variable name through `ProcArgument.CursorVariableName`.
Probed 2026-09-29 against SQL Server 2025: the parameter must be written `CURSOR VARYING OUTPUT`, in that order (**Msg 1051**, at CREATE); only an argument written `OUTPUT` receives the cursor, and only an open one; a scalar variable passed there is **Msg 206** and a cursor variable already holding a cursor **Msg 16951**, both before the body runs.
`DEALLOCATE` of an unallocated cursor variable is Msg 16950, as `OPEN` and `FETCH` of one are.

## Declaration grammar refusals

Contradicting options are **Msg 1048** naming the pair — in an order fixed per pair, not the order written — raised while the batch compiles, so nothing in it runs (probed 2026-09-29 against SQL Server 2025).
`ConflictingCursorOptions` in `Simulation.Cursor.cs` lists the pairs in the order real checks them, the first present being the one reported; `FOR READ ONLY` counts as `READ_ONLY`, and the SQL-92 `INSENSITIVE` conflicts with `FOR UPDATE`.
Real reports the error at the token after the declaration, or at its last line when the batch ends there.
Writing the same option twice is legal.
`READ_ONLY` beside `FOR READ ONLY` is **Msg 1058**; `INSENSITIVE` after `CURSOR` is **Msg 153** (the word belongs to the SQL-92 prefix, and is named lower-cased, for a `SET @c = CURSOR` too); and a SQL-92 prefix (`INSENSITIVE` / `SCROLL` before `CURSOR`) followed by any T-SQL option is **Msg 1049**.
The SQL-92 `INSENSITIVE` cursor is a forward-only snapshot unless `SCROLL` is written too.
A query carrying `SELECT … INTO` is **Msg 154** state 3 (probed 2026-10-02).

**Variables read at `DECLARE`.**
Every cursor type reads the variables its query names as the `DECLARE` found them, whatever they hold at `OPEN` or a later `FETCH` (`BatchContext.CursorDeclarationSnapshot`, which the query's variable references copy; probed 2026-10-02 against SQL Server 2025).
A cursor variable read or assigned as a scalar — `SELECT @c = 1` — is **Msg 16949**.

## FOR UPDATE OF

`FOR UPDATE OF (col, …)` captures the column list on the cursor (`Cursor.ForUpdateColumns`).
The list binds against the query's FROM sources while the batch compiles, whatever the cursor resolves to (probed 2026-09-29 against SQL Server 2025): a name no source carries — an output alias counts for nothing — is **Msg 207**, one only a constructed rowset such as `VALUES` carries is **Msg 412**, and an unprojected column, a joined table's, a view's own and a qualified `t.v` all bind.
A positioned `UPDATE … WHERE CURRENT OF` that assigns a column absent from the list raises **Msg 16932** (`"The cursor has a FOR UPDATE list and the requested column to be updated is not in this list."`).
`FOR UPDATE` without an OF list leaves every column updatable.
`ParseWhereCurrentOf` receives the UPDATE's assigned columns and checks them via `Cursor.IsColumnUpdatable`; DELETE passes null (no column gate).
FAST_FORWARD / STATIC / `FOR READ ONLY` cursors are implicitly read-only → positioned DML raises **Msg 16929** as before.

Over a multi-table cursor the list also narrows the updatable **slots** to those owning a listed column (`Cursor.IsSlotUpdatable`), so a positioned UPDATE *or* DELETE naming any other participating reference is **Msg 16933**, not 16932 — probe-confirmed: with `FOR UPDATE OF v` on `a JOIN b`, `DELETE FROM b … WHERE CURRENT OF` is 16933 while `UPDATE a SET id = …` is 16932 and `DELETE FROM a` succeeds.
A slot a view stamps is matched against the **view's** output columns rather than the base table's, so a `FOR UPDATE OF` list naming a renamed view column narrows as written.

## Concurrency: SCROLL_LOCKS and OPTIMISTIC

`Cursor.Concurrency` (`Default` / `ScrollLocks` / `Optimistic`) is resolved at DECLARE for updatable cursors (read-only cursors ignore it).

- **SCROLL_LOCKS** holds a **cursor-scoped U lock** on the currently-fetched row plus a table-IX for the cursor's open lifetime.
  The locks live directly on the `Cursor` (`scrollTableLock` / `scrollRowLock`), *not* in the statement / transaction release lists — probe-confirmed they persist across autocommit statement boundaries while the cursor is positioned.
  Each FETCH moves the U onto the new row (`MoveScrollLock` releases the row scrolled off); a concurrent writer of the held row blocks (U-X conflict), a writer of any other row proceeds.
  Positioned UPDATE upgrades the row to X through the normal writer path (the cursor's U and the writer's X coexist under same-owner re-entrance).
  Locks release on CLOSE, the last DEALLOCATE, frame teardown (LOCAL), and connection dispose (`ReleaseScrollLocks`).
  See [`locking.md`](locking.md).
- **OPTIMISTIC** holds no lock.
  At each FETCH the row's full stored bytes are snapshotted (`optimisticSnapshot`); a positioned UPDATE / DELETE re-reads the live bytes at the row's address and, if they differ (a value change, a rowversion bump, or the row's deletion), raises the optimistic-conflict chain: **Msg 16947** (`"No rows were updated or deleted."`, class 16 state 1 — the number a SqlClient consumer catches) plus the descriptive class-0 **Msg 16934** (`"Optimistic concurrency check failed. The row was modified outside of this cursor."`) and **Msg 3621**, all reproduced in `SimulatedSqlException.Errors`.
  A full-row byte compare subsumes both of real SQL Server's detection bases — the rowversion column when the table has one (its bytes change on any update), a column checksum otherwise.
  The compare comes after the write's U has waited out the row's other writers (`Cursor.CheckOptimisticConflict`), as real's does (probed 2026-10-03 against SQL Server 2025: `LCK_M_U`, then the conflict chain for a row deleted and inserted again meanwhile); comparing first let the write find its row gone afterwards and report success having changed nothing.
- **A KEYSET cursor reads committed rows.**
  OPEN reads its keyset and each FETCH its member as a READ COMMITTED read would — waiting out the writers of the rows it lands on, and the deletes in flight on its tables when a member is missing, then reading again while a wait let a write land (`Cursor.ReadKeyset`, `Cursor.SettleKeysetFetch`); under `SCROLL_LOCKS` the fetch's U is that wait (probed 2026-10-03 against SQL Server 2025: `LCK_M_S` on a member's key another transaction holds, then the row it committed or restored).
  Both once read the rows as the heap held them, so a member another transaction was deleting and putting back was missing from the keyset or fetched as deleted (-2), and a row that transaction then rolled back was read dirty.

## TYPE_WARNING

`TYPE_WARNING` emits **Msg 16956** (`"The created cursor is not of the requested type."`, info severity via `BatchContext.AppendInfoError`) at **DECLARE** time (probe-confirmed, not OPEN) when the requested sensitivity was silently converted to a lesser one — DYNAMIC / KEYSET over a non-navigable shape (DISTINCT, GROUP BY, aggregate, set op, a generator source, or a deferred body carrying any of those) forced to STATIC, DYNAMIC over a row limit or an unindexed ORDER BY forced to KEYSET, or KEYSET over a table carrying no [row locator](#the-keyset-row-locator) forced to STATIC.
The request the keywords **imply** counts as much as one spelled out, probe-confirmed in both directions: a cursor naming no sensitivity (or only `FORWARD_ONLY`) implies DYNAMIC and warns when that converts, and a plain `SCROLL` implies KEYSET and warns when *that* converts to a snapshot.
`STATIC` / `INSENSITIVE` / `FAST_FORWARD` never warn — a snapshot is what they asked for.
It surfaces through the standard `InfoMessage` pipeline.
A deferred body the cursor *can* follow warns about nothing, matching real: `DECLARE … DYNAMIC TYPE_WARNING` over a plain view is silent on both, and over a DISTINCT or TOP view fires on both (probe-confirmed).

## `SET CURSOR_CLOSE_ON_COMMIT`

With the option on, a `COMMIT` or `ROLLBACK` that ends a transaction closes every cursor opened inside it — static and cursor-variable cursors included — so a later `FETCH` is Msg 16917 and `CURSOR_STATUS` reads -1 (probed 2026-09-28 against SQL Server 2025).
A cursor opened before the transaction began stays open, as does one when an inner `COMMIT` only decrements `@@TRANCOUNT`, when a rollback to a savepoint ends nothing, or when an auto-commit statement's own transaction ends.
With the option off, neither `COMMIT` nor `ROLLBACK` closes anything, a dynamic or keyset cursor included.
`Cursor.Open` records itself on the session's transaction (`SimulatedDbTransaction.OpenedCursors`), whose end closes the list when the option is on.
`@@OPTIONS & 4` reports the option, `SET ANSI_DEFAULTS ON` turns it on, and a procedure's `SET` of it reverts on return — see [`session-options.md`](session-options.md).

## Describing cursors

`sp_cursor_list @cursor_return OUTPUT, @cursor_scope` and `sp_describe_cursor` / `_columns` / `_tables @cursor_return OUTPUT, @cursor_source, @cursor_identity` answer through a cursor variable they allocate: a scrollable read-only snapshot of their rows (probed 2026-09-29 against SQL Server 2025).
A cursor row carries the reference name, the cursor's name (a cursor a variable's `SET` built is named by that variable, whichever variable reaches it), scope (1 local, 2 global), `CURSOR_STATUS`, model (1 static, 2 keyset, 3 dynamic, 4 fast forward), concurrency (1 read-only, 2 scroll locks, 3 optimistic — the default of an updatable cursor), scrollability, open state, the qualifying row count (0 closed, -1 dynamic or fast forward), the cursor's own last fetch status (-9 before any, kept through CLOSE), column count, and the last operation with its row count (`Cursor.LastOperation`: 1 OPEN, 2 FETCH, 4 positioned UPDATE, 5 positioned DELETE, 6 CLOSE).
`sp_cursor_list` lists the scope's cursors in declaration order, local cursors and cursor variables at scope 1; an out-of-range scope is an informational Msg 16902 that leaves the variable unallocated.
A column row's flags are 0x2 for a fixed-length type, 0x4 for a column the projection reports nullable, and 0x10 for one a positioned UPDATE may assign — never through a read-only cursor, for a rowversion or a computed column, and only for listed columns under `FOR UPDATE OF`; the size is the storage length, 2147483647 for a `max` or LOB type; the ordering columns are always 0 / NULL.
A table row names each table or view the query reads — a view as itself, a `#temp` table under tempdb — for a static cursor as much as a keyset one, with no hint or lock type.
A NULL or unknown source is Msg 16902 (state 40 / 42), a NULL identity Msg 16902 state 43, a missing cursor Msg 16916 state 4, and an undeclared variable Msg 137 state 100, each from the procedure's own line.

### `sys.dm_exec_cursors`

`sys.dm_exec_cursors(session_id)` lists every cursor a session has declared, open or not, with real's 22 columns and shapes (probed 2026-09-30 against SQL Server 2025); 0 or NULL lists every session's, and a session id nobody holds none.
A session without `VIEW SERVER STATE` sees only its own, and the wrong argument count is Msg 313 / Msg 8144 at line 12, as for the other `sys.dm_exec_*` functions.

- **`properties`** is `TSQL | <type> | <concurrency> | <scope> (0)`: `Dynamic`, `Keyset`, `Snapshot` or `Fast_Forward`, then `Read Only`, `Optimistic` or `Scroll Locks`, then `Local` for a `LOCAL` cursor and `Global` otherwise, a cursor variable's included, which is named by its variable (`@cv`).
- **`sql_handle`** is the declaring batch's handle, and **`statement_start_offset`** / **`statement_end_offset`** the byte offsets of the `DECLARE`'s first and last characters in it, so `sys.dm_exec_sql_text` and a substring recover the declaration.
- **`statement_sql_handle`** / **`statement_context_id`** are the `DECLARE` text's Query Store statement handle (real's MD5 derivation, so the bytes match) and the store's context settings id for the session's settings — the declaration adds that row as real's does — while the database's store is READ_WRITE, and NULL with it off.
- **`fetch_status`** is the cursor's own last fetch status: -9 before any, kept through `CLOSE` and a re-`OPEN`.
- **`fetch_buffer_size`** is 1 while the last `FETCH` since `OPEN` landed on a row, else 0.
- **`fetch_buffer_start`** is 0 before a `FETCH` since `OPEN`, before the first row and while closed, -1 past the last row, and on a row its 1-based position for a STATIC or KEYSET cursor and -1 for a DYNAMIC or FAST_FORWARD one.
- **`is_open`** follows `OPEN` / `CLOSE`; `is_async_population` and `is_close_on_commit` read 0 — the latter for a cursor declared under `SET CURSOR_CLOSE_ON_COMMIT ON` too, as real reports one before its `OPEN` — and `ansi_position` 1.

## Divergences from SQL Server (documented, not byte-identical)

- **`sys.dm_exec_cursors`' cost and plan columns** — `worker_time`, `reads`, `writes` and `dormant_duration` read 0 and `plan_generation_num` 1, where real's count work done and recompiles (a cursor declared in the batch that created its table reports its statement's recompile count there); `cursor_id` is the simulator's own handle numbering, and `statement_context_id` the simulator's own store's id.
- **`sys.dm_exec_cursors` for another session** lists only its global cursors: its local cursors and cursor variables live on the batch it is running, which only the querying session reaches.
- **A cursor declared in a module body** reports offsets into the body's text and a handle of it, where real's are into the module's whole definition.

- **A cursor over a generator source is forced STATIC** — a TVF, a catalog view, `VALUES`, `OPENJSON`, PIVOT, `.nodes()`, a linked server.
  Real reports these as read-only snapshots too (probe-confirmed for `STRING_SPLIT`), so the sensitivity matches; what diverges is only that the simulator arrives there by refusing to plan the slot rather than by the source having no key.
  A `FOR SYSTEM_TIME` source resolves the same way and likewise matches — real reports all five forms as `Snapshot | Read Only`, so positioned DML through one is Msg 16929 on both.
- **Which ORDER BYs an index delivers is decided from the declared keys**, not from a finished plan the way real decides it — see the residual rows of [Index-delivered ORDER BY](#index-delivered-order-by).
  Real keeps an order-preserving expression over an indexed column (`ORDER BY v + 1`) and a filtered index matching the statement's own WHERE dynamic, and its functional-dependency reasoning carries a unique run across a 1:1 join into a run no index delivers; the simulator converts all three to KEYSET.
- **Position is tracked by the flattened tuple of stable heap addresses**, one per base table the plan reads, made possible by `Heap.UpdateAt`'s in-place / forwarding-pointer design (the simulator's UPDATE doesn't relocate rows).
  KEYSET membership keys on the table's [identifying key](#what-a-keyset-member-keys-on).
  The uniquifier stand-in is drawn from a per-heap counter only when an UPDATE assigns a key column, so a row *inserted* onto a key after another row was moved onto it walks ahead of that row, where real draws the inserted row's uniquifier later (the order not probed).
  The ordinary read path's clustered scan orders duplicates by address, so a SELECT with no ORDER BY doesn't follow a moved row's uniquifier the way the cursor does.
- **A view body is re-parsed at DECLARE and the resulting plan is what the cursor re-folds**, so a `CREATE OR ALTER VIEW` between DECLARE and FETCH doesn't reach an open cursor; the ordinary read path re-parses the body per execution and would.
  Real fixes the cursor's plan at DECLARE too, so the direction matches; what isn't modeled is real's schema-change detection.
- **DYNAMIC navigation order without an ORDER BY is each source's scan order nested left-to-right** — clustered key order, or write order for a heap — where real walks its chosen plan's order.
  For the left-deep scan shape the two agree; a plan real would run differently (a hash join reordering the inner, a covering nonclustered index) could emit the same rows in another order.
- **`@@CURSOR_ROWS` is `-1` throughout for DYNAMIC.**
  Real SQL Server may report a transient positive count for a freshly-opened dynamic cursor before the first fetch (asynchronous population heuristic); the simulator doesn't model the transition.
- **A deleted keyset member's row follows the projection's inferred nullability**, so where that isn't inferred (a join, a deferred source) every column reads NULL where real zeroes the NOT NULL ones.
  A NOT NULL `geography` / `geometry` column reads NULL too: real sends a zero-length spatial value, which the parsed value model can't carry.
- **OPTIMISTIC double-positioned-DML without an intervening FETCH** falsely conflicts: the snapshot is refreshed only at FETCH, so a second `UPDATE … WHERE CURRENT OF` on the same row (without re-fetching) sees its own first UPDATE as an out-of-band change.
  Pathological — well-formed cursor loops always FETCH between positioned mutations.
- **OPTIMISTIC over a forwarded (oversize) UPDATE**: detection reads `Heap.ReadSlotBytes` at the row's address.
  A fits-in-place rewrite returns the new bytes (conflict detected); an oversize rewrite that installs a forwarding pointer isn't followed by `ReadSlotBytes`, so such a change may go undetected.
  The common small-value case is exact.
- **DECLARE CURSOR inside an un-taken `IF` branch** still parses (and resolves names in) its SELECT — the same eager-resolution quirk all statements share.
- **FAST_FORWARD settles a DISTINCT at OPEN even when a key makes it redundant** — real's optimizer drops `DISTINCT` over a projection holding the table's key and then reads live, as it does a plain query.
- **Msg 1049's line** varies between runs on real (0 in most placements, a small number in others); the simulator reports 0.
- **A cursor parameter missing `VARYING OUTPUT`** is Msg 1051 alone, where real goes on to report the body's uses of the parameter as undeclared variables (Msg 137).
- **`sp_describe_cursor_tables`' server name** is the simulator's `@@SERVERNAME`, and object ids are the simulator's own.

## Not modeled yet

- **Asynchronous keyset population** under a non-default `cursor threshold` server option, where real reports a negative `@@CURSOR_ROWS` while it populates; the default (-1) populates synchronously, which is what the simulator always does.
- **`sys.dm_exec_cursors` rows for API server cursors** — an `sp_cursoropen` cursor lives on the TDS session rather than the connection, so the DMV doesn't list it, where real does as `API | <type> | …`.
- **A projection error per fetch.** Real evaluates a dynamic or keyset cursor's select list row by row as it fetches, so `SELECT 1 / (id - 2)` fetches row 1 and raises on row 2; here the plan projects every row at the first fetch (`Selection.EnumerateForCursor`), which raises before row 1 arrives (probed 2026-10-02 against SQL Server 2025).
