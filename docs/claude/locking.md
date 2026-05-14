# Locking — phase 0 (schema-stability locks)

Phase 0 of the lock-manager rollout: schema-stability (Sch-S) and
schema-modification (Sch-M) locks on every schema-bound object, plus the
per-connection plumbing (SPID, `@@LOCK_TIMEOUT`, currently-executing
thread) the data-lock phases will build on. **Data locks (S / X / U / IS
/ IX / SIX), lock hints' behavioral effect, and MVCC are NOT phase 0** —
those land in phases 1a / 1b / 2 / 3 of the rollout (see CLAUDE.md's
roadmap entry).

The user-visible new behaviors:

- A DDL statement (`DROP TABLE`, `ALTER TABLE`, `TRUNCATE TABLE`) on one
  connection waits for concurrent readers / writers on another
  connection to finish their statements before proceeding (cross-thread
  Sch-S → Sch-M serialization).
- `SET LOCK_TIMEOUT N` now has semantic effect: a blocked acquisition
  raises **Msg 1222** (`Lock request time out period exceeded.`) instead
  of waiting indefinitely. Default is `-1` (wait forever, probe-confirmed
  default on a fresh SqlConnection).
- A conflict against a holder running on the caller's own OS thread
  raises **Msg 1205** (`Transaction (Process ID <N>) was deadlocked on
  lock resources with another process and has been chosen as the deadlock
  victim. Rerun the transaction.`) immediately — that thread can't make
  progress on the holder until the caller releases it. SPID embedded is
  the victim's (probe-confirmed against SQL Server 2025).

## Implementation map

- [`LockResource.cs`](../../SqlServerSimulator/LockResource.cs) — the
  per-object reader/writer primitive. `Acquire(mode, owner,
  timeoutMillis)` blocks via `Monitor.Wait`; `Release` pulses every
  waiter so each re-checks under the gate. Re-entrant per `(owner,
  mode)`. Same-thread-deadlock check happens on every acquire-conflict
  path.
- [`SchemaObject.SchemaLock`](../../SqlServerSimulator/SchemaObject.cs)
  — one `LockResource` per `HeapTable` / `View` / `UserDefinedFunction` /
  `Procedure` / `Sequence` / `TableType` / `Trigger`. Inherited from the
  base class, so adding a new `SchemaObject`-derived kind picks the lock
  up automatically.
- [`SimulatedDbConnection.Spid`](../../SqlServerSimulator/SimulatedDbConnection.cs)
  / `LockTimeoutMillis` / `CurrentExecutingThreadId` — per-connection
  state allocated at construction / updated by `SET LOCK_TIMEOUT` /
  managed by the dispatch loop.
- [`Simulation.AllocateSpid`](../../SqlServerSimulator/Simulation/Simulation.cs)
  — monotonic counter; first user SPID = 51 (SQL Server convention).
- [`BatchContext.StatementSchemaLocks`](../../SqlServerSimulator/Parser/BatchContext.cs)
  — per-statement list of `(LockResource, LockMode)` tuples; the
  dispatch loop releases every entry in a `finally` at statement end,
  unconditionally.
- [`BatchContext.AcquireStatementLock`](../../SqlServerSimulator/Parser/BatchContext.cs)
  — helper that calls `LockResource.Acquire` with the connection's
  current `LockTimeoutMillis` and records the acquisition for statement-
  end release.

## Acquisition sites

**Sch-S** — acquired by every `TryResolve*` success path on
`BatchContext` (table / view / function / procedure / table-type /
sequence). Temp tables (`#foo`), table variables (`@t`), and trigger
pseudo-tables (`INSERTED` / `DELETED`) bypass — they're per-session /
per-batch and not concurrency-reachable. Re-entrance handles a single
statement that resolves the same object twice (`FROM t a JOIN t b`).

**Sch-M** — acquired by every DDL site:

- `DROP TABLE` / `DROP VIEW` / `DROP FUNCTION` / `DROP PROCEDURE` /
  `DROP TYPE` / `DROP SEQUENCE` / `DROP TRIGGER` — after the
  `TryGetValue` succeeds and before the `TryRemove`.
- `TRUNCATE TABLE` — after `TryGetValue` succeeds.
- `ALTER TABLE` — once at the dispatcher entry; sub-parsers' own
  `TryResolveTable` calls add re-entrant Sch-S holds that release with
  everything else at statement end.

