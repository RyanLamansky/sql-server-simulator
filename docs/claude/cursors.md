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
- **`Selection.Cursor.cs`** — `CursorShape` (the parse-time capture of a SELECT's cursor-navigability), `CursorSourcePlan` / `CursorSlot` (the resolved plan and its per-slot backing), `TryBuildCursorPlan` (the DECLARE-time resolution that follows deferred slots down to base tables), `EnumerateForCursor` (live enumeration over the base heaps, folding the JOIN chain and reusing `ResolveAcrossTuple` + `ComputeOrderKeys`), and the `CursorRow` / identity-comparison helpers, kept inside `Selection` where the private projection / ORDER BY machinery lives.
- **`Simulation.InvokeView.cs`'s `TryParseViewBodyPlan`** — the parse-only view-body seam cursor planning looks through a view with; mirrors `InvokeViewCore`'s child-batch setup but stops at parse and returns null rather than propagating a body error.
- **`Parser/Expressions/CursorScalars.cs`** — `@@FETCH_STATUS`, `@@CURSOR_ROWS`, `CURSOR_STATUS(scope, name)`.
- **`Errors/SimulatedSqlException.CursorErrors.cs`** — Msg 16905 / 16911 / 16915 / 16916 / 16917 / 16924 / 16925 / 16929 / 16931 / 16932 (FOR UPDATE OF) / 16933 (target not one of the cursor's tables) / 16947+3621 (nothing to mutate) / 16947+16934+3621 (OPTIMISTIC conflict chain) / 16950 (unallocated cursor variable) — all probe-confirmed verbatim.
  TYPE_WARNING's Msg 16956 and the self-join Msg 16961 ride the `BatchContext.AppendInfoError` info pipeline, not this factory set.

The dispatch routes `Keyword.Declare` to cursor handling when the token after `DECLARE` isn't `@`-prefixed (cursor names are bare identifiers; that's the only non-`@` DECLARE form).
`Keyword.Open` / `Fetch` / `Close` / `Deallocate` get their own dispatch cases and are in `IsStatementBoundary`.
The query after `FOR` parses through the shared body seam, so it may carry a `WITH cte AS (…)` prefix (the bindings are captured into the stored plan at DECLARE, and OPEN re-executes it) → [`ctes.md`](ctes.md#where-a-prefix-may-appear).

**API server cursors** (the `sp_cursor*` TDS RPC family SSMS's grid editor and legacy ODBC / OLE DB apps drive) reuse this engine surface from the wire layer: `Network/TdsSession.Cursors.cs` synthesizes a `DECLARE … CURSOR … FOR <stmt>; OPEN` batch, pulls the engine `Cursor` out of `SimulatedDbConnection.Cursors`, drives `Cursor.Fetch` per row, and runs `UPDATE/DELETE … WHERE CURRENT OF` for positioned edits.
Handle→cursor mapping, the scrollopt/ccopt option translation, and the probed wire contract live in [`tds-endpoint.md`](tds-endpoint.md).

## Sensitivity model (probe-confirmed)

The effective type is resolved at DECLARE from the requested keywords **and** whether the SELECT is navigable — a query whose FROM doesn't reach base tables the cursor can re-fold is forced to STATIC, matching SQL Server's silent conversion.
With a navigable query: explicit `STATIC` / `INSENSITIVE` / `FAST_FORWARD` → STATIC; `KEYSET` → KEYSET; `DYNAMIC` → DYNAMIC; unspecified → KEYSET when `SCROLL` was asked for, DYNAMIC for the forward-only default.
A **row limit** anywhere in the shape caps the result at KEYSET — see [Row-limited cursors](#row-limited-cursors).
Sensitivity and scrollability are separate: naming any of the three implies `SCROLL`, while a cursor that names none stays forward-only *whatever it resolved to* — probe-confirmed that a bare cursor converted to a snapshot (DISTINCT) and one converted to KEYSET (`TOP`) both report Msg 16911 for a scrolling direction, so the test is on the requested keyword, never the effective sensitivity.
A base table is **not** required to have a unique key — the row's stable heap address (delivered by `Heap.UpdateAt`'s in-place / forwarding-pointer machinery) is the fallback identity.
Probe-confirmed: real SQL Server's KEYSET on a no-unique-key heap also opens with a positive `@@CURSOR_ROWS`, so this matches rather than diverges.

| Type | Membership | Column values | `@@CURSOR_ROWS` | Updatable |
|------|-----------|---------------|-----------------|-----------|
| **STATIC** | frozen snapshot at OPEN | frozen | row count | no (read-only) |
| **KEYSET** | frozen at OPEN (identity set) | re-read live per FETCH | row count | yes |
| **DYNAMIC** | live (inserts appear, deletes vanish) | re-read live per FETCH | `-1` | yes |

- **STATIC** snapshots projected rows once (`Selection.Execute` → decoded `SqlValue[]`); immune to later changes; covers every non-navigable query.
- **KEYSET** snapshots an ordered list of identities at OPEN.
  Each FETCH re-enumerates the live base tables (`EnumerateForCursor`) and matches the snapshotted member — by unique key when a participating base table has a PK/UNIQUE (probe-confirmed: real SQL Server's KEYSET tracks the chosen unique-index columns, so an UPDATE to those columns invalidates the matching row), by stable address otherwise.
  A value change to non-identity columns shows through (status 0); a deleted-or-key-changed member yields `@@FETCH_STATUS = -2`.
- **DYNAMIC** stores no list; it tracks the last-emitted `(ORDER BY key, identity)` and re-enumerates live each FETCH to find the next/prior row by that total order.
  Deletes ahead are silently skipped; inserts ahead appear.

Cursor identity rides the row's stable `(page, slot)` heap address — one per base table the plan reads, however many layers of view / derived table / CTE sit above it.
`Heap.UpdateAt` (the in-place / forwarding-pointer machinery in `Storage/Heap.cs`) preserves that address through value updates: a fits-in-place rewrite overwrites the slot's bytes; an oversize rewrite appends the new row elsewhere and installs a single-level forwarding pointer at the original slot.
Either way the row's visible address is unchanged, so KEYSET re-reads and positioned `WHERE CURRENT OF` DML survive value updates without requiring a unique key — no PK/UNIQUE needed, and no forced STATIC.

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
| ORDER BY a non-indexed column | Keyset | *DYNAMIC* |
| DISTINCT, GROUP BY, set op (also inside a derived table or view body) | Snapshot / Read Only | STATIC |

The italicised row is the residual — see [Divergences](#divergences-from-sql-server-documented-not-byte-identical).
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

### Multi-source navigation

A cursor's identity is the **flattened tuple of stable addresses** of every base table the plan reads — one entry per base-table scan anywhere in the tree, depth-first in slot order, with a null entry on the NULL-extended side of an outer join or an empty OUTER APPLY.
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

## FETCH

`FETCH [NEXT|PRIOR|FIRST|LAST|ABSOLUTE n|RELATIVE n] [FROM] <cursor> [INTO @v,…]`.

- **Scrollability**: naming a sensitivity implies `SCROLL` — `STATIC`, `KEYSET` *and* `DYNAMIC` all scroll unless `FORWARD_ONLY` / `FAST_FORWARD` says otherwise (probe-confirmed).
  A cursor that names none is forward-only and allows only `NEXT`.
- **`ABSOLUTE` on a dynamic-sensitivity cursor** → **Msg 16925** (`"The fetch type Absolute cannot be used with dynamic cursors."`, direction title-cased).
  Real checks this *before* scrollability, so a bare `FORWARD_ONLY` cursor — which defaults to dynamic sensitivity — reports 16925 for `ABSOLUTE` and 16911 for everything else.
- **Any other non-`NEXT` direction on a non-scrollable cursor** → **Msg 16911** (`"fetch: The fetch type prior cannot be used with forward only cursors."`), whose direction name is **lower-cased**, unlike 16925's.
- **`RELATIVE` is legal on a scrollable DYNAMIC cursor** and walks the live set one row at a time, since there's no stable ordinal to jump to; a zero offset re-reads the current row.
  `ABSOLUTE` is the only direction dynamic sensitivity rejects.
- **INTO** assigns the projected columns to the variables (coerced to each declared type).
  A count mismatch raises **Msg 16924** regardless of whether the FETCH lands on a row.
  On a successful fetch the variables are written; on `-1` (past end) they retain their prior value (probe-confirmed).
- **Without INTO** a landed FETCH yields a single-row result set.
- `@@FETCH_STATUS`: `0` success, `-1` past end / no row, `-2` keyset member deleted.

## WHERE CURRENT OF

`UPDATE t SET … WHERE CURRENT OF c` / `DELETE FROM t WHERE CURRENT OF c` target exactly the row the cursor is positioned on, found by matching the address the cursor recorded for the identity slot the target resolved to (`PositionedCursorTarget` + `CursorRowMatches`).
The UPDATE / DELETE parsers branch in their WHERE clause: `Keyword.Current` → `ParseWhereCurrentOf`, otherwise a normal boolean WHERE.
The SI tombstone pre-flight is skipped for positioned DML (the cursor already fixed a single live row).

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

`ParseWhereCurrentOf` validates, in this order:

| Condition | Error |
|-----------|-------|
| cursor is read-only (STATIC / FAST_FORWARD / `FOR READ ONLY`) | **Msg 16929** `The cursor is READ ONLY.` |
| the reference isn't one the cursor reads, or a `FOR UPDATE OF` list names none of the slot's surface columns | **Msg 16933** `The cursor does not include the table being modified or the table is not updatable through the cursor.` |
| the reference reaches more than one identity slot (self-join, including a self-joined view) | **Msg 16961**, severity 0 info — binds the *first* instance and continues |
| cursor isn't positioned on a row (before first FETCH, past the end) | **Msg 16931** `There are no rows in the current fetch buffer.` |
| the target's slot is the NULL-extended side of an outer join or an empty OUTER APPLY, **or** the cursor sits on a keyset hole (last FETCH reported `-2`) | **Msg 16947** + **Msg 3621** `No rows were updated or deleted.` |
| a positioned UPDATE assigns a column outside the `FOR UPDATE OF` list | **Msg 16932** |
| OPTIMISTIC cursor whose row changed out-of-band | **Msg 16947** + **16934** + **3621** |

All probe-confirmed, including the split real makes between 16933 and 16931: naming an unrelated table is 16933 even when the cursor *is* positioned, while naming a correct table before any FETCH is 16931.
The off-a-row cases split by cause, also probe-confirmed: before the first FETCH and past the end are 16931, while a keyset hole is 16947 — the member is gone, so there is nothing to update rather than nothing in the buffer (`Cursor.OnKeysetHole`, set from the FETCH status).
Msg 16947 without the descriptive 16934 is the NULL-extended / keyset-hole case; the OPTIMISTIC conflict adds 16934 — and that detection reaches a base row behind a view, since it reads the flattened address the cursor recorded.

## Scope: GLOBAL vs LOCAL

Two independent cursor namespaces, both probe-confirmed against SQL Server 2025:

- **GLOBAL** cursors live on `SimulatedDbConnection.Cursors` and persist for the connection (visible across GO-separated batches).
- **LOCAL** cursors live on `BatchContext.LocalCursors` and are implicitly deallocated when the frame (batch / procedure / trigger body) exits — `DeclareCursorInScope` picks the map, `TeardownFrameCursors` (called in the batch `finally` and after proc invocation) releases them.

Default scope is **GLOBAL** — the simulator's fixed model of the `CURSOR_DEFAULT` database option (real SQL Server's install default `is_local_cursor_default = 0` for every system and freshly-created database; the per-database option isn't separately modeled).
A name may exist in **both** scopes at once.
Resolution at a use site (`OPEN` / `CLOSE` / `DEALLOCATE` / `FETCH … FROM` / `WHERE CURRENT OF`):

- Unqualified name → **LOCAL first, then GLOBAL** (probe-confirmed: an unqualified `OPEN c` / `FETCH c` binds the LOCAL `c` when both exist).
- `GLOBAL name` → the global map only.
- Unqualified `DEALLOCATE` removes from LOCAL first (probe-confirmed).

`ResolveCursor` / `ReadCursorReference` in `Simulation.Cursor.cs` centralize this; the use-site parsers all route through them.
`CURSOR_STATUS(scope, name)` is scope-aware: `'local'` / `'global'` consult the respective named map, `'variable'` consults the cursor-variable namespace, and asking the wrong scope returns `-3`.

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

## FOR UPDATE OF

`FOR UPDATE OF (col, …)` captures the column list on the cursor (`Cursor.ForUpdateColumns`).
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

## TYPE_WARNING

`TYPE_WARNING` emits **Msg 16956** (`"The created cursor is not of the requested type."`, info severity via `BatchContext.AppendInfoError`) at **DECLARE** time (probe-confirmed, not OPEN) when an explicitly-requested DYNAMIC or KEYSET sensitivity was silently converted to a lesser one — e.g. DYNAMIC / KEYSET over a non-navigable shape (DISTINCT, GROUP BY, aggregate, set op, a generator source, or a deferred body carrying any of those) forced to STATIC.
It surfaces through the standard `InfoMessage` pipeline.
A deferred body the cursor *can* follow warns about nothing, matching real: `DECLARE … DYNAMIC TYPE_WARNING` over a plain view is silent on both, and over a DISTINCT or TOP view fires on both (probe-confirmed).

## Divergences from SQL Server (documented, not byte-identical)

- **A positioned UPDATE through a view over a JOIN is Msg 4405** where real accepts one touching a single base table.
  The cursor reaches the row; the block is the view-DML rewrite, whose `ViewUpdatabilityProfile` accepts a single FROM source only, so `UPDATE <join view> SET …` raises before the positioned binding is consulted.
  A positioned DELETE through such a view is Msg 4405 on both, and naming the base table under it is Msg 16933 on both.
- **A cursor over a generator source is forced STATIC** — a TVF, a catalog view, `VALUES`, `OPENJSON`, PIVOT, `.nodes()`, a linked server.
  Real reports these as read-only snapshots too (probe-confirmed for `STRING_SPLIT`), so the sensitivity matches; what diverges is only that the simulator arrives there by refusing to plan the slot rather than by the source having no key.
  A `FOR SYSTEM_TIME` source resolves the same way and likewise matches — real reports all five forms as `Snapshot | Read Only`, so positioned DML through one is Msg 16929 on both.
- **ORDER BY on a non-indexed column stays DYNAMIC** where real downgrades to KEYSET (and, with `TYPE_WARNING`, says so via Msg 16956).
  The simulator has no index-coverage notion in cursor planning, so no downgrade occurs and no warning is emitted.
- **Position is tracked by the flattened tuple of stable heap addresses**, one per base table the plan reads, made possible by `Heap.UpdateAt`'s in-place / forwarding-pointer design (the simulator's UPDATE doesn't relocate rows).
  KEYSET membership additionally tracks the unique-key tuple per base table that has a PK/UNIQUE, so an UPDATE to those columns produces `@@FETCH_STATUS = -2` (matches real SQL Server's keyset-tracks-the-unique-index behavior, probe-confirmed).
- **A view body is re-parsed at DECLARE and the resulting plan is what the cursor re-folds**, so a `CREATE OR ALTER VIEW` between DECLARE and FETCH doesn't reach an open cursor; the ordinary read path re-parses the body per execution and would.
  Real fixes the cursor's plan at DECLARE too, so the direction matches; what isn't modeled is real's schema-change detection.
- **DYNAMIC navigation order without an ORDER BY is the address tuple**, ascending left-to-right across the sources, where real walks its chosen plan's order.
  For the left-deep heap-scan shape the two agree; a plan real would run differently (a hash join reordering the inner) could emit the same rows in another order.
- **`@@CURSOR_ROWS` is `-1` throughout for DYNAMIC.**
  Real SQL Server may report a transient positive count for a freshly-opened dynamic cursor before the first fetch (asynchronous population heuristic); the simulator doesn't model the transition.
- **Keyset `-2` leaves INTO variables unchanged.**
  Real SQL Server zeroes numeric / NULLs other INTO variables on a deleted-member fetch; the simulator retains their prior value (same as the `-1` case).
  The values are meaningless when `@@FETCH_STATUS ≠ 0` and loops check the status before reading them.
- **FETCH-without-INTO omits the trailing `ROWSTAT` column** real SQL Server appends to client-cursor fetch result sets.
- **OPTIMISTIC double-positioned-DML without an intervening FETCH** falsely conflicts: the snapshot is refreshed only at FETCH, so a second `UPDATE … WHERE CURRENT OF` on the same row (without re-fetching) sees its own first UPDATE as an out-of-band change.
  Pathological — well-formed cursor loops always FETCH between positioned mutations.
- **OPTIMISTIC over a forwarded (oversize) UPDATE**: detection reads `Heap.ReadSlotBytes` at the row's address.
  A fits-in-place rewrite returns the new bytes (conflict detected); an oversize rewrite that installs a forwarding pointer isn't followed by `ReadSlotBytes`, so such a change may go undetected.
  The common small-value case is exact.
- **TYPE_WARNING coalescing**: the Msg 16956 info text merges with any other info messages in the same batch into one coalesced `InfoMessage` event (the simulator's standard info-message behavior), rather than a distinct message.
- **TYPE_WARNING for the DYNAMIC→KEYSET "ORDER BY on a non-index" downgrade** is not emitted — no downgrade occurs to warn about (see the ORDER BY bullet above).
- **DECLARE CURSOR inside an un-taken `IF` branch** still parses (and resolves names in) its SELECT — the same eager-resolution quirk all statements share.
