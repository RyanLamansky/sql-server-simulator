# `SimulatedDbDataReader` client surface

Full `DbDataReader` contract. Typed accessors read `SqlValue` via the cursor indexer, unwrap via `As*` (no boxing); NULL → `SqlNullValueException` (SqlClient parity).

- `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date at midnight, `Kind=Unspecified`).
- A **`datetime` rounds to whole milliseconds at the ADO.NET boundary** (`DateTimeSqlType.RoundToClientMilliseconds`, in `GetDateTime` + `SqlValue.ToObject` — the latter covers `GetValue`/`GetFieldValue`/output-param writeback) matching SqlClient's `.000`/`.003`/`.007`; the engine keeps full 1/300-second resolution internally, so only the client surface rounds. (The TDS endpoint transfers the full internal resolution and lets real SqlClient do the same client-side rounding — see [`tds-endpoint.md`](tds-endpoint.md).)
- `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` / `TimeOnly`-over-`Time`.
- `GetOrdinal` two-pass linear (case-sensitive then -insensitive, SqlClient precedence). `HasRows` sticky. `GetChar(int)` always raises `InvalidCastException`.

## Batch-error surfacing (positional)

The reader consumes the unified continue-on-error outcome stream (see [`control-flow.md`](control-flow.md)), so a mid-batch statement error is a `SimulatedErrorOutcome` in the stream rather than a throw. `AdvanceToNextResult` (the constructor's and `NextResult`'s shared step) skips pure `SimulatedNonQuery` outcomes and stops on either a `SimulatedQueryResult` or a `SimulatedErrorOutcome`, mirroring SqlClient's positional error model:

- **Row-returning error** (`SimulatedErrorOutcome.RowReturning` — the failed statement was a SELECT / VALUES, which real SQL Server frames with COLMETADATA before the error): the reader advances *onto* the failed statement (the advance returns `true`) and the first `Read` throws, via an internal `ErrorCursor` whose first `MoveNext` throws and then reports no rows. The reader **survives** — a following `NextResult` reaches the next result set and reads clean. (`Read`-throws, reader-survives is the same shape as a SELECT that errors mid-scan, except materialization means zero partial rows precede the throw — see the divergence below.)
- **Non-row-returning error** (INSERT / UPDATE / DELETE / DDL — no result-set envelope): the error throws *eagerly* on the advance itself, so `ExecuteReader` (the constructor's advance) or `NextResult` throws rather than a later `Read`. This matches SqlClient surfacing an error token that no COLMETADATA precedes, and is what lets EF Core's no-OUTPUT modification batches — which never call `Read` — observe a failed write.

`ExecuteNonQuery` / `ExecuteScalar` bypass this positional model: they drain the whole outcome stream and aggregate every error into one `SimulatedSqlException` thrown at completion (`ExecuteScalar` returns the first result set's first value only when the batch had no error).

**Dispose = statement-level drain**: closing the reader executes the batch's remaining statements (side effects persist) and swallows their errors — a disposed reader never throws. Row-level pull *inside* the statement the reader was parked on stays abandoned (unchanged; a non-draining reader still doesn't run a SELECT iterator's post-yield code — see [`plan-cache.md`](plan-cache.md)).

## In-process MARS (overlapping readers)

The in-process `SimulatedDbConnection` has no wire and no "one open reader" enforcement: a second command — or a second open reader — while a reader is live **just works**, the permissive superset EF Core's lazy loading needs (iterate a parent query, touch a navigation per row). Probe-confirmed safe: a nested reader-per-row loop and two interleaved readers both return correct results, because a query result materializes before it streams, so overlapping enumeration never races shared session state (no two live engine iterations, no transaction/identity/plan-cache stomp). This mirrors a MARS-enabled wire connection (`MultipleActiveResultSets=True`), where the endpoint negotiates MARS and serializes execution — see [`tds-endpoint.md`](tds-endpoint.md#mars-multiple-active-result-sets). A non-MARS wire connection still rejects the overlap client-side in SqlClient, so the in-process surface is deliberately more permissive than a bare `SqlConnection`.

## Divergence

**`GetBytes` / `GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Per-call observation matches SqlClient; the streaming-memory guarantee doesn't.
