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

Not modeled: `BEGIN DISTRIBUTED TRANSACTION` → `NotSupportedException` at dispatch.

## `WITH MARK`

`BEGIN TRAN <name> WITH MARK ['description']` labels a point in the transaction log for a point-in-time restore to name.
There is no log here, so the description is parsed and discarded — the name may be a variable, the description a literal or a variable, and both `WITH MARK 'm'` and the bare `WITH MARK` are accepted.
Two consequences *are* observable and both are modeled:

- The transaction has to be **named**: an unnamed `BEGIN TRANSACTION WITH MARK` is **Msg 3901** (`The transaction name must be specified when it is used with the mark option.`) raised at run time, so `@@TRANCOUNT` stays where it was rather than the transaction opening anyway.
- A second `WITH MARK` under a transaction that already carries one raises the severity-10 **Msg 3920** (`The WITH MARK option only applies to the first BEGIN TRAN WITH MARK statement. The option is ignored.`) through the `InfoMessage` surface, at class 0 on the wire.
  Real emits it only for the second *marked* BEGIN — a `WITH MARK` nested under an unmarked transaction is silent — so `SimulatedDbTransaction.IsMarked` is what the check reads.

Everything else about a marked transaction is an ordinary one: `@@TRANCOUNT` nests the same way, savepoints and `ROLLBACK` behave the same, and nothing reads the mark back.

## `SET XACT_ABORT`

The option generalizes that plumbing conditionally, and the two shapes are **not** the same: Msg 8728 refuses a `BEGIN TRY` frame outright, while an XACT_ABORT-promoted error is caught normally and leaves the transaction *doomed* rather than rolled back.
`SimulatedDbConnection.XactAbort` holds the setting; `Simulation.ApplyXactAbortPromotion` applies it once, at the innermost dispatch frame, and marks the exception so an outer frame re-raising it doesn't ask twice.
Probed against SQL Server 2025 (2026-08-06).

**Uncaught**, the promotion covers the statement-terminating run-time family — Msg 245, 515, 547, 1222, 2601 / 2627, 2628, 8115, 8134, deferred-name 208, and an uncaught `THROW`:

| | `XACT_ABORT OFF` | `XACT_ABORT ON` |
| --- | --- | --- |
| the rest of the batch | runs (bar the batch-aborting name-resolution family) | never runs |
| `@@TRANCOUNT` | unchanged | 0, whatever the depth — 2 reads 0, not 1 |
| the transaction's writes | stand | rolled back |
| `XACT_STATE()` | 1 | 0 |

The batch ends even with no transaction open — the option is not conditional on one.
An error raised inside a procedure body ends the **calling** batch, not just the body.

**`RAISERROR` is the exemption**, at every severity and with or without `WITH LOG`: uncaught under the option it reports, the batch runs on, and the transaction stays committable at `XACT_STATE()` 1.
`THROW` is promoted like everything else, which is the observable split between the two.
`SimulatedSqlException.RaisedByRaiserror` marks the factory.

**Caught by a `TRY` frame**, every error including `RAISERROR` behaves the same way instead: the `CATCH` runs, the batch carries on past `END CATCH`, `@@TRANCOUNT` is untouched — and the transaction is doomed, `XACT_STATE()` reading `-1` (`SimulatedDbTransaction.Doomed`).
Whether an error rolls back or dooms is a question about the **whole session stack**, not one batch frame: a procedure with no `TRY` of its own, called from inside the caller's, dooms.
`SimulatedDbConnection.OpenTryFrames` is the session-wide counter that answers it.

A doomed transaction then:

- refuses any statement that writes to the log with **Msg 3930** class 16 state 1 (*"The current transaction cannot be committed and cannot support operations that write to the log file. Roll back the transaction."*) — DML, object DDL, `SAVE TRANSACTION` and `COMMIT` alike, while a `SELECT` and a `DECLARE` complete normally.
  Msg 3930 is itself batch-aborting and rolls the transaction back, and a nested `TRY` can catch it.
- is rolled back at end of batch with **Msg 3998** class 16 state 1 (*"Uncommittable transaction is detected at the end of the batch. The transaction is rolled back."*), emitted after the batch's own results.
- is cleared only by `ROLLBACK`, after which the batch runs on normally.

**Scoping.** Unlike the six ANSI toggles (which a module body ignores), `SET XACT_ABORT` inside a procedure, trigger or dynamic-SQL body takes effect for that body and reverts when it returns; a body with no `SET` of its own inherits the caller's.
`SimulatedDbConnection.SessionOptionScope` captures and restores it — together with `ROWCOUNT` and `DATEFIRST`, which scope identically — at the three invocation seams and around a parameterized ad-hoc command.
`@@OPTIONS & 16384` reports the setting (`OptionsExpression`); a fresh session reads 5432 with the bit clear under SqlClient and 5176 under sqlcmd, whose difference is `QUOTED_IDENTIFIER` alone.

The option also decides whether a client attention rolls an open transaction back — see the cancel bullet above.

**Divergences.** Msg 3930's write gate covers DML (through `RunMutation`) and the `CREATE` / `ALTER` / `DROP` / `TRUNCATE` verbs; a `GRANT`, an `sp_rename` or an extended-property write inside a doomed transaction still runs.
Msg 245 is transaction-aborting on real *without* the option — a conversion failure rolls the transaction back and reads `XACT_STATE() = -1` from a `CATCH` even under `XACT_ABORT OFF` — where the simulator treats it as statement-terminating like its neighbours until the option is on.
