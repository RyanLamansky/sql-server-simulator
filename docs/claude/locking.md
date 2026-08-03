# Locking

The model covers schema-stability locks (Sch-S / Sch-M) on every schema-bound object with the per-connection plumbing (SPID, `@@LOCK_TIMEOUT`, executing thread); **row-level data locks** under the full SQL Server 6-data-mode matrix (IS / IX / SIX / S / U / X) with transaction-scoped X retention, **SET TRANSACTION ISOLATION LEVEL** session state, and **escalation** to table-X past the per-tx-per-table threshold; **key-range locks** for SERIALIZABLE / HOLDLOCK phantom prevention; the **NOLOCK / HOLDLOCK / UPDLOCK / XLOCK / READPAST / NOWAIT / TABLOCK / TABLOCKX** hint semantics plus **REPEATABLE READ / SERIALIZABLE** isolation-level effects; auto-rollback on Msg 1205 with cross-thread waiter-graph cycle detection; and **hint-conflict detection** (Msg 1047 / 1065 / 1069).

Observability comes from the **`sys.dm_tran_locks`** and **`sys.dm_os_waiting_tasks`** DMVs plus the **`@@LOCK_TIMEOUT`** / **`@@SPID`** scalars.
Write-path coverage extends to **`ALTER PROCEDURE` / `ALTER TRIGGER` / `ALTER SEQUENCE`** Sch-M wiring, **alias-form `UPDATE` / `DELETE`** row-X acquire on the FROM-identified target, and row-X on history-table / cascade-FK / OUTPUT-INTO / SELECT INTO mutations.

User-visible behaviors:

