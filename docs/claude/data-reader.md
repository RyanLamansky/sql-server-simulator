# `SimulatedDbDataReader` client surface

Full `DbDataReader` contract. Typed accessors read `SqlValue` via the cursor indexer, unwrap via `As*` (no boxing); NULL → `SqlNullValueException` (SqlClient parity).

- `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date at midnight, `Kind=Unspecified`).
- A **`datetime` rounds to whole milliseconds at the ADO.NET boundary** (`DateTimeSqlType.RoundToClientMilliseconds`, in `GetDateTime` + `SqlValue.ToObject` — the latter covers `GetValue`/`GetFieldValue`/output-param writeback) matching SqlClient's `.000`/`.003`/`.007`; the engine keeps full 1/300-second resolution internally, so only the client surface rounds. (The TDS endpoint transfers the full internal resolution and lets real SqlClient do the same client-side rounding — see [`tds-endpoint.md`](tds-endpoint.md).)
- `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` / `TimeOnly`-over-`Time`.
- `GetOrdinal` two-pass linear (case-sensitive then -insensitive, SqlClient precedence). `HasRows` sticky. `GetChar(int)` always raises `InvalidCastException`.

## Divergence

**`GetBytes` / `GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Per-call observation matches SqlClient; the streaming-memory guarantee doesn't.
