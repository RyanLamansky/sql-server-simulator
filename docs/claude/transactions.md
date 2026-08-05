# Transactions

Three entry points share one per-connection undo log: implicit (statement atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`).
A fourth arrives over the network: TDS Transaction Manager requests map onto the SqlClient-API path — see [`tds-endpoint.md`](tds-endpoint.md).

- **Statement-level atomicity**: a mutation throwing mid-execution rolls back its partial writes.
  Multi-row INSERT failing on row 3 leaves zero rows.
- **Cancel (TDS attention / `CommandTimeout` / in-process `Cancel()`) vs. an open tx**: probed against SQL Server 2025 — under the default `SET XACT_ABORT OFF` the transaction **survives** the cancel intact and usable; under `SET XACT_ABORT ON` the cancel **rolls it back** (`@@TRANCOUNT` → 0).
  The TDS endpoint applies this at the attention safe point (`SimulatedDbConnection.XactAbort` gates the rollback).
  A cancel aborts the batch at a statement boundary, so already-committed statements' effects persist and un-run statements never fire; a single in-flight statement is not interrupted inside its row loop (materialization completes first — the reaction bound noted in [`tds-endpoint.md`](tds-endpoint.md#mid-stream-attention-cancel)), so it is not partial-rolled-back the way a mid-statement *error* is.
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` commits; `ROLLBACK` zeroes `TranCount` and walks the whole log.
  `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx.
  Parallel `BeginTransaction` → `InvalidOperationException`.
  `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both advance through rollback.
  (A rolled-back INSERT's off-row LOB chain + heap bytes are reclaimed — rollback is terminal, so an uncommitted insert is invisible to every snapshot.)
- **Temp-table CREATE/DROP participates in the log** via `TempTableCreation` / `TempTableRemoval` `UndoEntry` subtypes.
  Regular CREATE/DROP TABLE is NOT logged — asymmetry in [`temp-tables.md`](temp-tables.md).
- Locking + MVCC: full 8-mode matrix, row-X writers + row-mode readers per hints/iso, RR/SER/UPDLOCK/XLOCK/TABLOCK/HOLDLOCK/REPEATABLEREAD/NOLOCK/READPAST hints, escalation at 5000 row-locks, Msg 1205 deadlock / Msg 1222 timeout, SNAPSHOT + RCSI (version chains + GC + DMVs).
  See [`locking.md`](locking.md).
- Table-variable mutations use a statement-only undo log disjoint from the tx-scoped one, so `ROLLBACK TRAN` skips `@t` (the `CurrentTableVarUndoLog` / `CurrentUndoLog` split on `BatchContext`).
- **One transaction spans every database it wrote to.**
  The undo log is per-connection and its entries reference their `Heap` directly, so a write through a three-part name rolls back with the rest of the transaction with no extra routing; `@@TRANCOUNT` / `XACT_STATE()` never reflect the crossing (probe-confirmed).
  What *is* per-database — the rowversion counter, the version store's commit-Xid counter, trigger dispatch — follows the target table rather than the session; see the cross-database-writes section of [`schemas.md`](schemas.md#cross-database-writes).

## The transaction-aborting error class

Almost every error is statement-aborting: it ends its statement, leaves `@@TRANCOUNT` where it was, and a `BEGIN TRY` frame catches it.
A small class is different — it rolls the session's whole transaction stack back before anyone sees it, refuses to be caught, and takes the rest of the batch with it.
`SimulatedSqlException.AbortsTransaction` marks a factory as belonging to it; the statement dispatcher rolls the session's transaction back at the same point it already does for a deadlock victim (class 13), and skips the TRY-frame arm.

**Msg 8728** (a RANGE-framed window ordering by a MAX-typed expression — see [`query.md`](query.md#range-frame-order-by-msg-8728)) is the modeled member.
Probed against SQL Server 2025 (2026-08-05), with a transaction opened in an earlier batch:

- `@@TRANCOUNT` 1 → 0 and `XACT_STATE()` 1 → 0, and a row inserted inside the transaction is gone afterwards.
- `@@TRANCOUNT` 2 → **0**, not 1 — the whole stack, not one level.
- A surrounding `BEGIN TRY` never reaches its `CATCH`, and a `PRINT` after the failing statement in the same batch never runs.
- The neighbours all leave the transaction standing at 1: Msg 8134 (divide by zero), 208 (invalid object), 207 (invalid column), 306 (legacy LOB sorted), 4104 (multi-part identifier), 8120 (not in GROUP BY), 4194 (RANGE numeric offset).

Real settles Msg 8728 while *compiling*, so on real it also fires inside a branch the batch never takes (`IF 1 = 0 BEGIN <the query> END` raises and the batch never starts).
The simulator raises it while parsing the statement, which reaches the same place for the shapes that matter and additionally fires under the dispatch loop's skip mode.

Not modeled: `BEGIN DISTRIBUTED TRANSACTION` → `NotSupportedException` at dispatch; `BEGIN TRANSACTION <name> WITH MARK 'm'` → Msg 319 at parse (bare named transactions ship).
