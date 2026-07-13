# TDS network endpoint

`Simulation.ListenAsync(int port = 1433, CancellationToken cancellationToken = default)` opens a loopback TCP endpoint speaking TDS 7.4, so unmodified SQL Server clients (SqlClient, JDBC, sqlcmd, SSMS) reach the simulator with only a connection-string change. Returns `Task<SimulatedNetworkListener>`; `port: 0` binds an OS-assigned ephemeral port (read `Port`), the right shape for parallel tests. Port-in-use surfaces as the raw `SocketException`. The listener binds IPv4 loopback plus best-effort IPv6 loopback on the same port.

Connection-string requirements: `TrustServerCertificate=true` (the endpoint presents an ephemeral in-memory self-signed cert generated per listener) and any credentials (parsed, not validated — enforcement is backlogged with the Msg 18456 failure path reserved). `Encrypt=Strict` (TDS 8.0) is not supported; default/`Mandatory`/`Optional` all negotiate to full encryption via `ENCRYPT_REQ`. A client that cannot do TLS at all (`ENCRYPT_NOT_SUP`) is disconnected after the prelogin response — there is no plaintext mode.

`SimulatedNetworkListener` is `IDisposable`/`IAsyncDisposable`: disposal is aggressive and waits for nothing — listening sockets close, each session's backing `SimulatedDbConnection` is disposed with normal session teardown semantics (transactions roll back, temp tables drop), and mid-query clients see an abrupt connection reset. `DisposeAsync` is the same teardown returning a completed task.

## Architecture

Everything lives in `Network/` (internal) except the public `SimulatedNetworkListener` (root) and `Simulation.Listen.cs` (the `ListenAsync` partial). One task per accepted socket runs `TdsSession.RunAsync`: prelogin → TLS handshake → LOGIN7 → batch loop. The session maps 1:1 onto a `SimulatedDbConnection`; execution flows through `Simulation.CreateResultSetsForCommand`, which yields both result sets and per-statement `RecordsAffected` — the TDS layer is a pure translator and touches no engine code.

- `TdsPacketTransport` — packet framing both directions: reassembles inbound packet sequences into `TdsMessage` (EOM-terminated), stamps outbound headers (type 0x04, SPID truncated to 16 bits, incrementing packet id). The stream it rides is swapped from the raw `NetworkStream` to the `SslStream` after the handshake.
- `TlsHandshakeFramingStream` — the TDS 7.x TLS seam: handshake records travel wrapped in PRELOGIN-type packets, so this shim strips/adds packet headers under `SslStream` during `AuthenticateAsServerAsync`, then flips to transparent passthrough. **TLS is pinned to 1.2**: a TLS 1.3 server emits NewSessionTicket records at handshake completion, which would still be prelogin-wrapped after the client switched to reading raw records ("cannot determine frame size" on the client). Matches SqlClient/real-server behavior for pre-TDS-8 encryption.
- `Login7Request` — parses TDS version, packet size (accepted when 512–32767 and acked via ENVCHANGE type 4), hostname/username/appname/database. Requested database `master` or empty maps to the default database; anything else goes through `ChangeDatabase`, and its failure (Msg 911) is written as the login error (real server raises Msg 4060 here — known divergence).
- `TdsTokenWriter` — growable token buffer with packetizing flush; the session flushes after every row so memory stays bounded by max(row, packet).
- `TdsTypeCodec` — COLMETADATA TYPE_INFO + ROW value encoding (details below). Schema validated up front so unsupported column types fail as an ERROR token, never a mid-stream desync.
- `TdsCollationCodec` + `TdsCollationRegistry` — the COLMETADATA 5-byte collation structure, derived generatively (details below).

## Batch loop semantics

