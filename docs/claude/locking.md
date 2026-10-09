# Locking

The model covers schema-stability locks (Sch-S / Sch-M) on every schema-bound object with the per-connection plumbing (SPID, `@@LOCK_TIMEOUT`, executing thread); **row-level data locks** under the full SQL Server 6-data-mode matrix (IS / IX / SIX / S / U / X) with transaction-scoped X retention, **SET TRANSACTION ISOLATION LEVEL** session state, and per-statement **escalation** to a table S or X at real's threshold; **key and key-range locks** anchored on index keys for SERIALIZABLE / HOLDLOCK phantom prevention; the **NOLOCK / HOLDLOCK / UPDLOCK / XLOCK / READPAST / NOWAIT / TABLOCK / TABLOCKX** hint semantics plus **REPEATABLE READ / SERIALIZABLE** isolation-level effects; auto-rollback on Msg 1205 with cross-thread waiter-graph cycle detection; and **hint-conflict detection** (Msg 1047 / 1065 / 1069).

Observability comes from the **`sys.dm_tran_locks`** and **`sys.dm_os_waiting_tasks`** DMVs plus the **`@@LOCK_TIMEOUT`** / **`@@SPID`** scalars.
Write-path coverage extends to **`ALTER PROCEDURE` / `ALTER TRIGGER` / `ALTER SEQUENCE`** Sch-M wiring, **alias-form `UPDATE` / `DELETE`** row-X acquire on the FROM-identified target, and row-X on history-table / cascade-FK / OUTPUT-INTO / SELECT INTO mutations.

User-visible behaviors:

