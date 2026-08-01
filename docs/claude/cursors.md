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
- **`Selection.Cursor.cs`** — `CursorSourcePlan` (the parse-time capture of the cursor-navigable FROM shape), `EnumerateForCursor` (live enumeration over the base heaps, folding the JOIN chain and reusing `ResolveAcrossTuple` + `ComputeOrderKeys`), and the `CursorRow` / identity-comparison helpers, kept inside `Selection` where the private projection / ORDER BY machinery lives.
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
Sensitivity and scrollability are separate: naming any of the three implies `SCROLL`, while the *defaulted* DYNAMIC of a bare cursor stays forward-only.
A base table is **not** required to have a unique key — the row's stable heap address (delivered by `Heap.UpdateAt`'s in-place / forwarding-pointer machinery) is the fallback identity.
Probe-confirmed: real SQL Server's KEYSET on a no-unique-key heap also opens with a positive `@@CURSOR_ROWS`, so this matches rather than diverges.

| Type | Membership | Column values | `@@CURSOR_ROWS` | Updatable |
|------|-----------|---------------|-----------------|-----------|
| **STATIC** | frozen snapshot at OPEN | frozen | row count | no (read-only) |
| **KEYSET** | frozen at OPEN (identity set) | re-read live per FETCH | row count | yes |
| **DYNAMIC** | live (inserts appear, deletes vanish) | re-read live per FETCH | `-1` | yes |

- **STATIC** snapshots projected rows once (`Selection.Execute` → decoded `SqlValue[]`); immune to later changes; covers every non-navigable query.
- **KEYSET** snapshots an ordered list of identities at OPEN.
  Each FETCH re-enumerates the live base tables (`EnumerateForCursor`) and matches the snapshotted member — by unique key when a source's table has a PK/UNIQUE (probe-confirmed: real SQL Server's KEYSET tracks the chosen unique-index columns, so an UPDATE to those columns invalidates the matching row), by stable address otherwise.
  A value change to non-identity columns shows through (status 0); a deleted-or-key-changed member yields `@@FETCH_STATUS = -2`.
- **DYNAMIC** stores no list; it tracks the last-emitted `(ORDER BY key, identity)` and re-enumerates live each FETCH to find the next/prior row by that total order.
  Deletes ahead are silently skipped; inserts ahead appear.

Cursor identity rides the row's stable `(page, slot)` heap address.
`Heap.UpdateAt` (the in-place / forwarding-pointer machinery in `Storage/Heap.cs`) preserves that address through value updates: a fits-in-place rewrite overwrites the slot's bytes; an oversize rewrite appends the new row elsewhere and installs a single-level forwarding pointer at the original slot.
Either way the row's visible address is unchanged, so KEYSET re-reads and positioned `WHERE CURRENT OF` DML survive value updates without requiring a unique key — no PK/UNIQUE needed, and no forced STATIC.

### Which shapes are navigable

`CursorSourcePlan` is captured at parse time (`ComputeCursorPlan`, beside the view-updatability capture in `Selection.Execution.cs`) and is what makes a cursor KEYSET / DYNAMIC-eligible.
It requires: no DISTINCT, no aggregate / GROUP BY / HAVING, no window function, no TOP / OFFSET / FETCH, no set-op chain — and **every FROM source a direct base-table scan** joined by INNER / CROSS / LEFT / RIGHT / FULL.

Probed against SQL Server 2025 with `sys.dm_exec_cursors(0).properties`, which reports the effective type:

| Shape | Real | Simulator |
|-------|------|-----------|
| single base table | Dynamic | DYNAMIC |
| 2-, 3-table JOIN; LEFT / RIGHT / FULL / CROSS; comma FROM; self-join | Dynamic | DYNAMIC |
| JOIN + WHERE, JOIN + ORDER BY on an indexed column | Dynamic | DYNAMIC |
| CROSS / OUTER APPLY | Dynamic | *STATIC* |
| derived table, view over one table, view over a join, CTE | Dynamic | *STATIC* |
| `TOP n` (also a derived table containing TOP) | Keyset | *STATIC* |
| ORDER BY a non-indexed column | Keyset | *DYNAMIC* |
| DISTINCT, GROUP BY, set op (also inside a derived table) | Snapshot / Read Only | STATIC |