- **SQLBatch** (type 1): ALL_HEADERS skipped via its leading length DWORD; UCS-2 text executed on the session connection. Per result set: COLMETADATA + ROW stream + DONE (`DONE_COUNT` + `DONE_MORE` when more outcomes follow); per non-query statement: DONE with `DONE_COUNT` only when `RecordsAffected >= 0`. Zero outcomes → single final DONE. All tokens are `0xFD DONE` (DONEPROC/DONEINPROC are proc-scoped and RPC isn't shipped).
- **Errors**: `SimulatedSqlException.Errors` map field-for-field onto ERROR tokens (number/state/class/message/server/procedure/line) + DONE with `DONE_ERROR`; the session survives and keeps serving. `NotSupportedException` becomes a synthetic ERROR number 50000 class 16 prefixed `SqlServerSimulator:`.
- **PRINT / low-severity RAISERROR**: the session subscribes to `SimulatedDbConnection.InfoMessage` and drains the queue as INFO tokens between statements and at batch end.
- **`USE`**: database change detected by before/after comparison and emitted as ENVCHANGE type 1 (after the DONEs, before flush — SqlClient processes it anywhere pre-EOM).
- **Reset-connection status bit** (pooled-connection recycle): backing connection disposed and recreated on the same database, acked with the empty ENVCHANGE type 18 before the batch's tokens.
- **Attention** (type 6): acked with DONE `DONE_ATTN`. Execution is synchronous per message, so attention is only observed between messages — a cancel never interrupts a running statement server-side, it just gets acked when the stream drains. In-process execution is fast enough that this matches observable SqlClient behavior.
- **Bulk-load (7)**: ERROR 50000 naming the unsupported request type + DONE error (`SqlBulkCopy` is a planned follow-up).

## RPC requests

Parameterized `SqlCommand`s arrive as RPC (packet type 3), never as batches. `TdsRpc.cs` parses the request (proc name or well-known ProcID, option flags, parameter list with per-parameter TYPE_INFO — the read-side mirror of the type codec, including PLP in both known-length and unknown-length chunked forms; multiple requests in one message split on the 0xFF batch-flag). Parameter TYPE_INFO rejected with a clear error: TVP (0xF3), CLR UDT (0xF0), `sql_variant` (0x62), legacy `text`/`ntext`/`image`.

Dispatch (`TdsSession.Rpc.cs`):

- **sp_executesql** (ProcID 10 or by name): first param is the statement, second the declaration string (redundant for binding and dropped), the rest become `SimulatedDbParameter`s on a Text command — the engine's `SeedVariables` path binds them by name, with typed NULLs via `DbType`. Decimal scale rides the CLR decimal value (the engine ignores `DbParameter.Precision/Scale` by design).
- **Prepared statements**: `sp_prepexec` (13) stores statement + declaration-parsed parameter names in a per-session handle map, executes, and returns the handle via RETURNVALUE; `sp_execute` (12) looks up the handle (miss → Msg 8179) and names any unnamed wire params from the stored declaration order; `sp_prepare` (11) stores without executing; `sp_unprepare` (15) removes. Handles are per-session state, dropped on pooled-connection reset — SqlClient re-prepares transparently.
- **Direct proc invocation** (nonzero name): `CommandType.StoredProcedure` with a synthesized `ReturnValue`-direction parameter, so the proc's `RETURN n` lands in the RETURNSTATUS token.
- **Response shape**: per statement outcome DONEINPROC (0xFF, always `DONE_MORE` since trailing tokens follow), then RETURNSTATUS, RETURNVALUE per output parameter (name-matched by the client; values re-encoded via `DbType` → `SqlType.GetByDbType` + `ConvertParameter`), then DONEPROC final. Errors: ERROR token(s) + DONEPROC with `DONE_ERROR`.
- Output-parameter writeback happens when the engine's outcome enumerator is fully drained — the streaming loop always drains, which is what makes RETURNVALUE correct.

## Transaction Manager requests

`SqlTransaction` uses TM requests (packet type 14), not SQL text. Begin (5) maps the wire isolation byte onto `BeginTransaction(IsolationLevel)` and answers ENVCHANGE type 8 carrying an opaque 8-byte transaction descriptor (a per-session counter; the client echoes it in ALL_HEADERS, which the listener ignores — the session connection *is* the transaction scope). Commit (7) / rollback (8) map to the transaction object and answer ENVCHANGE 9 / 10 with empty values. `SqlTransaction.Save(name)` (9) and `Rollback(name)` route through SQL text (`SAVE TRANSACTION` / `ROLLBACK TRANSACTION [name]`, bracket-escaped); rollback-to-savepoint keeps the transaction alive so no transaction ENVCHANGE is emitted. **Names in TM requests are B_VARBYTE** — the length prefix counts UTF-16 *bytes*, unlike the char-counted B_VARCHAR elsewhere (SqlClient writes `name.Length * 2`); misreading it as B_VARCHAR overruns the payload on every savepoint call.

## Type wire codec

Nullable wire variants throughout (INTN 0x26, BITN 0x68, FLTN 0x6D, MONEYN 0x6E, DATETIMN 0x6F, DECIMALN 0x6A, GUIDN 0x24) — legal for non-nullable data, and COLMETADATA always claims nullable (result-set schema carries no nullability). Fixed usertype 0 except rowversion's 0x50. Specifics:

- **decimal**: TYPE_INFO length by precision (5/9/13/17); value = sign byte + little-endian magnitude rescaled to the declared scale via `BigInteger` (round-half-up when the stored .NET decimal scale exceeds the declared).
- **money/smallmoney**: `SqlValue.AsMoneyScaledUnits` (scale-4 int64); money wire order is high-int32 then low-uint32.
- **datetime**: days since 1900-01-01 + 1/300-second units, computed from full-resolution ticks with round-half-up and day-carry at 25 920 000. The engine's internal 1/300 resolution transfers exactly; client-visible millisecond rounding then happens in SqlClient itself, same as against a real server.
- **date/time/datetime2/datetimeoffset**: scaled encodings per declared precision (3/4/5 value bytes by scale); datetimeoffset sends UTC time+date plus offset minutes.
- **Strings**: `varchar/char` value bytes use the code page implied by the advertised collation (`TdsCollationCodec.WireEncoding`) so the client's decode round-trips; `nvarchar/nchar/sysname` are UCS-2. MAX types and `xml` use known-length PLP (total length + single chunk + terminator); PLP NULL is the 8×0xFF sentinel, non-PLP NULL is length 0xFFFF.
- **rowversion**: BIGBINARY(8), big-endian counter bytes from `SqlValue.AsBytes`.
- **Not encodable** (schema validation rejects with ERROR before COLMETADATA): `text`, `ntext`, `image` (legacy textptr wire form), `hierarchyid`, `geography`, `geometry` (UDT wire form). `sql_variant` has no simulator type at all.

## Collation wire structure

COLMETADATA's 5 bytes = packed uint (LCID bits 0–19, flags 20–27, version nibble 28–31, little-endian) + sortId byte. Derived generatively per collation (cached by interned reference), validated against a full 5540-name probe of the live reference (2026-07-13):

- Flags from name tokens: CI→bit20, AI→bit21, absent WS→bit22 (ignore-width), absent KS→bit23 (ignore-kana), BIN→bit24, BIN2→bit25. **Width=22/Kana=23 is load-bearing** for KS/WS-only names and contradicts MS-TDS's field-declaration order — the spec's own assembly line and SqlClient's constants agree with 22/23, probe-confirmed via `_CI_AS_KS` (0x05) / `_CI_AS_WS` (0x09).
- `_UTF8` → bit26, and it **displaces** the binary bits (`_BIN2_UTF8` reports 0x40, not 0x60); sensitivity bits are retained. Code page becomes 65001.
- `_SC` / `_VSS` set no wire bit — the structure is lossy (5540 names → 3987 distinct tuples).
- Version nibble from the name's number token: none/SQL_*→0, 90→1, 100→2, 140→3, **160→4**.
- LCID + ANSI code page per name-prefix from `TdsCollationRegistry` (probe-derived, one entry per `KnownPrefixes` key); SQL_* code page comes from the CPnnn token (CP1→1252). Anomaly: `SQL_Latin1_General_CP1254_*` reports the Turkish LCID 0x041F (special-cased).
- Baseline `SQL_Latin1_General_CP1_CI_AS` derives to the canonical `09 04 D0 00 34` (sortId 52).

## Login response shape

ENVCHANGE(database, old `master`) → INFO 5701 → ENVCHANGE(language `us_english`) → INFO 5703 → ENVCHANGE(SQL collation, type 7: the server collation's 5-byte structure) → LOGINACK (TDS 0x74000004 big-endian on the wire, prog name `Microsoft SQL Server`, version 17.0) → ENVCHANGE(packet size) → DONE. Server name in every token is `SIMULATED` (matches `SERVERPROPERTY`). **The collation ENVCHANGE is load-bearing for RPC**: SqlClient stores it as the default collation it stamps onto outbound parameter TYPE_INFO, and without it every parameterized command dies client-side in a `NullReferenceException` before any bytes hit the wire.

## Divergences / deferred

- Login to a missing database: Msg 911 (from `ChangeDatabase`) instead of real's Msg 4060 wrapping; login INFO states are approximations.
- No MARS (prelogin answers MARS off), no TDS 8.0 / `Encrypt=Strict`, no plaintext sessions, no integrated auth, no `SqlBulkCopy`.
- RPC parameter gaps: TVP / UDT / `sql_variant` / legacy-LOB parameters rejected with ERROR 50000; `sp_cursor*` and other well-known ProcIDs likewise.
- Attention is acked only between messages (no mid-stream cancel).
- SPID in packet headers truncates to 16 bits.

## Testing

`SqlServerSimulator.Tests.SqlClient` is the loopback oracle: real `Microsoft.Data.SqlClient` (direct package reference) against `ListenAsync(0)` endpoints. Where expected values are nontrivial (datetime tick rounding, money scale, collation-dependent varchar bytes), tests use the dual-read pattern — the same query through the in-process ADO surface and through the wire against the same `Simulation`, asserting equality — so the in-process behavior contract stays the single source of truth.
