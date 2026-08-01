# Cursors

T-SQL server-side cursors: `DECLARE … CURSOR`, `OPEN`, `FETCH`, `CLOSE`, `DEALLOCATE`, the `STATIC` / `KEYSET` / `DYNAMIC` sensitivity model, scroll fetches, the `@@FETCH_STATUS` / `@@CURSOR_ROWS` / `CURSOR_STATUS` status surface, and positioned `WHERE CURRENT OF` UPDATE / DELETE.
Behavior probed against SQL Server 2025.

## Layout

- **`Cursor.cs`** (root) — the session-scoped runtime cursor: effective sensitivity, scrollability, read-only flag, base table + unique-key ordinals, and per-sensitivity position state.
  `Open` / `Fetch` / `Close` live here.
- **`SimulatedDbConnection.Cursors`** — per-session `Dictionary<string, Cursor>` (case-insensitive, names are identifiers not `@`-prefixed).
  Plus `LastFetchStatus` (`@@FETCH_STATUS`) and `LastCursorRows` (`@@CURSOR_ROWS`).
  Cleared on `Dispose` (cursors auto-deallocate at session close).
- **`Simulation.Cursor.cs`** — the `DECLARE CURSOR` grammar (SQL-92 + T-SQL extended), `OPEN` / `FETCH` / `CLOSE` / `DEALLOCATE` dispatch, the `FETCH` direction parser, and the `WHERE CURRENT OF` helpers (`ParseWhereCurrentOf` / `CursorRowMatches`) shared by UPDATE / DELETE.
- **`Selection.Cursor.cs`** — `EnumerateForCursor` (live RID-free enumeration over the base heap, reusing `ResolveAcrossTuple` + `ComputeOrderKeys`) and the `CursorRow` / key-comparison helpers, kept inside `Selection` where the private projection / ORDER BY machinery and `UpdatabilityProfile` live.
- **`Parser/Expressions/CursorScalars.cs`** — `@@FETCH_STATUS`, `@@CURSOR_ROWS`, `CURSOR_STATUS(scope, name)`.
- **`Errors/SimulatedSqlException.CursorErrors.cs`** — Msg 16905 / 16911 / 16915 / 16916 / 16917 / 16924 / 16925 / 16929 / 16931 / 16932 (FOR UPDATE OF) / 16947+16934+3621 (OPTIMISTIC conflict chain) / 16950 (unallocated cursor variable) — all probe-confirmed verbatim.
  TYPE_WARNING's Msg 16956 rides the `BatchContext.AppendInfoError` info pipeline, not this factory set.