**Not yet wired** (phase 1a / 2 follow-ups): `ALTER PROCEDURE` / `ALTER
TRIGGER` / `ALTER SEQUENCE` / `ALTER SCHEMA TRANSFER` / `DROP SCHEMA`.
These rarely contend in practice and depend on the same primitive when
they land. `DROP SCHEMA` is special — `Schema` isn't a `SchemaObject`
and would need its own lock resource if schema-level concurrency
mattered.

## Same-thread deadlock detection (Msg 1205)

The user requested model: connections are owners, not threads — multiple
connections can be open on one OS thread, and they can deadlock each
other on that thread. The detection works as follows:

- Every `LockResource.Acquire` that finds a conflict walks the holder
  list and asks each holder: *"is your current-executing thread the same
  as mine?"* If yes, the caller's thread is the one that would need to
  release the holder, which it can't do while it's also the requester →
  no progress is possible → immediate Msg 1205.
- `CurrentExecutingThreadId` is set to `Environment.CurrentManagedThreadId`
  at the top of `Simulation.DispatchOneStatement` and restored in
  `finally`. Save+restore handles nested-body dispatches (procedure body,
  trigger body, scalar UDF, multi-statement TVF) — each entry uses the
  parent connection, so the value should observe nesting correctly.

Cross-thread waiter-graph cycle detection isn't part of phase 0 — Sch-S
/ Sch-M alone, given the simulator's per-statement acquisition pattern,
can't form a cross-thread cycle (Sch-S is released at statement end; a
connection can't hold Sch-S across statements and then need Sch-M on
another resource the symmetric peer holds). Cross-thread cycles emerge
with data X locks held across transactions; the detector will land in
phase 1a or 1b.

## Lock-timeout semantics

`SET LOCK_TIMEOUT N`:

- `N = -1` (the default): wait indefinitely on conflict.
- `N = 0`: fail-fast on first conflict (probe-confirmed: real SQL Server
  raises Msg 1222 within milliseconds, no grace period).
- `N > 0`: wait up to `N` milliseconds; expiry raises Msg 1222.

The acquisition path uses `Monitor.Wait(gate, remainingMs)` with the
deadline tracked via `Environment.TickCount64`. On every spurious wake
the deadline is re-checked; on every grant-fail the conflict matrix is
re-checked. Single, fixed wording for Msg 1222 — probe-confirmed
identical for row-lock and schema-lock timeouts (only the `State`
differs: `45` for row paths, `56` for schema paths; the simulator's
factory uses `56` as the default since phase 0 has only schema-level
acquires).

## SPID

`Simulation.nextSpid` seeded at `50`, `AllocateSpid()` returns the next
value via `Interlocked.Increment`. First user connection gets `51`,
matching SQL Server's "SPIDs 1-50 reserved for system, user starts at
51" convention. Surfaced in Msg 1205's `Process ID <N>` slot; will
surface in `@@SPID` / `sys.dm_exec_sessions.session_id` if those land.

## Phase-0 gaps (intentional)

- **Data locks (S / X / U / IS / IX / SIX) aren't modeled** — concurrent
  INSERT / UPDATE / DELETE across connections rely on the per-Heap
  natural-row-order discipline; phase 1a adds proper serialization with
  row-level X locks.
- **Heap mutations aren't thread-safe** across simultaneous writers on
  different threads — `Heap.Insert` / `DeleteAt` are not synchronized.
  Phase 0 tests use one-thread-at-a-time DML; phase 1a's S/X locks
  serialize naturally.
- **Lock hints (`WITH (NOLOCK)`, `HOLDLOCK`, etc.) still parse-and-
  discard** — see [`query-hints.md`](query-hints.md). They acquire no
  data locks since none exist in phase 0.
- **Isolation levels still parse-and-discard** — `SET TRANSACTION
  ISOLATION LEVEL …` accepts every recognized level and is a no-op.
- **`@@LOCK_TIMEOUT` scalar function isn't wired** — the state lives on
  `SimulatedDbConnection.LockTimeoutMillis` but the SQL reader has no
  way to introspect it through a `SELECT @@LOCK_TIMEOUT` query.
- **`SET LOCK_TIMEOUT -1`** (or any negative literal) currently parses
  through the generic Integer-option path that only accepts a single
  `Numeric` token — the tokenizer produces two tokens (`-`, `1`) for
  negatives. The default is already `-1`, so this rarely matters in
  practice; a fix would route the value through `Expression.Parse`.