- Writers on **different rows** of the same table don't block each other (table-IX + row-X per RID).
- Readers under default READ COMMITTED only block on the **specific row** another connection's tx is mutating, not the whole table.
- `WITH (UPDLOCK)` takes row-U tx-scoped — the classic "select-for-update" idiom — and a second connection's `UPDLOCK` on the same row blocks until the first commits.
- `WITH (XLOCK)` takes row-X tx-scoped; a concurrent read of the same row blocks (the X-X conflict surfaces through the row-X probe).
- `WITH (READPAST)` skips rows whose RID has a conflicting row-X holder instead of waiting.
- `WITH (TABLOCK)` / `WITH (TABLOCKX)` skips row-level and takes table-S / table-X directly.
- `SET TRANSACTION ISOLATION LEVEL SERIALIZABLE` / `WITH (SERIALIZABLE)` / `WITH (HOLDLOCK)` locks the index keys its predicate reaches plus the next key past them, each lock fencing the gap below its key, and leaves the rest of the table free — two SERIALIZABLE transactions over disjoint key ranges don't block each other.
  A read whose shape offers no narrower interval locks every key of the clustered index plus the infinity anchor, and a heap scan takes table-S — see [Key-range locks](#key-range-locks).
- A SERIALIZABLE reader carrying `UPDLOCK` / `XLOCK` takes the same keys in `RangeS-U` / `RangeX-X`, the modes real reports there.
- `SET TRANSACTION ISOLATION LEVEL REPEATABLE READ` / `WITH (REPEATABLEREAD)` acquires row-S tx-scoped on each row it returns; concurrent INSERTs of *new* rows still succeed (RR doesn't prevent phantoms).
- A tx-scoped row lock (RR, `UPDLOCK`, `XLOCK`) taken on a row the read's sargable predicate rejects is released again, as real's is, so the transaction keeps only the rows it returns.
- `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED` makes every read behave like `WITH (NOLOCK)` (dirty reads).
- A statement taking past real's escalation point on one table trades its row and key locks for a table S or X — see [Escalation](#escalation).

## Lock modes

Twelve modes across four orthogonal families:

| Family | Modes                         | Purpose                                          |
| ------ | ----------------------------- | ------------------------------------------------ |
| Schema | `SchemaStability`, `SchemaModification` | Sch-S held during object use; Sch-M during DDL. |
| Intent | `IntentShared`, `IntentExclusive`, `SharedIntentExclusive` | Table-level signal that some child (row) is held in S / X / both. |
| Data   | `Shared`, `Update`, `Exclusive` | Read / read-with-intent-to-update / write. Held at row OR table level depending on hint / direction. |
| Range  | `RangeSharedShared`, `RangeSharedUpdate`, `RangeExclusiveExclusive`, `RangeInsertNull` | Phantom prevention over the gap below an index key — see [Key-range locks](#key-range-locks). |

The range family lives on key-lock anchors (`HeapTable.KeyLockGroups`), which also carry a plain S / U / X — a unique index's point lock, the instant X a writer tests an anchor with — so its cells are real's key-range matrix, settled ahead of the eight-mode table (rows requested, columns held):

```
          S  U  X  RangeS-S RangeS-U RangeI-N RangeX-X
S         ✓  ✓  ✗  ✓        ✓        ✓        ✗
U         ✓  ✗  ✗  ✓        ✗        ✓        ✗
X         ✗  ✗  ✗  ✗        ✗        ✓        ✗
RangeS-S  ✓  ✓  ✗  ✓        ✓        ✗        ✗
RangeS-U  ✓  ✗  ✗  ✓        ✗        ✗        ✗
RangeI-N  ✓  ✓  ✓  ✗        ✗        ✓        ✗
RangeX-X  ✗  ✗  ✗  ✗        ✗        ✗        ✗
```

A range mode's key part behaves as the S / U / X it names, and RangeI-N tests only the gap.
Probe-confirmed cells: a second SERIALIZABLE reader of an overlapping range proceeds (S-S × S-S), an overlapping `UPDLOCK` reader proceeds in both orders (S-S × S-U), two `UPDLOCK` readers meeting on one key wait (S-U × S-U), an `XLOCK` holder blocks a plain SERIALIZABLE reader (X-X × S-S), an `UPDLOCK` read of a key another reader holds in RangeS-S proceeds (U × S-S), and a writer's insert into the gap blocks whichever of the three holds it (× I-N).

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
| `WITH (XLOCK)`                         | IX tx      | X tx-scoped (plus `RangeX-X` key locks under SER / HOLDLOCK) |
| `WITH (UPDLOCK)`                       | IX tx      | U tx-scoped (plus `RangeS-U` key locks under SER / HOLDLOCK) |
| `WITH (HOLDLOCK)`/`WITH (SERIALIZABLE)`/ session SER | IS tx | none — `RangeS-S` key locks (or the heap's table-S) cover |
| `WITH (REPEATABLEREAD)` / session RR   | IS tx      | S tx-scoped                      |
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

Lock owner is the session — a `SessionToken`, reached from a connection as `connection.LockOwner`, which is `connection.Session` (see [Abandoned-session reclamation](#abandoned-session-reclamation) for why the recorded owner is the token and not the connection) save while the session shares a transaction with others (see [Sessions sharing a transaction](#sessions-sharing-a-transaction)).
Scope (when the lock releases) depends on the mode and surrounding transaction state:

| Acquired at                   | Scope                                 |
| ----------------------------- | ------------------------------------- |
| `TryResolve*` Sch-S           | Statement end                         |
| DDL site Sch-M                | Statement end                         |
| Reader RC default IS          | Statement end                         |
| Reader HOLDLOCK / SER table-IS | COMMIT / ROLLBACK (tx-scoped)        |
| Reader HOLDLOCK / SER key locks | COMMIT / ROLLBACK (tx-scoped)       |
| Reader HOLDLOCK / SER heap table-S | COMMIT / ROLLBACK (tx-scoped)    |
| Reader RR table-IS            | COMMIT / ROLLBACK                     |
| Reader UPDLOCK / XLOCK IX     | COMMIT / ROLLBACK                     |
| Reader RR / HOLDLOCK row-S    | COMMIT / ROLLBACK                     |
| Writer IX (or X via TABLOCK*) | COMMIT / ROLLBACK                     |
| Writer row-X (per mutated row)| COMMIT / ROLLBACK                     |
| Escalated table-X             | COMMIT / ROLLBACK                     |

**Cursor-scoped locks** are a third scope, introduced for `SCROLL_LOCKS` cursors (see [`cursors.md`](cursors.md)): a table-IX held for the cursor's open lifetime plus a row-U that follows the fetched row.
They live directly on the `Cursor` (`scrollTableLock` / `scrollRowLock`), *not* in either release list, so they persist across statement and autocommit boundaries while the cursor is positioned (probe-confirmed).
Each FETCH moves the row-U (`Cursor.MoveScrollLock` releases the row scrolled off, acquires U on the new one); `Cursor.ReleaseScrollLocks` frees both on CLOSE, the last DEALLOCATE, frame teardown (LOCAL cursor), and connection dispose.
A concurrent writer of the held row blocks on the U-X conflict; a positioned UPDATE upgrades the row to X via the normal writer path (same-owner re-entrance lets the cursor's U and the writer's X coexist).

Statement-scoped locks live in `BatchContext.StatementSchemaLocks` and release in `DispatchOneStatement`'s `finally` (`StatementLifecycle.Leave`) — for a `SELECT` whose rows go out as its client reads them, once its last row has ([A reader suspended mid-result](#a-reader-suspended-mid-result)).
**A synthesized child batch has its own list that the dispatch loop never sees**, so every site that builds one to parse or run a module body has to release it itself — a view or inline-TVF body binding takes Sch-S / IS on everything it names, and nothing else will let them go.
Missing that release does not merely hold a lock for too long: the locks outlive the connection (teardown releases the transaction, the application locks and the temp tables, not these), so they persist for the life of the `Simulation`, held by a SPID whose session no longer exists.
The symptom is a later Sch-M — an `ALTER`, a startup re-applying its programmable objects — blocking forever against a holder nobody can find.
`ModuleCreationLockLeakTests` walks every module kind, created and invoked, and asserts `sys.dm_tran_locks` is empty; a new body-inspection site that forgets the release fails there rather than in a consumer's startup.
Transaction-scoped locks live in `SimulatedDbTransaction.HeldLocks` and release in `Commit()` / `Rollback()` / dispose-implicit-rollback.
**A trigger body's statements under an auto-commit statement** hand their data locks to that statement as each ends (`SimulatedDbConnection.TriggerStatementLocks`), keeping every write's X and what a `REPEATABLE READ` or `SERIALIZABLE` read keeps until the firing statement — the transaction they belong to — ends; a `READ COMMITTED` read's object IS and every Sch-S go with their statement.
Probed 2026-10-09 against SQL Server 2025: another session's read of a row a trigger body inserted waits while the firing statement's client reads the body's later rows; it once read the row there, uncommitted.
Savepoint partial rollbacks (`ROLLBACK TRAN <savepoint>`) do NOT release locks — matches real SQL Server (probe-confirmed).

### Sessions sharing a transaction

`sp_bindsession` binds a session to the transaction an `sp_getbindtoken` token names, and a loopback linked server's remote call inside a transaction runs its session in the caller's (see [`linked-servers.md`](linked-servers.md#transactions)).
Probed 2026-10-07 against SQL Server 2025, the sessions share one transaction and one lock space:
- each reads the others' uncommitted rows and writes over their locks without waiting, while a third session waits on all of them;
- each nests on a `@@TRANCOUNT` of its own, and any bound one's outermost `COMMIT`, or `ROLLBACK`, ends the transaction for all — the others hear the informational Msg 3926 at their next batch — where an enlisted call's only dooms it (see [`linked-servers.md`](linked-servers.md#transactions));
- a session leaving (`sp_bindsession NULL`, closing) leaves the transaction to the rest, even the one that began it;
- `sys.dm_tran_locks` lists the transaction's locks under the member that ran last, and its database locks — one per database — under the first member still in it;
- `sys.dm_tran_session_transactions` lists every member: the one that began it local, a bound one bound, a loopback's enlisted session neither, `enlist_count` 1 only for a member running a request.

The shared lock space is one lock owner: the members' `LockOwner` is the transaction's (`SimulatedDbTransaction.LockOwner`), the token of the session that began it, so a member's locks, uncommitted rows, deleted-key registrations, row versions and snapshot registration are all the same owner's and every "own write" rule holds across the members unchanged.
Nothing moves when a session binds, since the joining session takes the owner the transaction already has, so the session that began it takes and releases under its own token throughout and an unshared transaction's path is untouched.
When the session that began the transaction leaves while others stay, its holds pass to a token of the transaction's own (`PassLockOwnership`, `LockManager.TransferHolds`), since the leaving session goes on to work of its own under its token.
A statement's locks are released under the owner they were taken under (`StatementSchemaLocks` records it), since a statement can end the shared transaction, or bind, partway.

The owner token records the wait of whichever member is running (`SessionToken.RunningMember`, read through `Acting`): its cancellation, its deadlock priority, its executing thread, and the `@@SPID` the lock DMVs report.
That is sound because members run **one at a time**: each stretch of a member's execution waits until no other member is running (`SimulatedDbTransaction.EnterExecution`, from `Simulation.MoveNextAsMember`), save a loopback server's session running inside its caller on the caller's thread, and swaps its `@@TRANCOUNT` into the transaction's.

Divergences:
- **Real runs the members side by side** (probed: one's insert completing during the other's long query), where here one waits for the other's statement.
  A member waiting on another session's lock holds the others off meanwhile; real lets them run, and in that probe left the waiting member's request waiting past the lock's release, until its command timed out.
- **A session whose transaction another member ended stays listed** in `sys.dm_tran_session_transactions` on real until its next batch; here it leaves at once.
- **A SCROLL_LOCKS cursor's locks and a session's own application locks** stay its session's, so they block another member.
- **The token's bits** aren't real's encoding: it has real's alphabet (`-` through `l`) and fixed characters, the rest drawn.

## Lock owners of a MARS session's requests

Real separates a session's MARS requests by the transaction each works in — an autocommit statement's own among them — so one request's locks meet another's as another session's would (probed 2026-10-05 against SQL Server 2025: a request's update waited on the key locks the session's suspended `REPEATABLE READ` reader held, its `ALTER TABLE` on the reader's `IS`, while requests in one API transaction shared their locks).
`connection.LockOwner` reads the bound transaction's shared owner, else the current transaction's own (`SimulatedDbTransaction.LockOwner`, the token it began under), else the executing request's (`SessionRequest.LockOwner`), else the session's token.
A request takes a token of its own only when it begins while another request of the session is unfinished (`SimulatedDbConnection.ResumeRequest`), so commands that never overlap — nearly all — lock under the session's token as before; the token reports the session's `@@SPID` (`RunningMember` is the session).
Three things keep the split from reaching where the session is one:
- **The same-thread deadlock check passes over a parked request's locks** (`SessionToken.RequestParked`, set as a request parks and cleared as one resumes under the token): the statement this thread runs doesn't hold them, so a wait on them goes on until the parked request lets go or a timeout ends it — real's deadlock monitor ended 5 of 27 such waits with Msg 1205 ("lock | generic waitable object") within five seconds and let the rest run to their `CommandTimeout` (probed 2026-10-05 and 2026-10-08 against SQL Server 2025), so the simulator waits.
- **A cursor's scroll locks belong to the session** (`SimulatedDbConnection.SessionScope`, `SessionToken.IsSessionScope`), compatible with every request of it (`LockManager.SharesSessionScope`), so a positioned update meets its cursor's U as its own whichever request runs it; a request's own token likewise shares what the session's token holds while no request is parked on it, its application locks.
- **A request writing in the transaction a suspended reader works in leaves the reader where it stood**, its rows read as the statement found them (`Storage.OwnWriteImages`) — a reader at a snapshot's too — so the reader keeps only the locks its position holds; a redefinition of an object the reader holds is refused with Msg 3970, which dooms the transaction (`SimulatedDbConnection.RefuseDefinitionBesideSuspendedRead`) — see [`data-reader.md`](data-reader.md#a-request-of-the-readers-own-transaction).

## Abandoned-session reclamation

Without reclamation, an application that opens a `SimulatedDbConnection` and drops it without disposing leaks its whole session forever: an open transaction kept its locks and pinned the MVCC version store, `##global` temp tables lingered, session application locks stayed held, and the SPID accumulated.
Real SqlClient's own finalizer eventually closes such a connection and the server resets the session, so the divergence ran in the over-permissive-adjacent direction — state real releases stayed held here, and a second session real would unblock stayed blocked.

### Why the connection could never be collected

With the registries holding strong references, nothing is collectable and no finalizer can ever fire — the resource itself is the pin.
Three *global* structures held the connection strongly, and each held exactly the connections that had leaked something worth reclaiming:

| Structure | Path to the connection | Pinned |
| --- | --- | --- |
| `LockResource.Hold.Owner` | `Database` → `Schema` → `HeapTable` → row / range / table `LockResource` → holder | every lock- or session-app-lock-holding session |
| `HeapTable.OwnerSession` | `Simulation.GlobalTempTables` → table | every `##temp` owner |
| `Simulation.ActiveSnapshotTxs` | set → `SimulatedDbTransaction` → its `Connection` | every open-snapshot session |

`Simulation.Connections` held one too, but weakening it alone accomplishes nothing: the resource *is* the pin.

### The one-way `SessionToken`

Each connection owns a `SessionToken` — a handful of fields carrying the SPID, the wait edge (`WaitingOnResource` / `WaitingForMode`) and the executing-thread marker the lock manager reads about *other* sessions.
Every shared structure names the token instead: `LockResource.Hold.Owner`, `HeapTable.OwnerSession`, and `Simulation.Sessions` (which replaced `Connections`).
`ActiveSnapshotTxs` is keyed by token and valued by an `ActiveSnapshotRegistration` copying the three facts its readers need (`transaction_id`, snapshot Xid, SPID) rather than by the transaction, because a transaction holds its connection and ADO.NET's `DbTransaction.Connection` contract requires that it keep doing so.
The only path back is `SessionToken.Owner`, a **resurrection-tracking weak reference**.

### The sweep

`System.ComponentModel.Component` already gives every `DbConnection` a finalizer, so an abandoned connection reaches `Dispose(disposing: false)` at some GC's discretion.
That arm hands the connection to `Simulation`'s abandoned-session queue — enqueuing `this` from a finalizer **resurrects** it, which is the point: the teardown needs the state the connection still holds.
`Simulation.ReclaimAbandonedSessions()` drains the queue on a normal worker thread and runs the session's **own `Close()` + `Dispose()`**, so the open transaction rolls back through the transaction's own machinery, application locks release through the same bulk release `Close` uses, `##temp` tables drop under the same ownership rule, cursors deallocate, and the token retires from `Sessions`.
No parallel cleanup implementation exists, deliberately: the moment the two paths diverge one of them starts leaking what the other releases.

Sweep triggers, all on paths already being taken:

- **`CreateDbConnection()`** — a new session opening.
- **`LockManager.TryAcquire`**, gate-free at entry behind a cheap empty-queue check — this is what stops a live session blocking on a leaked one's lock.
- **`VersionStore.RunGarbageCollection`** — a leaked SNAPSHOT registration would otherwise pin every later version.

One sweep runs at a time (`Interlocked` gate): a teardown rolls its transaction back, which runs the version-store collector, which sweeps, so an unguarded drain would re-enter itself once per queued session.
A session whose token still reports a `CurrentExecutingThreadId` is skipped and re-queued.
In practice that can't happen — the executing thread's stack holds the connection, so it isn't collectable, and a parallel-aggregate worker thread never writes that marker (only the dispatcher on the session's own thread does) — but the check makes it an assertion rather than an assumption.

### Timing, and what a leaked session still reports

Reclamation is GC-nondeterministic, as real's is: real's client finalizer closes the socket when a collection gets to it and the server resets the session then.
Here the same collection is the first half and the next sweep trigger is the second, so a leaked session's locks survive some unspecified interval either way.
Across that interval the session keeps reporting: the token's weak reference tracks resurrection, so `sp_who`, `sys.dm_exec_sessions` and `sys.dm_tran_locks` list it for exactly as long as it holds anything — a row that vanished before its locks did would be a lie.
A connection disposed properly never enters the queue at all (`Component.Dispose()` suppresses finalization).

Oracle: `AbandonedSessionReclamationTests` (Tests.Internal), one test per state kind plus the mid-statement rule, the `sp_who` window, the idempotence rule, and one end-to-end `GC.Collect()` test that fails if any global reference is reintroduced.

## `KILL`

`KILL <session id> [WITH STATUSONLY]` ends another session of the same `Simulation`, an in-process connection or a TDS session alike (`Simulation.ParseKill`, `SimulatedDbConnection.Kill`).
What a session id meets, in order (probed 2026-09-30 against SQL Server 2025): `WITH COMMIT | ROLLBACK` is Msg 6108, an open user transaction Msg 6115, an id outside 1 to 32767 Msg 6101, a system session Msg 6107, an id no session holds Msg 6106, the caller's own Msg 6104, and a caller holding neither sysadmin nor `ALTER ANY CONNECTION` Msg 6102 — after the existence checks, so a login without it still hears Msg 6106 for a missing id.
The target must be a literal: a variable, a parenthesized value or a bare `NULL` is a syntax error, and a decimal or past-`int` integer is Msg 1080.
A string is `KILL UOW`, whose ids are checked as a GUID (Msg 8169), refused inside a transaction (Msg 6115) and otherwise Msg 6110, the simulator having no distributed transaction.
`WITH STATUSONLY` is Msg 6120 for every live session, since a kill here finishes before anything could report its rollback.
Every one of these is a statement-level error: the batch carries on, and a `CATCH` reads it.

The victim, by what it is doing:

- **Idle**: its transaction rolls back at once, so the locks it held and the rows it wrote are gone for the killer's next statement; it leaves `sys.dm_exec_sessions`; a TDS session's socket closes.
  An in-process connection's next command raises SqlClient's own severity-20 Msg 0 (`The connection is broken and recovery is not possible.`) and closes it, as real does for a session holding a transaction.
- **Running** (a `WAITFOR`, a lock wait, a long statement): the command is cancelled at its next safe point, ends with Msg 596 at severity 21 and then SqlClient's severity-20 Msg 0, which no `CATCH` intercepts, and the connection closes with its transaction rolled back.
  Over TDS the two errors and a DONE carrying the server-error bit stand where an attention's acknowledgment would.

Whether the victim is idle or running is decided under the session's own gate, which its command start, `Close` and `Dispose` take too, so a kill landing as a command starts rolls the transaction back once: a kill reading the session idle while its command was starting once rolled back beside the command's own unwind, releasing locks twice.

**Divergences.**
Over TDS, an idle victim's next command is SqlClient's `A transport-level error has occurred` (error 2) where real's is the `connection is broken` error for a session holding a transaction and a transparent reconnect for one that holds none: SqlClient's idle-connection resiliency needs the server's session-recovery feature acknowledgment in the LOGINACK, which the endpoint doesn't send.
Sessions 1 to 50 are all system sessions here (Msg 6107); only session 7 was probed, and real answers Msg 6106 for one of them that doesn't exist.
`KILL` has no DONE kind of its own on the wire yet (its real code wasn't captured).

## Row-lock storage

Per-row `LockResource`s live in `HeapTable.RowLocks`, a `ConcurrentDictionary<(int pageIndex, int slotIndex), LockResource>` keyed by RID.
Entries are lazily-interned via `GetOrCreateRowLock` and retired with the final release of the X that deleted their row (`HeapTable.RetireRowLock`); every other entry leaks as the heap's slots do.
The dict-lookup itself is thread-safe without taking the lock manager's gate; only mutations to a `LockResource`'s `Holders` list go through the gate.

`HeapTable.TableDataLock` is the table-level `LockResource` for IS / IX / SIX / S / U / X, and the same object as the table's inherited `SchemaObject.SchemaLock`, so Sch-S / Sch-M sit beside them on one resource, as on real's object lock (see [Table-level and schema-lock behaviors](#table-level-and-schema-lock-behaviors)).

`HeapTable.KeyLockGroups` is the third store: one `KeyLockGroup` per key constraint or index that ever took a key lock, each interning an anchor `LockResource` per key tuple (plus one infinity anchor), leaking the same way `RowLocks` does.
`HeapTable.ActiveKeyRangeLocks` counts the holds live on those anchors and `KeyLockGroup.Holds` the holds per group — the `Interlocked` companions the writer's fast path reads, the exact mirror of `ActiveDataWriters`, maintained by `LockManager` on every grant / final release on an anchor.

## Key-range locks

A SERIALIZABLE (or `HOLDLOCK`-hinted) reader has to make the rows it *didn't* read unappearable for the rest of its transaction.
Real does that by locking index keys and letting each range lock cover the gap below its key down to the next lower key, and the simulator locks the same keys (`KeyLockGroup`): one anchor per key tuple of the index the read walks, plus an infinity anchor past the last key (real's `ffffffffffff`).
The coverage of an anchor is never stored.
A writer inserting a key asks the seek cache for the first key above it and tests that anchor, as real's insert tests the next key's lock, so a gap that widens or narrows between the reader's lock and the writer's insert is judged as it stands.

A group's anchor tuple is the index key; a non-unique nonclustered index appends the clustered key columns it doesn't already name — real's row locator — so each entry of a duplicated value anchors separately.
The table's clustered key is its **row group**: its anchors are a row's identity, so they meet every write of the row and a reader's own row lock.
A nonclustered index's anchors meet only a write that changes a column the index row carries (key, `INCLUDE` or clustered key), since real's update of a column no index names takes no lock on that index.

### What the reader takes

The table-level acquisition is only **IS** (or **IX** behind `UPDLOCK` / `XLOCK`), tx-scoped, and the phantom fence is settled later — the predicate that decides what to lock isn't known when the FROM source resolves.
`DataLockPlan.SerializableRangeMode` carries the obligation forward, naming the mode the key locks are taken in — `RangeS-S` for a plain read, `RangeS-U` behind `UPDLOCK`, `RangeX-X` behind `XLOCK`, all three probe-confirmed against real.
Three places discharge it:

- **`Selection.WalkKeyFence`**, for a read of intervals of an index it can walk in order — the clustered key, or an ascending unfiltered nonclustered index — which produces the rows itself, locking each key as it reaches it (see [A reader suspended mid-result](#a-reader-suspended-mid-result)).
  `ComputeKeyFence`, called from `MaybeApplyIndexSeek` once the WHERE conjuncts have been collected, walks the table's keys then its indexes (so the choice doesn't ride on dictionary order), scoring each by how deep an **equality prefix** the conjuncts pin on it plus whether a range bound lands on the key column right after that prefix; the longest prefix wins and a bound continuation breaks a tie.
  An `IN` list reads one interval per value — per tuple of the cartesian product across a multi-column prefix, up to `KeyFenceProbeCap` of them, past which the hull of each column's values is read instead.
  An interval pinning every column of a **unique** key by equality is the exception real makes on a hit: a plain key S (a row S for the clustered key); a miss locks the next key like any range.
  Reading through a nonclustered index also takes the row S real's lookup takes on each row the index finds.
  The ordered-scan path walks too, for an order the clustered key leads — a range, a pinned prefix, an `IN` list on the order column, either direction — as does a row-locking scan with nothing to seek on.
- **`Selection.SettleSerializablePhantomFence`**, for an interval of an index the walk can't read in key order — a descending or filtered one, a clustered key with a nullable column — which `BatchContext.AcquireKeyFence` locks up front, *before* any candidate address is read: it asks the seek cache (`HeapSeekCache.KeyLockAnchors`) for every key inside each interval and the first key past it — the infinity anchor when none follows — and locks them.
- **`BatchContext.EnsureSerializableTableLock`**, for a read with no narrower interval — a whole-table scan, a non-sargable predicate, a predicate on an unindexed or non-leading column, a cross-column `OR`, an ordered scan — which locks the whole key space: every key of the clustered index plus the infinity anchor, or over a heap a table S (folding in the IS, which real reports converted).
  A plain scan of a clustered table takes it key by key as it reaches each row, the infinity anchor at its end (see [A reader suspended mid-result](#a-reader-suspended-mid-result)).
  Reached from `WrapWithRowConflictChecks` (the un-narrowed scan's own iterator), from the ordered-scan path over a nonclustered index, and from a union of an `OR`'s seeks.
  Idempotent per batch per table, since a source can be re-enumerated many times.

They are mutually exclusive **per source**, which `DataLockPlan.Fence` (a `PhantomFenceState` cell the plan's struct copies share) enforces: a source that locked its keys must not then have the whole key space added on top by the scan wrapper, which would re-block the keys the seek deliberately left free.
The fence itself is *not* short-circuited on the cell: a correlated inner re-plans per outer row and each outer value names keys of its own — which is how a join's inner side, seeked per outer row, locks each outer value's keys.
A key the session already holds in the mode is not taken again (`LockManager.IsHeldBy`), so a re-planned inner doesn't pile up held-lock entries.

Every conjunct considered is a top-level `AND` factor of the predicate, so every row the query can ever return satisfies it, so every row that could become a phantom carries a key inside one of the intervals — which is why the fence stays sound whichever access path the read then takes.
A conjunct that can't be evaluated cleanly (NULL probe, cross-collation string, unpromotable pair) bounds the prefix there rather than narrowing the fence past what it can justify.

After locking an anchor on the row group, the reader tests the anchored rows' own row locks in the mode's key part (`LockManager.KeyPartOf`) — real meets another session's X on the next key as a conflict on the key lock itself, and the simulator's writers hold their X on the row.

### What a SERIALIZABLE writer takes

An UPDATE / DELETE under SERIALIZABLE — single-table or joined — or with a `HOLDLOCK` / `SERIALIZABLE` hint on its single-table target, locks the keys its own WHERE reaches in **`RangeX-X`** — `Selection.SettleSerializableWriteFence`, called before the target's rows are read — the next key included, so an insert into that span waits even where no row matched, while one past it goes through.
A hit on a unique key takes only the row X the write itself takes, and no range, as real's does.
With no interval to take, a clustered table gets **`RangeS-U`** on every key plus infinity — real's own shape for a scanning write — and a heap the **table X** real takes (all probed 2026-09-26 and 2026-09-28).
A MERGE under SERIALIZABLE or `WITH (HOLDLOCK)` locks per source row the next key past the key its ON clause probes, in **`RangeS-U`** — real's mode for the upsert probe, and what makes two concurrent upserts of one missing key wait on each other rather than both insert — and a unique hit takes only its row X.
One with `WHEN NOT MATCHED BY SOURCE`, or whose ON pins no interval, takes the whole-key-space fallback above.

A key a transaction inserts into a gap it itself range-locks splits that gap, so the new key takes **`RangeX-X`** to keep the lower half fenced (`TestGapLock`), as real's HOLDLOCK MERGE's insert shows.

### What an `IGNORE_DUP_KEY` check takes

An INSERT's check of a nonclustered PRIMARY KEY, UNIQUE constraint or unique index with `IGNORE_DUP_KEY` reads the index the way a SERIALIZABLE `UPDLOCK` read does, whatever the session's isolation level, and keeps what it took to the transaction's end (`BatchContext.LockIgnoreDupKeyProbe`): U on the key when a row carries it — the duplicate it then ignores — and `RangeS-U` on the next key past it, or the infinity anchor, when none does.
The insert then splits that range, so its own key takes `RangeX-X` through the same `TestGapLock` a SERIALIZABLE MERGE's insert goes through.
A second insert of the key waits in U on it, and one of another key into the same gap waits in `RangeS-U` on the next key; an UPDATE moving a key into the index, and a clustered key with the option, take none of it (all probed 2026-10-01 against SQL Server 2025, in a multi-row insert and an `INSERT … SELECT` too).

### What the writer probes

`BatchContext.TestKeyLocksForWrite` and `ProbeKeyLocksForUpdate` run on every writer **whatever its own isolation level** — fencing sessions that know nothing about the fence is the entire point.
They read `ActiveKeyRangeLocks` first and return immediately at zero, so a database with no SERIALIZABLE reader pays nothing; otherwise they walk only the groups somebody holds a lock in.
What each write tests follows `RowLockPurpose`:

- An **insert** tests the gap its key lands in: `RangeI-N`, acquired and immediately released, on the first key above it (`HeapSeekCache.NextKeyAbove`) — real's instant-duration insert-range mode, which never shows up in a lock snapshot taken after the write.
- A **delete** tests the lock on each of its keys with an instant X.
- An **update** tests its clustered key the same way on the pre-image; the rewrite site (`ProbeKeyLocksForUpdate`) then tests a nonclustered index's old key only when the update changes a column that index carries, and the gap each moved key lands in — a row moving *into* a fenced gap is a phantom the old image can't reveal.

Two hooks put the tests on every write path:

- Inside `AcquireRowLockTxScoped` when the purpose is a write, against the row's **live slot bytes**.
  Each site's ordering makes that the image that matters: an INSERT locks after the heap write, so it reads its new row; an UPDATE / DELETE locks before, so it reads the row it is about to supersede.
- Explicitly against the **post-update image** at each `Heap.UpdateAt` site (`Simulation.Update`, MERGE's update branch, the two FK cascade rewrites).

The main INSERT path tests once more, *before* the heap write rather than after: a wait on a range can last until the reader commits, and a row sitting in the heap with no row-X on it yet would be dirty-readable for that whole window.

A reader tests too, against the clustered key of each row it locks: an `UPDLOCK` read's U meets another reader's `RangeS-U`, and a READ COMMITTED read's S meets a `RangeX-X` — but only once the table has changed since the holder's transaction began (`Heap.LastModifiedEpoch` against `SimulatedDbTransaction.BeginEpoch`), since real's READ COMMITTED read takes its S only on a page changed since the oldest open transaction began.
Probed 2026-09-28: the `RangeX-X` behind an `XLOCK` read, or behind a DELETE that removed nothing, lets the read through, and the same lock refuses it once any write lands on the page — even one rolled back.
Real's check is per page and the simulator's per heap, so on a table of many pages a write elsewhere refuses a read real would let through.
**Settled — don't re-pitch:** which page a row sits on is real's allocation accident, not a guarantee, so the per-heap test stands.
A `RangeX-X` counts in `ActiveDataWriters`, which is what sends the READ COMMITTED reader off its lock-free fast path to test it.

Key-lock waits go through `LockManager.Acquire` like everything else, so they enter the wait-for graph unchanged — two transactions each fencing one range and inserting into the other's deadlock with Msg 1205, and `SET LOCK_TIMEOUT` (or a `NOWAIT` hint on the table, an INSERT target's included) yields Msg 1222.
Same-owner holds are skipped by the conflict check, so a SERIALIZABLE transaction inserting into its own fenced gap isn't self-blocked.

### Probed reference behavior

Against SQL Server 2025 CU7, `sys.dm_tran_locks` under SERIALIZABLE (probed 2026-09-26 and 2026-09-28), all reproduced:

| Read                                        | What real takes                                              |
| ------------------------------------------- | ------------------------------------------------------------ |
| Equality **hit** on a unique index           | plain `KEY` **S** — uniqueness already forbids a second row at that key |
| Equality **miss** on a unique index          | `RangeS-S` on the next key                                    |
| Equality **hit** on a non-unique index       | `RangeS-S` on each matching entry *and* the next one          |
| `k BETWEEN a AND b`, `k > a AND k < b`       | `RangeS-S` on every key in the interval plus the next one past it |
| `k > a` past the last key                    | `RangeS-S` on each matching key plus the infinity range (`ffffffffffff`) |
| Any predicate over an empty table            | `RangeS-S` on the infinity range alone                        |
| `k IN (…)`                                   | per value: a plain `KEY` S on a hit, the next key on a miss   |
| Whole-table scan / non-sargable predicate / cross-column `OR` | `RangeS-S` on every key plus infinity — the whole key space |
| Seek through a nonclustered index            | `RangeS-S` on the index entries plus the next, `KEY` S on each looked-up clustered row |
| Predicate on a heap scanned                  | object-level **S**, no IS                                     |
| Same reads under REPEATABLE READ             | plain `KEY` S on the rows returned, no ranges, IS kept        |
| `WITH (HOLDLOCK)` under READ COMMITTED       | identical to SERIALIZABLE                                     |
| `WITH (UPDLOCK)`                             | `RangeS-U` at the keys, **IX** at the object, no key U beside it |
| `WITH (XLOCK)`, and a SERIALIZABLE UPDATE / DELETE | `RangeX-X` at the keys, IX at the object (the same write under READ COMMITTED takes plain `KEY` X) |
| A scanning SERIALIZABLE UPDATE               | `RangeS-U` on every key plus infinity, the updated key converted to `RangeX-X` |

Composite reads follow the single-column shape — `a = 2` locks the keys of the group plus the next key past it, so the gap reaches down to the last key of the group before, and a predicate on the **second** column alone locks every key plus infinity.

And the blocking matrix, session A holding a SERIALIZABLE `k BETWEEN 15 AND 25` over keys 10 / 20 / 30 / 40:

| Session B                                    | Real   |
| -------------------------------------------- | ------ |
| INSERT inside the interval, or between 10 and 30 | blocks |
| INSERT past the next key (35)                 | proceeds |
| UPDATE / DELETE of a row inside it, or of the next key's row | blocks |
| UPDATE moving a row from outside *into* it    | blocks |
| UPDATE of a row below it (10)                 | proceeds |
| `UPDLOCK` read of the next key                | proceeds |
| `XLOCK` read of the next key                  | blocks |
| SERIALIZABLE SELECT of an overlapping interval | proceeds |
| SERIALIZABLE `UPDLOCK` SELECT sharing only the next key with an `UPDLOCK` holder | blocks |
| Crossed intervals, each inserting into the other's | Msg 1205 |

`KeyLockAnchorTests` holds the matrix row by row.

### Divergences

- **The access path is the simulator's, not real's optimizer's.**
  Real can scan a small table rather than seek it and then locks every key of the clustered index: probed, a nonclustered seek over four rows, a three-row `BETWEEN` over a 2000-row nonclustered index, a 300-row heap with a nonclustered index and an `EXISTS` driven from the inner table all ran as scans there, where the simulator seeks and locks only the keys the seek reaches.
  Blocking follows the chosen path on both engines, so a shape real scans blocks more there.
  **Settled — don't re-pitch:** real doesn't guarantee its access path — its optimizer picks scan or seek by cost — so its lock footprint isn't a contract to match.
- **A nonclustered read takes its lookup row S even when the index covers the query**, where real's covering seek reads no base row — so an update of a column the query never read waits here and proceeds on real.
  Taking it always keeps the non-covering case, the common one, from admitting a write real refuses.
  **Settled — don't re-pitch:** whether real's plan covers is its optimizer's choice, not a guarantee.
- **The `UPDLOCK` / `XLOCK` row lock stays on top of the key lock**, where real folds the two into one key lock; the readers and writers that take a row lock meet it there.
  `sys.dm_tran_locks` folds them back (`LockDmvs.FoldRowLocksIntoKeyLocks`), reporting the one key lock in the combined mode — `RangeX-X` for a written key — as real does.
- **No ghost records.**
  On real a `DELETE` leaves its key behind as a ghost until cleanup runs, and a `ROWLOCK, UPDLOCK` seek for that key locks the ghost, so a concurrent `INSERT` of the key waits; here the seek finds nothing to lock and the insert lands first.
  Real's outcome turns on whether ghost cleanup has run yet (probed 2026-10-02 against SQL Server 2025 through Django's `get_or_create.UpdateOrCreateTransactionTests.test_creation_in_transaction`, whose predecessor deletes the same key).
  **Settled — don't re-pitch:** real itself doesn't guarantee the outcome — it is a race against its background ghost cleanup's timing.
- **A key lock's `resource_description` is real's hash** of the index row's key (`KeyLockHash` carries the recovered function; `KeyLockDescriptionTests` holds real's descriptions across the key types, NULL and empty components, and the row locators).
  Two keys hash short of real's: a non-unique nonclustered index over a heap, whose real row carries the base row's RID, which the simulator's pages don't reproduce, and the second and later rows of a non-unique clustered key, which real tells apart by uniquifier and the simulator, holding one anchor per key value, describes as the first.
- **A non-default isolation level disables the plan cache.**
  A cached plan's FROM sources carry the lock acquisitions their parsing session made, so replaying one under a different level would settle the wrong session's protection, or none.
  Anything but the default READ COMMITTED skips both the plan-cache lookup and the promotion and re-parses per execution — see [`plan-cache.md`](plan-cache.md).

## Lock-free read fast path

Every grant / release / probe funnels through `LockManager`'s single gate, so under heavy concurrent reads a per-row gate acquisition would serialize the workers.
The READ COMMITTED row-conflict check (`BatchContext.TouchRowForRead`) avoids the gate on the common path via `HeapTable.ActiveDataWriters` — an `Interlocked` count of connections currently holding a data-`Exclusive` lock anywhere on the table (a row-X or the table-X), or a `RangeX-X` key lock.
`LockManager` increments it on an `Exclusive` / `RangeX-X` grant and decrements on the final release of one, keyed by a `LockResource.OwningTable` back-reference set when the resource is interned.
The reader:

1. `Volatile.Read`s the count; if 0, no row is X-locked, so every row is committed-readable — return immediately, **no `RowLocks` intern, no gate**.
2. If non-zero, look up the specific row with `RowLocks.TryGetValue` (still no intern — a row with no interned entry has no holder, so it reads through); only when an entry exists does it probe under the gate and wait / READPAST-skip as before.

Counting only `Exclusive` (U is `S`-compatible; IX / SIX are table-level intent the row probe already ignores) keeps the visible behavior identical to the always-probe path — it only elides gate traffic when no X exists.
The zero read is sound because a writer's row X is granted before its write is visible: an UPDATE / DELETE locks the row ahead of the heap write, and an INSERT takes the new row's X inside the heap's latch before the slot is published (`Simulation.InsertRow`, see [`heap-storage.md`](heap-storage.md#concurrent-writers-the-heap-latch)) — before that, a READ COMMITTED reader could meet an uncommitted insert with no lock on it yet and read it.

**A reader that waited reads the row again.**
A scan reads the row's bytes, then probes its lock; when the probe waited out a writer, the image it read may be the writer's, which a rollback then took back.
The heap scan notes the heap's write sequence (`Heap.WriteSequence`) before each row and re-reads the slot after the probe when it moved — skipping the row if the write deleted it — so a scan waiting out a rolled-back UPDATE returns the restored row rather than the never-committed one; the clustered-order and seek paths read the row after the probe already.
The probe holds nothing once it returns, so a write can take the row between the probe and the read, and roll back after it: when the heap moved over that span the row is probed and read again until two reads agree (`BatchContext.SettleReadCommitted`), where the stress harness once saw a READ COMMITTED scan return a balance its writer then rolled back.
Snapshot / RCSI reads never reach this path (they resolve through the version store), so the fast path is a pure READ COMMITTED non-snapshot win.
The `ActiveDataWriters` invariant (0 at rest, follows the X through commit / rollback / escalation) is guarded by `LockResourceTests.ActiveDataWriters_*`.

## Escalation

Real escalates a statement's row and key locks on one table to a single table lock once that statement holds enough of them — counted per statement, not per transaction, and counting the table's intent lock and a page intent lock per page the rows sit on.
Probed 2026-09-28 against SQL Server 2025: the first attempt comes at 6 250 locks in all (a SERIALIZABLE scan of a narrow table escalates at about 6 235 keys, an UPDATE at about 6 235 rows), two statements of 4 000 keys each in one transaction never escalate, and the mode is S when every lock is S-family (a REPEATABLE READ or SERIALIZABLE read) and X otherwise (an UPDATE, an `UPDLOCK` read with or without SERIALIZABLE).

`BatchContext.CountLocksForEscalation` keeps the tally on the statement (`StatementContext.LockTallies`), with the page locks estimated from the heap's rows per page (`EstimatedLockTotal`); a row lock under a key lock the statement holds on the clustered key adds nothing, as real holds one lock per key.
A nonclustered index's key locks keep a tally of their own, as real counts per index (probed 2026-10-09 against SQL Server 2025: a SERIALIZABLE read through a nonclustered index held 3,603 range locks on its keys and 3,602 on its rows, unescalated).
At the attempt point `TryEscalate`:

1. Tries the table S or X **without waiting** — real escalates only when the table lock is grantable at once, and otherwise keeps the fine-grained locks and tries again 1 250 locks later.
2. Releases the row and key locks the table lock covers (every one for an X, the S-family ones for an S), and the table's intent lock the new mode subsumes.
3. Marks the table (`SimulatedDbTransaction.EscalatedTables` / `SharedEscalatedTables`, or the statement's own set outside a transaction) so later row and key locks there short-circuit.

A table set `LOCK_ESCALATION = DISABLE` (`HeapTable.LockEscalation`) never escalates; `AUTO` behaves as `TABLE`, even on a partitioned table, where real escalates to the partition — partition-level locks aren't modeled.

## Acquisition sites

**Sch-S** — every successful `BatchContext.TryResolve*` path (table / view / function / procedure / table-type / sequence) on a schema-bound object.
Skipped for temp tables / table variables / trigger `INSERTED` / `DELETED` pseudo-tables / system tables.
A procedure call lets its procedure's go once the body has compiled (`BatchContext.ReleaseStatementLock`), as real holds it only to compile: a body running, or suspended mid-result, holds none, and another session drops or alters the procedure meanwhile (probed 2026-10-09 against SQL Server 2025).

**Sch-M** — every DDL site: `DROP {TABLE,VIEW,FUNCTION,PROCEDURE,TYPE, SEQUENCE,TRIGGER}` after the lookup; `TRUNCATE TABLE`; `ALTER TABLE`; `DROP INDEX` and a clustered `CREATE INDEX`, while a nonclustered `CREATE INDEX` takes the table's S (see [Table-level and schema-lock behaviors](#table-level-and-schema-lock-behaviors)).

**Data locks** — `BatchContext.AcquireDataLockIfApplicable(table, hints, isWrite)` from FROM-source resolution in `Selection.FromClause.cs` and INSERT / UPDATE / DELETE / MERGE target / MERGE bare-table-source sites.

**Row locks (X)** — `BatchContext.AcquireRowLockTxScoped(table, pageIndex, slotIndex, Exclusive)` from each `Heap.Insert` / `Heap.DeleteAt` callsite inside INSERT / UPDATE / DELETE / MERGE (the four user-DML statement kinds).
Update is a delete+insert pair, so both the old RID and the new RID get row-X.

**Row probe / row-S / row-U / row-X (reads)** — `BatchContext.TouchRowForRead(table, pageIndex, slotIndex, plan)` during heap row enumeration (wrapped by `BatchContext.WrapWithRowConflictChecks`).
Iterators that need addresses go through `Heap.EnumerateRowsWithAddress` instead of `Heap.EnumerateRows`.

Table variables and system tables bypass all data-lock acquisition (and row-lock acquisition); a local temp table takes its object lock alone ([below](#local-temp-tables)).

### Local temp tables

A local temp table is its session's alone, so real locks it whole, never a row, a page or a key, and lists the lock in tempdb (probed 2026-10-09 against SQL Server 2025, `BatchContext.AcquireLocalTempLock`):
- a read takes `S`, to the statement's end under `READ COMMITTED` and `TABLOCK`, to the transaction's under `REPEATABLE READ`, `SERIALIZABLE` and their hints;
- `UPDLOCK`, `XLOCK`, `TABLOCKX` and every write take `X` to the transaction's end;
- a read of no committed state — `NOLOCK`, `READ UNCOMMITTED`, `SNAPSHOT` — holds `Sch-S` alone;
- outside MARS, a session in a transaction takes `X` for any access, a read of no committed state included (`SimulatedDbConnection.RunsMars`).
Only the session's other MARS requests meet these locks, so a request's write waits on a reader suspended over the table, as real's does; a redefinition takes the object's `Sch-M` as a user table's does.
A global temp table locks as a user table, shared across sessions, and `sys.dm_tran_locks` lists both kinds in tempdb (`LockDmvs.EnumerateTempTableLocks`).

## Request queue

Requests for one resource are granted in arrival order (`LockResource.Queue`): a request compatible with every holder still waits behind an earlier queued request it conflicts with, unless it converts a lock its own session already holds, so a stream of shared readers can't starve a writer (probed 2026-10-03 against SQL Server 2025: a `TABLOCK` read behind a waiting `TABLOCKX` waits for it, as does a plain read behind a waiting Sch-M).
A statement's own Sch-S is no such conversion (`LockManager.Converts`): real holds Sch-S only while it compiles and takes its IS or S afresh, so that request keeps its turn — except behind a queued Sch-M, which waits for that Sch-S itself and could never go first.
Granting whatever was compatible with the holders, as it once did, starved an X or Sch-M waiter for as long as readers kept arriving.
A waiter blocked on a session that was abandoned rather than closed sweeps the abandoned sessions between its wait slices (see [the sweep](#the-sweep)), so the abandoned transaction's locks don't hold it for the life of the process.

## Cycle detection

When a conflict-driven wait would block, `LockManager.Acquire`:

1. **Same-thread short-circuit**: if any conflicting holder's `CurrentExecutingThreadId` equals the caller's managed thread id, raise Msg 1205 immediately — unless the holder is another request of the caller's session parked for it ([Lock owners of a MARS session's requests](#lock-owners-of-a-mars-sessions-requests)).
2. **Cross-thread cycle walk**: `FindDeadlockVictim` walks the wait-for graph starting at each conflicting holder.
   A waiter's edges are the ones its grant waits on (`LockManager.Blockers`): the holders it conflicts with and the requests queued ahead of it; a compatible holder is no edge, which once reported cycles that weren't there.
   Each connection's `WaitingOnResource` is read consistently under the manager's gate.
   If any walk reaches the caller's connection, a cycle exists, and its victim is the session in it with the lowest `SET DEADLOCK_PRIORITY`, the caller on a tie (probed 2026-09-28 against SQL Server 2025, whose tie picks the requester in the two-session shape).
   A victim other than the caller is blocked in its own wait: it is flagged (`SessionToken.ChosenAsDeadlockVictim`) and woken, its wait ends with Msg 1205, and the caller waits on until the victim's rollback releases what it held.
3. **The victim's rollback on Msg 1205**: `StatementLifecycle.SettleError` undoes the victim's work and releases every lock it held — waking the survivor — before the error reaches anyone.
   Uncaught, the transaction ends with it; caught by a `TRY`, the transaction stays open and doomed (`@@TRANCOUNT` unchanged, `XACT_STATE()` -1) until a `ROLLBACK`, or the batch's end with Msg 3998 (`SimulatedDbTransaction.UndoAsDeadlockVictim`; probed 2026-10-03 against SQL Server 2025).
   Ending it outright, as it once did, let the `CATCH` read `@@TRANCOUNT` 0.
   The error's state names the kind of lock the victim waited on, as Msg 1222's does.

## Lock-timeout semantics

`SET LOCK_TIMEOUT N` → `connection.LockTimeoutMillis`.
Negative = wait forever (default), `0` = fail-fast on first conflict, positive `N` = wait up to `N` ms before raising Msg 1222, whose state names the kind of lock (`LockManager.TimeoutState`); a write it ends on a row or key lock is followed by Msg 3621, one it ends on the object lock isn't (probed 2026-10-03 against SQL Server 2025).
Applies uniformly to schema locks, data locks, and row locks.

**A blocked wait also observes the command's own cancellation** — its `CommandTimeout`, a TDS attention, or an in-process `Cancel()`.
Without that, `SET LOCK_TIMEOUT`'s default of "wait forever" makes any block permanent, whatever the client asked for.
`Monitor.Wait` can't take a token, so the wait is sliced (`LockManager.CancellationPollMillis`) and re-checks between slices; the slice bounds only how long a cancel goes unnoticed, since a release still `Pulse`s and wakes every waiter at once, so nothing is added to the granted path.
The token is resolved from the waiting `SessionToken`'s own connection rather than threaded through every caller — a lock wait belongs to the session, which is where the answer already is, and the alternative (an optional parameter on `Acquire`) would oblige 40 existing test call sites to pass one.
The two deadlines keep their own errors: `SET LOCK_TIMEOUT` elapsing is Msg 1222, while a cancellation reports what the command surface reports for an aborted execution — **Msg -2** for a `CommandTimeout` and **Msg 0** for a caller's `Cancel()`, the same split `SimulatedDbCommand` already makes.

## Hint surface

| Hint                              | Effect                                         |
| --------------------------------- | ---------------------------------------------- |
| `NOLOCK` / `READUNCOMMITTED`      | Skip every acquisition (dirty read).           |
| `HOLDLOCK` / `SERIALIZABLE`       | Take table-IS tx-scoped plus `RangeS-S` on the keys the predicate reaches and the next one, or on every key when it reaches no narrower interval; a heap takes table-S. |
| `REPEATABLEREAD`                  | Take table-IS + row-S tx-scoped per row returned. |
| `UPDLOCK`                         | Take table-IX + row-U tx-scoped per row returned, plus `RangeS-U` key locks under SERIALIZABLE / `HOLDLOCK`. |
| `XLOCK`                           | Take table-IX + row-X tx-scoped per row returned, plus `RangeX-X` key locks under SERIALIZABLE / `HOLDLOCK`. |
| `READPAST`                        | Skip rows another connection holds incompatibly instead of waiting — the row-X a writer holds, and the row-U / row-X the `UPDLOCK` / `XLOCK` pairing meets. |
| `TABLOCK`                         | Reader: table-S, or table-X to the transaction's end beside `UPDLOCK` or `XLOCK` at any level (probed 2026-10-09 against SQL Server 2025); Writer: table-X. Skip row-level. |
| `TABLOCKX`                        | Take table-X regardless of direction.          |
| `READCOMMITTED`                   | The default READ COMMITTED read — versioned under `READ_COMMITTED_SNAPSHOT`. |
| `READCOMMITTEDLOCK`               | A locking READ COMMITTED read, under `READ_COMMITTED_SNAPSHOT` too. |
| `ROWLOCK`                         | Parse-and-discard (row-level is default). |
| `PAGLOCK`                         | Lock the pages the rows are on in place of the rows ([Page locks](#page-locks-paglock)). |
| `NOWAIT`                          | Zero the lock timeout for the hinted table, so a conflicting acquisition raises Msg 1222 at once rather than waiting. |
| `KEEPIDENTITY`, etc.              | Parse-and-discard.                             |

A hint that locks the read — `UPDLOCK`, `XLOCK`, `TABLOCKX`, `HOLDLOCK` / `SERIALIZABLE`, `REPEATABLEREAD`, `READCOMMITTEDLOCK` (`TableHintInfo.LocksRead`) — makes it a locking read whatever the level: under READ UNCOMMITTED it waits out the writer instead of reading dirty, and under `READ_COMMITTED_SNAPSHOT` or SNAPSHOT it waits and reads the latest committed row instead of a version; under SNAPSHOT, `UPDLOCK`, `XLOCK` and `TABLOCKX` meet a row changed since the snapshot as an update would, with Msg 3960, while `HOLDLOCK` / `SERIALIZABLE` just read it (`DataLockPlan.LockingRead` / `SnapshotConflictCheck`; probed 2026-10-03 against SQL Server 2025).
`TABLOCK`, `ROWLOCK`, `PAGLOCK`, `READPAST` and `NOWAIT` leave a versioned read versioned.
The reads once kept their level's behavior under every hint, so an `UPDLOCK` read-modify-write under READ UNCOMMITTED or `READ_COMMITTED_SNAPSHOT` read a value another transaction then overwrote — a lost update.

The closed `TableHintNames` accept-list still raises Msg 321 on unknown names.
Conflict-detection (`Msg 1047` on `NOLOCK + XLOCK`, `Msg 1065` on NOLOCK against a DML target) is unmodeled.

## Isolation-level semantics

`SET TRANSACTION ISOLATION LEVEL` mutates `SimulatedDbConnection.SessionIsolationLevel` when it runs — in a branch an `IF` doesn't take it changes nothing, though the batch's compile walks it (probed 2026-10-03 against SQL Server 2025); it once took effect there, putting a session under SNAPSHOT that never asked for it.
The session value persists across statements until the next SET.
Per-isolation reader behavior:

| Level              | Reader behavior                                                |
| ------------------ | -------------------------------------------------------------- |
| `READ UNCOMMITTED` | Skip every conflict check (dirty read). Equivalent to NOLOCK on every read. |
| `READ COMMITTED` (default) | Table-IS + per-row probe (wait on row-X holders, no row-S acquire); under `READ_COMMITTED_SNAPSHOT`, the statement's snapshot and no IS. |
| `REPEATABLE READ`  | Table-IS tx-scoped + row-S tx-scoped per row returned.         |
| `SERIALIZABLE`     | Table-IS tx-scoped + key-range locks on the keys the predicate reaches, every key otherwise, table-S over a heap. `UPDLOCK` / `XLOCK` shift the table lock to IX and the range mode to `RangeS-U` / `RangeX-X`. |
| `SNAPSHOT`         | Reads at the transaction's snapshot (see [Snapshot isolation + MVCC](#snapshot-isolation--mvcc)), holding the Sch-S its name took and no IS. |

A `READCOMMITTED` or `READCOMMITTEDLOCK` hint reads its table at READ COMMITTED under a `REPEATABLE READ` or `SERIALIZABLE` session, keeping no row or range lock past the row (probed 2026-10-08 against SQL Server 2025).

## A reader suspended mid-result

A `SELECT` sends its rows as its client reads them and waits on the client between windows (see [`data-reader.md`](data-reader.md#rows-go-out-as-the-reader-reads-them)), so what it holds while it waits is what its locks have reached by its position, not by the end of a finished scan.
Its acquisitions are the ones every read makes — the object lock where its source resolves, the row and key locks as its scan reaches each row — and the suspension keeps the statement's own scope open, releasing its statement-scoped locks with its last row.
One lock is the suspension's own: a plain `READ COMMITTED` scan, which holds no row lock past the row it reads, holds S on the row it stands on — the last it produced — while it waits, and lets it go as it reads on (`ResultStream.Suspend`, `BatchContext.HoldScanPosition`, the position recorded as `ProbeRowForRead` probes each row), so a writer of that row waits on the reader and a writer of any other goes ahead (probed 2026-10-08 against SQL Server 2025: a reader 2, 50, 100 and 300 rows into a result of 2,000 rows of `char(2000)` held `KEY S` on its last row produced, in a transaction or not; a sort, which read every row before its first went out, holds none).
Every scan the last row came through stands on a row of its own, and each holds its S — a correlated subquery's or an `APPLY` body's over another table beside the outer scan's, one over the same table standing on the outer row (probed 2026-10-09 against SQL Server 2025: two `KEY S` beside a correlated `COUNT(*)`, three beside an `EXISTS` over a third table, one for a correlated read of the same table; `StatementContext.OtherProbed`).
A reader resuming into a row an uncommitted writer holds waits there, its `LOCK_TIMEOUT`, `NOWAIT`, `READPAST`, cancellation and deadlock detection applying as at any row.
Another MARS request of the reader's own session meets these locks as another session's would ([Lock owners of a MARS session's requests](#lock-owners-of-a-mars-sessions-requests)).

Probed 2026-10-08 against SQL Server 2025 over a MARS connection, a reader two rows into 20,000 rows of `char(2000)` and a second session acting meanwhile (an `UPDATE` of row 1, one of a row ahead, an `INSERT` past the last key, an `ALTER TABLE … ADD` and a `TABLOCKX` update), `StreamedResultTests` holding each row:

| Read | Real holds | Here |
| ---- | ---------- | ---- |
| `READ COMMITTED` | `OBJECT IS`, the current page's `PAGE S` — over 2,000 rows, `KEY S` on the row it stands on, `PAGE IS` | `KEY S` on the row it stands on, `OBJECT IS` |
| `REPEATABLE READ`, the `REPEATABLEREAD` hint | `KEY S` on the 20 rows produced, `PAGE IS` on their 5 pages, `OBJECT IS` | the 20 `KEY S`, `OBJECT IS` |
| `SERIALIZABLE`, `HOLDLOCK` / `SERIALIZABLE` hints, a scan | `KEY RangeS-S` on the 20 rows, `PAGE IS`, `OBJECT IS` — an insert past the last key goes ahead | the 20 `RangeS-S`, `OBJECT IS` |
| `READ_COMMITTED_SNAPSHOT`, `SNAPSHOT`, and under RCSI the `READCOMMITTED` and `TABLOCK` hints | `OBJECT Sch-S` alone — a `TABLOCKX` update goes ahead | the same |
| `NOLOCK` / `READUNCOMMITTED` | `OBJECT Sch-S` | the same |
| `TABLOCK` at any level | `OBJECT S` | the same |
| `TABLOCKX` | `OBJECT X` | the same |
| `UPDLOCK` / `XLOCK` | `KEY U` / `KEY X` on the 20 rows, `PAGE IU` / `IX`, `OBJECT IX` | the 20 `KEY U` / `X`, `OBJECT IX` |
| `READCOMMITTED` / `READCOMMITTEDLOCK` under a `REPEATABLE READ` or `SERIALIZABLE` session, `READCOMMITTEDLOCK` under RCSI | `OBJECT IS`, `PAGE S` — the hint reads the table at READ COMMITTED | `KEY S` on the row it stands on, `OBJECT IS` |
| `ROWLOCK` | as the level's, row locks, `PAGE IS` | as the level's |
| `PAGLOCK` | `PAGE S` on the current page (READ COMMITTED) or the pages produced (REPEATABLE READ) | the same |

How the read came to its level doesn't matter — `SET TRANSACTION ISOLATION LEVEL` with a transaction begun in SQL, one the API began at the level, or a table hint — and neither does an `ORDER BY` the clustered key satisfies: a row-locking read (`REPEATABLE READ`, `UPDLOCK`, `XLOCK`) takes the ordered scan and locks each row as it reaches it, letting go a row its own `WHERE` rejects (below), and a plain `SERIALIZABLE` read in ascending key order is the table's own scan above.
A row-locking read lets go the lock of a row its query's own `WHERE` rejects, however the predicate reads the row — a sargable bound or not, beside a `NEWID()` or under an `OR` — as real's scan applies such a predicate itself, while one reached through a subquery or a join's `ON` filters above the scan, which keeps every lock it took (probed 2026-10-09 against SQL Server 2025: `UPDLOCK … WHERE v % 3 = 0` held twenty keys two rows in and 666 at its end, as did `XLOCK` and a `REPEATABLE READ` read; through a correlated subquery or an `ON`, sixty and 2,000).
The scan judges each row by the `WHERE`'s conjuncts that answer alike however often they are asked (`RowLockQualifier`), since the residual `WHERE` asks them again of the rows it keeps: one reaching a subquery, drawing (`NEWID`, `RAND`, `CRYPT_GEN_RANDOM`, `NEXT VALUE FOR`) or calling a user or CLR function decides nothing there, so a row only such a conjunct rejects keeps its lock.
Before, a row-locking read always sorted, locking every row first — the shape that turned real's 2,119 keys into a table S for an API-begun `REPEATABLE READ` reader of `ORDER BY id`.
A sort, an aggregate or a join's build reads its whole input before its first row goes out, so under `REPEATABLE READ` every row is locked at once (real: 20,000 `KEY S`), while `READ COMMITTED` still holds `OBJECT IS` until the sorted rows are out.
Every reader holding `IS` or more keeps an `ALTER TABLE` out, as each one's `Sch-S` keeps it out under the versioned levels.
`NOWAIT` raises Msg 1222 at the locked row it reaches and `READPAST` passes it, a row a writer locked after the read began included.

A `SERIALIZABLE` scan of a clustered table locks each key as it reaches it and the infinity anchor at its end (`PhantomFenceState.LocksAsScanned`), as real's does — so a reader suspended mid-result holds the ranges behind its position alone, and an insert ahead of it goes in.
The scan reads its key order when it begins, so a key inserted ahead of it since would be passed: once a row's key is locked, the keys that arrived in the gap below it are read first (`BatchContext.KeysArrivedBetween`), and the infinity anchor's lock at the end is followed by the keys that arrived past the last one (`FenceScanEnd`), which is what keeps the scan free of the phantoms the fence exists against (`StreamedResultTests.Serializable_ScanLocksKeysAsItReachesThem`).
A scan whose order comes without its keys — a nullable or descending clustered key — locks the whole key space as it begins, as do the reads below.
So does every other `SERIALIZABLE` scan that can't seek: one a predicate no key seeks on filters takes its fence as it scans, every key it passes — a row the predicate rejects included — and one taking `UPDLOCK` / `XLOCK` locks each key in its range mode before the row's own lock (`TouchRowForRead`), as real's do (probed 2026-10-08 against SQL Server 2025: twenty `RangeS-S` for the twenty rows a filtered reader had sent, sixty where its predicate kept one row in three, twenty `RangeS-U` / `RangeX-X` under `UPDLOCK` / `XLOCK`, and an insert ahead of each going in).
`Selection.MaybeApplyIndexSeek` leaves such a read's fence to the scan (`scanFences`), taking the whole key space itself only for the union of an `OR`'s seeks; a row-locking scan with nothing to seek on walks the key space as below.

A `SERIALIZABLE` seek locks each key as it reaches it too (`Selection.WalkKeyFence`): it produces its rows by walking the keys of its intervals — a range, an equality prefix of a composite key, each value of an `IN` list, a read in key order either way — rather than locking them all and then reading (probed 2026-10-09 against SQL Server 2025, every shape two rows into 2,007-byte rows: twenty keys held, a write behind the position waiting, an insert ahead going in and read when the seek got there).
Which anchors outside the rows it locks follows the direction:
- **ascending**, the key past each interval once it has read the interval — the infinity anchor when none is — even when the upper bound is a key it read;
- **descending**, that same key before its first row, unless the interval's top is a key equal to an inclusive upper bound, so a descending scan begins with the infinity anchor; and at its end the last key below the interval, which has no infinity counterpart;
- **a full key of a unique index**, the plain key lock on a hit and the next key's range on a miss, each as the seek reaches it, so twenty values of an `IN` list hold twenty locks.
A seek through a nonclustered index locks its entries in the range mode and takes each row's S as it looks it up; a key an earlier delete in flight took away is waited out before the walk reads its keys, as the seek's other paths do.

### Divergences

- **A `READ COMMITTED` reader's position is a row, never a page**, where over a large enough table real's is a page, whose `PAGE S` keeps out a writer of any row on it.
  **Settled — don't re-pitch:** real's own position lock isn't deterministic.
  Probed 2026-10-09 against SQL Server 2025 with readers two rows into tables of various widths: the choice between `KEY S` and `PAGE S` turned on both the row count and the row width — `KEY S` through 7,500 rows of 2,000-byte rows and `PAGE S` from 7,700, `KEY S` through 8,000 rows of 100 bytes and `PAGE S` at 9,000, `KEY S` at 10,000 rows of 10 bytes and `PAGE S` at 20,000 — and identical runs held no position lock at all on some passes (100-byte rows at 8,000 rows, 2,000-byte rows at 200, 10-byte rows at 7,500), as the scan happened to stop between rows or pages when its client's window filled.
  The pages themselves were predictable — rows inserted in key order filled them 4, 19, 69 and 299 to a page by width, and the page held was the position's — but a client can't rely on which lock the position holds, so neither is a contract to match.
- **A `PAGLOCK` read's pages are the ones a load in key order leaves** ([Page locks](#page-locks-paglock)).
- **The page intent locks above row locks aren't listed**, which only `sys.dm_tran_locks` shows, and a page is locked only under `PAGLOCK` — so a READ COMMITTED `READPAST` read passes a locked row real's page-locked scan waits on (probed: real passes it once `ROWLOCK` is added, as here).
- **An object S or X lock is one lock here**, where real's host takes it on each of its 16 lock partitions (`OBJECT S` ×16 in `sys.dm_tran_locks`).
- **A few `SERIALIZABLE` reads still take their whole fence as they begin**: one through a nonclustered index read in descending order, a descending or filtered index, a clustered key with a nullable or descending column, and the union of an `OR`'s seeks — past the escalation threshold, the table S or X — where real locks each key as the read reaches it.
  A reader suspended mid-result so blocks writes ahead of it that real lets through, never the reverse.
- **A row-locking read keeps its lock on a row only a drawing, subquery-reaching or function-calling conjunct of its `WHERE` rejects**, where real lets it go, since asking that conjunct again could answer differently (above).
- **A `READ COMMITTED` join holds no position while it waits**, where real's merge or nested-loop join holds one per input (probed 2026-10-09 against SQL Server 2025: two `KEY S` two rows into a join of two 2,000-row tables): the join here reads its build side whole and its probe side's rows come through it unprobed.
- **A sort over `REPEATABLE READ` escalates** at real's threshold, where real's parallel sort of the same 20,000 rows held its 20,000 key locks.

## Diagnostic DMVs

- **`sys.dm_tran_locks`** — one row per held / waiting lock across every database — every schema-bound `SchemaLock`, every `HeapTable.TableDataLock`, every per-row entry in `HeapTable.RowLocks` (and the row locks of rows a session deleted, found through `HeapTable.SupersededKeyImages`), and every key-lock anchor in `HeapTable.KeyLockGroups`.
  A row lock reports `KEY` on a clustered table, whose row real locks by its key, described by that key, and `RID` on a heap; a row lock and a key-range lock one session holds on the same clustered key fold into one row in the combined mode (see [Divergences](#divergences)), as do a U and the X its holder took over it, and a session's modes on one table into the one lock real converts them to (`LockDmvs.FoldObjectModes`).
  A key a session deleted and inserted again is two rows at two addresses here and one key on real, which keeps the key in place, so it reports once per index (probed 2026-10-07 against SQL Server 2025).
  A statement's Sch-S stands for real's compile-time lock, so the view leaves it out beside anything else the session holds or waits for on the object — a read's IS, a write's IX, a redefinition's Sch-M — as real shows that one request (probed 2026-10-07 against SQL Server 2025: a waiting reader's IS, a waiting redefinition's Sch-M); a `NOLOCK` read shows its Sch-S.
  Beside a written row's own lock the view reports the index key locks real takes with it, which the simulator folds into the row's X (`LockDmvs.EmitRowLocks` carries the rule): an inserted or deleted row's key in every index it is in, an updated row's old and new key in every index whose row the update changed, a moved clustered key's old key, and a filtered index only where its filter admits the image (probed 2026-10-01 against SQL Server 2025 over heaps and clustered tables with unique, non-unique, filtered and `INCLUDE` indexes).
  A uniqueness or foreign-key check waiting on the row that carries a unique key reports its wait on that key (`SessionToken.WaitingOnKey`), as real waits on the key's own lock.
  Column subset: `resource_type` (`OBJECT` / `RID` / `KEY` / `APPLICATION` / `DATABASE`), `resource_subtype` (empty for every one), `resource_database_id`, `resource_description`, `resource_associated_entity_id` (`object_id`), `request_mode` (`Sch-S` / `Sch-M` / `IS` / `IX` / `SIX` / `S` / `U` / `X` / `RangeS-S` / `RangeS-U` / `RangeX-X` / `RangeI-N`), `request_status` (`GRANT` / `WAIT`), `request_session_id`.
  Every row's `resource_description` is blank-padded to the column's 256 characters, as real's are, an OBJECT row's empty one included — the object is `resource_associated_entity_id` — and `resource_database_id` is the database's own id (probed 2026-09-30 and 2026-10-07 against SQL Server 2025).
  A `DATABASE` row is real's shared lock of a session's workspace, an `S` with entity 0 (probed 2026-10-07 against SQL Server 2025): one on its current database, master and tempdb excepted; one on the database of each execution context it is nested in — a dynamic batch's `USE`, a procedure in another database — beside the enclosing ones (`SimulatedDbConnection.EnclosingDatabases`); one on each database it holds or awaits a lock in; and, in a transaction, one on each database whose tables the transaction locked anything in, kept to its end (`SimulatedDbTransaction.TouchedDatabases`).
  The rows are synthesized as the view runs rather than taken, so nothing waits on them.
  Not modeled yet: a `DATABASE` row for a database whose catalog views alone a statement read (`msdb.sys.objects`), which takes no lock here, and the hash slot of an `APPLICATION` description, which is FNV-1a here rather than real's (probed 2026-10-07 against SQL Server 2025).
  Row generator at `LockDmvs.EnumerateDmTranLocks`.
- **`sys.dm_os_waiting_tasks`** — one row per currently-blocked connection: `session_id` (waiter's SPID), `wait_type` (`LockDmvs.WaitType`, real's names: `LCK_M_X`, `LCK_M_SCH_M`, `LCK_M_RS_U`, `LCK_M_RIn_NL` …), `resource_description`, `blocking_session_id` (one conflicting holder's SPID, or with none the request queued ahead that the wait is behind, as real names a waiting Sch-M for the Sch-S queued behind it — probed 2026-10-07 against SQL Server 2025).
  Row generator at `LockDmvs.EnumerateDmOsWaitingTasks`.
  Waiter / mode state lives on the token the wait is recorded on (`SimulatedDbConnection.WaitRecord`, the session's lock owner while it is the one acting for it), written when the wait begins and cleared once the acquisition leaves.

Neither DMV holds the manager's gate across the enumeration — concurrent acquires / releases may shift the result between rows — but each resource's holders are copied under it (`LockManager.HoldersOf`): copying the list another session was appending to threw, or handed back a half-written hold whose owner was null.
That gate-free walk is why the waiter registration spans the **whole** wait rather than each `Monitor.Wait` slice: a waiter that cleared and re-set it around every slice reads as idle for the length of its own between-slice re-check, and the re-checking thread can be descheduled there while holding the gate.
Measured over a tight poll of a session blocked on a row-U conflict, that window swallowed 0.01% of observations on an idle 16-core box and 0.19–0.81% with the participants pinned to one core — enough to fail the `sys.dm_os_waiting_tasks` / `sys.dm_tran_locks` / `sp_who` blocked-session tests on a loaded CI runner, and zero once the registration is held across slices.

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
- **Cascade-FK SET NULL / SET DEFAULT / CASCADE writes on child tables, and an edge constraint's cascade** — each deletes or rewrites through the path a DELETE or UPDATE of the row takes (`Simulation.DeleteRowAt` / `RewriteRowAt`): row-X, the noted pre-image, the version capture.
- **OUTPUT INTO target / SELECT INTO destination** — per-row row-X on the destination table.
- **A deleted row's lock entry** outlives the DELETE until the X that deleted it goes (`HeapTable.RetireRowLock`, from `LockManager.Release`): a rollback restores the row at that address, and a session queued on the lock then holds the row it waited for.
  Removing the entry as the delete ran let a rollback restore the row under no entry, so the next locker interned a second lock for it beside the one a waiting session had just been granted — two sessions each holding the row's U, both writing it, a lost update.

## Uncommitted keys and deletes make their readers wait

A PRIMARY KEY / UNIQUE constraint or unique index check against a key another open transaction is writing waits for that transaction rather than deciding on its uncommitted state, as real's check waits on the key's lock (probed 2026-09-26): a second insert of a key blocks until the first commits (then Msg 2627) or rolls back (then succeeds), and a key an uncommitted DELETE or key-changing UPDATE took away can't be reused until that write settles.
Without the second half a rollback restored the deleted row beside its replacement — two rows with one key.

Two sources feed the wait (`Simulation.AwaitUncommittedKeyWriters`, called once per seekable key before the duplicate probe):

- **A live row carrying the key** under another session's row X, found through the seek cache's `MatchingRows` — the uncommitted insert, or a rewrite *to* the key.
- **`HeapTable.SupersededKeyImages`**, each session's pre-images of the rows it deleted or rewrote while it still holds their row X (`BatchContext.NoteSupersededRow`, called at every UPDATE / DELETE / MERGE / FK-cascade rewrite site after the row X is taken).
  An entry retires with the final release of its row X — `LockResource.RowAddress` tells `LockManager.Release` which — so the registry holds only writes still in flight, and a session's own entries are never consulted by its own checks.

A check whose key has a NULL component scans rather than seeks, and waits on a live row carrying the key the same way; it doesn't consult the delete registry, so a NULL key an uncommitted DELETE took away is free to reuse at once.

A uniqueness check waits in **X**, as real's second writer of a key requests X on it (probed 2026-10-01 against SQL Server 2025: `LCK_M_X` on the first writer's `KEY` for an INSERT and a key-moving UPDATE alike, a NULL in a UNIQUE column included); a foreign key's check waits in S.

Foreign keys take the same wait on both sides: the child-side parent-existence check waits on the parent key, and the parent-side check for referencing children (a DELETE, a key-changing UPDATE) on the child key — so a child can't slip in under an uncommitted parent insert that a rollback then removes, and a parent can't go while an uncommitted child delete that a rollback restores still points at it.
Real's parent-side check scans an unindexed child table and so waits on *any* locked child row; the simulator waits only on children carrying the key.

The same registry covers **locking reads over an uncommitted DELETE**: the heap walk never reaches a tombstoned slot, where real's scan meets the deleted row's X-locked key and waits on it, so a READ COMMITTED / REPEATABLE READ / SERIALIZABLE / `UPDLOCK` read used to report the delete before it committed.
A scan (`BatchContext.AwaitUncommittedDeletes`) waits on every other session's tombstoned entry for its table up front — earlier within the statement than real's wait at the row's turn, with the same outcome — and an equality or range seek on those whose pre-image its probes or bounds reach, so a seek elsewhere in the key space proceeds as on real.
NOLOCK / READ UNCOMMITTED, READPAST and the snapshot readers don't wait.

### Writers racing to one new key

The check can't decide alone for a key no row carries yet: two sessions each check, find nothing, and write — a duplicate neither saw.
Real closes the window by taking the new key's X as the row enters the index, so the second writer waits on it and then raises Msg 2627 / 2601, or writes after a rollback.
The simulator folds that key lock into the row's own X: `UniqueKeyWriteGuard` (its declaration holds the design) takes one last look under the heap's latch, as the write publishes the row with its X, at the images other sessions inserted or updated since the check began, read off the seek journal.
One carrying a key the write would duplicate refuses it, and the second check meets that row — published with its X — and waits on it like any uncommitted key, which keeps the wait in the lock manager, so deadlock detection and `SET LOCK_TIMEOUT` (Msg 1222) see it.
Every path that writes a checked key takes the look: INSERT (`VALUES`, `SELECT`, `EXEC`), `BULK INSERT` and the TDS bulk load, `OUTPUT … INTO` a keyed table, MERGE's inserts and updates, and UPDATE; `SELECT … INTO` creates a table no other session can see yet, with no key to race on.
`ConcurrentUniqueKeyTests` races eight sessions per key over a heap's UNIQUE constraint, a clustered key, a unique index and a composite key through each of those paths, plus NULL keys and `IGNORE_DUP_KEY`; without the guard nearly every variant left duplicates on nearly every run.

Uncontended the look is a generation compare, since nothing but the guard's own writes moved the heap — measured 2026-10-01 one case per process, the range of each process's best round (or median batch, where marked) over three to five processes:

| Case | Before | After |
| ---- | ------ | ----- |
| `INSERT … VALUES` into a clustered primary key, plan-cached | 4.41–4.54 µs | 4.34–4.54 µs |
| the same into a heap with a UNIQUE constraint | 4.47–4.55 µs | 4.48–4.56 µs |
| the same into an identity-keyed table with a unique index | 6.15–6.34 µs | 6.28–6.40 µs |
| EF Core 10's identity-key `MERGE`, 10 / 100 rows (median) | 15.2–15.7 / 112.9–115.9 µs | 15.2–15.7 / 113.4–115.2 µs |
| key-moving `UPDATE` of 20,000 rows over two unique keys (median) | 51.4–54.9 ms | 52.7–54.0 ms |
| bacpac import — AdventureWorks / WWI Standard / WWI Full / Insite (mean of 3) | 1.97 / 2.63 / 1.77 / 1.26 s | 1.92 / 2.58 / 1.89 / 1.19 s |

A lock-manager lock per unique key — the shape real's DMVs show — would have put the manager's one gate on every insert and interned a lock resource per key for the table's life.

Probed 2026-10-01 against SQL Server 2025 and reproduced: the second writer waits however the first wrote the key (INSERT, key-moving UPDATE, MERGE's insert) and raises Msg 2627 / 2601 once it commits, writes once it rolls back, and under `IGNORE_DUP_KEY` reports Msg 3604 and writes nothing; `SET LOCK_TIMEOUT` ends the wait with Msg 1222; two transactions each inserting the key the other holds deadlock, the one closing the cycle the victim.

The lock DMVs report the shape real's do: the first writer's `KEY X` on the key in every index the row entered beside its row lock, and the second writer's wait on the unique index's key — `KEY X` on a heap's UNIQUE constraint or a unique nonclustered index, the clustered key otherwise — though the wait itself is on the first writer's row lock, which stands for its keys ([Diagnostic DMVs](#diagnostic-dmvs)).
A MERGE's second writer waits in U, its matching read's mode ([A writer's target read](#a-writers-target-read)), and an `IGNORE_DUP_KEY` index's in U or `RangeS-U` ([What an `IGNORE_DUP_KEY` check takes](#what-an-ignore_dup_key-check-takes)).

Divergences in what the lock DMVs show:

- **The second writer holds nothing on its own row while it waits** — not modeled yet.
  Real's has entered its base row — and any index before the one it waits on — so it shows `KEY X` or `RID X` on its own new row, which a scan then waits on too (probed 2026-10-01 and again 2026-10-07 against SQL Server 2025: a heap's `RID X` and a clustered key's `KEY X` beside the `KEY X WAIT` on the unique index); the simulator checks before it writes.
  The index entry here is the row itself, so publishing the row before its check would leave two racing writers each waiting on the other's row, a deadlock real's separate index entry never meets; modeling it takes a row that is in the heap but not yet in its unique indexes.
- **A MERGE inserting into an `IGNORE_DUP_KEY` index** waits on real in `RangeI-N`, its insert's range test against the first writer's `RangeX-X`; here the uniqueness wait's X.

### A key deleted and put back in one transaction

Real's index keeps a deleted key in place as a ghost under its writer's X, and an insert of the key in the same transaction fills that ghost, so a reader or writer meeting the key waits once and then reads the row the key holds.
Here the reinserted row lands at a new address, and every path that meets the key has to follow it there (probed 2026-10-03 against SQL Server 2025: a locking seek or scan, a joined UPDATE and a MERGE each wait on the key and then read or write the reinserted row; `KeyReinsertConcurrencyTests`).
The pieces, each closing a race the randomized stress harness found as a lost update, a missed row or a row read unlocked:

- **Following the key.**
  A read or a target walk whose row was deleted under it looks the row's key up again (`BatchContext.RowsOfDeletedKey`, keyed by `RowIdentityKey` — the clustered key, else the first unique key), waiting out whichever session holds the key deleted by then.
  The rows it finds carry the key on, so a row deleted again before it is read is followed in turn.
  It concludes the key is gone only when a read of the rows and a read of the deletes in flight saw it gone with nothing put back in between (`BatchContext.RowsCarryingKey`, `HeapTable.KeysPutBack`): the two reads are taken at different moments, and a delete settling between them read as a key that never came back.
- **Keys a scan's order lacks.**
  A key-order scan places each in-flight delete's row at its key's position (`BatchContext.LockingScanOrder` / `PlaceInFlightDeletes`), the ghost real keeps, and reads the order and the deletes again when a key was put back meanwhile; a delete its transaction rolled back is placed too when the order was read before the row came back.
- **Counting before retiring.**
  A rolled-back delete counts in `KeysPutBack` before its registry entry retires, and a writer notes the count before its seek chooses rows: in the other order a key settled in the gap was in neither read.
- **Running the statement again.**
  A writer whose walk met a key put back elsewhere — waited on a row that came back deleted, or saw the count move — rewinds and runs again (`BatchContext.TargetKeyReinserted`, up to `Simulation.MaxTargetWalks`), the plain UPDATE and DELETE, the joined forms, MERGE and a write through a partitioned view alike, each waiting out the deletes still in flight once its walk is done; a MERGE stops before its NOT MATCHED inserts when it will run again (`BatchContext.TargetWalkMayRunAgain`), so a source row whose target it missed doesn't insert a key that stands.
- **A SERIALIZABLE fence over moving keys.**
  The fence locks the keys the table holds as it is taken, and a key deleted in flight is missing from them; a row the read then reaches after the table changed has its key locked as it is read (`PhantomFenceState.FencedGroup`, `BatchContext.HoldFencedRowKey`), or its row S for a fence of unique points or one a heap's unique index takes through lookups, before the row's writers are waited out, so the read can't be changed under the reader.
  A scan waits out in-flight deletes before taking the fence, as a seek does: fencing first held the next key's range while the deleting transaction's reinsert waited on it.
- **The key test and the row X.**
  A write tests key locks and then takes the row's X, two acquisitions where real's key lock is one; a SERIALIZABLE reader locking the key between them found the row unlocked and read it, and the write then changed it under the reader's lock.
  A write that finds a key lock appeared once it holds the X gives the row back and waits for the key (`BatchContext.AcquireRowLockTxScoped`).
- **The seek cache across a rollback.**
  A rollback journals each row write's reversal under the latch hold that undoes it (`UndoLog.RollbackTo`, see [`indexes.md`](indexes.md#equality-seek-acceleration)); invalidating once the whole log was undone let a scan read the cache at its old generation while the heap already held a restored row the cache lacked.

A heap (no clustered index) follows a key through its first unique key, but its scan reads in allocation order, places no ghosts, and passes a row whose key was deleted and inserted again at an address it already passed — as real's heap scan does (probed 2026-10-07 against SQL Server 2025: a REPEATABLE READ scan waiting on one row while another transaction deleted a later key and put it back in a slot the scan had read counted eight rows of nine, and the next scan nine).
The stress harness's REPEATABLE READ total over a heap, which this left one row short, checks a guarantee real doesn't make either.

## A writer's target read

An UPDATE, a DELETE and a MERGE read their target under U: a row another session holds X on is waited out in U and judged as that session's write leaves it, and a row that qualifies is written under X, so no other writer changes it between the judgement and the write (probed 2026-10-01 against SQL Server 2025: a MERGE, seeking or scanning, waits `LCK_M_U` on the writer's key; an UPDATE scanning a heap waits `LCK_M_U` on another session's written row however its own predicate reads, and already holds X on the rows it passed).
The target walk once read rows with no lock at all and the write took X at commit, so an UPDATE judged a row another session was rewriting by that session's uncommitted image — `SET v = v + 1` over a write that then rolled back wrote 51 for 6 — and two sessions incrementing one row lost increments; a MERGE matched, or failed to match, uncommitted values.

Two single-table shapes read otherwise, by where the plan reaches the row (probed 2026-10-07 against SQL Server 2025):

- **A write wholly through a seek of the clustered key** — equalities on a leading run of its columns, then at most a range on the next, and nothing else in the `WHERE` — writes through the seek without reading ahead, so it waits in **X** (`Selection.WhereIsClusteredKeySeek`): `WHERE k = 5`, `k >= 5` and `a = 1 AND b >= 2` wait `LCK_M_X`, over a key another transaction deleted, or deleted and inserted again, too; a residual predicate, a range ahead of another column, `k IN (1, 5)` and a scan wait in U.
- **A seek through a nonclustered index** reads the row's index key under U and holds it while it waits on the row; a row another transaction deleted, or whose key in that index it moved, is met on the index key itself, the wait reported there (`Selection.MutationSeekIndex`).

A joined UPDATE or DELETE reads in X too where real folds everything beside the target to constants: every other source one row of constants — a one-row `VALUES`, a FROM-less `SELECT` (an expression or a variable in it included), a CTE or `APPLY` body of one — and the target's inner-join `ON` conditions with the `WHERE`, the constants' columns standing for their values, a clustered key seek by the rule above (`Selection.JoinedWriteIsClusteredKeySeek`; probed 2026-10-03 and 2026-10-08 against SQL Server 2025, either side of the join, comma-joined, cross-applied, over a row another transaction updated and over a key it deleted and inserted again alike).
A table or table variable partner, a derived table reading one, two `VALUES` rows, an outer join's `ON`, a residual predicate, an `IN` or `EXISTS` over the constants and a heap or nonclustered key wait in U, and a `MERGE` from the same `VALUES` waits in U too.

The joined forms read their target the same way: `UPDATE … FROM` / `DELETE … FROM` with the target on either side of a join or an APPLY, aliased or not, an UPDATE through a join view, and a joined write whose target is a view or CTE in its FROM clause.
Real waits in U on the target row whichever side it sits, holding S on the partner row it read, and the partner waits in S (probed 2026-10-01 against SQL Server 2025, for each of those shapes, a CTE over a join, a correlated `EXISTS`, a MERGE into a join view and a joined UPDATE or DELETE through a single-table or join view included).
EF Core's ExecuteUpdate / ExecuteDelete emit exactly this shape — `UPDATE [m] SET … FROM [Members] AS [m] INNER JOIN [Teams] AS [t] ON …`, `DELETE TOP(@p) FROM [m] FROM …` — whenever the LINQ query filters through a navigation.
They once judged the target off whatever image the heap held and took X only in the commit, so eight sessions each running `UPDATE t SET v = t.v + 1 FROM t JOIN …` forty times ended at 70–112 of 320, and eight draining one queue with a joined `DELETE TOP (3)` deleted 467–613 rows of 200.

The pieces, each shared by the single-table and joined forms:

- **The walk** waits on a row it reaches only when another session holds a lock U conflicts with (`BatchContext.AwaitTargetRow`, skipped lock-free while `HeapTable.ActiveDataWriters` and its U companion `ActiveUpdateLocks` read zero).
  A joined form waits once a tuple has passed the join and the WHERE, so it waits only on a row that joins, as real's plan seeking the target by its join key does, and judges a row the wait changed again against its partners, re-running the join with the target narrowed to that row (`WithTargetNarrowedTo`, the join-view path's `SourcesAlongPath` with a one-row source).
- **The walk holds nothing.**
  UPDATE and DELETE let the U go once the waited row is read (`BatchContext.AwaitTargetRowWriters`) and take every qualifying row's X after the walk and its `TOP`, in walk order (`Simulation.HoldQualifyingRows`), reading a row again and judging it afresh when the heap's `MutationGeneration` has moved.
  A statement writing as its client reads its `OUTPUT` rows takes each row's X as it writes it instead, one row at a time, the walk reading each row as it reaches it, or, where real's plan spools the read, holding every row read in U until its write ([`data-reader.md`](data-reader.md#a-dml-statements-output-rows)); a `MERGE` writing so holds its matched rows in U rather than X.
  Holding the walk's U, a session could keep a later row while its X waited on an earlier one, held by another session whose X waited on the later: eight sessions each updating the same two rows forty times deadlocked one to seventeen times per run, single-table or joined, where real meets no deadlock because its walk takes U on every row in order.
  MERGE takes X inline, matched row by matched row, right after the U it waited in, which keeps the same order; its actions take it again re-entrantly.
- **A view target read as the view yields it.**
  A joined write whose target is a single-table view or CTE reads the target as the view's rows — its filter, row limit and window applied (`Simulation.MaterializeViewTarget`) — through a body read that would wait in S, so it waits out in U first every row another session holds, and every in-flight delete or rewrite whose prior image the view shows, then reads; a row whose image moved between the body's read and the slot's is shown as the image reads.
  The walk then judges and holds rows as the table forms do.
- **A MERGE into a join view holds its U.**
  It matches the view's rows as a whole before writing any, so the walk can't let a row go between its match and its write the way UPDATE does: `Simulation.LoadJoinViewMergeRows` takes U on each written-table row the join reaches as it meets it (`BatchContext.HoldTargetRowForUpdate`), reads a row a wait found changed again from its settled image, and gives the U back once the statement has written, its writes holding X.
  Without it eight sessions each running `MERGE` into a join view forty times lost increments.
- **Rows the walk can't reach.**
  A row another session has deleted — the walk never meets a tombstoned slot — or rewritten so a seek or the join no longer reaches it, or the WHERE no longer passes it, is invisible to the wait above; real's read meets the deleted key, or the old index key, under that session's X, waits on it in U and, after a rollback, writes the restored row (probed 2026-10-01 against SQL Server 2025 for a scan and a seek over a deleted row, a nonclustered seek on a key moved away, and a joined write whose ON or WHERE the uncommitted image fails).
  Before the walk, each such row in flight (`HeapTable.SupersededKeyImages`) whose prior image the statement's predicate passes is waited out in U (`Simulation.AwaitSupersededTargetRows`), so the walk meets it settled; a seek is computed again after any such wait.
  It costs one lock-free read of the registry while no other session has a delete or rewrite in flight on the table, and otherwise a predicate test per registry entry.

Measured 2026-10-01 against the build whose joined forms judged off the heap's image, one case per process, five processes (ten for the 20,000-row case and the single-row UPDATE), each process's best of seven rounds:

| Case | Before | After |
| ---- | ------ | ----- |
| joined `UPDATE` of one row of a 1,000-row table | 186–298 µs | 189–257 µs |
| joined `DELETE` + `INSERT` of one row of a 1,000-row table | 201–290 µs | 207–287 µs |
| joined `UPDATE` of every row of a 20,000-row table (median) | 23.7 ms | 25.0 ms |
| `UPDATE` of one row by its clustered key, plan-cached (median) | 4.59 µs | 4.56 µs |
| EF Core ExecuteUpdate through a navigation, 10 of 1,000 rows | 517–646 µs | 497–649 µs |
| EF Core ExecuteDelete through a navigation, one row, re-inserted | 513–648 µs | 499–630 µs |

The joined forms walked their target as a full scan then, which is what the per-statement figures are; the 20,000-row case pays the post-walk X pass the single-table form already paid.
They now seek it where the WHERE or the join key pins it (see [`dml.md`](dml.md#joined-row-sources)), reading without a lock as the scan did, so a seek changes which rows the walk reaches and never which it waits on or locks: real waits in U on the sought key another session holds and passes a row the seek doesn't reach, as the walk does (probed 2026-10-07 against SQL Server 2025 for the target sought by the WHERE, by the join key and through a join view, over an uncommitted update, delete and delete-and-reinsert).

Divergences:

- **The partner row's S** — real holds S on the row of the other source while it waits on the target where it read that row by a single-row seek (probed 2026-10-07 against SQL Server 2025: `WHERE c1.k = 5` or `WHERE d1.k = 5` over `c1 JOIN d1 ON c1.k = d1.k` hold the partner key's S), and none where it scanned the partner; the simulator's READ COMMITTED read of the partner holds nothing by then.
- **A non-joining row**: the joined forms wait only on rows that join, where real's scan of a target it can't seek — a heap, an unindexed join column — waits on every row another session holds; the outcome is the same.
- **Constants the simulator doesn't fold**: a joined write from a CTE over a `VALUES` row (`WITH c AS (SELECT * FROM (VALUES (2)) x (id))`) or a `TOP` derived table of one constant row, and one whose target is an `APPLY` body over `VALUES` (`UPDATE x … FROM (VALUES (2)) d (id) CROSS APPLY (SELECT * FROM t WHERE t.id = d.id) x`), wait in U, where real waits `LCK_M_X`; and an `APPLY` body reading the target beside the target joined to it waits in S here where real waits in U (probed 2026-10-08 against SQL Server 2025).
- **A joined write's or a MERGE's seek through a nonclustered index** waits on the row's own lock, the clustered key or the RID; real holds U on the index key it read and waits on the row — the same row and mode, the index key's U missing here — or, when the holder changed that key, waits on the index key itself, which the simulator reports as the row.
  The single-table UPDATE and DELETE read it as real does (above).
- **A qualifying row's X comes after the walk**, real's as the plan writes the row — except for a statement writing as its client reads its `OUTPUT` rows, which takes each as it writes it; a MERGE holds X on a matched row an `AND` condition then declines, where real's U is released.
- **The prior image a rewrite registry entry carries** is the row before the session's latest write of it, so a row a transaction rewrote twice is tested on its intermediate image rather than its committed one.

- **A MERGE into a join view keeps U on every row the join reached** until it has written, where real's scan releases the U on a row it doesn't write; and a joined write through a single-table view waits on every row of the table another session holds before its read, where real's scan waits on the rows it reaches.

## Page locks (`PAGLOCK`)

`PAGLOCK` locks the pages a table's rows are on in place of the rows, as real does (probed 2026-10-09 against SQL Server 2025 with readers two rows into 2,000 rows of `int` and `char(2000)`):
- a `READ COMMITTED` read holds `PAGE S` on the page it stands on, letting it go as it moves on, so while it waits on its client a writer of any row of that page waits;
- `REPEATABLE READ` and `SERIALIZABLE` hold `PAGE S` on every page read to the transaction's end, with no row or range lock beside them, an insert landing on a held page waiting;
- `UPDLOCK` and `XLOCK` hold `PAGE U` and `PAGE X`;
- a write takes `PAGE X` on each page it writes a row of, which `sys.dm_tran_locks` shows in place of its rows' X;
- a versioned read stays versioned.
A page lock's timeout is Msg 1222 state 52.
Which page a row is on comes from `RealPageLayout`: the table's rows in clustered-key order — a heap's in insertion order — filling pages of 8,096 bytes with real's record format and a two-byte slot each, the versioning tag of a database with `ALLOW_SNAPSHOT_ISOLATION` or `READ_COMMITTED_SNAPSHOT` on included, which takes four rows of `int` and `char(2000)` to a page down to three; it reproduces real's page fill for a table loaded in key order exactly (`RealPageLayoutTests`, twenty shapes against `%%physloc%%`).
A row lock meets a page lock through its page's intent, IS for an S, IU for a U, IX for an X, which `BatchContext.LockRowPage` takes only while some session holds a page lock on the table (`HeapTable.ActivePageLocks`), and a `READ COMMITTED` read tests its row's page the same way; those intents stay out of `sys.dm_tran_locks`, as every other row lock's page intent does.

**Divergences**:
- A table loaded out of key order, or changed since, has the pages its splits left on real, which follow its history; the layout here is a rebuild's in key order.
- An insert past a full last page locks that page's X alone, where real's also locks the page it allocates.

## Granularity approximations

- **`ALTER SCHEMA TRANSFER`** — Sch-M on the moved object isn't acquired.

## Snapshot isolation + MVCC

`ALLOW_SNAPSHOT_ISOLATION` and `READ_COMMITTED_SNAPSHOT` are per-database flags on `Database` (both default `false`, flipped via `ALTER DATABASE … SET (ALLOW_SNAPSHOT_ISOLATION | READ_COMMITTED_SNAPSHOT) { ON | OFF }`).
When either flag is on, every INSERT / UPDATE / DELETE / MERGE captures a row-version entry in the heap's `Heap.RowVersions` dict; readers under SNAPSHOT or RCSI consult the chain to substitute pre-write payloads.
A memory-optimized table is read at a snapshot whatever the level — the transaction's, taken at its first read of one, or the statement's outside one; its reads once walked the live rows unlocked, so one transaction's two reads disagreed.

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
The commit counter is monotonic and **instance-wide**; each committing transaction takes one stamp however many databases it wrote to, and SI readers acquire their snapshot via `Simulation.CurrentTransactionCommitId`.
Counter starts at zero so pre-versioning rows (implicit Xmin = 0) are visible to every snapshot.
A commit draws the next stamp, stamps every row it wrote, and only then publishes the stamp, all under `Simulation.CommitGate`: a snapshot taken while the stamps land still reads the old counter and sees none of the transaction's rows.
Publishing first — which the counter once did, as an increment ahead of the stamping — let a snapshot read the new stamp while the rows were still marked in flight, see them hidden, and see them appear on its next read inside the same transaction.

Instance scope mirrors real, whose transaction sequence number is server-wide (its version store lives in `tempdb`, not per database), and it is what makes a snapshot stamp comparable across databases.
Probed: a SNAPSHOT transaction fixes **one** stamp at its first data-access statement and reads *every* database as of that instant — a transaction whose first read was in one database still sees another's pre-update state when it reads it later, and `BEGIN TRAN` alone fixes nothing (a commit landing before the first read is visible).
`Simulation.ActiveSnapshotTxs` is instance-wide for the same reason: an open snapshot anywhere pins history everywhere, so the GC cutoff reads the simulation's oldest active Xid.

### Version-store data structures
Per-`Heap`: `ConcurrentDictionary<(int Page, int Slot), RowVersionChain> RowVersions`, written only under the table's `RowVersionsGate` — a writer's capture, a commit's stamps, a rollback's discard and the sweep — and read lock-free.
The chains belong to the heap whose addresses key them: an `ALTER TABLE` rewriting the rows into a new heap leaves them with the old one, and a rollback restoring that heap brings them back, while a `TRUNCATE` or either side of a `SWITCH`, which keep the heap, set them aside with an undo (`VersionStore.SetAsideVersions`).
On the table, they once outlived the rows they described, and a versioned read after the rewrite resolved them against the new rows at the same addresses — hiding some and resurrecting others, a table of 39 rows reading as 37 under `READ_COMMITTED_SNAPSHOT` (`VersionChainRewriteTests`).

`RowVersionChain`:
- `LiveXmin: long` — commit Xid of the live row.
- `WriterSession: SessionToken?` — the session whose uncommitted write occupies the live slot, a transaction's or an auto-commit statement's alike.
  Another session's SI reader sees it and walks history; the writer's own reads see the live row.
  It named the transaction once, which left an auto-commit statement's in-flight write unmarked — a concurrent snapshot read it as committed long ago.
- `IsDeletedLive: bool` — true after a committed DELETE tombstones the slot; readers with snapshot before the delete Xid still see the historical payload through `Head`.
- `Head: HistoricalVersion?` — linked list of older committed versions, newest-first.
  Walked by the visibility predicate `Xmin <= SX < Xmax` (with `Xmax = long.MaxValue` denoting a still-in-flight superseder).

### Writer-side capture
`VersionStore.CaptureWrite(batch, table, newRid, oldRid?, oldPayload?, kind)` is called from every INSERT / UPDATE / DELETE / MERGE mutation site — a foreign key's or an edge constraint's cascade included, which writes through `Simulation.DeleteRowAt` / `RewriteRowAt` as a DELETE or UPDATE does — **before the write is visible to another session**: an UPDATE or DELETE captures ahead of its heap mutation, and an INSERT captures from the hook `Heap.Insert` runs under the heap's latch before it publishes the slot.
Capturing after the heap write, as it once did, left a window in which a concurrent snapshot met a changed row whose chain didn't say so.
- **INSERT**: creates chain at `newRid` marked in flight, `LiveXmin = 0` (sentinel); commit stamps `LiveXmin = commitXid` and clears the mark; rollback removes the chain entirely.
- **UPDATE**: reads the existing chain at `oldRid` (if any) to inherit its `LiveXmin` + `Head`, builds a fresh `HistoricalVersion { Payload = oldPayload, Xmin = oldLiveXmin, Xmax = PendingXmax, Next = oldHead }`, creates chain at `newRid` with that HV at `Head`, marked in flight once the HV is in place.
  Commit replaces the pending Xmax with the real commit Xid, stamps `LiveXmin`, drops the abandoned old-slot chain.
  Rollback removes the new chain entirely (old chain stays).
  A second write of a row by the same unit (`RowVersionChain.PendingEntries` names the transaction's, or an auto-commit statement's, pending list) pushes nothing: the history its first write recorded — or, for a row it inserted, the absence of any — is the pre-transaction state, so a row keeps one version per committed transaction and no snapshot ever sees an intermediate one, as real keeps one version per row per transaction (probed 2026-09-28 against SQL Server 2025: three UPDATEs of one row and one of another leave two rows in `sys.dm_tran_version_store`).
  `PendingVersionEntry.PushedHistory` tells a rollback which entries pushed the pending version it must pop.
  Measured over 300 transactions each updating 50 rows twice and once more, with a snapshot open: 199 ms against 400 ms before, the chains no longer growing per UPDATE; with no snapshot open, unchanged (130 ms).
- **DELETE**: pushes the pre-delete payload as a pending HV exactly as UPDATE does, under the same one-version-per-transaction rule; commit stamps its Xmax and `LiveXmin` with the delete Xid and sets `IsDeletedLive`; rollback pops it.
  It once pushed the payload only at commit, so while the delete was in flight a snapshot walking a history-less row's chain found nothing and lost the row.

Capture is a no-op when neither flag is on for the database, when the table is a table-variable / local-temp / system table, or when the writer's iso level doesn't participate (uncovered — versioning happens for any writer when the flag is on, regardless of writer's iso).

### Pending-entries lifecycle
Each `SimulatedDbTransaction.PendingVersionEntries` accumulates captures across the tx; `Commit` hands the list to `VersionStore.FinalizePendingEntries` (one commit Xid for the whole batch, see the allocator above), `Rollback` / implicit-Dispose hands it to `VersionStore.DiscardPendingEntries` (walks each entry undoing the in-flight mark).
A rollback rewinds the heap **before** it discards the entries, a statement's as well as a transaction's: while the entries stand, a snapshot reads past the rolled-back rows to the versions they superseded, and discarding first exposed the rolled-back image as committed for the moment between.

For auto-commit DML (no active tx), `RunMutation` allocates a fresh list on `BatchContext.CurrentStatementVersionEntries`, drains on success / discards on failure — same surface as the existing per-statement undo log.
A rollback to a savepoint discards the entries written after it, as it undoes their heap writes, and leaves the in-flight marks of the rows the transaction's earlier entries still name; clearing them with the rolled-back entries showed a snapshot reader the transaction's uncommitted earlier write as committed.

### Reader-side visibility
`BatchContext.ResolveSnapshotXidForRead(table)` returns:
- `tx.SnapshotXid` (lazy-allocated at first user-table read) for SI sessions inside a transaction.
- `BatchContext.RcsiStatementSnapshotXid` (lazy-allocated at first user-table read in this statement) for default-RC sessions when `ReadCommittedSnapshot` is on.
- `null` for every other reader path (NOLOCK, default-RC without RCSI, RR, SERIALIZABLE, table variables, temp tables, system catalogs).

`BatchContext.WrapWithRowConflictChecks` consults the snapshot Xid and walks every slot once, deleted ones included (`Heap.EnumerateSlots`), resolving each through `VersionStore.ReadSnapshotSlot`: the live payload, a historical payload, or `null` (skip the row — inserted-after-snapshot or already-deleted-pre-snapshot).
The slot and its chain are read at different moments, so the pair is checked against the heap's write sequence and the slot read again when a write landed between; the capture-before-write and rewind-before-discard orders above are what make a pair read inside one sequence consistent.
The seek path (`MaterializeSnapshotCandidates`) resolves its candidates and its sweep of `RowVersions` the same way.
Walking live rows first and deleted slots in a second pass, as the scan once did, counted a row twice when its delete committed between the passes and lost it when the delete rolled back; a SNAPSHOT transaction reading one table twice under eight concurrent writers saw its count move.

### Update-conflict detection (Msg 3960)
`VersionStore.CheckSnapshotUpdateConflict(batch, table, rid)` runs at the top of `CommitUpdate` and `CommitDelete` when the writer's iso is Snapshot.
Raises **Msg 3960** verbatim (`Snapshot isolation transaction aborted due to update conflict. You cannot use snapshot isolation to access table '<schema>.<table>' directly or indirectly in database '<db>' to update, delete, or insert the row that has been modified or deleted by another transaction. Retry the transaction or change the isolation level for the update/delete statement.` Cls 16) when the chain at the target Rid shows `LiveXmin > snapshotXid` or another session's write in flight — state 2 for a table with a clustered index, 6 for a heap (probed 2026-09-28 against SQL Server 2025).
Auto-rolls back the SI transaction before throwing (probe-confirmed `@@TRANCOUNT = 0` in the CATCH block).
A row another transaction changed so that the live row no longer matches the WHERE — its key moved, or the column the predicate reads — is a conflict too when the version the snapshot sees matches: the writer's pre-flight judges it by that version (`VersionStore.ResolveChangedLiveSlotForSnapshot`), as the next section's does a deleted row (probed 2026-09-28 against SQL Server 2025).

### Tombstoned-slot snapshot resolution
A deleted slot whose pre-delete payload is still visible at the snapshot resolves through `VersionStore.ResolveTombstonedSlotForSnapshot`, at both sites — readers (the slot walk above) and writers (`Simulation.CheckSnapshotConflictOnTombstonedRows`, called at the top of UPDATE / DELETE before the affected-rows mutation loop).
The writer-side scan decodes each candidate, evaluates the WHERE predicate against it, and raises Msg 3960 + auto-rolls back if WHERE matches a tombstoned-but-visible row.
A chain whose address the heap no longer has — its page trimmed or truncated away — is swept after the slot walk.

### MVCC observability

Three DMVs cover version-store state, with column shapes probe-confirmed against SQL Server 2025 so existing diagnostic queries port unchanged:

- **`sys.dm_tran_version_store`**: one row per finalized `HistoricalVersion` across every per-table chain.
  Columns: `transaction_sequence_num` (= HV.Xmax, the commit Xid that retired the version), `version_sequence_num` (per-tx sub-sequence synthesized at enumeration time), `database_id`, `rowset_id` (= table.ObjectId), `status` (0), `min_length_in_bytes` / `record_length_first_part_in_bytes` (= payload byte count), `record_image_first_part` (raw payload bytes), `record_length_second_part_in_bytes` (0) / `record_image_second_part` (NULL — payloads always fit in the first part since the simulator stores them as a single `byte[]`).
  Pending HVs (Xmax = `VersionStore.PendingXmax`) excluded.
- **`sys.dm_tran_version_store_space_usage`**: one row per database aggregating payload bytes — `reserved_page_count` = ceil(bytes / 8192) approximates the buffer-pool figure that real SQL Server reports, `reserved_space_kb` = ceil(bytes / 1024).
  Always yields one row (matches probe — empty stores show as `0` not row-empty).
- **`sys.dm_tran_active_snapshot_database_transactions`**: one row per active SI tx with `tx.SnapshotXid != null`.
- **`sys.dm_tran_active_transactions`** / **`sys.dm_tran_session_transactions`** / **`sys.dm_tran_current_transaction`** (probed 2026-09-25 against SQL Server 2025): each session's user transaction under its `CURRENT_TRANSACTION_ID()`, named as its outermost BEGIN named it (`user_transaction` otherwise), plus the querying statement's autocommit transaction, named for the statement and read-only for a SELECT.
  A nested BEGIN still reads `open_transaction_count` 1, as real's does; `dm_tran_current_transaction` reports a SNAPSHOT transaction's stamp and the commit counter as the version sequence.
  `enlist_count` is 1 for a session running a request in its transaction and 0 for an idle one, and a transaction sessions share is listed once in `dm_tran_active_transactions` and per session in `dm_tran_session_transactions` (probed 2026-10-07 against SQL Server 2025; see [Sessions sharing a transaction](#sessions-sharing-a-transaction)).
  Not modeled yet: the descriptor's first four bytes, which count the transactions the session has begun — its autocommit ones included — where here they read 1.
  Real's system transactions (worktables, the version-store cleanup) aren't listed.
  Columns: `transaction_id` (synthesized from object hash code), `transaction_sequence_num` (= `tx.SnapshotXid`), `commit_sequence_num` (NULL — tx is still in flight), `session_id` (= `tx.connection.Spid`), `is_snapshot` (always true — RCSI per-statement snapshots aren't tracked, matching real server behavior for this DMV), `first_snapshot_sequence_num` (NULL), `max_version_chain_traversed` / `average_version_chain_traversed` / `elapsed_time_seconds` (0 — simulator doesn't instrument those).

### Version-store garbage collection

`VersionStore.RunGarbageCollection(Database)` runs at every `SimulatedDbTransaction.Commit / Rollback / Dispose`.
Walks every per-table `RowVersions` chain and drops trailing `HistoricalVersion` nodes whose `Xmax <= oldest_active_snapshot_xid` (no active SI transaction needs them anymore).
When no SI tx is in flight, the cutoff is `Simulation.CurrentTransactionCommitId` so every finalized HV becomes collectible.
Chains that lose their only HV AND aren't `IsDeletedLive` AND have no in-flight writer AND whose `LiveXmin` every active snapshot has reached get removed from the dict entirely — a row inserted since an open snapshot keeps its history-less chain, which is what hides it from that snapshot; chains with an in-flight writer are skipped (a `PendingXmax`-marked HV must not be disturbed mid-tx).
The sweep runs per table under `RowVersionsGate` and frees the dropped versions' off-row chains after leaving it; two sweeps from two committing sessions once trimmed the same chain and freed its LOB pages twice.

The oldest active Xid comes from `Simulation.ActiveSnapshotTxs`, populated at `BatchContext.ResolveSnapshotXidForRead` (first user-table read of an SI tx) and drained at tx finalization.
A transaction registers before it reads its stamp, and the sweep reads the counter before the registrations, so a sweep that misses a registration read a counter no later than that snapshot's stamp — reading the stamp first let a concurrent sweep drop the history-less chains of rows committed in between, which the snapshot then saw.
A statement's own snapshot — `READ_COMMITTED_SNAPSHOT`'s, an autocommit SNAPSHOT statement's, a memory-optimized read outside a transaction — registers the same way, on its session (`SessionToken.StatementSnapshotXid`), cleared as the statement ends; unregistered, a commit landing mid-read collected the versions the read still needed, and the read saw the new images of rows beside the old images of rows it had passed.
The off-row chains such a statement reads are held for it besides, by the statement-scoped LOB reclamation in [`heap-storage.md`](heap-storage.md#a-freed-lob-chain-waits-for-the-statements-that-could-read-it).

### Definition changes (Msg 3961)
Metadata isn't versioned, so a SNAPSHOT transaction whose snapshot predates a committed change to a table's definition can't reach the table: reading it, writing it or reading it through a view raises **Msg 3961**, which rolls the transaction back, or dooms it inside `TRY` (probed 2026-10-01 against SQL Server 2025; `VersionStore.NoteDefinitionChange` lists what counts and what doesn't).
A `NOLOCK` read goes ahead, and so does a transaction whose snapshot was taken after the change committed.
`HeapTable.DefinitionXid` carries the commit stamp of the change, drawn at its transaction's commit beside the version stamps — or at once outside a transaction — and the check sits in `BatchContext.AcquireDataLockIfApplicable`, after the table's schema lock has waited the change out.
So `TRUNCATE`, `SWITCH` and the `ALTER TABLE` rebuilds need no versioning of their own: a snapshot old enough to need the rows they replaced is refused the table.

### Known MVCC limitations
- **A redefinition inside a `SNAPSHOT` transaction runs**, where real refuses it with Msg 3964 (`Transaction failed because this DDL statement is not allowed inside a snapshot isolation transaction. …`; probed 2026-10-09 against SQL Server 2025 for an `ALTER TABLE … ADD`); which other statements real refuses that way isn't probed.
- **A failed change outside a transaction still stamps the table**: an autocommit DDL statement draws its stamp as it runs, so one that then fails keeps an older snapshot out as a committed one would.
- **Msg 3960's state 4**: real reports a conflict it meets scanning a table with a clustered index at state 4 and one it meets seeking at state 2, where the simulator, knowing no access path there, reports 2 for every table with a clustered index (probed 2026-09-28 against SQL Server 2025).
- **`sys.dm_tran_version_store` timing**: real lists a version while its writer is still in flight and keeps it until its cleanup task runs, where the simulator lists only finalized versions and collects them at commit once no snapshot needs them.

## Concurrency stress findings

A randomized harness, run outside the repo, drives 2 to 16 sessions through a seeded mix of transfers between accounts (two UPDATEs, CASE, MERGE, joined, through a view, a CTE and a partitioned view, `UPDLOCK` and isolation-level read-modify-writes, DELETE and INSERT of a key, `OUTPUT … INTO`, savepoints, cursors), readers at every level and hint, uniqueness races, identity inserts, foreign-key cascades, triggers, temporal and change-tracked tables, memory-optimized tables under SNAPSHOT, application locks, DDL (`ALTER TABLE`, index DDL, `TRUNCATE`, `SWITCH`), lock timeouts, deadlock priorities, cancels, `KILL` and abandoned connections.
It checks that the accounts' total and count are conserved and agree with a ledger written in the same transactions, every read at REPEATABLE READ or above or under a snapshot sees a consistent and repeatable total, no read but a dirty one sees an uncommitted balance, identities are issued once, unique keys and foreign keys hold, no session hangs, every deadlock rolls back exactly one victim, no error escapes as other than `SimulatedSqlException`, and no lock, waiter or version-store pin outlives its session.
A deadlock ring of 2 to 8 sessions checks that the victim is the lowest `DEADLOCK_PRIORITY` and the rest commit.
What it found is recorded where each fix lives — the key-move races under [Uncommitted keys](#a-key-deleted-and-put-back-in-one-transaction), the request queue, the lock DMVs, `KILL`, the hints under versioned and dirty reads, the version chains and statement snapshots under MVCC, the index and statistics lists a concurrent `CREATE INDEX` or `CREATE STATISTICS` changed under a read (`InvalidOperationException`, "Collection was modified") under [Table-level and schema-lock behaviors](#table-level-and-schema-lock-behaviors), and, outside this file, the temporal transaction time ([`temporal-tables.md`](temporal-tables.md)) and the cursors' committed reads, optimistic compare and schema check ([`cursors.md`](cursors.md)).


## Table-level and schema-lock behaviors

Retained at table / schema granularity:

- A table's schema lock and data lock are one `LockResource`, real's object lock, carrying Sch-S / Sch-M beside the intent and table modes.
  A statement that redefines a table or swaps its rows out — `ALTER TABLE`, `TRUNCATE`, `SWITCH` on both its tables, a clustered index's `CREATE` or any index's `DROP` — takes Sch-M on it for the statement and to the transaction's end (`BatchContext.AcquireTableRedefinitionLock`), so it waits out every transaction still holding the table's intent lock, and new readers and writers wait for it (probed 2026-10-01 and 2026-10-07 against SQL Server 2025: `LCK_M_SCH_M` on the object behind an open insert, for each).
  Taking it on the schema lock alone, as it once did, let a `TRUNCATE` swap the pages out from under an open insert, whose rollback then failed on pages that were gone; taking the two halves on two resources let the redefinition hold the schema half while it waited for the data half, so the open writer's next statement deadlocked with it, where on real that writer converts the object lock it holds and carries on while a newcomer's Sch-S waits behind the redefinition (`ObjectLockTests`).
- A nonclustered index builds under the table's S, to the transaction's end (probed 2026-10-07 against SQL Server 2025: `LCK_M_S` behind an open insert, a reader going on beside it, and the S held by a build in an open transaction), so it waits out an open writer and holds writers off while readers read on.
  Building under no lock, as it once did, changed the table's index list under a concurrent read, which failed with "Collection was modified"; the list (`HeapTable.Indexes`) is now published whole on every change, since a read may still walk it beside the build, and so is the statistics list (`HeapTable.UserStatistics`), which a `CREATE STATISTICS` changes under no blocking lock, as real's does, and a query's read changes too by auto-creating one.
- A batch compiles under Sch-S on everything it names, so a table another session holds in Sch-M holds the whole batch before any of it runs, and a lock timeout there is Msg 1222 state 56 at the statement's line with nothing run, no `CATCH` reached and an open transaction left as it was — a statement in an untaken branch included (probed 2026-10-03 and 2026-10-07 against SQL Server 2025).
  A batch whose text compiled before under the current schema runs on its kept plan, as real's cached one does: the statements ahead run and the one reading the table waits where it stands.
  The table's own Sch-M once lasted only the redefining statement, the transaction's hold being the data half's, so the walk passed it and the statements ahead ran.

- `LockResource` data carrier + `LockManager` (gate, Acquire / Release, re-entrance counting, cycle detection).
- `SchemaObject.SchemaLock` field.
- `SimulatedDbConnection.Spid` / `LockTimeoutMillis` / `CurrentExecutingThreadId` / `WaitingOnResource`.
- `Simulation.AllocateSpid()` (first user SPID = 51).
- Msg 1222 verbatim wording (Class 16), its state naming the lock that timed out: 51 for a key lock — a row of a table with a clustered index is one — 45 for a heap's row, 56 for a table or schema lock (probed 2026-09-28 against SQL Server 2025).
- Msg 1205 verbatim wording with SPID interpolation; auto-rollback of victim's tx.
- Same-thread-deadlock short-circuit.
- HOLDLOCK retain-until-tx-end semantic, over a key range where the predicate offers one and table-S otherwise, in the range mode any `UPDLOCK` / `XLOCK` alongside it names.
- NOLOCK / READ UNCOMMITTED dirty-read semantic.
