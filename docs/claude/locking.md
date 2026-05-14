# Locking — phases 0 + 1a

Phase 0 shipped schema-stability locks (Sch-S / Sch-M) on every schema-
bound object plus the per-connection plumbing (SPID, `@@LOCK_TIMEOUT`,
currently-executing thread). Phase 1a adds **table-level data locks
(Shared / Exclusive)**, **transaction-scoped X retention**, the
**NOLOCK / HOLDLOCK hint semantics**, **auto-rollback on deadlock victim
(Msg 1205)**, and **cross-thread waiter-graph cycle detection**.

User-visible behaviors:

- `BEGIN TRAN; INSERT/UPDATE/DELETE; …` holds **X** on the target table
  until COMMIT / ROLLBACK. A concurrent reader on another connection
  blocks on **S** until the writer commits.
- `SET LOCK_TIMEOUT N` raises **Msg 1222** on a blocked acquisition
  whose wait exceeds `N` milliseconds.
- A classic 2-cycle deadlock (A holds t1 / waits on t2; B holds t2 /
  waits on t1) raises **Msg 1205** on the connection that closes the
  cycle (the requester). The victim's transaction is auto-rolled-back;
  the survivor's UPDATE completes.
- `WITH (NOLOCK)` / `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED`
  reads through the X holder — dirty-read semantics.
- `WITH (HOLDLOCK)` / `WITH (SERIALIZABLE)` / `WITH (REPEATABLEREAD)`
  upgrades the read's **S** to transaction-scoped retention.
- A conflict against a holder running on the caller's own OS thread
  raises **Msg 1205** immediately (no progress possible — the thread is
  busy with the requester).

## Lock modes

| Mode                  | Semantics                                            |
| --------------------- | ---------------------------------------------------- |
| `SchemaStability`     | Phase 0. Held during read / use of a schema object.  |
| `SchemaModification`  | Phase 0. Exclusive against everything; held by DDL.  |
| `Shared`              | Phase 1a. Held during table reads. Multiple OK.      |
| `Exclusive`           | Phase 1a. Held during table writes. Exclusive.       |

Compatibility matrix (phase 1a):

|        | Sch-S | Sch-M | S     | X     |
| ------ | ----- | ----- | ----- | ----- |
| Sch-S  | ✓     | ✗     | ✓     | ✓     |
| Sch-M  | ✗     | ✗     | ✗     | ✗     |
| S      | ✓     | ✗     | ✓     | ✗     |
| X      | ✓     | ✗     | ✗     | ✗     |

Schema family (Sch-S / Sch-M) and data family (S / X) are orthogonal —
DDL takes Sch-M and conflicts with everything; DML takes S or X plus the
implicit Sch-S that comes with `TryResolveTable`. Same-owner re-entrance
always succeeds (the conflict check skips holders whose owner matches
the requester).

## Lock-owner / lock-scope model

Lock owner is always the `SimulatedDbConnection`. The *scope* (when the
lock releases) depends on the mode and surrounding transaction state:

| Acquired at               | No tx (auto-commit)        | Inside `BEGIN TRAN`             |
| ------------------------- | -------------------------- | ------------------------------- |
| `TryResolve*` (Sch-S)     | Released at statement end. | Released at statement end.      |
| DDL site (Sch-M)          | Released at statement end. | Released at statement end.      |
| FROM source plain (S)     | Released at statement end. | Released at statement end.      |
| FROM source HOLDLOCK (S)  | Released at statement end. | Released at COMMIT / ROLLBACK.  |
| DML target (X)            | Released at statement end. | Released at COMMIT / ROLLBACK.  |

Statement-scoped locks live in `BatchContext.StatementSchemaLocks` and
release in `DispatchOneStatement`'s `finally`. Transaction-scoped locks
live in `SimulatedDbTransaction.HeldLocks` and release in `Commit()` /
`Rollback()` / dispose-implicit-rollback. Savepoint partial rollbacks
(`ROLLBACK TRAN <savepoint>`) do NOT release locks — matches real SQL
Server (probe-confirmed).

## Acquisition sites

**Sch-S** — every successful `BatchContext.TryResolve*` path (table /
view / function / procedure / table-type / sequence) on a schema-bound
object. Skipped for temp tables / table variables / trigger
`INSERTED` / `DELETED` pseudo-tables / system tables (none cross-
connection-reachable).

**Sch-M** — every DDL site: `DROP {TABLE,VIEW,FUNCTION,PROCEDURE,TYPE,
SEQUENCE,TRIGGER}` after the lookup; `TRUNCATE TABLE`; `ALTER TABLE`
(once at the dispatcher entry).

**Shared (S)** — `BatchContext.AcquireDataLockIfApplicable(table, hints,
isWrite: false)` from FROM-source resolution in `Selection.cs` and from
MERGE bare-table source resolution in `Simulation.Merge.cs`. Skipped on
`NOLOCK` / `READUNCOMMITTED` hint. Tx-scoped on `HOLDLOCK` /
`REPEATABLEREAD` / `SERIALIZABLE` hint.

**Exclusive (X)** — `AcquireDataLockIfApplicable(table, default,
isWrite: true)` from INSERT / UPDATE / DELETE / MERGE target sites.
Tx-scoped when an explicit transaction is active.

Table variables / local temp tables / system tables bypass all data-lock
acquisition.

## Cycle detection

When a conflict-driven wait would block, `LockManager.Acquire`:

1. **Same-thread short-circuit** (carried forward from phase 0): if any
   conflicting holder's `CurrentExecutingThreadId` equals the caller's
   managed thread id, raise Msg 1205 immediately. That thread can't
   release the holder while it's also the requester.
