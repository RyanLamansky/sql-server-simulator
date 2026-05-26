# Cursors

T-SQL server-side cursors: `DECLARE … CURSOR`, `OPEN`, `FETCH`, `CLOSE`,
`DEALLOCATE`, the `STATIC` / `KEYSET` / `DYNAMIC` sensitivity model, scroll
fetches, the `@@FETCH_STATUS` / `@@CURSOR_ROWS` / `CURSOR_STATUS` status
surface, and positioned `WHERE CURRENT OF` UPDATE / DELETE. Behavior probed
against SQL Server 2025.

## Layout

- **`Cursor.cs`** (root) — the session-scoped runtime cursor: effective
  sensitivity, scrollability, read-only flag, base table + unique-key ordinals,
  and per-sensitivity position state. `Open` / `Fetch` / `Close` live here.
- **`SimulatedDbConnection.Cursors`** — per-session `Dictionary<string, Cursor>`
  (case-insensitive, names are identifiers not `@`-prefixed). Plus
  `LastFetchStatus` (`@@FETCH_STATUS`) and `LastCursorRows` (`@@CURSOR_ROWS`).
  Cleared on `Dispose` (cursors auto-deallocate at session close).
- **`Simulation.Cursor.cs`** — the `DECLARE CURSOR` grammar (SQL-92 +
  T-SQL extended), `OPEN` / `FETCH` / `CLOSE` / `DEALLOCATE` dispatch, the
  `FETCH` direction parser, and the `WHERE CURRENT OF` helpers
  (`ParseWhereCurrentOf` / `CursorRowMatches`) shared by UPDATE / DELETE.
- **`Selection.Cursor.cs`** — `EnumerateForCursor` (live RID-free enumeration
  over the base heap, reusing `ResolveAcrossTuple` + `ComputeOrderKeys`) and the
  `CursorRow` / key-comparison helpers, kept inside `Selection` where the
  private projection / ORDER BY machinery and `UpdatabilityProfile` live.
- **`Parser/Expressions/CursorScalars.cs`** — `@@FETCH_STATUS`,
  `@@CURSOR_ROWS`, `CURSOR_STATUS(scope, name)`.
- **`Errors/SimulatedSqlException.CursorErrors.cs`** — Msg 16905 / 16915 /
  16916 / 16917 / 16924 / 16925 / 16929 / 16931 (all probe-confirmed verbatim).

The dispatch routes `Keyword.Declare` to cursor handling when the token after
`DECLARE` isn't `@`-prefixed (cursor names are bare identifiers; that's the
only non-`@` DECLARE form). `Keyword.Open` / `Fetch` / `Close` / `Deallocate`
get their own dispatch cases and are in `IsStatementBoundary`.

## Sensitivity model (probe-confirmed)

The effective type is resolved at DECLARE from the requested keywords **and**
whether the SELECT is updatable — a query that isn't a single base table is
forced to STATIC, matching SQL Server's silent conversion. With an updatable
query: explicit `STATIC` / `INSENSITIVE` / `FAST_FORWARD` → STATIC; `KEYSET`
→ KEYSET; `DYNAMIC` → DYNAMIC; unspecified → KEYSET when `SCROLL` was asked
for, DYNAMIC for the forward-only default. The base table is **not** required
to have a unique key — the row's stable heap address (delivered by
`Heap.UpdateAt`'s in-place / forwarding-pointer machinery) is the fallback
identity. Probe-confirmed: real SQL Server's KEYSET on a no-unique-key heap
also opens with a positive `@@CURSOR_ROWS`, so this matches rather than
diverges.

| Type | Membership | Column values | `@@CURSOR_ROWS` | Updatable |
|------|-----------|---------------|-----------------|-----------|
| **STATIC** | frozen snapshot at OPEN | frozen | row count | no (read-only) |
| **KEYSET** | frozen at OPEN (unique-key set) | re-read live per FETCH | row count | yes |
| **DYNAMIC** | live (inserts appear, deletes vanish) | re-read live per FETCH | `-1` | yes |

- **STATIC** snapshots projected rows once (`Selection.Execute` → decoded
  `SqlValue[]`); immune to later changes; covers every non-updatable query.
- **KEYSET** snapshots an ordered list of `(UniqueKey?, Rid)` pairs at OPEN.
  Each FETCH re-enumerates the live base table (`EnumerateForCursor`) and
  matches the snapshotted member — by unique key when the base table has a
  PK/UNIQUE (probe-confirmed: real SQL Server's KEYSET tracks the chosen
  unique-index columns, so an UPDATE to those columns invalidates the
  matching row), by stable address otherwise. A value change to non-identity
  columns shows through (status 0); a deleted-or-key-changed member yields
  `@@FETCH_STATUS = -2`.
- **DYNAMIC** stores no list; it tracks the last-emitted `(ORDER BY key,
  Rid)` and re-enumerates live each FETCH to find the next/prior row by that
  total order. Deletes ahead are silently skipped; inserts ahead appear.

