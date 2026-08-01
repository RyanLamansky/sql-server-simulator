# Locking

The model covers schema-stability locks (Sch-S / Sch-M) on every schema-bound object with the per-connection plumbing (SPID, `@@LOCK_TIMEOUT`, executing thread); **row-level data locks** under the full SQL Server 6-data-mode matrix (IS / IX / SIX / S / U / X) with transaction-scoped X retention, **SET TRANSACTION ISOLATION LEVEL** session state, and **escalation** to table-X past the per-tx-per-table threshold; the **NOLOCK / HOLDLOCK / UPDLOCK / XLOCK / READPAST / TABLOCK / TABLOCKX** hint semantics plus **REPEATABLE READ / SERIALIZABLE** isolation-level effects; auto-rollback on Msg 1205 with cross-thread waiter-graph cycle detection; and **hint-conflict detection** (Msg 1047 / 1065 / 1069).

Observability comes from the **`sys.dm_tran_locks`** and **`sys.dm_os_waiting_tasks`** DMVs plus the **`@@LOCK_TIMEOUT`** / **`@@SPID`** scalars.
Write-path coverage extends to **`ALTER PROCEDURE` / `ALTER TRIGGER` / `ALTER SEQUENCE`** Sch-M wiring, **alias-form `UPDATE` / `DELETE`** row-X acquire on the FROM-identified target, and row-X on history-table / cascade-FK / OUTPUT-INTO / SELECT INTO mutations.

User-visible behaviors:

- Writers on **different rows** of the same table don't block each other (table-IX + row-X per RID).
- Readers under default READ COMMITTED only block on the **specific row** another connection's tx is mutating, not the whole table.
- `WITH (UPDLOCK)` takes row-U tx-scoped — the classic "select-for-update" idiom — and a second connection's `UPDLOCK` on the same row blocks until the first commits.
- `WITH (XLOCK)` takes row-X tx-scoped; a concurrent read of the same row blocks (the X-X conflict surfaces through the row-X probe).
- `WITH (READPAST)` skips rows whose RID has a conflicting row-X holder instead of waiting.
- `WITH (TABLOCK)` / `WITH (TABLOCKX)` skips row-level and takes table-S / table-X directly.
- `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE` / `WITH (SERIALIZABLE)` / `WITH (HOLDLOCK)` takes table-S tx-scoped (the simulator's approximation of key-range locks at table granularity — see Granularity approximations).
- `SET TRANSACTION ISOLATION LEVEL REPEATABLE READ` / `WITH (REPEATABLEREAD)` acquires row-S tx-scoped per row read; concurrent INSERTs of *new* rows still succeed (RR doesn't prevent phantoms).
- `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED` makes every read behave like `WITH (NOLOCK)` (dirty reads).
- Per-tx row-lock count past 5000 escalates to table-X automatically.

## Lock modes

Eight modes across three orthogonal families:

| Family | Modes                         | Purpose                                          |
| ------ | ----------------------------- | ------------------------------------------------ |
| Schema | `SchemaStability`, `SchemaModification` | Sch-S held during object use; Sch-M during DDL. |
| Intent | `IntentShared`, `IntentExclusive`, `SharedIntentExclusive` | Table-level signal that some child (row) is held in S / X / both. |
| Data   | `Shared`, `Update`, `Exclusive` | Read / read-with-intent-to-update / write. Held at row OR table level depending on hint / direction. |

Compatibility matrix:

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
| `WITH (XLOCK)`                         | IX tx      | X tx-scoped                      |
| `WITH (UPDLOCK)`                       | IX tx      | U tx-scoped                      |
| `WITH (HOLDLOCK)`/`WITH (SERIALIZABLE)`/ session SER | S tx | none (table-S covers)       |
| `WITH (REPEATABLEREAD)` / session RR   | IS         | S tx-scoped                      |
| default RC                             | IS         | probe-only (no acquire)          |

Writer selection:

| Condition                  | Table mode | Row mode (per mutated row) |
| -------------------------- | ---------- | -------------------------- |
| `WITH (TABLOCKX)` / `WITH (TABLOCK)` | X tx | none (table-X covers)      |
| default                    | IX tx      | X tx-scoped                |

`READPAST` is a per-row modifier: when the row-X probe finds a conflict, the reader skips the row instead of waiting.
Applied to the reader path on top of any of the row modes above.

## Lock-owner / lock-scope model

Lock owner is always the `SimulatedDbConnection`.
Scope (when the lock releases) depends on the mode and surrounding transaction state:

| Acquired at                   | Scope                                 |
| ----------------------------- | ------------------------------------- |
| `TryResolve*` Sch-S           | Statement end                         |
| DDL site Sch-M                | Statement end                         |
| Reader RC default IS          | Statement end                         |
| Reader HOLDLOCK / SER table-S | COMMIT / ROLLBACK (tx-scoped)         |
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
| `HOLDLOCK` / `SERIALIZABLE`       | Take table-S tx-scoped (phantom prevention via table granularity). |
| `REPEATABLEREAD`                  | Take table-IS + row-S tx-scoped per row.       |
| `UPDLOCK`                         | Take table-IX + row-U tx-scoped per row.       |
| `XLOCK`                           | Take table-IX + row-X tx-scoped per row.       |
| `READPAST`                        | Skip rows with conflicting row-X holder.       |
| `TABLOCK`                         | Reader: table-S; Writer: table-X. Skip row-level. |
| `TABLOCKX`                        | Take table-X regardless of direction.          |
| `READCOMMITTED` / `READCOMMITTEDLOCK` | Parse-and-discard (equivalent to default). |
| `ROWLOCK` / `PAGLOCK`             | Parse-and-discard (row-level is default; page granularity not modeled). |
| `NOWAIT`, `KEEPIDENTITY`, etc.    | Parse-and-discard.                             |

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
| `SERIALIZABLE`     | Table-S tx-scoped (phantom prevention at table granularity).   |
| `SNAPSHOT`         | Parses-and-discards; behaves as READ COMMITTED. |

## Diagnostic DMVs

- **`sys.dm_tran_locks`** — one row per held / waiting lock across every schema-bound `SchemaLock`, every `HeapTable.TableDataLock`, and every per-row entry in `HeapTable.RowLocks`.
  Column subset: `resource_type` (`OBJECT` / `RID`), `resource_database_id`, `resource_description`, `resource_associated_entity_id` (`object_id`), `request_mode` (`Sch-S` / `Sch-M` / `IS` / `IX` / `SIX` / `S` / `U` / `X`), `request_status` (`GRANT` / `WAIT`), `request_session_id`.
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

- **Key-range locks** — real SQL Server uses key-range locks for SERIALIZABLE/HOLDLOCK to lock between rows along an index.
  Without indexes that model range structure, the simulator degenerates to table-S for phantom prevention.
  Conservative — blocks more than real SQL Server but never incorrectly allows a phantom-creating insert.
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
- HOLDLOCK retain-S-until-tx-end semantic (scope widens to table-S since key-range locks aren't modeled).
- NOLOCK / READ UNCOMMITTED dirty-read semantic.
