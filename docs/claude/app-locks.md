# Application locks

`sp_getapplock` / `sp_releaseapplock` / `APPLOCK_MODE` / `APPLOCK_TEST`: cooperative named locks over the shared `LockManager`.
Every behavior below was probe-confirmed against SQL Server 2025.
This surface is what makes **EF Core 9/10's `Database.Migrate()` work end-to-end** — the migrator unconditionally wraps its history-check + apply sequence in `sp_getapplock @Resource = '__EFMigrationsLock', @LockOwner = 'Session', @LockMode = 'Exclusive'` (emitted as literal text, return value read back through `EXEC @result = …; SELECT @result`), without this surface, `Migrate()` cannot complete.

## Architecture

- **One `LockResource` per (database-principal id, resource name)**, interned in `Database.ApplicationLocks` under the dictionary's own lock (`GetOrCreateApplicationLock`); entries are never removed.
  The `LockManager` remains the single conflict authority — Session-owned and Transaction-owned holds on one resource conflict correctly across connections because they share the resource.
- **Per-owner ledgers** carry the identity the owner-scoped views need: `SimulatedDbConnection.SessionAppLocks` and `SimulatedDbTransaction.TransactionAppLocks`, one `AppLockHold` entry per successful acquire (so entry count = outstanding reference count).
  `APPLOCK_MODE`, `sp_releaseapplock`'s not-held check, and the `sys.dm_tran_locks` APPLICATION rows read the ledgers; the manager holds enforce conflicts.
- **Transaction-owned acquires ride `SimulatedDbTransaction.HeldLocks`** (the generic tx-scoped release list) for their manager release at COMMIT / ROLLBACK; `TransactionAppLocks` clears alongside in `ReleaseAllLocks`.
  An explicit `sp_releaseapplock` retires one matching `HeldLocks` entry so transaction end doesn't double-release.
  Session-owned locks release at `Close()` / `Dispose()` (`ReleaseSessionAppLocks`, idempotent).
- **`LockManager.TryAcquire`** is the non-throwing acquire core added for this feature (the throwing `Acquire` wraps it): outcomes Granted / GrantedAfterWait / TimedOut / Deadlocked map to sp_getapplock's return codes.
  It reuses the full existing machinery — compatibility matrix, re-entrance counting, same-thread + cross-thread deadlock detection, gate waits.
- Modes map 1:1 onto existing `LockMode` members: Shared, Update, IntentShared, IntentExclusive, Exclusive.
  Mode and owner strings parse case-insensitively (probe: `'exclusive'` grants and reports `Exclusive`).
- Dispatch: two branches in `Simulation.Exec.cs` beside `sp_set_session_context`, receiving `returnCodeVar` so `EXEC @rc = sp_getapplock …` writes the code into the batch's variable slot (`Simulation.AppLock.cs`).
  Scalars register in `Expression.ResolveBuiltIn` (`Parser/Expressions/AppLockFunctions.cs`).

## Return codes vs raised errors (the load-bearing asymmetry)

`sp_getapplock` **returns codes, never raises, for lock arbitration**: 0 granted immediately, 1 granted after a wait, -1 timeout, **-3 deadlock victim — no Msg 1205 exception** (unlike ordinary lock deadlocks; probe-confirmed the victim's connection sees only the return code).
Because -3 is a return code rather than a rollback, the victim's *other* holds stay live — so the surviving side of the deadlock isn't granted; it keeps waiting and returns **-1** when its own timeout elapses (probe- and test-pinned: the pair of codes is exactly {-3, -1}).
-999 covers validation: unrecognized `@LockMode` / `@LockOwner` strings, Transaction owner with no active transaction, and a **missing** `@Resource`.

Raised errors are reserved for:

| Condition | Error |
|---|---|
| explicit NULL `@Resource` (either proc) | Msg 1224 `An invalid application lock resource was passed to xp_userlock.` (State 5) |
| `@LockTimeout < -1` | Msg 1227 (State 2) |
| release of a not-held resource | Msg 1223 `Cannot release the application lock (Database Principal: '<p>', Resource: '<r>') because it is not currently held.` |
| missing `@LockMode` | Msg 201 (binding-time, precedes the body's -999 checks) |
| unknown `@DbPrincipal` | Msg 1202 `The database-principal '<name>' does not exist or user is not a member.` |

The functions differ from the procs on the same inputs: invalid mode string → Msg 1225 (`applock_test`), invalid owner string → Msg 1226 (function name interpolated), NULL principal/resource/mode → Msg 8116 with the argument index, Transaction owner (explicit or NULL-defaulted) outside a tx → Msg 3918.

## Semantics

- **Reference counting**: N acquires need N releases; each `sp_releaseapplock` decrements one.
  After a same-owner mode conversion (Shared then Exclusive → rc 0, `APPLOCK_MODE` reports the stronger), both holds are outstanding and release strongest-first.
- **`APPLOCK_MODE(principal, resource, owner)`** → `nvarchar(32)`: the calling session's given owner's strongest held mode as verbatim strings `NoLock` / `Shared` / `Update` / `IntentShared` / `IntentExclusive` / `Exclusive`.
  Owner-scoped: the same connection's lock under the *other* owner reads `NoLock`.
- **`APPLOCK_TEST(principal, resource, mode, owner)`** → `smallint` 1/0: could the caller acquire now — a re-entrant grant over its own holds counts as 1.
- **Resource names**: case-sensitive, trailing-space-significant, silently truncated to 255 characters (names sharing a 255-char prefix collide; no error at any length).
- **Principals**: part of the lock identity — a lock under `dbo` neither conflicts with nor is visible to a same-named lock under `public`.
  Default `public`.
  `@LockTimeout` defaults to the session `LOCK_TIMEOUT` (`SET LOCK_TIMEOUT` honored; NULL timeout = default); `@LockOwner` defaults to `Transaction`.
  Empty-string resource is a legal distinct name.
- **Lifecycle**: transaction-owned locks release on COMMIT and full ROLLBACK but **survive rollback-to-savepoint**; session-owned survive any transactions and release at session end.
- **`sys.dm_tran_locks`**: APPLICATION rows with `resource_description` = `<principal-id>:[<name>]:(<8-hex-hash>)` and abbreviated `request_mode` (`S`/`U`/`X`/`IS`/`IX`).

## Divergences

- The `resource_description` hash is FNV-1a-32 over the name — the shape matches, the hash value won't byte-match real SQL Server's undocumented hash.
- No principal-membership gate: any existing database principal passes `@DbPrincipal`; real SQL Server additionally requires the caller to be a member of it.
- After a mode conversion, `APPLOCK_MODE` between the two releases reports whichever converted-pair hold remains (strongest-first release order); the real server's intermediate report wasn't probed.
- The shipped `dm_tran_locks` column subset has no `request_owner_type` column (seven-column shape), so SESSION-vs-TRANSACTION ownership isn't visible there.