The dispatch routes `Keyword.Declare` to cursor handling when the token after `DECLARE` isn't `@`-prefixed (cursor names are bare identifiers; that's the only non-`@` DECLARE form).
`Keyword.Open` / `Fetch` / `Close` / `Deallocate` get their own dispatch cases and are in `IsStatementBoundary`.
The query after `FOR` parses through the shared body seam, so it may carry a `WITH cte AS (…)` prefix (the bindings are captured into the stored plan at DECLARE, and OPEN re-executes it) → [`ctes.md`](ctes.md#where-a-prefix-may-appear).

**API server cursors** (the `sp_cursor*` TDS RPC family SSMS's grid editor and legacy ODBC / OLE DB apps drive) reuse this engine surface from the wire layer: `Network/TdsSession.Cursors.cs` synthesizes a `DECLARE … CURSOR … FOR <stmt>; OPEN` batch, pulls the engine `Cursor` out of `SimulatedDbConnection.Cursors`, drives `Cursor.Fetch` per row, and runs `UPDATE/DELETE … WHERE CURRENT OF` for positioned edits.
Handle→cursor mapping, the scrollopt/ccopt option translation, and the probed wire contract live in [`tds-endpoint.md`](tds-endpoint.md).

## Sensitivity model (probe-confirmed)

The effective type is resolved at DECLARE from the requested keywords **and** whether the SELECT is updatable — a query that isn't a single base table is forced to STATIC, matching SQL Server's silent conversion.
With an updatable query: explicit `STATIC` / `INSENSITIVE` / `FAST_FORWARD` → STATIC; `KEYSET` → KEYSET; `DYNAMIC` → DYNAMIC; unspecified → KEYSET when `SCROLL` was asked for, DYNAMIC for the forward-only default.
Sensitivity and scrollability are separate: naming any of the three implies `SCROLL`, while the *defaulted* DYNAMIC of a bare cursor stays forward-only.
The base table is **not** required to have a unique key — the row's stable heap address (delivered by `Heap.UpdateAt`'s in-place / forwarding-pointer machinery) is the fallback identity.
Probe-confirmed: real SQL Server's KEYSET on a no-unique-key heap also opens with a positive `@@CURSOR_ROWS`, so this matches rather than diverges.

| Type | Membership | Column values | `@@CURSOR_ROWS` | Updatable |
|------|-----------|---------------|-----------------|-----------|
| **STATIC** | frozen snapshot at OPEN | frozen | row count | no (read-only) |
| **KEYSET** | frozen at OPEN (unique-key set) | re-read live per FETCH | row count | yes |
| **DYNAMIC** | live (inserts appear, deletes vanish) | re-read live per FETCH | `-1` | yes |

- **STATIC** snapshots projected rows once (`Selection.Execute` → decoded `SqlValue[]`); immune to later changes; covers every non-updatable query.
- **KEYSET** snapshots an ordered list of `(UniqueKey?, Rid)` pairs at OPEN.
  Each FETCH re-enumerates the live base table (`EnumerateForCursor`) and matches the snapshotted member — by unique key when the base table has a PK/UNIQUE (probe-confirmed: real SQL Server's KEYSET tracks the chosen unique-index columns, so an UPDATE to those columns invalidates the matching row), by stable address otherwise.
  A value change to non-identity columns shows through (status 0); a deleted-or-key-changed member yields `@@FETCH_STATUS = -2`.
- **DYNAMIC** stores no list; it tracks the last-emitted `(ORDER BY key, Rid)` and re-enumerates live each FETCH to find the next/prior row by that total order.
  Deletes ahead are silently skipped; inserts ahead appear.

Cursor identity rides the row's stable `(page, slot)` heap address.
`Heap.UpdateAt` (the in-place / forwarding-pointer machinery in `Storage/Heap.cs`) preserves that address through value updates: a fits-in-place rewrite overwrites the slot's bytes; an oversize rewrite appends the new row elsewhere and installs a single-level forwarding pointer at the original slot.
Either way the row's visible address is unchanged, so KEYSET re-reads and positioned `WHERE CURRENT OF` DML survive value updates without requiring a unique key — no PK/UNIQUE needed, and no forced STATIC.

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

`UPDATE t SET … WHERE CURRENT OF c` / `DELETE FROM t WHERE CURRENT OF c` target exactly the row the cursor is positioned on, found by matching the cursor's current unique-key tuple against the base heap (`CursorRowMatches`).
The UPDATE / DELETE parsers branch in their WHERE clause: `Keyword.Current` → `ParseWhereCurrentOf` (which validates read-only → **Msg 16929** and no-current-row → **Msg 16931**), otherwise a normal boolean WHERE.
The SI tombstone pre-flight is skipped for positioned DML (the cursor already fixed a single live row).

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

`TYPE_WARNING` emits **Msg 16956** (`"The created cursor is not of the requested type."`, info severity via `BatchContext.AppendInfoError`) at **DECLARE** time (probe-confirmed, not OPEN) when an explicitly-requested DYNAMIC or KEYSET sensitivity was silently converted to a lesser one — e.g. DYNAMIC / KEYSET over a non-updatable shape (DISTINCT, GROUP BY, aggregate, multi-source) forced to STATIC.
It surfaces through the standard `InfoMessage` pipeline.

## Divergences from SQL Server (documented, not byte-identical)

- **Cursors over a JOIN / derived table / view are forced to STATIC.**
  Probed against SQL Server 2025: such cursors are **DYNAMIC** on the real server (`@@CURSOR_ROWS = -1`, mid-loop changes visible, `WHERE CURRENT OF` updates the named base table).
  Only a *direct single base table* takes the KEYSET / DYNAMIC / updatable path here (`CursorBaseTable` is null for any indirect source → STATIC).
  The forced-STATIC snapshot returns the **correct rowset** for a read-only forward loop — it only diverges on sensitivity (no mid-loop change visibility), `@@CURSOR_ROWS` (count instead of `-1`), and positioned DML (Msg 16929 instead of updating).
  Faithful multi-source cursors would need per-source row identity carried through the join driver (`EnumerateJoinedRows` yields identity-less `byte[]?[]` tuples) plus live re-execution + navigation — a separate subsystem.
  A set-op (UNION/…) cursor is forced STATIC on the real server too, so that case matches.
- **Position is tracked by the row's stable heap address**, made possible by `Heap.UpdateAt`'s in-place / forwarding-pointer design (the simulator's UPDATE doesn't relocate rows).
  KEYSET membership additionally tracks the unique-key tuple when the base table has a PK/UNIQUE, so a UPDATE to those columns produces `@@FETCH_STATUS = -2` (matches real SQL Server's keyset-tracks-the-unique-index behavior, probe-confirmed).
  Multi-source cursors still force STATIC — see the JOIN/derived-table/view bullet above; that's the sole structural restriction.
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
- **TYPE_WARNING for the DYNAMIC→KEYSET "ORDER BY on a non-index" downgrade** is not emitted — the simulator keeps a single-base-table ORDER BY cursor DYNAMIC, so no downgrade occurs to warn about (real converts it to KEYSET and warns).
- **DECLARE CURSOR inside an un-taken `IF` branch** still parses (and resolves names in) its SELECT — the same eager-resolution quirk all statements share.