Cursor identity rides the row's stable `(page, slot)` heap address.
`Heap.UpdateAt` (see [`storage.md`](storage.md) — TODO if you split that out;
for now the in-place / forwarding-pointer machinery lives in `Storage/Heap.cs`)
preserves that address through value updates: a fits-in-place rewrite
overwrites the slot's bytes; an oversize rewrite appends the new row
elsewhere and installs a single-level forwarding pointer at the original
slot. Either way the row's visible address is unchanged, so KEYSET re-reads
and positioned `WHERE CURRENT OF` DML survive value updates without
requiring a unique key — a strict improvement over the previous "force
STATIC when no PK/UNIQUE" rule.

## FETCH

`FETCH [NEXT|PRIOR|FIRST|LAST|ABSOLUTE n|RELATIVE n] [FROM] <cursor> [INTO @v,…]`.

- **Scrollability**: forward-only cursors (DYNAMIC default, `FORWARD_ONLY`,
  `FAST_FORWARD`) allow only `NEXT`; STATIC / KEYSET and any `SCROLL` cursor
  allow all six. A scroll fetch on a forward-only cursor → **Msg 16925** (`"The
  fetch type Absolute cannot be used with dynamic cursors."`, direction
  title-cased). DYNAMIC never supports `ABSOLUTE` / `RELATIVE` even when SCROLL.
- **INTO** assigns the projected columns to the variables (coerced to each
  declared type). A count mismatch raises **Msg 16924** regardless of whether
  the FETCH lands on a row. On a successful fetch the variables are written; on
  `-1` (past end) they retain their prior value (probe-confirmed).
- **Without INTO** a landed FETCH yields a single-row result set.
- `@@FETCH_STATUS`: `0` success, `-1` past end / no row, `-2` keyset member
  deleted.

## WHERE CURRENT OF

`UPDATE t SET … WHERE CURRENT OF c` / `DELETE FROM t WHERE CURRENT OF c` target
exactly the row the cursor is positioned on, found by matching the cursor's
current unique-key tuple against the base heap (`CursorRowMatches`). The
UPDATE / DELETE parsers branch in their WHERE clause: `Keyword.Current` →
`ParseWhereCurrentOf` (which validates read-only → **Msg 16929** and
no-current-row → **Msg 16931**), otherwise a normal boolean WHERE. The SI
tombstone pre-flight is skipped for positioned DML (the cursor already fixed a
single live row).

## Divergences from SQL Server (documented, not byte-identical)

- **Cursors over a JOIN / derived table / view are forced to STATIC.** Probed
  against SQL Server 2025: such cursors are **DYNAMIC** on the real server
  (`@@CURSOR_ROWS = -1`, mid-loop changes visible, `WHERE CURRENT OF` updates
  the named base table). Only a *direct single base table* takes the
  KEYSET / DYNAMIC / updatable path here (`CursorBaseTable` is null for any
  indirect source → STATIC). The forced-STATIC snapshot returns the
  **correct rowset** for a read-only forward loop — it only diverges on
  sensitivity (no mid-loop change visibility), `@@CURSOR_ROWS` (count instead
  of `-1`), and positioned DML (Msg 16929 instead of updating). Faithful
  multi-source cursors would need per-source row identity carried through the
  join driver (`EnumerateJoinedRows` yields identity-less `byte[]?[]` tuples)
  plus live re-execution + navigation — a separate subsystem. A set-op
  (UNION/…) cursor is forced STATIC on the real server too, so that case
  matches.
- **Position is tracked by the row's stable heap address**, made possible by
  `Heap.UpdateAt`'s in-place / forwarding-pointer design (the simulator's UPDATE
  no longer relocates rows). KEYSET membership additionally tracks the
  unique-key tuple when the base table has a PK/UNIQUE, so a UPDATE to those
  columns produces `@@FETCH_STATUS = -2` (matches real SQL Server's
  keyset-tracks-the-unique-index behavior, probe-confirmed). Multi-source
  cursors still force STATIC — see the JOIN/derived-table/view bullet above;
  that's now the sole structural restriction.
- **`@@CURSOR_ROWS` is `-1` throughout for DYNAMIC.** Real SQL Server may report
  a transient positive count for a freshly-opened dynamic cursor before the
  first fetch (asynchronous population heuristic); the simulator doesn't model
  the transition.
- **Keyset `-2` leaves INTO variables unchanged.** Real SQL Server zeroes
  numeric / NULLs other INTO variables on a deleted-member fetch; the simulator
  retains their prior value (same as the `-1` case). The values are meaningless
  when `@@FETCH_STATUS ≠ 0` and loops check the status before reading them.
- **FETCH-without-INTO omits the trailing `ROWSTAT` column** real SQL Server
  appends to client-cursor fetch result sets.
- **GLOBAL / LOCAL scope is collapsed** to one per-connection map; the scope
  keywords (and `SCROLL_LOCKS` / `OPTIMISTIC` / `TYPE_WARNING`) parse and
  discard. `CURSOR_STATUS`'s scope argument is likewise ignored.
- **Cursor variables (`DECLARE @c CURSOR`, cursor-typed parameters) aren't
  modeled** — `NotSupportedException`. Named cursors only.
- A `DECLARE CURSOR` inside an un-taken `IF` branch still parses (and resolves
  names in) its SELECT — the same eager-resolution quirk all statements share.