- Writers on **different rows** of the same table don't block each other (table-IX + row-X per RID).
- Readers under default READ COMMITTED only block on the **specific row** another connection's tx is mutating, not the whole table.
- `WITH (UPDLOCK)` takes row-U tx-scoped — the classic "select-for-update" idiom — and a second connection's `UPDLOCK` on the same row blocks until the first commits.
- `WITH (XLOCK)` takes row-X tx-scoped; a concurrent read of the same row blocks (the X-X conflict surfaces through the row-X probe).
- `WITH (READPAST)` skips rows whose RID has a conflicting row-X holder instead of waiting.
- `WITH (TABLOCK)` / `WITH (TABLOCKX)` skips row-level and takes table-S / table-X directly.
- `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE` / `WITH (SERIALIZABLE)` / `WITH (HOLDLOCK)` fences the key-space interval its predicate pins on the leading columns of some key or index and leaves the rest of the table free — two SERIALIZABLE transactions over disjoint key ranges don't block each other.
  A composite key is fenced as a tuple, so `a = 1 AND b BETWEEN 2 AND 5` over a PK on `(a, b)` admits an insert of `(2, 3)` while blocking `(1, 3)`.
  A read whose shape offers no such interval falls back to table-S — see [Key-range locks](#key-range-locks).
- A SERIALIZABLE reader carrying `UPDLOCK` / `XLOCK` fences the same interval in `RangeS-U` / `RangeX-X`, the modes real reports there.
- `SET TRANSACTION ISOLATION LEVEL REPEATABLE READ` / `WITH (REPEATABLEREAD)` acquires row-S tx-scoped per row read; concurrent INSERTs of *new* rows still succeed (RR doesn't prevent phantoms).
- `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED` makes every read behave like `WITH (NOLOCK)` (dirty reads).
- Per-tx row-lock count past 5000 escalates to table-X automatically.

## Lock modes

Twelve modes across four orthogonal families:

| Family | Modes                         | Purpose                                          |
| ------ | ----------------------------- | ------------------------------------------------ |
| Schema | `SchemaStability`, `SchemaModification` | Sch-S held during object use; Sch-M during DDL. |
| Intent | `IntentShared`, `IntentExclusive`, `SharedIntentExclusive` | Table-level signal that some child (row) is held in S / X / both. |
| Data   | `Shared`, `Update`, `Exclusive` | Read / read-with-intent-to-update / write. Held at row OR table level depending on hint / direction. |
| Range  | `RangeSharedShared`, `RangeSharedUpdate`, `RangeExclusiveExclusive`, `RangeInsertNull` | Phantom prevention over a key-space interval — see [Key-range locks](#key-range-locks). |

The range family lives on its own resources (`HeapTable.KeyRangeLocks`) and never meets the other three, so its cells are settled ahead of the eight-mode table:

```
          RangeS-S RangeS-U RangeX-X RangeI-N
RangeS-S  ✓        ✓        ✗        ✗
RangeS-U  ✓        ✗        ✗        ✗
RangeX-X  ✗        ✗        ✗        ✗
RangeI-N  ✗        ✗        ✗        ✓
```

Probe-confirmed cells: a second SERIALIZABLE reader of an overlapping interval proceeds (S-S × S-S), an overlapping `UPDLOCK` reader proceeds in both orders (S-S × S-U), a second `UPDLOCK` reader of the same interval waits (S-U × S-U), an `XLOCK` holder blocks a plain SERIALIZABLE reader (X-X × S-S), and a writer's insert into the interval blocks whichever of the three holds it (× I-N).

Compatibility matrix for the other three families:

```
        Sch-S Sch-M IS    IX    SIX   S     U     X
Sch-S   ✓     ✗     ✓     ✓     ✓     ✓     ✓     ✓
Sch-M   ✗     ✗     ✗     ✗     ✗     ✗     ✗     ✗
IS      ✓     ✗     ✓     ✓     ✓     ✓     ✓     ✗
IX      ✓     ✗     ✓     ✓     ✗     ✗     ✗     ✗
SIX     ✓     ✗     ✓     ✗     ✗     ✗     ✗     ✗
S       ✓     ✗     ✓     ✗     ✗     ✓     ✓     ✗
U       ✓     ✗     ✓     ✗     ✗     ✓     ✗     ✗
X       ✓     ✗     ✗     ✗     ✗     ✗     ✗     ✗
```

Same-owner re-entrance is always compatible — the conflict check skips holders whose owner matches the requester.

## Granularity dispatch

Each user-facing DML / SELECT site goes through `BatchContext.AcquireDataLockIfApplicable(table, hints, isWrite)` which:

1. Acquires the appropriate **table-level** mode based on the matrix below.
2. Returns a `DataLockPlan` describing what per-row work to do during iteration / mutation.

Reader (no TABLOCK*) selection:

| Condition                              | Table mode | Row mode (per touched row)       |
| -------------------------------------- | ---------- | -------------------------------- |
| `WITH (NOLOCK)` / session RU           | bypass     | bypass (dirty read)              |
| `WITH (XLOCK)`                         | IX tx      | X tx-scoped (plus a `RangeX-X` fence under SER / HOLDLOCK) |
| `WITH (UPDLOCK)`                       | IX tx      | U tx-scoped (plus a `RangeS-U` fence under SER / HOLDLOCK) |
| `WITH (HOLDLOCK)`/`WITH (SERIALIZABLE)`/ session SER | IS tx | none — a `RangeS-S` key range (or the table-S fallback) covers |
| `WITH (REPEATABLEREAD)` / session RR   | IS         | S tx-scoped                      |
| default RC                             | IS         | probe-only (no acquire)          |

Writer selection:

| Condition                  | Table mode | Row mode (per mutated row) |
| -------------------------- | ---------- | -------------------------- |
| `WITH (TABLOCKX)` / `WITH (TABLOCK)` | X tx | none (table-X covers)      |
| default                    | IX tx      | X tx-scoped                |

`READPAST` is a per-row modifier: when the probe finds a conflicting holder, the reader skips the row instead of waiting.
Applied to the reader path on top of any of the row modes above — the read-committed probe looks for a row-X holder, while the `UPDLOCK` / `XLOCK` pairing probes for its own requested mode, since the holder it most often meets is another `UPDLOCK` reader's row-U (probe-confirmed: real returns the unlocked rows immediately).

`NOWAIT` is real's "equivalent to specifying `SET LOCK_TIMEOUT 0` for a specific table", and it is scoped to the table it sits on: a statement reading a second, unhinted source still waits on that one (probe-confirmed).
The hinted tables are recorded on the `BatchContext` when the source's data lock is acquired and cleared with the statement's locks, which is what lets the per-row acquisitions — reached from every DML path with nothing but a table and a RID in hand — find the scope again.
`SELECT … WITH (NOWAIT, ROWLOCK, UPDLOCK)` and `WITH (ROWLOCK, UPDLOCK, READPAST)` are the shapes `mssql-django` emits for `select_for_update(nowait=True)` / `(skip_locked=True)`.

## Lock-owner / lock-scope model

Lock owner is always the `SimulatedDbConnection`.
Scope (when the lock releases) depends on the mode and surrounding transaction state:

| Acquired at                   | Scope                                 |
| ----------------------------- | ------------------------------------- |
| `TryResolve*` Sch-S           | Statement end                         |
| DDL site Sch-M                | Statement end                         |
| Reader RC default IS          | Statement end                         |
| Reader HOLDLOCK / SER table-IS | COMMIT / ROLLBACK (tx-scoped)        |
| Reader HOLDLOCK / SER key range | COMMIT / ROLLBACK (tx-scoped)       |
| Reader HOLDLOCK / SER table-S fallback | COMMIT / ROLLBACK (tx-scoped) |
| Reader UPDLOCK / XLOCK IX     | COMMIT / ROLLBACK                     |
| Reader RR / HOLDLOCK row-S    | COMMIT / ROLLBACK                     |
| Writer IX (or X via TABLOCK*) | COMMIT / ROLLBACK                     |
| Writer row-X (per mutated row)| COMMIT / ROLLBACK                     |
| Escalated table-X             | COMMIT / ROLLBACK                     |

**Cursor-scoped locks** are a third scope, introduced for `SCROLL_LOCKS` cursors (see [`cursors.md`](cursors.md)): a table-IX held for the cursor's open lifetime plus a row-U that follows the fetched row.
They live directly on the `Cursor` (`scrollTableLock` / `scrollRowLock`), *not* in either release list, so they persist across statement and autocommit boundaries while the cursor is positioned (probe-confirmed).
Each FETCH moves the row-U (`Cursor.MoveScrollLock` releases the row scrolled off, acquires U on the new one); `Cursor.ReleaseScrollLocks` frees both on CLOSE, the last DEALLOCATE, frame teardown (LOCAL cursor), and connection dispose.
A concurrent writer of the held row blocks on the U-X conflict; a positioned UPDATE upgrades the row to X via the normal writer path (same-owner re-entrance lets the cursor's U and the writer's X coexist).

Statement-scoped locks live in `BatchContext.StatementSchemaLocks` and release in `DispatchOneStatement`'s `finally`.
Transaction-scoped locks live in `SimulatedDbTransaction.HeldLocks` and release in `Commit()` / `Rollback()` / dispose-implicit-rollback.
Savepoint partial rollbacks (`ROLLBACK TRAN <savepoint>`) do NOT release locks — matches real SQL Server (probe-confirmed).

## Row-lock storage

Per-row `LockResource`s live in `HeapTable.RowLocks`, a `ConcurrentDictionary<(int pageIndex, int slotIndex), LockResource>` keyed by RID.
Entries are lazily-interned via `GetOrCreateRowLock`; they leak across DELETE (matches the heap's existing slot / payload leak pattern, intentional).
The dict-lookup itself is thread-safe without taking the lock manager's gate; only mutations to a `LockResource`'s `Holders` list go through the gate.

`HeapTable.TableDataLock` is the table-level `LockResource` for IS / IX / SIX / S / U / X.
Distinct from the inherited `SchemaObject.SchemaLock` which carries only Sch-S / Sch-M.

`HeapTable.KeyRangeLocks` is the third store: `ConcurrentDictionary<KeyRange, LockResource>`, interned per interval and leaking the same way `RowLocks` does.
`HeapTable.ActiveKeyRangeLocks` is the `Interlocked` companion the writer's fast path reads — the exact mirror of `ActiveDataWriters`, maintained by `LockManager` on every grant / final release of a range mode.

## Key-range locks

A SERIALIZABLE (or `HOLDLOCK`-hinted) reader has to make the rows it *didn't* read unappearable for the rest of its transaction.
Real does that by locking index keys and letting each lock cover the gap below its key; the simulator locks the **key-space interval** the predicate names, which is what a `KeyRange` is: a tuple of storage ordinals, a promoted comparison type per ordinal, and a lower / upper bound tuple each optionally absent and each independently inclusive.
Bound tuples compare **lexicographically**, and either may be *shorter* than the ordinal tuple — a bound that runs out pins only the components it names, so every deeper value sits inside it.
That is the whole of the composite story: `a = 1` over a key on `(a, b)` is the one-component interval `[(1), (1)]`, `a = 1 AND b BETWEEN 2 AND 5` is `[(1,2), (1,5)]`, and `a = 1 AND b > 2` is `((1,2), (1)]` — every `b` above 2 under `a = 1`, and no other `a`.

### What the reader takes

The table-level acquisition is only **IS** (or **IX** behind `UPDLOCK` / `XLOCK`), tx-scoped, and the phantom fence is settled later — the predicate that decides between an interval and the whole table isn't known when the FROM source resolves.
`DataLockPlan.SerializableRangeMode` carries the obligation forward, naming the mode the fence has to be taken in — `RangeS-S` for a plain read, `RangeS-U` behind `UPDLOCK`, `RangeX-X` behind `XLOCK`, all three probe-confirmed against real.
Exactly two places discharge it:

- **`Selection.SettleSerializablePhantomFence`**, called from `MaybeApplyIndexSeek` once the WHERE conjuncts have been collected and *before* any candidate address is read.
  It walks the table's keys then its indexes (so the choice doesn't ride on dictionary order), scoring each by how deep an **equality prefix** the conjuncts pin on it plus whether a range bound lands on the key column right after that prefix, and acquires the plan's range mode tx-scoped over the winning tuple interval.
  The longest prefix wins, a bound continuation breaks a tie, and an `IN` list collapses to the hull of its values per column.
- **`BatchContext.EnsureSerializableTableLock`**, which takes table-S tx-scoped instead.
  Reached from `WrapWithRowConflictChecks` (the un-narrowed scan's own iterator), from the ordered-scan path, and from `SettleSerializablePhantomFence` itself when no conjunct offers an interval.
  Idempotent per batch per table, since a source can be re-enumerated many times.

The two are mutually exclusive **per source**, which `DataLockPlan.Fence` (a `PhantomFenceState` cell the plan's struct copies share) enforces: a source that claimed a range must not then have the table-S added on top by the scan wrapper, which would re-block the key space the range deliberately left free.
That matters because an `UPDLOCK` / `XLOCK` reader keeps the whole-table scan — its tx-scoped row locks decline the seek — while still fencing only its own interval.
The fence itself is *not* short-circuited on the cell: a correlated inner re-plans per outer row and each outer value names an interval of its own.

Soundness is the seek's own property: every conjunct considered is a top-level `AND` factor, so every row the query can ever return satisfies it, so every row that could become a phantom carries a key tuple inside the interval — which is why the fence stays sound even when the access path scans.
A conjunct that can't be evaluated cleanly (NULL probe, cross-collation string, unpromotable pair) bounds the prefix there rather than narrowing the fence past what it can justify.

### What the writer probes

`BatchContext.ProbeKeyRangesForWrite` runs on every writer **whatever its own isolation level** — fencing sessions that know nothing about the fence is the entire point.
It reads `ActiveKeyRangeLocks` first and returns immediately at zero, so a database with no SERIALIZABLE reader pays nothing; otherwise it decodes one value per distinct ranged ordinal, assembles each range's own ordinal tuple out of them, and — for each range containing that tuple — acquires and immediately releases `RangeI-N`.
That mirrors real's instant-duration insert-range mode: it exists to test the interval and never shows up in a lock snapshot taken after the write.

Two hooks put it on every write path:

- Inside `AcquireRowLockTxScoped` when the mode is `Exclusive`, against the row's **live slot bytes**. Each site's ordering makes that the image that matters: an INSERT locks after the heap write, so it reads its new row; an UPDATE / DELETE locks before, so it reads the row it is about to supersede.
- Explicitly against the **post-update image** at each `Heap.UpdateAt` site (`Simulation.Update`, MERGE's update branch, the two FK cascade rewrites) — a row moving *into* a fenced interval is a phantom the old image can't reveal, probe-confirmed to block on real.

The main INSERT path probes once more, *before* the heap write rather than after: a wait on a range can last until the reader commits, and a row sitting in the heap with no row-X on it yet would be dirty-readable for that whole window.

Range waits go through `LockManager.Acquire` like everything else, so they enter the wait-for graph unchanged — two transactions each fencing one interval and inserting into the other's deadlock with Msg 1205, and `SET LOCK_TIMEOUT` yields Msg 1222.
Same-owner holds are skipped by the conflict check, so a SERIALIZABLE transaction inserting into its own fenced interval isn't self-blocked.

### Probed reference behavior

Against SQL Server 2025 CU7, `sys.dm_tran_locks` under SERIALIZABLE:

| Read                                        | What real takes                                              |
| ------------------------------------------- | ------------------------------------------------------------ |
| Equality **hit** on a unique index           | plain `KEY` **S** — uniqueness already forbids a second row at that key |
| Equality **miss** on a unique index          | `RangeS-S` on the next key                                    |
| Equality **hit** on a non-unique index       | `RangeS-S` on the matched key *and* the next one              |
| `k > a AND k < b`                            | `RangeS-S` on every key in the interval plus the next one past it |
| `k > a` past the last key                    | `RangeS-S` on each matching key plus the infinity range (`ffffffffffff`) |
| Whole-table scan / non-sargable predicate    | `RangeS-S` on every key plus infinity — the whole key space   |
| Predicate on an unindexed heap               | object-level **S**                                            |
| Same reads under REPEATABLE READ             | plain `KEY` S, no ranges                                      |
| `WITH (HOLDLOCK)` under READ COMMITTED       | identical to SERIALIZABLE                                     |
| `WITH (UPDLOCK)`                             | `RangeS-U` at the key, **IX** at the object, no key U beside it |
| `WITH (XLOCK)`, and a SERIALIZABLE UPDATE / DELETE | `RangeX-X` at the key, IX at the object (the same write under READ COMMITTED takes plain `KEY` X) |

The uniqueness exemption is a **full-tuple** rule, probed on a composite key both ways: `a = 1 AND b = 2` hitting a unique `(a, b)` index takes plain `KEY` S, the same predicate against a non-unique one takes `RangeS-S` on the matched key plus the next.
Composite reads otherwise follow the single-column shape — `a = 1` alone locks the three keys of the group plus the next key past it, `a = 1 AND b BETWEEN 2 AND 5` locks the three keys it spans, and a predicate on the **second** column alone locks every key plus infinity.

And the blocking matrix, session A holding a SERIALIZABLE `BETWEEN` over an indexed key:

| Session B                                    | Real   |
| -------------------------------------------- | ------ |
| INSERT inside the interval                    | blocks |
| INSERT outside it                             | proceeds |
| UPDATE / DELETE of a row inside it            | blocks |
| UPDATE moving a row from outside *into* it    | blocks |
| UPDATE of a row outside it                    | proceeds |
| SERIALIZABLE SELECT of an overlapping interval | proceeds |
| SERIALIZABLE SELECT of a disjoint interval    | proceeds |
| Crossed intervals, each inserting into the other's | Msg 1205 |

Over a composite `a = 1 AND b BETWEEN 2 AND 5` fence, `INSERT (1, 3)` blocks and `INSERT (2, 3)` proceeds — the leading component is what separates them, and it is exactly the separation the tuple interval reproduces.
The same holds for the prefix-only `a = 1` fence: `(1, 100)` blocks and `(2, 5)` proceeds.
A `RangeS-U` fence blocks an insert inside it and admits one outside, like `RangeS-S`.

### Divergences

- **Predicate-exact intervals, not real's key-anchored ones.**
  Real's range is `(previous key, named key]`, so its coverage runs out to the neighbouring key on each side; the simulator's runs to the predicate's own bound.
  Phantom protection is identical — a value real blocks but the simulator doesn't is by construction a value the reader's predicate excludes, so admitting it can't change any result the reader could re-read.
  The observable difference is confined to that gap: probed, `WHERE k = 205` against keys 200 / 210 blocks an insert of 207 on real (207 falls in key 210's range) where the simulator admits it, and both block an insert of 205.
  The tuple case is the same gap one component deeper: an `a = 1 AND b BETWEEN 2 AND 5` fence over keys `(1,2)` / `(1,5)` / `(1,9)` blocks an insert of `(1, 7)` on real (it falls in key `(1,9)`'s range) where the simulator admits it.
- **A held range meets another reader's range only when the two intervals are *identical*.**
  Ranges intern per interval in `HeapTable.KeyRangeLocks`, so two readers fencing overlapping-but-different intervals take different resources and never test each other's mode.
  Containment is tested on the **write** path only, where `ProbeKeyRangesForWrite` walks every held range.
  Reader-versus-reader that matters (`RangeS-U` × `RangeS-U`, `RangeX-X` × anything) therefore surfaces on the repeated-predicate shape and not on a merely overlapping one.
- **An `IN` list fences its hull**, gaps between the listed values included — over-blocking rather than leaving a listed value unfenced.
  Across a multi-column prefix the hull is taken per column, so the lexicographic interval spans the whole cartesian product and then some.
- **The `UPDLOCK` / `XLOCK` row lock stays on top of the range**, where real folds the two into one key lock.
  Range modes live on resources of their own here, so dropping the row-U / row-X would stop blocking the readers and writers that take a row lock without ever probing a range.
- **A SERIALIZABLE `UPDATE` / `DELETE` takes no fence of its own.**
  Probed, real converts the writer's key locks to `RangeX-X` under SERIALIZABLE; the simulator's writer path takes table-IX plus row-X whatever the isolation level, so the rows it touches are locked and the gaps between them are not.
  Reads at the same level fence normally, and every writer is still fenced by *other* sessions' ranges.
- **`resource_description` names the interval**, e.g. `0:[15,25]` for one column and `0,1:[(1,2),(1,5)]` for a tuple — the ranged ordinals, then the interval in bracket notation with `*` for an unbounded side and for a component a shorter bound tuple leaves open.
  Real prints a hash of the anchoring index key there, so the `resource_type` (`KEY`) and `request_mode` (`RangeS-S` / `RangeS-U` / `RangeX-X`) match and the description doesn't.
- **A non-default isolation level disables the plan cache.**
  A cached plan's FROM sources carry the lock acquisitions their parsing session made, so replaying one under a different level would settle the wrong session's protection, or none.
  Anything but the default READ COMMITTED now skips both the plan-cache lookup and the promotion and re-parses per execution — see [`plan-cache.md`](plan-cache.md).

## Lock-free read fast path

Every grant / release / probe funnels through `LockManager`'s single gate, so under heavy concurrent reads a per-row gate acquisition would serialize the workers.
The READ COMMITTED row-conflict check (`BatchContext.TouchRowForRead`) avoids the gate on the common path via `HeapTable.ActiveDataWriters` — an `Interlocked` count of connections currently holding a data-`Exclusive` lock anywhere on the table (a row-X or the table-X).
`LockManager` increments it on an `Exclusive` grant and decrements on the final release of one, keyed by a `LockResource.OwningTable` back-reference set when the resource is interned.
The reader:

1. `Volatile.Read`s the count; if 0, no row is X-locked, so every row is committed-readable — return immediately, **no `RowLocks` intern, no gate**.
2. If non-zero, look up the specific row with `RowLocks.TryGetValue` (still no intern — a row with no interned entry has no holder, so it reads through); only when an entry exists does it probe under the gate and wait / READPAST-skip as before.

Counting only `Exclusive` (U is `S`-compatible; IX / SIX are table-level intent the row probe already ignores) keeps the visible behavior identical to the always-probe path — it only elides gate traffic when no X exists.
Snapshot / RCSI reads never reach this path (they resolve through the version store), so the fast path is a pure READ COMMITTED non-snapshot win.
The `ActiveDataWriters` invariant (0 at rest, follows the X through commit / rollback / escalation) is guarded by `LockResourceTests.ActiveDataWriters_*`.

## Escalation

`SimulatedDbTransaction.RowLockCountsByTable` tracks the per-tx per-table row-lock count; `EscalatedTables` is the set of tables already escalated.
When the count crosses `RowLockEscalationThreshold` (5000, matching real SQL Server's default), `BatchContext.EscalateToTableX`:

1. Acquires table-X tx-scoped on the table (may block if other connections hold conflicting locks; throws as usual on timeout / deadlock).
2. Releases every entry in `tx.HeldLocks` that's a row-lock on the escalated table.
3. Marks the table in `EscalatedTables` so subsequent row-X requests on the same table short-circuit (the table-X already covers).

`AcquireRowLockTxScoped` checks the escalated-set before acquiring; the short-circuit means escalation amortizes across long bulk-DML sequences.

## Acquisition sites

**Sch-S** — every successful `BatchContext.TryResolve*` path (table / view / function / procedure / table-type / sequence) on a schema-bound object.
Skipped for temp tables / table variables / trigger `INSERTED` / `DELETED` pseudo-tables / system tables.

**Sch-M** — every DDL site: `DROP {TABLE,VIEW,FUNCTION,PROCEDURE,TYPE, SEQUENCE,TRIGGER}` after the lookup; `TRUNCATE TABLE`; `ALTER TABLE`.

**Data locks** — `BatchContext.AcquireDataLockIfApplicable(table, hints, isWrite)` from FROM-source resolution in `Selection.cs` and INSERT / UPDATE / DELETE / MERGE target / MERGE bare-table-source sites.

**Row locks (X)** — `BatchContext.AcquireRowLockTxScoped(table, pageIndex, slotIndex, Exclusive)` from each `Heap.Insert` / `Heap.DeleteAt` callsite inside INSERT / UPDATE / DELETE / MERGE (the four user-DML statement kinds).
Update is a delete+insert pair, so both the old RID and the new RID get row-X.

**Row probe / row-S / row-U / row-X (reads)** — `BatchContext.TouchRowForRead(table, pageIndex, slotIndex, plan)` during heap row enumeration (wrapped by `BatchContext.WrapWithRowConflictChecks`).
Iterators that need addresses go through `Heap.EnumerateRowsWithAddress` instead of `Heap.EnumerateRows`.

Table variables / local temp tables / system tables bypass all data-lock acquisition (and row-lock acquisition).

## Cycle detection

When a conflict-driven wait would block, `LockManager.Acquire`:

1. **Same-thread short-circuit**: if any conflicting holder's `CurrentExecutingThreadId` equals the caller's managed thread id, raise Msg 1205 immediately.
2. **Cross-thread cycle walk**: `WouldCreateCycle` walks the wait-for graph starting at each conflicting holder.
   Each connection's `WaitingOnResource` is read consistently under the manager's gate.
   If any walk reaches the caller's connection, a cycle exists; caller is the victim (always-the-requester policy).
3. **Auto-rollback on Msg 1205**: `DispatchOneStatement` catches the exception, rolls back the connection's current transaction (releasing every held lock and waking the survivor), and propagates.
   Done BEFORE the TRY/CATCH frame check so both the propagating and TRY-captured paths observe the auto-rollback (probe-confirmed: `@@TRANCOUNT` reads 0 in the catch handler).

## Lock-timeout semantics

`SET LOCK_TIMEOUT N` → `connection.LockTimeoutMillis`.
Negative = wait forever (default), `0` = fail-fast on first conflict, positive `N` = wait up to `N` ms before raising Msg 1222.
Applies uniformly to schema locks, data locks, and row locks.

## Hint surface

| Hint                              | Effect                                         |
| --------------------------------- | ---------------------------------------------- |
| `NOLOCK` / `READUNCOMMITTED`      | Skip every acquisition (dirty read).           |
| `HOLDLOCK` / `SERIALIZABLE`       | Take table-IS tx-scoped plus a `RangeS-S` key range over the predicate's interval, or table-S when there is no interval to take. |
| `REPEATABLEREAD`                  | Take table-IS + row-S tx-scoped per row.       |
| `UPDLOCK`                         | Take table-IX + row-U tx-scoped per row, plus a `RangeS-U` fence under SERIALIZABLE / `HOLDLOCK`. |
| `XLOCK`                           | Take table-IX + row-X tx-scoped per row, plus a `RangeX-X` fence under SERIALIZABLE / `HOLDLOCK`. |
| `READPAST`                        | Skip rows another connection holds incompatibly instead of waiting — the row-X a writer holds, and the row-U / row-X the `UPDLOCK` / `XLOCK` pairing meets. |
| `TABLOCK`                         | Reader: table-S; Writer: table-X. Skip row-level. |
| `TABLOCKX`                        | Take table-X regardless of direction.          |
| `READCOMMITTED` / `READCOMMITTEDLOCK` | Parse-and-discard (equivalent to default). |
| `ROWLOCK` / `PAGLOCK`             | Parse-and-discard (row-level is default; page granularity not modeled). |
| `NOWAIT`                          | Zero the lock timeout for the hinted table, so a conflicting acquisition raises Msg 1222 at once rather than waiting. |
| `KEEPIDENTITY`, etc.              | Parse-and-discard.                             |

The closed `TableHintNames` accept-list still raises Msg 321 on unknown names.
Conflict-detection (`Msg 1047` on `NOLOCK + XLOCK`, `Msg 1065` on NOLOCK against a DML target) is unmodeled.

## Isolation-level semantics

`SET TRANSACTION ISOLATION LEVEL` mutates `SimulatedDbConnection.SessionIsolationLevel`.
The session value persists across statements until the next SET.
Per-isolation reader behavior:

| Level              | Reader behavior                                                |
| ------------------ | -------------------------------------------------------------- |
| `READ UNCOMMITTED` | Skip every conflict check (dirty read). Equivalent to NOLOCK on every read. |
| `READ COMMITTED` (default) | Table-IS + per-row probe (wait on row-X holders, no row-S acquire). |
| `REPEATABLE READ`  | Table-IS + row-S tx-scoped per row read.                       |
| `SERIALIZABLE`     | Table-IS tx-scoped + a key-range lock per sargable predicate, table-S otherwise. `UPDLOCK` / `XLOCK` shift the table lock to IX and the range mode to `RangeS-U` / `RangeX-X`. |
| `SNAPSHOT`         | Parses-and-discards; behaves as READ COMMITTED. |

## Diagnostic DMVs

- **`sys.dm_tran_locks`** — one row per held / waiting lock across every schema-bound `SchemaLock`, every `HeapTable.TableDataLock`, every per-row entry in `HeapTable.RowLocks`, and every interned interval in `HeapTable.KeyRangeLocks`.
  Column subset: `resource_type` (`OBJECT` / `RID` / `KEY`), `resource_database_id`, `resource_description`, `resource_associated_entity_id` (`object_id`), `request_mode` (`Sch-S` / `Sch-M` / `IS` / `IX` / `SIX` / `S` / `U` / `X` / `RangeS-S` / `RangeS-U` / `RangeX-X` / `RangeI-N`), `request_status` (`GRANT` / `WAIT`), `request_session_id`.
  Row generator at `LockDmvs.EnumerateDmTranLocks`.
- **`sys.dm_os_waiting_tasks`** — one row per currently-blocked connection: `session_id` (waiter's SPID), `wait_type` (`LCK_M_<mode>`), `resource_description`, `blocking_session_id` (one conflicting holder's SPID).
  Row generator at `LockDmvs.EnumerateDmOsWaitingTasks`.
  Waiter / mode state lives in `SimulatedDbConnection.WaitingOnResource` / `WaitingForMode` (set inside `LockManager.Acquire`'s wait, cleared in `finally`).

Neither DMV takes the manager's gate during enumeration — concurrent acquires / releases may shift the result between rows, but per-resource snapshots stay consistent (Holders list is read field-by-field; the struct copy can't tear).

## Hint-conflict detection

- **Msg 1047** — `Conflicting locking hints specified.`
  Raised when `NOLOCK` / `READUNCOMMITTED` appears alongside any of `UPDLOCK` / `XLOCK` / `HOLDLOCK` / `SERIALIZABLE` / `REPEATABLEREAD` / `TABLOCKX`.
  Fired at `Selection.ValidateHintCombinations` after the hint-list parse.
  Probe-confirmed verbatim wording.
- **Msg 1065** — `The NOLOCK and READUNCOMMITTED lock hints are not allowed for target tables of INSERT, UPDATE, DELETE or MERGE statements.`
  Raised at INSERT / UPDATE / DELETE / MERGE target sites via `Selection.ValidateDmlTargetHints` when the parsed hints carry `NoLock`.
  Probe-confirmed verbatim.
- **Msg 1069** — `Index hints are only allowed in a FROM or OPTION clause.`
  Raised at the same DML target sites when the parsed hints carry `IndexHint` (set on `INDEX(…)` / `FORCESEEK` / `FORCESCAN`).
  Probe-confirmed verbatim.

## Write-path row-X coverage

- **UPDATE / DELETE multi-table-alias form** — `UPDATE x SET … FROM t x JOIN …` acquires table-IX on the FROM-identified target before the row-X loop, matching the simple-form behavior.
- **History-table writes for system-versioned UPDATE / DELETE** — per-row history-table inserts acquire row-X tx-scoped.
- **Cascade-FK SET NULL / SET DEFAULT / CASCADE writes on child tables** — each cascade-rewrite acquires row-X on the old + new RID.
- **OUTPUT INTO target / SELECT INTO destination** — per-row row-X on the destination table.
- **Row-lock cleanup on DELETE** — per-row `DeleteAt` is followed by `table.RowLocks.TryRemove((page, slot), out _)` for every successfully tombstoned slot (guard: `IsLockableTable(table)` so table-vars / temp-tables / system tables skip the path that never populated the dict).
  Safe because slot directory entries never reuse (the heap's slot-leak quirk doubles as a guarantee here) and concurrent accessors can't reach a tombstoned slot — heap iteration skips them, and SI / RCSI tombstoned-slot resolution walks via the separate `RowVersions` dict without probing `RowLocks`.
  The row-X acquired during the DELETE remains held in `tx.HeldLocks` / `StatementSchemaLocks` until commit / statement end; the LockResource reference there keeps the resource alive even after the dict entry is dropped.

## Granularity approximations

- **Key-range granularity** — a range fences the leading-column tuple the predicate pins, so a non-sargable predicate, a predicate on an unindexed (or non-leading) column, and a whole-table scan all fall back to table-S.
  Conservative — blocks more than real SQL Server but never incorrectly allows a phantom-creating insert.
  Full account in [Key-range locks](#key-range-locks).
- **Page-level locks (`PAGLOCK`)** — page granularity isn't modeled; the hint parses-and-discards.
  Locking is row-level by default, so the hint is a no-op semantically.
- **`ALTER SCHEMA TRANSFER`** — Sch-M on the moved object isn't acquired.

## Snapshot isolation + MVCC

`ALLOW_SNAPSHOT_ISOLATION` and `READ_COMMITTED_SNAPSHOT` are per-database flags on `Database` (both default `false`, flipped via `ALTER DATABASE … SET (ALLOW_SNAPSHOT_ISOLATION | READ_COMMITTED_SNAPSHOT) { ON | OFF }`).
When either flag is on, every INSERT / UPDATE / DELETE captures a row-version entry in the per-table `HeapTable.RowVersions` dict; readers under SNAPSHOT or RCSI consult the chain to substitute pre-write payloads.

### Database flags
Both flags are read off the **table's own database**, not the session's (probe-confirmed in all four combinations): a session in a non-RCSI database reading a three-part name into an RCSI one reads versioned, the reverse blocks on the writer's X lock, and a SNAPSHOT session's Msg 3952 names the target database it reached rather than the one it sits in.

- `Database.AllowSnapshotIsolation` — gates `SET TRANSACTION ISOLATION LEVEL SNAPSHOT` reads.
  When OFF and a session at the Snapshot iso level accesses a user table, **Msg 3952** fires verbatim: `Snapshot isolation transaction failed accessing database '<db>' because snapshot isolation is not allowed in this database. Use ALTER DATABASE to allow snapshot isolation.` (Cls 16, State 1).
  Probe-confirmed the rejection point is the first user-table access — `set transaction isolation level snapshot` is silent, system-catalog reads (sys.tables / sys.objects) succeed silently, and the check fires whether the access is read or write.
  Table-variable / temp-table / system-catalog access bypasses the gate.
  Real SQL Server's "requires brief stabilization" semantic on the ON flip is not modeled — the simulator's flip takes effect immediately.
- `Database.ReadCommittedSnapshot` — when ON, default-RC reads switch to version-store reads with a per-statement snapshot Xid (carried in `BatchContext.RcsiStatementSnapshotXid`, cleared between statements by the dispatch loop).
  Writers under RCSI behave identically to vanilla RC (row-X tx-scoped).
  Real SQL Server's "requires single-user-mode" semantic on the flip is not modeled.

### Commit-Xid allocator
`Simulation.AllocateTransactionCommitId()` is a monotonic **instance-wide** counter; each committing transaction reads one stamp however many databases it wrote to, and SI readers acquire their snapshot via `Simulation.CurrentTransactionCommitId`.
Counter starts at zero so pre-versioning rows (implicit Xmin = 0) are visible to every snapshot.

Instance scope mirrors real, whose transaction sequence number is server-wide (its version store lives in `tempdb`, not per database), and it is what makes a snapshot stamp comparable across databases.
Probed: a SNAPSHOT transaction fixes **one** stamp at its first data-access statement and reads *every* database as of that instant — a transaction whose first read was in one database still sees another's pre-update state when it reads it later, and `BEGIN TRAN` alone fixes nothing (a commit landing before the first read is visible).
`Simulation.ActiveSnapshotTxs` is instance-wide for the same reason: an open snapshot anywhere pins history everywhere, so the GC cutoff reads the simulation's oldest active Xid.

### Version-store data structures
Per-`HeapTable`: `ConcurrentDictionary<(int Page, int Slot), RowVersionChain> RowVersions`.

`RowVersionChain`:
- `LiveXmin: long` — commit Xid of the live row.
- `WriterTx: SimulatedDbTransaction?` — non-null while an in-flight writer's pre-commit payload occupies the live slot.
  SI readers see this and walk history.
- `IsDeletedLive: bool` — true after a committed DELETE tombstones the slot; readers with snapshot before the delete Xid still see the historical payload through `Head`.
- `Head: HistoricalVersion?` — linked list of older committed versions, newest-first.
  Walked by the visibility predicate `Xmin <= SX < Xmax` (with `Xmax = long.MaxValue` denoting a still-in-flight superseder).

### Writer-side capture
`VersionStore.CaptureWrite(batch, table, newRid, oldRid?, oldPayload?, kind)` is called from every DML mutation site after the heap mutation lands:
- **INSERT**: creates chain at `newRid` with `WriterTx = tx`, `LiveXmin = 0` (sentinel); commit stamps `LiveXmin = commitXid`, clears `WriterTx`; rollback removes the chain entirely.
- **UPDATE**: reads the existing chain at `oldRid` (if any) to inherit its `LiveXmin` + `Head`, builds a fresh `HistoricalVersion { Payload = oldPayload, Xmin = oldLiveXmin, Xmax = PendingXmax, Next = oldHead }`, creates chain at `newRid` with that HV at `Head` and `WriterTx = tx`.
  Commit replaces the pending Xmax with the real commit Xid, stamps `LiveXmin`, drops the abandoned old-slot chain.
  Rollback removes the new chain entirely (old chain stays).
- **DELETE**: marks existing chain's `WriterTx = tx`; commit pushes pre-delete payload to `Head`, stamps `LiveXmin = commitXid` (the delete Xid), sets `IsDeletedLive`.
  Rollback clears `WriterTx`.

Capture is a no-op when neither flag is on for the database, when the table is a table-variable / local-temp / system table, or when the writer's iso level doesn't participate (uncovered — versioning happens for any writer when the flag is on, regardless of writer's iso).

### Pending-entries lifecycle
Each `SimulatedDbTransaction.PendingVersionEntries` accumulates captures across the tx; `Commit` hands the list to `VersionStore.FinalizePendingEntries` (allocates one commit Xid for the whole batch, walks each entry stamping chains), `Rollback` / implicit-Dispose hands it to `VersionStore.DiscardPendingEntries` (walks each entry undoing the in-flight mark).

For auto-commit DML (no active tx), `RunMutation` allocates a fresh list on `BatchContext.CurrentStatementVersionEntries`, drains on success / discards on failure — same surface as the existing per-statement undo log.

### Reader-side visibility
`BatchContext.ResolveSnapshotXidForRead(table)` returns:
- `tx.SnapshotXid` (lazy-allocated at first user-table read) for SI sessions inside a transaction.
- `BatchContext.RcsiStatementSnapshotXid` (lazy-allocated at first user-table read in this statement) for default-RC sessions when `ReadCommittedSnapshot` is on.
- `null` for every other reader path (NOLOCK, default-RC without RCSI, RR, SERIALIZABLE, table variables, temp tables, system catalogs).

`BatchContext.WrapWithRowConflictChecks` consults the snapshot Xid and routes through `VersionStore.ResolveVisibleVersion` per row: returns the live payload, a historical payload, or `null` (skip the row — inserted-after-snapshot or already-deleted-pre-snapshot).

### Update-conflict detection (Msg 3960)
`VersionStore.CheckSnapshotUpdateConflict(batch, table, rid)` runs at the top of `CommitUpdate` and `CommitDelete` when the writer's iso is Snapshot.
Raises **Msg 3960** verbatim (`Snapshot isolation transaction aborted due to update conflict. You cannot use snapshot isolation to access table '<schema>.<table>' directly or indirectly in database '<db>' to update, delete, or insert the row that has been modified or deleted by another transaction. Retry the transaction or change the isolation level for the update/delete statement.` Cls 16, State 2) when the chain at the target Rid shows `LiveXmin > snapshotXid` or a foreign `WriterTx`.
Auto-rolls back the SI transaction before throwing (probe-confirmed `@@TRANCOUNT = 0` in the CATCH block).

### Tombstoned-slot snapshot pass
SI / RCSI iteration walks tombstoned slots in a second pass after the live-heap pass so deleted rows whose pre-delete payload is still visible at the snapshot surface correctly.
Same per-row visibility check (`VersionStore.ResolveTombstonedSlotForSnapshot`) used at both sites — readers (via `WrapWithRowConflictChecks`) and writers (via `Simulation.CheckSnapshotConflictOnTombstonedRows`, called at the top of UPDATE / DELETE before the affected-rows mutation loop).
The writer-side scan decodes each candidate, evaluates the WHERE predicate against it, and raises Msg 3960 + auto-rolls back if WHERE matches a tombstoned-but-visible row.
`Heap.IsSlotTombstoned(pageIndex, slotIndex)` (and the underlying `HeapPage.IsSlotTombstoned`) exposes the per-slot tombstone bit so the snapshot-aware iterators filter duplicate yields against the live-heap pass.

### MVCC observability

Three DMVs cover version-store state, with column shapes probe-confirmed against SQL Server 2025 so existing diagnostic queries port unchanged:

- **`sys.dm_tran_version_store`**: one row per finalized `HistoricalVersion` across every per-table chain.
  Columns: `transaction_sequence_num` (= HV.Xmax, the commit Xid that retired the version), `version_sequence_num` (per-tx sub-sequence synthesized at enumeration time), `database_id`, `rowset_id` (= table.ObjectId), `status` (0), `min_length_in_bytes` / `record_length_first_part_in_bytes` (= payload byte count), `record_image_first_part` (raw payload bytes), `record_length_second_part_in_bytes` (0) / `record_image_second_part` (NULL — payloads always fit in the first part since the simulator stores them as a single `byte[]`).
  Pending HVs (Xmax = `VersionStore.PendingXmax`) excluded.
- **`sys.dm_tran_version_store_space_usage`**: one row per database aggregating payload bytes — `reserved_page_count` = ceil(bytes / 8192) approximates the buffer-pool figure that real SQL Server reports, `reserved_space_kb` = ceil(bytes / 1024).
  Always yields one row (matches probe — empty stores show as `0` not row-empty).
- **`sys.dm_tran_active_snapshot_database_transactions`**: one row per active SI tx with `tx.SnapshotXid != null`.
  Columns: `transaction_id` (synthesized from object hash code), `transaction_sequence_num` (= `tx.SnapshotXid`), `commit_sequence_num` (NULL — tx is still in flight), `session_id` (= `tx.connection.Spid`), `is_snapshot` (always true — RCSI per-statement snapshots aren't tracked, matching real server behavior for this DMV), `first_snapshot_sequence_num` (NULL), `max_version_chain_traversed` / `average_version_chain_traversed` / `elapsed_time_seconds` (0 — simulator doesn't instrument those).

### Version-store garbage collection

`VersionStore.RunGarbageCollection(Database)` runs at every `SimulatedDbTransaction.Commit / Rollback / Dispose`.
Walks every per-table `RowVersions` chain and drops trailing `HistoricalVersion` nodes whose `Xmax <= oldest_active_snapshot_xid` (no active SI transaction needs them anymore).
When no SI tx is in flight, the cutoff is `Simulation.CurrentTransactionCommitId` so every finalized HV becomes collectible.
Chains that lose their only HV AND aren't `IsDeletedLive` AND have no in-flight `WriterTx` get removed from the dict entirely; chains with non-null `WriterTx` are skipped (a `PendingXmax`-marked HV must not be disturbed mid-tx).

The oldest active Xid comes from `Simulation.ActiveSnapshotTxs`, a `ConcurrentDictionary<SimulatedDbTransaction, byte>` populated at `BatchContext.ResolveSnapshotXidForRead` (first user-table read of an SI tx) and drained at tx finalization.
RCSI per-statement snapshots don't register here — their sub-statement lifetime means the once-per-tx GC cadence won't observe them as load-bearing, and the short window of risk is bounded by statement execution time.

### Known MVCC limitations
- **Multi-update-within-one-tx history collapse**: real SQL Server collapses intra-tx intermediate states (only the pre-tx + post-tx states are visible).
  The simulator records every capture, so the chain has one HV per UPDATE rather than one per committed transaction.
  Visibility outcome is identical for the common case (single UPDATE per tx); divergence surfaces only when a snapshot lands between intermediate states of a single tx.

## Table-level and schema-lock behaviors

Retained at table / schema granularity:

- `LockResource` data carrier + `LockManager` (gate, Acquire / Release, re-entrance counting, cycle detection).
- `SchemaObject.SchemaLock` field.
- `SimulatedDbConnection.Spid` / `LockTimeoutMillis` / `CurrentExecutingThreadId` / `WaitingOnResource`.
- `Simulation.AllocateSpid()` (first user SPID = 51).
- Msg 1222 verbatim wording (Class 16, State 56).
- Msg 1205 verbatim wording with SPID interpolation; auto-rollback of victim's tx.
- Same-thread-deadlock short-circuit.
- HOLDLOCK retain-until-tx-end semantic, over a key range where the predicate offers one and table-S otherwise, in the range mode any `UPDLOCK` / `XLOCK` alongside it names.
- NOLOCK / READ UNCOMMITTED dirty-read semantic.