The italicised rows are the residual — see [Divergences](#divergences-from-sql-server-documented-not-byte-identical).

### Multi-source navigation

A join cursor's identity is the **tuple of per-source stable addresses**, one slot per FROM source, with a null slot on the NULL-extended side of an outer join.
`EnumerateForCursor` snapshots each participating heap (address + bytes), folds the JOIN chain left-deep into row-index tuples (`FoldCursorTuples`), then runs the WHERE excluders and projections through the shared hoisted resolver — so the whole join is re-derived on every FETCH and mid-loop changes to *either* side show.
Probe-confirmed consequences, all covered by `CursorMultiSourceTests`:

- An UPDATE to a column on either side between FETCHes is visible on the next FETCH.
- A row inserted mid-loop appears — including a row inserted into the *inner* side of a CROSS JOIN, against an outer row the cursor hasn't reached yet.
- A row whose partner is deleted mid-loop silently vanishes from a DYNAMIC cursor and yields `@@FETCH_STATUS = -2` on a KEYSET one.
- `@@CURSOR_ROWS` is `-1` for the forward-only default and the row count for `SCROLL` (which resolves to KEYSET, exactly as on a single table), and `FETCH ABSOLUTE` on a `SCROLL DYNAMIC` join cursor is Msg 16925 while `RELATIVE` walks.

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

`UPDATE t SET … WHERE CURRENT OF c` / `DELETE FROM t WHERE CURRENT OF c` target exactly the row the cursor is positioned on, found by matching the address the cursor recorded for `t`'s own FROM slot (`CursorRowMatches`).
The UPDATE / DELETE parsers branch in their WHERE clause: `Keyword.Current` → `ParseWhereCurrentOf`, otherwise a normal boolean WHERE.
The SI tombstone pre-flight is skipped for positioned DML (the cursor already fixed a single live row).

**The target names a table, not a cursor alias**: `UPDATE a SET …` where `a` is only the cursor's alias is Msg 208 (`Invalid object name 'a'`) from ordinary name resolution, matching real.

`ParseWhereCurrentOf` validates, in this order:

| Condition | Error |
|-----------|-------|
| cursor is read-only (STATIC / FAST_FORWARD / `FOR READ ONLY`) | **Msg 16929** `The cursor is READ ONLY.` |
| target table isn't one of the cursor's FROM sources, or a `FOR UPDATE OF` list names none of its columns | **Msg 16933** `The cursor does not include the table being modified or the table is not updatable through the cursor.` |
| target table appears in more than one FROM slot (self-join) | **Msg 16961**, severity 0 info — binds the *first* instance and continues |
| cursor isn't positioned on a row (before first FETCH, past the end, keyset hole) | **Msg 16931** `There are no rows in the current fetch buffer.` |
| the target's slot is the NULL-extended side of an outer join | **Msg 16947** + **Msg 3621** `No rows were updated or deleted.` |
| a positioned UPDATE assigns a column outside the `FOR UPDATE OF` list | **Msg 16932** |
| OPTIMISTIC cursor whose row changed out-of-band | **Msg 16947** + **16934** + **3621** |

All probe-confirmed, including the split real makes between 16933 and 16931: naming an unrelated table is 16933 even when the cursor *is* positioned, while naming a correct table before any FETCH is 16931.
Msg 16947 without the descriptive 16934 is the NULL-extended case; the OPTIMISTIC conflict adds 16934.

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

Over a multi-table cursor the list also narrows the updatable **tables** to those owning a listed column (`Cursor.IsTableUpdatable`), so a positioned UPDATE *or* DELETE naming any other participating table is **Msg 16933**, not 16932 — probe-confirmed: with `FOR UPDATE OF v` on `a JOIN b`, `DELETE FROM b … WHERE CURRENT OF` is 16933 while `UPDATE a SET id = …` is 16932 and `DELETE FROM a` succeeds.

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

`TYPE_WARNING` emits **Msg 16956** (`"The created cursor is not of the requested type."`, info severity via `BatchContext.AppendInfoError`) at **DECLARE** time (probe-confirmed, not OPEN) when an explicitly-requested DYNAMIC or KEYSET sensitivity was silently converted to a lesser one — e.g. DYNAMIC / KEYSET over a non-navigable shape (DISTINCT, GROUP BY, aggregate, set op, a deferred source) forced to STATIC.
It surfaces through the standard `InfoMessage` pipeline.

## Divergences from SQL Server (documented, not byte-identical)

- **Cursors over a deferred source are forced to STATIC** — a derived table, a view (over one table or over a join), a CTE, an APPLY right side, a TVF or `OPENJSON`.
  Probed against SQL Server 2025: such cursors are **DYNAMIC** there (`@@CURSOR_ROWS = -1`, mid-loop changes visible, `WHERE CURRENT OF` updating through the source).
  Those sources reach the row stream through a `FromSource.LateralPlan`, which yields projected bytes carrying no heap address, so the per-source identity the fold needs isn't available; a direct base-table scan (alone or joined) is what `CursorSourcePlan` accepts.
  The forced-STATIC snapshot returns the **correct rowset** for a read-only forward loop — it diverges on sensitivity (no mid-loop change visibility), `@@CURSOR_ROWS` (count instead of `-1`), and positioned DML (Msg 16929 instead of updating).
  Real's positioned DML through a *view* cursor names the **view** (its CHECK OPTION then applies, out-of-range → Msg 550) and rejects naming the base table under it with Msg 16933; a view over a join accepts a positioned UPDATE touching one base table and rejects a positioned DELETE with Msg 4405.
  A set-op (UNION/…), DISTINCT, GROUP BY or aggregate cursor is a read-only snapshot on the real server too, so those cases match.
- **A `TOP` / `OFFSET` / `FETCH` cursor is forced STATIC** where real converts it to KEYSET (probe-confirmed, including a derived table whose interior carries the TOP).
  Row limiting isn't applied by the cursor enumeration path, so accepting the shape would return the unlimited rowset.
- **A `FOR SYSTEM_TIME` cursor is forced STATIC.**
  The temporal source reads the history sibling through its own row source, which the base-heap fold doesn't see; STATIC keeps the rowset correct.
- **ORDER BY on a non-indexed column stays DYNAMIC** where real downgrades to KEYSET (and, with `TYPE_WARNING`, says so via Msg 16956).
  The simulator has no index-coverage notion in cursor planning, so no downgrade occurs and no warning is emitted.
- **Position is tracked by the tuple of stable heap addresses**, one per FROM source, made possible by `Heap.UpdateAt`'s in-place / forwarding-pointer design (the simulator's UPDATE doesn't relocate rows).
  KEYSET membership additionally tracks the unique-key tuple per source whose table has a PK/UNIQUE, so an UPDATE to those columns produces `@@FETCH_STATUS = -2` (matches real SQL Server's keyset-tracks-the-unique-index behavior, probe-confirmed).
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
