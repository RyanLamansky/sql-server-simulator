# `SimulatedDbDataReader` client surface

Full `DbDataReader` contract.
Typed accessors read `SqlValue` via the cursor indexer, unwrap via `As*` (no boxing); NULL → `SqlNullValueException` (SqlClient parity).

- `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date at midnight, `Kind=Unspecified`).
- A **`datetime` rounds to whole milliseconds at the ADO.NET boundary** (`DateTimeSqlType.RoundToClientMilliseconds`, in `GetDateTime` + `SqlValue.ToObject` — the latter covers `GetValue`/`GetFieldValue`/output-param writeback) matching SqlClient's `.000`/`.003`/`.007`; the engine keeps full 1/300-second resolution internally, so only the client surface rounds.
  (The TDS endpoint transfers the full internal resolution and lets real SqlClient do the same client-side rounding — see [`tds-endpoint.md`](tds-endpoint.md).)
- `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` / `TimeOnly`-over-`Time`.
- `GetOrdinal` two-pass linear (case-sensitive then -insensitive, SqlClient precedence).
  `HasRows` sticky.
  `GetChar(int)` always raises `InvalidCastException`.

## `RecordsAffected`

Rows the batch's statements **changed**, summed — never rows a SELECT returned.
The number is the same one `ExecuteNonQuery` reports for the same batch, in every shape probed against SQL Server 2025 (2026-08-03): DDL, each DML verb, SELECT, `SELECT INTO`, MERGE, `OUTPUT`-to-client DML, mixed batches, procedures, `SET NOCOUNT`.
`-1` means no statement contributed a count.

What contributes:

- **DML contributes its rows-affected** — `INSERT` / `UPDATE` / `DELETE` / `MERGE`, a `WHILE` body's every iteration, a `SELECT … INTO` (it writes rows), and a statement whose `OUTPUT` clause returns rows to the client (tabular, but its count is still a rows-affected count).
  A statement matching nothing contributes `0`, which is distinct from `-1`.
- **A SELECT contributes nothing**, however many rows it returned — including the assignment-only `SELECT @x = col FROM t`, which reads rows without returning them, and a cursor `FETCH`.
- **DDL, `SET`, `DECLARE`, `PRINT` and an un-taken branch contribute nothing.**
- **`SET NOCOUNT ON` suppresses the contribution** of every statement that runs while it is on, whatever the kind — the count is recorded per statement rather than read when the client consumes the outcome, because a procedure body's `SET NOCOUNT` reverts at the body's exit, before the caller pulls what the body produced.

`SET NOCOUNT`'s **scope** decides how far the suppression reaches, and real's is narrower than "the session" in four cases (probe-confirmed by running a counting statement on the same connection afterward):

| where `SET NOCOUNT ON` runs | reaches the next command? |
|---|---|
| a plain batch (no parameters) | yes — session state, and it outlives the batch |
| a command carrying parameters | no — SqlClient sends one as `sp_executesql`, whose SET options revert with the scope |
| `EXEC('…')` / `sp_executesql` | no |
| a procedure body | no |
| a trigger body | no — and the firing statement keeps its own count |

The simulator restores the flag at each of those scope exits, next to the `TEXTSIZE` / `QUOTED_IDENTIFIER` restores already there; the in-process front door treats a parameterized command as the ad-hoc scope SqlClient turns it into, which is what EF Core's modification batches (they open with `SET NOCOUNT ON`) depend on.
The wider SET-option set is not scoped that way for a parameterized command — only `NOCOUNT` is.

The value accumulates as the reader is advanced and is final once it is closed: a statement ahead of the current result set has already contributed, one behind it has not yet, and `Close` / `Dispose` runs the rest of the batch and folds in what it counted (`Close` is overridden for exactly that reason — `DbDataReader`'s base `Close` is a no-op).

The wire renderer answers the same question with the two DONE-token fields real uses, both captured off SQL Server 2025's wire.
`CurCmd` names the kind of statement that produced the token and is what a client keys on to leave a SELECT's count out of the sum — real tags a plain SELECT and a cursor `FETCH` `0x00C1`, and `SELECT INTO` / `INSERT` / `DELETE` / `UPDATE` / `MERGE` their own kinds (`0x00C2` / `0x00C3` / `0x00C4` / `0x00C5` / `0x0117`).
`DONE_COUNT` says whether there is a count at all, and NOCOUNT clears the flag while leaving the row count in the token.
The simulator classifies SELECT and leaves every other kind `0`; see [`tds-endpoint.md`](tds-endpoint.md).

### Divergences

- **Mid-stream timing after a result set is exhausted.**
  Real's client reads tokens ahead to the next result-set boundary, so once `Read` has returned false the counts of the *following* non-row-returning statements are already in; the simulator folds them on the `NextResult` that steps over them.
  A batch's final value agrees, and a single-statement batch is unaffected.
- **A DML statement's `OUTPUT` count lands early.**
  The simulator materializes a result before streaming it, so `RecordsAffected` reports an `INSERT … OUTPUT`'s count the moment the reader parks on it; real learns it from the DONE that follows the rows, and reads `-1` until they are drained.
- **A trigger's own DML doesn't contribute.**
  Real counts the writes a trigger body performs into the firing statement's total (an INSERT firing a trigger that writes two rows reports 3); the simulator reports the firing statement's own count alone.
  Both front doors agree with each other — the counts never reach the outcome stream.

## Batch-error surfacing (positional)

The reader consumes the unified continue-on-error outcome stream (see [`control-flow.md`](control-flow.md)), so a mid-batch statement error is a `SimulatedErrorOutcome` in the stream rather than a throw.
`AdvanceToNextResult` (the constructor's and `NextResult`'s shared step) skips pure `SimulatedNonQuery` outcomes and stops on either a `SimulatedQueryResult` or a `SimulatedErrorOutcome`, mirroring SqlClient's positional error model:

- **Row-returning error** (`SimulatedErrorOutcome.RowReturning` — the failed statement was a SELECT / VALUES, which real SQL Server frames with COLMETADATA before the error): the reader advances *onto* the failed statement (the advance returns `true`) and the first `Read` throws, via an internal `ErrorCursor` whose first `MoveNext` throws and then reports no rows.
  The reader **survives** — a following `NextResult` reaches the next result set and reads clean.
  (`Read`-throws, reader-survives is the same shape as a SELECT that errors mid-scan, except materialization means zero partial rows precede the throw — see the divergence below.)
- **Non-row-returning error** (INSERT / UPDATE / DELETE / DDL — no result-set envelope): the error throws *eagerly* on the advance itself, so `ExecuteReader` (the constructor's advance) or `NextResult` throws rather than a later `Read`.
  This matches SqlClient surfacing an error token that no COLMETADATA precedes, and is what lets EF Core's no-OUTPUT modification batches — which never call `Read` — observe a failed write.

`ExecuteNonQuery` / `ExecuteScalar` bypass this positional model: they drain the whole outcome stream and aggregate every error into one `SimulatedSqlException` thrown at completion (`ExecuteScalar` returns the first result set's first value only when the batch had no error).

**Dispose = statement-level drain**: closing the reader executes the batch's remaining statements (side effects persist) and swallows their errors — a disposed reader never throws.
Row-level pull *inside* the statement the reader was parked on stays abandoned (unchanged; a non-draining reader still doesn't run a SELECT iterator's post-yield code — see [`plan-cache.md`](plan-cache.md)).

## In-process MARS (overlapping readers)

The in-process `SimulatedDbConnection` has no wire and no "one open reader" enforcement: a second command — or a second open reader — while a reader is live **just works**, the permissive superset EF Core's lazy loading needs (iterate a parent query, touch a navigation per row).
Probe-confirmed safe: a nested reader-per-row loop and two interleaved readers both return correct results, because a query result materializes before it streams, so overlapping enumeration never races shared session state (no two live engine iterations, no transaction/identity/plan-cache stomp).
This mirrors a MARS-enabled wire connection (`MultipleActiveResultSets=True`), where the endpoint negotiates MARS and serializes execution — see [`tds-endpoint.md`](tds-endpoint.md#mars-multiple-active-result-sets).
A non-MARS wire connection still rejects the overlap client-side in SqlClient, so the in-process surface is deliberately more permissive than a bare `SqlConnection`.

## Divergence

**`GetBytes` / `GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer.
Per-call observation matches SqlClient; the streaming-memory guarantee doesn't.