2. **Cross-thread cycle walk**: `WouldCreateCycle` walks the wait-for
   graph starting at each conflicting holder. Each connection's
   `WaitingOnResource` (set under the gate at wait-entry, cleared in
   `finally` at wait-exit) is read consistently under the manager's
   gate. If any walk reaches the caller's connection, a cycle exists;
   caller is the victim (phase-1a policy: always-the-requester).
3. **Auto-rollback on Msg 1205**: when `LockManager` raises Msg 1205,
   `DispatchOneStatement` catches the exception, rolls back the
   connection's current transaction (releasing every held lock and
   waking the survivor), and propagates. Done BEFORE the TRY/CATCH
   frame check so both the propagating and TRY-captured paths observe
   the same auto-rollback (probe-confirmed: `@@TRANCOUNT` reads 0 in
   the catch handler).

The walker uses DFS with a `visited` set so degenerate cycles in the
holder set itself don't loop forever. The detection is O(holders ×
walk-depth) per blocked acquire — acceptable at simulator scale.

## Lock-timeout semantics

Unchanged from phase 0: `SET LOCK_TIMEOUT N` → `connection.LockTimeoutMillis`.
Negative = wait forever (default), `0` = fail-fast on first conflict,
positive `N` = wait up to `N` ms before raising Msg 1222. Phase 1a
adds the timeout to every data-lock acquire too (the same `LockManager.Acquire`
path handles all four modes).

## Hint surface

| Hint                              | Phase 1a effect                                |
| --------------------------------- | ---------------------------------------------- |
| `NOLOCK` / `READUNCOMMITTED`      | Skip S acquisition (dirty read).               |
| `HOLDLOCK` / `REPEATABLEREAD` / `SERIALIZABLE` | S upgraded to tx-scoped.          |
| `READPAST`                        | Parse-and-discard (phase 1b — row-level skip). |
| `UPDLOCK` / `XLOCK`               | Parse-and-discard (phase 1b — row U / X).      |
| `TABLOCK` / `TABLOCKX` / `ROWLOCK` / `PAGLOCK` | Parse-and-discard (phase 1b granularity). |
| Everything else                   | Parse-and-discard.                             |

The closed `TableHintNames` accept-list still raises Msg 321 on
unknown names. Conflict-detection (`Msg 1047` on `NOLOCK + XLOCK`) is
unmodeled; phase 1b's row-level dispatch will surface those naturally
once the simulator has lock state to conflict over.

## Phase-1a gaps (intentional)

- **Granularity = table-only**. Row / page / key locks not modeled —
  phase 1b. Practical effect: phase 1a is *stronger* than real SQL
  Server's READ COMMITTED (a SELECT blocks on any X-locked row in the
  table, not just the row being read). Apps that depend on row-level
  RC concurrency need phase 1b.
- **`READPAST` / `UPDLOCK` / `XLOCK` / `TABLOCK` / `TABLOCKX` /
  `ROWLOCK` / `PAGLOCK`** all parse-and-discard. The first four
  matter most in phase 1b; the last three become no-ops since
  table-only granularity collapses them.
- **REPEATABLE READ isolation level** isn't a separate mode — the
  simulator's table-level Shared lock combined with `HOLDLOCK` already
  delivers stronger-than-RR semantics. `SET TRANSACTION ISOLATION
  LEVEL REPEATABLE READ` still parses-and-discards.
- **SERIALIZABLE-as-isolation-level** vs `HOLDLOCK` hint: the hint is
  modeled (tx-scoped S); the isolation-level form is not (still
  parse-and-discard via `SET TRANSACTION ISOLATION LEVEL`).
- **UPDATE / DELETE multi-table form (alias + FROM)**: the X acquire
  fires only when the leading identifier resolves to a concrete table
  directly (the simple `UPDATE t SET …` shape). The alias-form
  `UPDATE x SET … FROM t x JOIN …` defers X acquire to phase 1b when
  proper target identification through the FROM clause lands.
- **SNAPSHOT / READ_COMMITTED_SNAPSHOT** — phase 3 (requires MVCC
  version chain on every row).
- **Lock escalation** — Msg 1204-style threshold-based promotion from
  row-level → table-level. Phase 1a is always-table-level so escalation
  is a no-op; phase 1b will need this once row locks ship.
- **`sys.dm_tran_locks` / `sys.dm_os_waiting_tasks`** — diagnostic
  views over the lock manager's state. Phase 2.
- **`@@LOCK_TIMEOUT` scalar function** — the state lives on
  `SimulatedDbConnection.LockTimeoutMillis` but isn't surfaced through
  `SELECT @@LOCK_TIMEOUT`. Phase 1b / 2.
- **`Heap.Insert` / `DeleteAt` are not thread-safe across concurrent
  writers on different threads**. Phase 1a's X locks serialize naturally
  for concurrent INSERTs into the same table; cross-table concurrent
  INSERTs from different connections are serialized only by Sch-S /
  Sch-M (not by data locks since they're on different tables). Phase 1b
  will refine via row-level locks.

## Phase-0 carry-forwards

These ship from phase 0 unchanged:

- `LockResource` + `LockManager` infrastructure (gate, holder list,
  Acquire / Release, re-entrance counting).
- `SchemaObject.SchemaLock` field.
- `SimulatedDbConnection.Spid` / `LockTimeoutMillis` /
  `CurrentExecutingThreadId`.
- `Simulation.AllocateSpid()` (first user SPID = 51).
- Msg 1222 verbatim wording (`"Lock request time out period exceeded."`,
  Class 16, State 56).
- Msg 1205 verbatim wording with SPID interpolation.
- Same-thread-deadlock short-circuit.
