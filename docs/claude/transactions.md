# Transactions

Three entry points share one per-connection undo log: implicit (statement atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`). A fourth arrives over the network: TDS Transaction Manager requests map onto the SqlClient-API path — see [`tds-endpoint.md`](tds-endpoint.md).

- **Statement-level atomicity**: a mutation throwing mid-execution rolls back its partial writes. Multi-row INSERT failing on row 3 leaves zero rows.
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` commits; `ROLLBACK` zeroes `TranCount` and walks the whole log. `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx. Parallel `BeginTransaction` → `InvalidOperationException`. `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both advance through rollback. (A rolled-back INSERT's off-row LOB chain + heap bytes are reclaimed — rollback is terminal, so an uncommitted insert is invisible to every snapshot.)
- **Temp-table CREATE/DROP participates in the log** via `TempTableCreation` / `TempTableRemoval` `UndoEntry` subtypes. Regular CREATE/DROP TABLE is NOT logged — asymmetry in [`temp-tables.md`](temp-tables.md).
- Locking + MVCC: full 8-mode matrix, row-X writers + row-mode readers per hints/iso, RR/SER/UPDLOCK/XLOCK/TABLOCK/HOLDLOCK/REPEATABLEREAD/NOLOCK/READPAST hints, escalation at 5000 row-locks, Msg 1205 deadlock / Msg 1222 timeout, SNAPSHOT + RCSI (version chains + GC + DMVs). See [`locking.md`](locking.md).
- Table-variable mutations use a statement-only undo log disjoint from the tx-scoped one, so `ROLLBACK TRAN` skips `@t` (the `CurrentTableVarUndoLog` / `CurrentUndoLog` split on `BatchContext`).

Not modeled: `BEGIN DISTRIBUTED TRANSACTION` → `NotSupportedException` at dispatch; `BEGIN TRANSACTION <name> WITH MARK 'm'` → Msg 319 at parse (bare named transactions ship).
