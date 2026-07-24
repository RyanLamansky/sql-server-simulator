# TDS network endpoint

`Simulation.ListenLocalAsync(int port = 1433, CancellationToken cancellationToken = default)` opens a loopback TCP endpoint speaking TDS 7.4, so unmodified SQL Server clients (SqlClient, JDBC, sqlcmd, SSMS) reach the simulator with only a connection-string change.
Returns `Task<SimulatedNetworkListener>`; `port: 0` binds an OS-assigned ephemeral port (read `Port`), the right shape for parallel tests.
Port-in-use surfaces as the raw `SocketException`.
The listener binds IPv4 loopback plus best-effort IPv6 loopback on the same port.
Both listen methods also take a **`SimulatedNetworkListenerOptions`** overload (`Port`, default 1433 matching the int overloads; `BindAddress`; `ServerCertificate`, default null = generate ephemeral): a supplied certificate must carry a private key (`ArgumentException` otherwise) and **stays owned by the caller — the listener never disposes it** — so one certificate serves many listeners (created once at suite setup, its public part exported once for strict-mode pinning instead of per-listener temp files).
`BindAddress` narrows `ListenNetworkAsync` from all interfaces to exactly one address — its family decides the socket family and **no best-effort other-family sibling is bound** — while `ListenLocalAsync` rejects a non-null value with `ArgumentException` (honoring it would put the loopback method's accept-anyone-until-`CREATE LOGIN` credential model on a network interface).
**`ListenNetworkAsync`** is the all-interfaces sibling (`IPAddress.Any` + best-effort `IPv6Any`, same core): it throws `InvalidOperationException` at call time when no logins are registered — the loopback listener's accept-anyone-until-`CREATE LOGIN` model must never face a network — and its XML docs carry the honest caveat that authentication is the *only* enforcement (no authorization model; every login has unrestricted access; the self-signed cert doesn't authenticate the server).
Oracle: `NetworkListenerTests`.

Connection-string requirements: `TrustServerCertificate=true` (the endpoint presents an ephemeral in-memory self-signed cert generated per listener, unless the options overload supplied one — a CA-trusted supplied certificate makes the flag unnecessary) and credentials per the enforcement rule below (any credentials when no logins exist).
Default/`Mandatory`/`Optional` all negotiate to full encryption via `ENCRYPT_REQ`.
A client that cannot do TLS at all (`ENCRYPT_NOT_SUP`) is disconnected after the prelogin response — there is no plaintext mode.

**`Encrypt=Strict` (TDS 8.0) ships**: the client opens with a bare TLS ClientHello negotiating ALPN `tds/8.0`, and every TDS packet — prelogin included — flows inside the TLS channel; the session routes on the first wire byte (TLS handshake record `0x16` vs cleartext PRELOGIN `0x12`).
SqlClient **ignores `TrustServerCertificate` in strict mode** and always validates the certificate (chain + hostname), so a strict client must pin instead: export `SimulatedNetworkListener.ServerCertificate` (the presented certificate, public part only) to a file and reference it with the connection string's `ServerCertificate` keyword (empirically confirmed against SqlClient 7.0.2 — `TrustServerCertificate=True` alone fails strict with `UntrustedRoot` + hostname mismatch).
Supplying one certificate through `SimulatedNetworkListenerOptions` makes the pin file a create-once artifact shared by every listener (the shape `StrictEncryptionTests` uses: one class-level certificate, its public part written to a fixed-name file in the OS temp directory).
The strict TLS handshake is not version-pinned (TLS 1.3 negotiates; raw records make NewSessionTicket harmless), and LOGINACK echoes TDS version `0x08000000` when LOGIN7 requested it.
MARS and `SqlBulkCopy` ride the strict channel unchanged.
Oracle: `StrictEncryptionTests`.

## Credential enforcement

The knob is T-SQL, not API: `CREATE LOGIN name WITH PASSWORD = '…'` (run through any in-process connection — those never authenticate) populates the server-scope registry `Simulation.Logins`, and the endpoint enforces it.
While the registry is empty — the zero-configuration default — any LOGIN7 credentials are accepted.
Once at least one login exists, the LOGIN7 username must resolve in the registry (keyed case-insensitively, `BuiltInToken.Comparer` like the sibling server-scope dicts) and the password must verify against the stored hash (`PasswordHash.Verify`).
The registry stores the legacy `0x0200` single-pass-SHA-512 format rather than PWDENCRYPT's `0x0300` PBKDF2 — the hashes never leave the simulation's memory, so 100k PBKDF2 iterations would be pure per-connection-open cost; verification dispatches on the version tag so either form verifies.
Failure — wrong password, unknown login, empty password alike — writes ERROR **Msg 18456 severity 14 state 1**, message `Login failed for user '<name>'.`, then DONE with `DONE_ERROR` and closes the connection.
Shape probe-confirmed against SQL Server 2025 (2026-07-13): the real server masks the detailed state, so all three failure causes are client-indistinguishable.

The LOGIN7 password field de-obfuscates per MS-TDS (each byte XOR 0xA5 then nibble-swap, inverting the client's swap-then-XOR) and its length pair is **char-counted like every other LOGIN7 field** — oracle-confirmed with a surrogate-pair password, where a byte-counted read would overrun.
`ALTER LOGIN … WITH PASSWORD` / `DROP LOGIN` update the registry live (entries are immutable and replaced wholesale, so a concurrent login sees a consistent hash); dropping the last login reverts the endpoint to accept-anything.
Login DDL details in [`permissions.md`](permissions.md).

`SimulatedNetworkListener` is `IDisposable`/`IAsyncDisposable`: disposal is aggressive and waits for nothing — listening sockets close, each session's backing `SimulatedDbConnection` is disposed with normal session teardown semantics (transactions roll back, temp tables drop), and mid-query clients see an abrupt connection reset.
`DisposeAsync` is the same teardown returning a completed task.

## Architecture

Everything lives in `Network/` (internal) except the public `SimulatedNetworkListener` (root) and `Simulation.Listen.cs` (the `ListenLocalAsync` / `ListenNetworkAsync` partial).
One task per accepted socket runs `TdsSession.RunAsync`: prelogin → TLS handshake → LOGIN7 → batch loop (TDS 7.x), or TLS handshake → prelogin → LOGIN7 → batch loop (TDS 8.0 strict, routed by the peeked first byte).
The session maps 1:1 onto a `SimulatedDbConnection`; execution flows through `Simulation.CreateResultSetsForCommand`, which yields both result sets and per-statement `RecordsAffected` — the TDS layer is a pure translator and touches no engine code.

- `TdsPacketTransport` — packet framing both directions: reassembles inbound packet sequences into `TdsMessage` (EOM-terminated), stamps outbound headers (type 0x04, SPID truncated to 16 bits, incrementing packet id).
  The stream it rides is swapped from the raw `NetworkStream` to the `SslStream` after the handshake.
- `TlsHandshakeFramingStream` — the TDS 7.x TLS seam: handshake records travel wrapped in PRELOGIN-type packets, so this shim strips/adds packet headers under `SslStream` during `AuthenticateAsServerAsync`, then flips to transparent passthrough.
  **TLS is pinned to 1.2 on this path**: a TLS 1.3 server emits NewSessionTicket records at handshake completion, which would still be prelogin-wrapped after the client switched to reading raw records ("cannot determine frame size" on the client).
  Matches SqlClient/real-server behavior for pre-TDS-8 encryption.
  The TDS 8.0 strict path needs no shim (the `SslStream` sits directly on the socket, records flow raw) and no version pin.
- `Login7Request` — parses TDS version, packet size (accepted when 512–32767 and acked via ENVCHANGE type 4), hostname/username/password (de-obfuscated)/appname/database.
  An **empty** requested database maps to the default (user) database; **any non-empty name — including `master` — resolves genuinely through `ChangeDatabase`** (master is a real seeded database now, so `Database=master` lands in master rather than being aliased to the default).
  A `ChangeDatabase` failure becomes the probe-confirmed login pair — Msg 4060 severity 11 (`Cannot open database "x" requested by the login. The login failed.`, double-quoted name) then Msg 18456 severity 14 — before the connection closes.
  Mid-session `USE` keeps the engine's Msg 911.
- `TdsTokenWriter` — growable token buffer with packetizing flush; the session flushes after every row so memory stays bounded by max(row, packet).
- `TdsTypeCodec` — COLMETADATA TYPE_INFO + ROW value encoding (details below).
  Schema validated up front so unsupported column types fail as an ERROR token, never a mid-stream desync.
- `TdsCollationCodec` — the COLMETADATA 5-byte collation structure, derived generatively from the collation name plus the core-layer `Collation.LcidAndCodePageByPrefix` probe table (details below).

## Batch loop semantics

- **SQLBatch** (type 1): ALL_HEADERS skipped via its leading length DWORD; UCS-2 text executed on the session connection.
  Per result set: COLMETADATA + ROW stream + DONE (`DONE_COUNT` + `DONE_MORE` when more outcomes follow); per non-query statement: DONE with `DONE_COUNT` only when `RecordsAffected >= 0`.
  **`SET NOCOUNT ON`** (session flag `SimulatedDbConnection.NoCount`) suppresses that `DONE_COUNT` on the non-query DML DONE — so an ODBC/pyodbc driver skips the DML's rowcount result and advances to a trailing `SELECT SCOPE_IDENTITY()` (the mssql-django / Django identity pattern; without it the driver stalls on the INSERT's rowcount and never reaches the SELECT), and SqlClient's `ExecuteNonQuery` returns -1.
  Zero outcomes → single final DONE.
  Batch-level statements use `0xFD DONE`; statements inside an **`EXEC('…')` / sp_executesql dynamic-SQL scope use DONEINPROC** — see [Dynamic-SQL exec scope](#dynamic-sql-exec-scope-doneinprocdoneproc) below.
- **Errors**: `SimulatedSqlException.Errors` map field-for-field onto ERROR tokens (number/state/class/message/server/procedure/line) + DONE with `DONE_ERROR`; the session survives and keeps serving.
  `NotSupportedException` becomes a synthetic ERROR number 50000 class 16 prefixed `SqlServerSimulator:`.
- **Statement-terminating error continuation**: the wire calls `CreateResultSetsForCommand(command, continueOnError: true)`, so a statement-terminating error (severity 11-16, excluding deadlock class 13) mid-batch is emitted as a `SimulatedErrorOutcome` rather than thrown — real SQL Server's default (non-XACT_ABORT) behavior, and the fix that lets SMO's all-`DROP #tmp` Object-Explorer cleanup batch run to completion so it proceeds to enumerate.
  `StreamOutcomesAsync` writes the error's tokens + a DONE carrying `DONE_ERROR` plus the usual more/final bit (mid-batch errors get `DONE_MORE`, a trailing error closes with the final DONE), then continues to the next outcome.
  Batch-aborting errors (deadlock class 13, class ≥ 17, `NotSupportedException`) still throw out of the outcome stream into `ExecuteBatchAsync`'s / the RPC loop's top-level `catch`, which closes the batch with `DONE_ERROR`.
  Continuation applies to both SQLBatch and RPC (they share `StreamOutcomesAsync`); an RPC whose earlier statement set an output parameter before a later statement errored still writes that parameter's RETURNVALUE (probe-confirmed against real SQL Server 2025).
  The in-process ADO path never opts in — it stays fail-fast (first error throws).
  Classification and the two known gaps (a genuine syntax error mid-batch continues over the wire; `SET XACT_ABORT ON` batch-abort not honored) are in [`control-flow.md`](control-flow.md).
- **PRINT / low-severity RAISERROR**: the session subscribes to `SimulatedDbConnection.InfoMessage` and drains the queue as INFO tokens between statements and at batch end.
  **Each INFO flush in a SQLBatch response gets its own DONE**, mirroring real per-statement DONEs: info preceding an outcome is followed by `DONE_MORE` count 0 before the outcome's tokens, and trailing info (batch ends in PRINT) forces the last outcome's DONE to `DONE_MORE` with a closing `DONE_FINAL` count 0 after the INFO — an INFO token must never follow the final DONE.
  Without both, SqlClient's token reader stalls until command timeout on any batch mixing PRINT with a result set, and go-mssqldb silently drops the message (go-sqlcmd shakedown, 2026-07-14; the pre-fix oracle only covered PRINT-without-result-set).
  RPC responses are unaffected — every DONEINPROC already carries `DONE_MORE`.
- **`USE`**: database change detected by comparing the session database against its value at message start (`databaseAtMessageStart`) and emitted as ENVCHANGE type 1 + INFO 5701 (`Changed database context to '<db>'.`) **before the response's final DONE** — the seam fires at every final-DONE site (batch outcome DONEs, the closing DONE, error-path DONEs, and the RPC handlers' final DONEPROC) and is idempotent, so it emits at most once per message.
  Ordering is load-bearing: SqlClient's token reader stalls until command timeout on an ENVCHANGE that arrives after the last DONE (probe-confirmed 2026-07-15 — this froze SSMS on its first `use [master]` once master existed; go-mssqldb tolerates the late position, which is how the original after-the-DONEs ordering shipped unnoticed).
  The INFO 5701 is wire-layer-only — the in-process engine raises no InfoMessage for `USE`, a minor in-process/wire asymmetry matching the login response's synthesized 5701.
- **Reset-connection status bit** (pooled-connection recycle): backing connection disposed and recreated on the same database, acked with the empty ENVCHANGE type 18 before the batch's tokens.
- **Attention** (type 6, mid-stream cancel): a client `SqlCommand.Cancel()` or expiring `CommandTimeout` sends an attention while a batch executes or streams.
  The session notices it *concurrently* — see [Mid-stream attention](#mid-stream-attention-cancel) below — aborts the batch at the next safe point, and replies with a single DONE carrying `DONE_ATTN` and **no error token** (SqlClient synthesizes the surfaced exception itself: Msg -2 "Execution Timeout Expired" for a timeout, Msg 0 "Operation cancelled by user" for an explicit cancel).
  The session stays alive and reusable.
  An idle attention (or one racing a just-completed response) is acked the same way.
- **Bulk-load (7)**: `SqlBulkCopy` — the `INSERT BULK` SQL batch opens bulk mode and the following BulkLoadBCP data packet streams rows.
  Full flow + options matrix in [Bulk load](#bulk-load-sqlbulkcopy) below.

### Dynamic-SQL exec scope (DONEINPROC/DONEPROC)

Real SQL Server runs an `EXEC('…')` / `sp_executesql` body as a **nested procedure scope**: the statements inside report **DONEINPROC (0xFF)**, and the scope closes with **RETURNSTATUS (0) + DONEPROC (0xFE)** — the shape a plain batch-level DONE (`0xFD`) does not carry.
Cleartext-probed 2026-07-19 against SQL Server 2025 (`Encrypt=False` login-only encryption leaves post-login tokens in the clear through a tee proxy) with the SSMS report viewer's environment-probe batch (three batch-level statements — a `SET` assignment and two `SELECT`s — then an `IF … ELSE EXEC('select … CONNECTIONPROPERTY …')`): the batch-level statements reported `0xFD DONE`, the `EXEC`'s inner SELECT reported `0xFF DONEINPROC` with `DONE_MORE|DONE_COUNT`, then `RETURNSTATUS 0` + `0xFE DONEPROC`.
The old simulator emitted a plain `0xFD DONE` for the exec's result set and no RETURNSTATUS/DONEPROC — the divergence that froze the report viewer's connection, whose app is `.NET Framework`'s legacy `System.Data.SqlClient` (native SNI + a stricter, older TDS parser than `Microsoft.Data.SqlClient`).

Modeled via outcome-stream markers: the engine's `ExecuteDynamicBatch` brackets the dynamic body's outcomes with `SimulatedProcScopeBoundary` (Enter / Exit) markers; `StreamOutcomesAsync` raises a `procScopeDepth` on Enter (statements then render with DONEINPROC), and on Exit lowers it and emits `RETURNSTATUS 0 + DONEPROC` (with the usual more/final bit) — even when the body produced no result set.
Every in-process outcome consumer (the reader, `ExecuteNonQuery` / `ExecuteScalar`) ignores the markers, since a boundary is neither a query nor an affected-rows outcome.
This reuses the RPC path's existing DONEINPROC/DONEPROC discipline for a body that arrives inside a SQLBatch rather than as an RPC.
Oracles: `ProcScopeBoundaryTests` (Tests.Internal, marker emission), `DynamicSqlExecWireTests` (Tests.SqlClient, end-to-end over real SqlClient).

**Residual divergences (still present, none regressed by the fix; not reproducible from Linux, which lacks the native-SNI Framework parser — managed `System.Data.SqlClient` / `Microsoft.Data.SqlClient` read both the old and new streams fine):** the DONE `curcmd` field stays 0 where real carries statement-type codes (0xC1 SELECT/SET, 0xC0 IF, 0xE0 EXECUTE) — cosmetic, needs statement-type surfacing; the simulator emits no DONE for a `SET`-assignment or an `IF` statement (real does — those yield no engine outcome).
A direct `EXEC <proc>` in a batch (through `InvokeProcedure`, not `ExecuteDynamicBatch`) is the identical-shaped sibling that would want the same markers — not yet wired.

## Terminal crash boundary

The typed catch lists in `RunAsync` / `RunMarsSessionAsync` (`IOException` / `SocketException` / `ObjectDisposedException` / `OperationCanceledException` / `InvalidDataException` / `AuthenticationException`) plus the per-handler `SimulatedSqlException` / `NotSupportedException` conversions handle every anticipated failure.
Behind them sits a **terminal backstop**: an exception of no anticipated type (an internal bug, an unmodeled engine path that throws a raw CLR exception) used to end the session task silently, so the client saw only a raw transport reset with no message — the first thing a real user's tool hits when it sends something the simulator didn't anticipate.
The backstop now emits a best-effort **Msg 0 / severity 20** ERROR (`"A severe error occurred on the current command. The results, if any, should be discarded."`) — the shape real SQL Server sends for an internal failure — then lets the connection close.
SqlClient treats severity ≥ 20 as fatal: it surfaces a `SqlException` and marks the connection dead, so the failure is diagnosable instead of a bare reset.
(Matched to SqlClient's documented severity-20 handling; not separately probed on a triggered real-server internal error.)

**Mid-token safety is the hard constraint.**
An ERROR token may be appended only at a token boundary — a crash that struck mid-COLMETADATA / mid-ROW left a partial token in the writer's buffer, and appending another token there would desync the stream and make it worse.
`TdsTokenWriter.AtTokenBoundary` tracks this: it is true whenever the buffer ends at a complete token (every self-contained token method runs to completion synchronously, leaving it true), and the three writers that interleave a throw-capable per-column sub-write between a single token's bytes — `WriteColMetadata`, `WriteRow`, `WriteReturnValue` — bracket their body with `EnterComposite()` / `LeaveComposite()`, so a throw mid-body leaves it false.
The backstop (`TryWriteSevereErrorAsync`) appends the ERROR only when `AtTokenBoundary` is true; otherwise it just closes.
Bytes already flushed for the current response stay well-formed either way — a split token's remainder is still buffered, and an ERROR token legally follows any complete tokens (even a partial result set the client then discards).
The MARS per-session loop applies the same backstop so one logical session's crash surfaces as a severe error rather than silently killing the whole mux.
Oracles: `CrashBoundaryTests` (Tests.SqlClient, end-to-end severity-20 `SqlException`), `TokenBoundaryTests` (Tests.Internal, the `AtTokenBoundary` flag invariant).
The forcing seam is a test-only per-`Simulation` hook (`NetworkBatchCrashHookForTesting`, internal, not public API).

## RPC requests

Parameterized `SqlCommand`s arrive as RPC (packet type 3), never as batches.
`TdsRpc.cs` parses the request (proc name or well-known ProcID, option flags, parameter list with per-parameter TYPE_INFO — the read-side mirror of the type codec, including PLP in both known-length and unknown-length chunked forms; multiple requests in one message split on the 0xFF batch-flag).
**Table-valued parameters (0xF3), CLR-UDT (0xF0), and `sql_variant` (0x62) parameters are decoded** (see [TVP parameters](#tvp-parameters-sqldbtypestructured) + [CLR-UDT / sql_variant parameters](#clr-udt--sql_variant-parameters) below); **every input-parameter TYPE_INFO is now accepted** — the last rejection, legacy `image` (0x22), decodes as of 2026-07-19 (see [Legacy text / ntext / image](#legacy-text--ntext--image-wire-forms)).
**Legacy `ntext` (0x63) and `text` (0x23) string parameters ARE decoded** — SqlClient sends the `sp_executesql` `@statement` / `@params` as `ntext` once they exceed nvarchar(4000) (the proc's declared parameter type), so any parameterized query over ~4000 chars arrives this way.
Their wire value is the **legacy 4-byte-length form** (LONGLEN max size + 5-byte collation, then a 4-byte data length + the string bytes — `0xFFFFFFFF` length = NULL), NOT PLP.
This was the multi-round SSMS Object-Explorer Databases-node blocker: SMO's HADR-aware user-database enumeration is a large parameterized query, so it always arrived as an `ntext` param and was rejected before executing — the node stayed empty with no surfaced error while every other server-side fix landed on a code path that never ran.
Probe-confirmed wire shape 2026-07-15.

Dispatch (`TdsSession.Rpc.cs`):

- **sp_executesql** (ProcID 10 or by name): first param is the statement, second the declaration string, the rest become `SimulatedDbParameter`s on a Text command — the engine's `SeedVariables` path binds them by name, with typed NULLs via `DbType`.
  **Any unnamed value params (index 2+, name='') are named positionally from the declaration** (index 1) via the shared `NameUnnamedParameters` helper, the same mapping `sp_prepexec` / `sp_execute` use — mssql-jdbc's `PreparedStatement` sends its value params positionally, so without this every prepared query failed with Msg 137 "Must declare the scalar variable "@P0"."; SqlClient names its own, so its oracle never caught the gap (`SpExecuteSqlUnnamedParameterTests`).
  Decimal scale rides the CLR decimal value (the engine ignores `DbParameter.Precision/Scale` by design).
- **Prepared statements**: `sp_prepexec` (13) stores statement + declaration-parsed parameter names in a per-session handle map, executes, and returns the handle via RETURNVALUE; `sp_execute` (12) looks up the handle (miss → Msg 8179); **both name any unnamed wire params positionally from the stored declaration order** (the shared `NameUnnamedParameters` helper) — native ODBC / OLE DB drivers send prepared value params positionally with an empty name (name=''), so without this they fail with Msg 137 "Must declare the scalar variable"; SqlClient names its own, which is why its oracle never caught the gap (`PrepExecUnnamedParameterTests`).
  `sp_prepare` (11) stores without executing; `sp_unprepare` (15) removes.
  `sp_cursorprepexec` names its cursor value params the same way (`BindTail` → `NameUnnamedParameters`).
  Handles are per-session state, dropped on pooled-connection reset — SqlClient re-prepares transparently.
- **Direct proc invocation** (nonzero name): `CommandType.StoredProcedure` with a synthesized `ReturnValue`-direction parameter, so the proc's `RETURN n` lands in the RETURNSTATUS token.
  **Unnamed value params (name='') bind to the proc's declared parameters by position**, an empty name yielding a positional `ProcArgument` (null Name), a supplied name binding by name — the same positional/named split `EXEC p 5, 6` vs `EXEC p @a=5` takes.
  Native DB-Library clients (pymssql / FreeTDS, legacy PHP mssql) send every RPC parameter positionally with an empty name, so without this any `callproc` with ≥1 argument failed with Msg 201 "expects parameter '@x', which was not supplied"; SqlClient / ODBC / JDBC name their proc params, which is why those oracles never caught the gap.
  The output-slot writeback key falls back to the parameter ordinal when the name is empty so two positional OUTPUT params don't collide on the shared empty name (`StoredProcedureTests.CommandType_StoredProcedure_UnnamedParameters_BindPositionally`; DB-Library's positional shape surfaces in-process as an empty `SimulatedDbParameter.ParameterName`, which — mirroring ADO.NET — reads back as the empty string, never null).
- **Response shape**: per statement outcome DONEINPROC (0xFF, always `DONE_MORE` since trailing tokens follow), then RETURNSTATUS, RETURNVALUE per output parameter (name-matched by the client; values re-encoded via `DbType` → `SqlType.GetByDbType` + `ConvertParameter`), then DONEPROC final.
  Errors: ERROR token(s) + DONEPROC with `DONE_ERROR`.
- Output-parameter writeback happens when the engine's outcome enumerator is fully drained — the streaming loop always drains, which is what makes RETURNVALUE correct.

## API server cursors (sp_cursor\* RPC family)

`TdsSession.Cursors.cs` handles the special-ProcID cursor family SSMS's query-editor grid and legacy ODBC / OLE DB server-cursor apps drive.
`WellKnownProcId` in `TdsSession.Rpc.cs` maps both the well-known numeric ProcIDs (1 sp_cursor, 2 sp_cursoropen, 3 sp_cursorprepare, 4 sp_cursorexecute, 5 sp_cursorprepexec, 6 sp_cursorunprepare, 7 sp_cursorfetch, 8 sp_cursoroption, 9 sp_cursorclose) **and** the by-name form (SqlClient sends `CommandType.StoredProcedure` with `CommandText = "sp_cursoropen"` as ProcID 0 + name) to the dispatch.
Each open cursor rides an engine `Cursor` (built by synthesizing a `DECLARE … CURSOR … FOR <stmt>; OPEN` batch and pulling the object out of `SimulatedDbConnection.Cursors` under an opaque `sss_apicursor_<handle>` name), stored in a per-session `Dictionary<int, ApiCursor>` — wire-protocol state, so on the session not the engine.
Fetch drives `Cursor.Fetch` directly per row; positioned DML sets `Cursor.CurrentRid` to a buffered RID and runs a synthesized `UPDATE/DELETE … WHERE CURRENT OF <name>` so the full engine machinery (triggers, constraints, statement atomicity) fires.
Probed against SQL Server 2025 (2026-07-17).

**sp_cursoropen**(@cursor OUT, @stmt, @scrollopt IN/OUT, @ccopt IN/OUT, @rowcount OUT) — builds + opens the cursor and writes a **metadata-only announce**: COLMETADATA for the projection plus a trailing `ROWSTAT` int column, **zero rows**.
Return status 0.
The OUT scrollopt/ccopt are the *effective* (resolved) options, and @rowcount is the row count for keyset/static or −1 for the non-materialized shapes:

| scrollopt (low bits) | requested | effective OUT scrollopt | @rowcount |
|---|---|---|---|
| 0x1 KEYSET | updatable single table | 0x1 | row count |
| 0x2 DYNAMIC | updatable single table | 0x2 | −1 |
| 0x4 FORWARD_ONLY | updatable single table | 0x4 | −1 |
| 0x8 STATIC | any | 0x8 | row count |
| 0x10 FAST_FORWARD | updatable single table | 0x10 | −1 |
| any | **non-updatable** (GROUP BY / DISTINCT / join) | **0x8** (forced STATIC), ccopt → **0x1** READ_ONLY | row count |

ccopt low bits: 0x1 READ_ONLY, 0x2 SCROLL_LOCKS, 0x4 OPTIMISTIC (values), 0x8 OPTIMISTIC (rowversion).
The `0x1000`-series flag bits (PARAMETERIZED_STMT / AUTO_FETCH / AUTO_CLOSE / …) are stripped from the effective value.
On an **invalid statement**, the engine's error (e.g. Msg 208) plus **Msg 16945** (`The cursor was not declared.`, state 2) are emitted, the handle comes back 0, the option values echo the requested low bits, and the return status is the engine error number.

**sp_cursorfetch**(@cursor, @fetchtype, @rownum, @nrows) — @rownum / @nrows are **input** for a data fetch (a real server rejects them ByRef with Msg 16902 for non-INFO fetch types; the simulator simply reads them as input).
Rows come back as an ordinary result set of up to @nrows rows, each with the trailing ROWSTAT = 1 column. fetchtype: 0x1 FIRST, 0x2 NEXT, 0x4 PREV, 0x8 LAST, 0x10 ABSOLUTE (@rownum), 0x20 RELATIVE (@rownum), 0x100 INFO.
The first buffer row uses the requested direction; subsequent rows advance NEXT.
A **past-end** fetch returns an empty result set with return status 0.
**INFO** writes no rows and reports the current 1-based position (@rownum) and total row count (@nrows) as OUT params.
An **invalid handle** → **Msg 16909** (`sp_cursorfetch: The cursor identifier value provided (<hex>) is not valid.`, state 1), return status 1.
Each fetch's RIDs are buffered on the ApiCursor for positioned DML.

**sp_cursor**(@cursor, @optype, @rownum, @table, @col=value…) — positioned DML against the last fetch buffer, @rownum 1-based into it. optype 0x1 UPDATE (named `@col` params become `SET [col] = @col`), 0x2 DELETE, 0x20 SETPOSITION (no DML, just repositions).
Empty buffer → **Msg 16931** (`There are no rows in the current fetch buffer.`) + Msg 3621; @rownum past the buffer → **Msg 16930** (`The requested row is not in the fetch buffer.`) + Msg 3621; in both the return status is the primary Msg number.
A DML enforcement failure surfaces the engine error and returns its number.

**sp_cursorprepexec**(@prep OUT, @cursor OUT, @paramdef, @stmt, @scrollopt IN/OUT, @ccopt IN/OUT, @rowcount OUT, params…) — prepares (stores statement + declaration-parsed parameter names in a per-session handle map) and opens in one call, returning both handles; the parameterized statement's bindings are the trailing params.
**sp_cursorexecute**(@prep, @cursor OUT, @scrollopt IN/OUT, @ccopt IN/OUT, @rowcount OUT, params…) re-opens the stored statement with fresh param values, yielding a **new** cursor handle (probe-confirmed).
**sp_cursorprepare** / **sp_cursorunprepare** store / drop without executing (miss → Msg 8179).
Prepared-cursor parameter values are frozen at open onto the ApiCursor and re-applied to every fetch batch, since a keyset / dynamic cursor re-runs its SELECT per fetch.

**sp_cursorclose**(@cursor) — closes + unbinds, return status 0.
Double-close or invalid handle → **Msg 16909** (state 1), return status 1.

**Accept-and-ignore / divergences (documented, not byte-identical):**

- **sp_cursoroption** is accepted and discarded (return status 0) — its codes weren't reachably probed from SqlClient.
- **Concurrency control is not wired for the API path.** ccopt SCROLL_LOCKS / OPTIMISTIC keep the cursor updatable and echo in the OUT ccopt, but no scroll locks are held and **no optimistic conflict is raised** — probe-confirmed the real API cursor did **not** surface a conflict even with a second connection modifying a buffered row (unlike the T-SQL `OPTIMISTIC` cursor, which raises the Msg 16947 chain).
  The positioned DML uses the default relocate-and-rewrite.
- **Handle values** are a simple per-session counter, not the real server's descriptor-derived integers (opaque to the client).
- **FAST_FORWARD @rowcount** reports −1 (matching real) even though the engine materializes it as STATIC internally.
- **sp_cursor @rownum = 0** (real: "apply to every buffered row" batch update) is not modeled — only 1-based single-row positioned DML.
- **REFRESH fetchtype (0x80)** maps to a plain re-fetch rather than an in-place buffer refresh.
- The `0x1000` PARAMETERIZED_STMT flag requirement (real raises Msg 16902 when a parameterized prepexec omits it) is **not enforced** — the flag is simply stripped.

## Bulk load (SqlBulkCopy)

`SqlBulkCopy.WriteToServer` runs a three-message handshake per batch, all probed against SQL Server 2025 + SqlClient 6.0.2 / 7.0.2 (2026-07-18).
`TdsSession.BulkLoad.cs` + `TdsBulkLoadReader.cs` (wire) and `Simulation.BulkLoad.cs` (engine) implement it.

1. **Metadata pre-batch** (SQLBatch) — once per `WriteToServer`.
   SqlClient 6.x sends `select @@trancount; SET FMTONLY ON select * from [dest] SET FMTONLY OFF exec ..sp_tablecollations_100 N'[dest]'`; SqlClient 7.x wraps a bigger version that reads the ordered column list from `.[sys].[all_columns]` via `sp_executesql`, then `SET FMTONLY ON; EXEC(N'SELECT '+@cols+' FROM [dest]'); SET FMTONLY OFF; EXEC ..sp_tablecollations_100 …`.
   This drove four cross-cutting engine fixes: **FMTONLY is now session state** (`SimulatedDbConnection.FmtOnly`) — while ON a SELECT returns metadata-only zero rows and DML is suppressed (probe-confirmed a FMTONLY-wrapped INSERT persists nothing); **leading-empty-segment names** (`..sp_tablecollations_100`, `FROM .[sys].[all_columns]`) drop their omitted db/schema positions in `BatchContext.ParseObjectName` and the FROM-source dispatch; **`sp_tablecollations_100`** is a modeled system proc returning `colid / name / tds_collation binary(5) / collation` per column (the 5-byte TDS collation from `TdsCollationCodec`, NULL for non-string columns); and **bare `SELECT TOP n *`** parses (the count is now a single operand via `Expression.ParsePrimary`, so `TOP 1 *` no longer folds into `1 * …`).
2. **`INSERT BULK` statement** (SQLBatch) — `insert bulk [schema].[table] ([col] Type [COLLATE c], …) [WITH (opt, …)]`.
   The session parses it into a `BulkInsertPlan` (target table + ordered target columns + options), answers a bare DONE (SqlClient reads it before streaming), and holds the plan.
   One `INSERT BULK` per `BatchSize` chunk (BatchSize 2 over 5 rows → three statements), the metadata pre-batch only once.
3. **BulkLoadBCP data packet** (type 7) — COLMETADATA + ROW tokens + DONE, the same wire encoding results use, decoded by `TdsBulkLoadReader`.
   The session writes the rows through `Simulation.ExecuteBulkInsert` and answers DONE with the row count.
   Wire-decode notes: SqlClient sends **FIXEDLENTYPE tokens** (INT4TYPE `0x38`, etc.) with raw un-prefixed values for NOT NULL columns and the nullable variants for nullable columns; **numeric/decimal values carry a fixed sign + 16-byte mantissa** (17 value bytes) behind a length byte that only flags NULL — the precision-implied width the byte reports is ignored.
   **Legacy `text` / `ntext` / `image` destination columns decode** (2026-07-19): the COLMETADATA TYPE_INFO is the LONGLEN form (4-byte max size, the 5-byte collation for the string pair, then a two-byte zero-part TableName field SqlClient always sends in a client value stream — no source-table identity), and the ROW value is the in-band text-pointer form (1-byte pointer length, `0` = NULL; else a 16-byte pointer + 8-byte timestamp placeholder — SqlClient fills both with `0xFF` — a 4-byte data length, then the data).
   Cleartext-probed against SqlClient 7.0.2.
   Oracle: `BulkCopyTests.LegacyLobColumns_TextNtextImage_InsertAndRoundTrip`.

**Options matrix** (probed → modeled):

| Option | Real behavior | Simulator |
|---|---|---|
| default (no options) | CHECK / FK not enforced; both left `is_not_trusted = 1` | matches — enforcement skipped, trust flipped on success via the existing plumbing |
| `CheckConstraints` | CHECK / FK enforce (Msg 547); trust unchanged | matches |
| PK / UNIQUE | always enforce, even default (Msg 2627 / 2601) | matches |
| NOT NULL | always enforces (Msg 515) | matches |
| triggers | AFTER triggers do **not** fire by default | matches |
| `FireTriggers` | AFTER triggers fire (INSERTED populated) | matches |
| KeepIdentity | expressed by the **identity column's presence in the column list**, not a WITH option; source values kept, seed advances past the max | matches (via IDENTITY_INSERT-style `ObserveExplicit`) |
| no KeepIdentity | identity column omitted from the list; server generates | matches |
| `KeepNulls` off | a NULL supplied for a defaulted column takes the DEFAULT | matches |
| `KeepNulls` on | NULL stored as NULL; omitted columns still take their DEFAULT | matches |
| computed / rowversion / period | client never sends them; server computes / stamps | matches (shared INSERT machinery) |
| external `SqlTransaction` | rollback undoes the bulk rows | matches — `ExecuteBulkInsert` runs under `RunMutation`, so the tx undo log covers it |
| `BatchSize > 0` | one `INSERT BULK` round per batch, each committing separately | matches |
| `TableLock` / `ORDER` / `ROWS_PER_BATCH` | storage-organization decorations | accepted and ignored |

**Divergences / not modeled:** client-side validations (string truncation, NULL into a DataTable NOT-NULL column) throw in SqlClient before any bytes hit the wire, so the server never sees them.
ANSI (`varchar`) bulk values decode via CP1252 / UTF-8 (the fUTF8 collation bit) like RPC params — a non-CP1252 ANSI collation would mis-decode.
`INSERT BULK` into a temp table / view isn't exercised.
The shared column decoder handles the scalar + MAX-LOB + `xml` + legacy-LOB set plus `sql_variant` / CLR-UDT columns; `sql_variant` / spatial / `hierarchyid` bulk destinations aren't separately exercised (they decode through the shared path but no `BulkCopyTests` case drives them).

## Client value decode (`TdsWireValue` + `TdsColumnDecoder`)

Client-authored values reach the endpoint in two framings — as **RPC parameters** (`TdsRpc.cs`, one TYPE_INFO-plus-value per parameter, yielding a `DbType`/`object` carrier the ADO layer binds) and as **columns inside a bulk-load / TVP row** (`TdsColumnDecoder`, COLMETADATA read once then many rows, yielding a `SqlValue` the engine coerces).
These were two parallel TYPE_INFO→value implementations; they now share one decode home:

- **`TdsWireValue`** owns the framing-independent core: the low-level primitives (`ReadPlp`, `ScaledUnitsToTicks`, `AssembleLittleEndian`, `ReadThreeByteInt`, `TimeValueBytes`, `ReadCollationUtf8`, `Epoch1900`) and the two **self-describing** value decoders — the `sql_variant` body (`ReadVariantBody`, the read mirror of `TdsTypeCodec.BuildVariantBody`) and the CLR-UDT value builder (`BuildUdtValue`: OrdPath bytes for `hierarchyid`, spatial WKB → WKT for `geography` / `geometry`, Msg 8064 / 8023 on unknown-type / invalid-bytes).
  Because these bodies carry their own type, they decode identically in either framing.
- **`TdsColumnDecoder`** is the single COLMETADATA-shaped value-decode home (bulk + TVP).
  `TdsRpc.cs` keeps only its per-parameter framing and delegates the shared pieces to `TdsWireValue`.
- **Genuinely per-framing wire differences stay separate** (not forced into one function): RPC parameters carry a decimal at its precision-implied width, while the bulk / TVP column stream always sends a fixed 17-byte (sign + 16) decimal behind a NULL-only length byte; RPC uses nullable-variant tokens throughout, the column stream uses FIXEDLENTYPE tokens with raw un-prefixed values for NOT NULL columns.
  Each framing keeps its own scalar switch over these; only the self-describing decoders and primitives are shared.

Unifying the two surfaces is what let `sql_variant` and CLR-UDT **columns** decode inside a TVP (previously rejected) and `text` / `ntext` / `image` **columns** decode inside a `SqlBulkCopy` stream — each closed against a cleartext capture of what SqlClient actually sends (see [TVP parameters](#tvp-parameters-sqldbtypestructured) and [Bulk load](#bulk-load-sqlbulkcopy)).

## TVP parameters (`SqlDbType.Structured`)

A `SqlParameter` with `SqlDbType.Structured` (value = `DataTable` / `IEnumerable<SqlDataRecord>` / `DbDataReader`, `TypeName = "schema.type"`) arrives as RPC parameter TYPE_INFO `0xF3` (MS-TDS §2.2.5.5.5 TVP_TYPE_INFO), decoded by `TdsTableValuedParameterReader` and materialized through the **same engine Structured-parameter binding the in-process ADO.NET path uses** — the wire decode produces a `TableValuedParameterData` carrier that joins `DataTable` / `IDataReader` as a third recognized source shape in `BatchContext.SeedTableVariablesFromStructuredParameters`, resolves the named `TableType`, clones it, and inserts the rows through `Simulation.InsertTableValuedParameterRow`.
Probed against SQL Server 2025 + SqlClient 7.0.2 (2026-07-18).

- **Wire form** (after the 0xF3 token): TVP_TYPENAME (db / schema / type, each B_VARCHAR — the db segment is empty, schema+type carry the `TypeName`), TVP_COLMETADATA (`USHORT` column count then per-column UserType `ULONG` / Flags `USHORT` / TYPE_INFO / ColName B_VARCHAR), the TVP_END_TOKEN `0x00`, then TVP_ROW (`0x01`) tokens each carrying every column's value, terminated by a final `0x00`.
  **Column metadata + per-value decode are the shared `TdsColumnDecoder`** (the single client-value decode home — same FIXEDLENTYPE-for-NOT-NULL and 17-byte decimal quirks; `nvarchar(N)` columns arrive as maxlen `0xFFFF` with PLP values), which routes `sql_variant` and CLR-UDT columns through the same self-describing decoders the RPC parameter path uses (`TdsWireValue` — see [Client value decode](#client-value-decode-tdswirevalue--tdscolumndecoder)).
  Column names are empty — **binding is positional** (source column N → type column N), and SqlClient sends the source's columns in source order, so a reordered / subset `DataTable` maps positionally exactly as the in-process path does.
- **Sources**: `DataTable`, `IEnumerable<SqlDataRecord>` (SqlClient serializes it to the same wire form — so it works over the wire even though the in-process path can't take it without a SqlClient dependency), and `DbDataReader` (a `SqlDataReader` from a second connection).
  Both `CommandType.StoredProcedure` (direct proc RPC — the TVP arg binds by name into a `ProcArgument.TableValue`) and `CommandType.Text` (sp_executesql, ProcID 10, the TVP as its own 0xF3 parameter) paths bind identically.
- **Error parity** (real Msg numbers, not ERROR 50000, all probe-confirmed): column-count mismatch (subset **or** extra columns) → **Msg 500**; positional type clash (reordered incompatible column, e.g. nvarchar under an int column) → **Msg 245**; NULL into a NOT NULL column → **Msg 515**; CHECK violation → **Msg 547**; PK / UNIQUE duplicate → **Msg 2627** (constraint name uses the simulator's `PK__@rows__<hex>` convention, not byte-identical); UNIQUE index duplicate → **Msg 2601**; unknown `TypeName` → **Msg 2715**.
  Enforcing NOT NULL / CHECK / PK / UNIQUE on the structured path is a fidelity upgrade shared with the in-process path (which previously skipped them via a direct `Heap.Insert`).
- **`sql_variant` / UDT columns** decode (2026-07-19): a `sql_variant` TVP column arrives as `0x62` + a 4-byte max length with a 4-byte-total-length (0 = NULL) self-describing body value; a `hierarchyid` / `geography` / `geometry` column arrives as `0xF0` + three B_VARCHARs (db / schema / type) with a PLP value — **the same wire forms the matching RPC parameters carry** (cleartext-probed inside TVP_COLMETADATA / TVP_ROW against SQL Server 2025 + SqlClient 7.0.2), so unification made them decode for near-free.
  Oracle: `TvpVariantUdtColumnTests`.
  A LOB-backed TVP column (`geography` / `geometry`, like `nvarchar(max)`) round-trips through both the `sp_executesql` text path (the parameter is a batch table variable) **and** a **stored-procedure READONLY parameter** — the proc-parameter copy re-homes off-row values into the parameter's own heap (see [`table-valued-parameters.md`](table-valued-parameters.md)); `TvpVariantUdtColumnTests.LobBackedColumns_ThroughProcReadonlyParameter_RoundTrip` covers the proc route.
- **Residuals**: a `DBNull`-valued Structured parameter is rejected by SqlClient client-side (`NotSupportedException`) before any bytes hit the wire, so the server never sees a null TVP (a wire TVP_NULL, column count `0xFFFF`, is handled defensively as an empty table).
  TVP_ORDER_UNIQUE (0x10) / TVP_COLUMN_ORDERING (0x11) optional metadata — never emitted by `DataTable` / `SqlDataReader` sources — is rejected with a clear ERROR rather than a guessed parse.
  An identity column present in a TVP raises **Msg 1077** on real SQL Server even for a `DBNull` value; the simulator's structured path auto-generates on `DBNull` (pre-existing in-process divergence, unchanged).

## CLR-UDT / sql_variant parameters

RPC parameter TYPE_INFO `0xF0` (CLR UDT) and `0x62` (`sql_variant`) decode straight into the engine's internal representations and bind by riding a **pre-built `SqlValue` on the parameter** — a fourth `SimulatedDbParameter.Value` carrier shape the variable-seed path (`BatchContext`) and the direct-proc argument binder both recognize alongside the DbType path, since no `DbType` expresses `hierarchyid` / spatial / `sql_variant`.
Both the `sp_executesql` text path (ProcID 10, the value as its own parameter) and direct proc RPC (`CommandType.StoredProcedure`, binds by name) work.
The RPC framing lives in `TdsRpc.cs` (`DecodeClrUdt` / `DecodeSqlVariant`); the self-describing value decoders themselves (`ReadVariantBody`, the UDT value builder) live in `TdsWireValue` and are shared with the TVP / bulk column decoder — see [Client value decode](#client-value-decode-tdswirevalue--tdscolumndecoder).
Probed against SQL Server 2025 + SqlClient 7.0.2 (2026-07-19).

- **CLR UDT (`0xF0`)** — the client UDT_INFO is **three B_VARCHARs** (db / schema / type; SqlClient fills only the type from `SqlParameter.UdtTypeName`, db + schema empty) with **neither** the leading `USHORT` max byte size **nor** the assembly-qualified name the server's COLMETADATA form carries — a shorter shape than the write side.
  The value is PLP: OrdPath bytes for `hierarchyid` (stored verbatim via `SqlValue.FromHierarchyIdBytes`), the MS spatial binary for `geography` / `geometry` (decoded back to WKT via `SpatialWkbDecoder.TryDecode`).
  NULL is the PLP `0xFF…` sentinel.
  Type name resolves case-insensitively.
  SqlClient serializes a typed `SqlGeography` / `SqlHierarchyId` **or** a raw `byte[]` value to the identical wire form.
  Errors: an unrecognized type name → **Msg 8064** (`Parameter N ([<current-db>].[].[<type>]): The CLR type does not exist or you do not have permissions to access it.` — the server substitutes the current database for the client's empty db segment); spatial bytes the WKB decoder can't model → **Msg 8023** (`… not a valid instance of data type geography …`, at bind).
  Invalid `hierarchyid` bytes are **stored verbatim and fail only at use** (Msg 6522 when `.ToString()` / a method runs) — matching real, which likewise accepts the bytes at bind.
- **`sql_variant` (`0x62`)** — TYPE_INFO is a 4-byte max length (ignored); the value is a 4-byte total length (**0 = NULL**) then the MS-TDS §2.2.5.5.3 body (base-type token + property-byte count + property bytes + inner value), the read mirror of `TdsTypeCodec.BuildVariantBody`, decoded into the inner `SqlValue` and wrapped by `SqlValue.FromVariant` so `SQL_VARIANT_PROPERTY(@p,'BaseType')` reports the sent type.
  SqlClient's per-CLR-type base-type choices (probe-confirmed): `int`/`bigint`/`smallint`/`tinyint`/`bit` direct; `decimal` → `numeric(38, 2)` (base token `0x6C`); `float`/`real` direct; **both `string` and `SqlString` → `nvarchar`** (varchar is not sent as a variant); `DateTime` → `datetime`; `Guid` → `uniqueidentifier`; `SqlMoney` → `money`; `byte[]` → `varbinary`; `TimeSpan` → `time(7)`.
  (SqlClient 7.0.2 mis-serializes a `DateOnly` variant — the stream truncates client-side before the server sees it; not a server concern.)
- **Output direction ships** (probe-captured RETURNVALUE shapes, SQL Server 2025 + SqlClient 7.0.2, 2026-07-19): a `sql_variant` output parameter's RETURNVALUE TYPE_INFO is `0x62` + ULONG max length (8009) with the same 4-byte-total-length + self-describing-body value a variant result column carries; a CLR-UDT output parameter's TYPE_INFO is the **COLMETADATA-shaped** UDT_INFO (USHORT max byte size — 892 for `hierarchyid`, `0xFFFF` for spatial — then db / schema / type B_VARCHARs and the US_VARCHAR assembly-qualified name; richer than the three-B_VARCHAR client request form) with a PLP value, PLP NULL for NULL.
  Real fills the db segment with the current database; the simulator reuses the column writers verbatim (empty db segment — SqlClient reads both).
  The engine value reaches the writer via `SimulatedDbParameter.OutputSqlValue`, an internal side-channel stamped at end-of-batch write-back alongside the CLR `Value` conversion, because the CLR object alone no longer carries the variant inner type / UDT kind; `TdsSession.Rpc.WriteOutputReturnValues` routes `DbType.Object`-marked outputs through the `SqlValue` overload of `TdsTypeCodec.WriteReturnValue`.
  **Variant decimal bodies are always 1 sign byte + a 16-byte magnitude** regardless of precision (probe-captured in both a result column and a RETURNVALUE): SqlClient's row-value reader tolerates a narrower magnitude but its RETURNVALUE reader reads the fixed 17 data bytes and desyncs on anything shorter — `BuildVariantDecimal` writes the fixed form.
  (**TVP columns** of UDT / `sql_variant` type also decode — the RPC decoders and the column decoder share `TdsWireValue`; see [TVP parameters](#tvp-parameters-sqldbtypestructured).)
  A `sql_variant` carrying an inner value implicitly converted to a narrower sink (e.g. `insert … (int_col) values (@variant_int)`) is accepted by the simulator's engine-level variant coercion where **real raises Msg 257** — a pre-existing in-process `sql_variant` divergence surfaced (not introduced) by wire parameters.
  Oracles: `UdtRpcParameterTests`, `SqlVariantRpcParameterTests`, `SqlVariantWireTests` (Tests.SqlClient), using the real `Microsoft.SqlServer.Types` package for typed `SqlGeography` / `SqlHierarchyId` construction (works headless on Linux; `SqlGeometry.STGeomFromText` does **not** — geometry is built from raw WKB bytes).

## Transaction Manager requests

`SqlTransaction` uses TM requests (packet type 14), not SQL text.
Begin (5) maps the wire isolation byte onto `BeginTransaction(IsolationLevel)` and answers ENVCHANGE type 8 carrying an opaque 8-byte transaction descriptor (a per-session counter; the client echoes it in ALL_HEADERS, which the listener ignores — the session connection *is* the transaction scope).
Commit (7) / rollback (8) map to the transaction object and answer ENVCHANGE 9 / 10 **carrying the ending transaction's 8-byte descriptor in the old-value field** (begin puts the new descriptor in the new-value field) — captured byte-for-byte against SQL Server 2025 (2026-07-23). The old stunted 3-byte form (type + two empty values) desynced ODBC Driver 18's manual-commit mode ("Protocol error in TDS stream"), which pairs the descriptor across begin/end; SqlClient tolerated it (its transaction tests never caught the gap).
**`fBeginXact` (bit 0 of the trailing flags byte on commit / rollback)**: the client asks the server to open a fresh transaction immediately after ending the current one — how ODBC's autocommit-off mode holds `@@TRANCOUNT` at 1 continuously (probe-confirmed; `IMPLICIT_TRANSACTIONS` stays off). The commit / rollback response then emits the end ENVCHANGE, a begin ENVCHANGE for the follow-on transaction (new descriptor), then DONE. Native DB-Library / ODBC drivers and SQLAlchemy (via pyodbc) depend on this; SqlClient begins each transaction explicitly, so it never sets the flag.
`SqlTransaction.Save(name)` (9) and `Rollback(name)` route through SQL text (`SAVE TRANSACTION` / `ROLLBACK TRANSACTION [name]`, bracket-escaped); rollback-to-savepoint keeps the transaction alive so no transaction ENVCHANGE is emitted (fBeginXact isn't meaningful there).
**Names in TM requests are B_VARBYTE** — the length prefix counts UTF-16 *bytes*, unlike the char-counted B_VARCHAR elsewhere (SqlClient writes `name.Length * 2`); misreading it as B_VARCHAR overruns the payload on every savepoint call.

## Type wire codec

Nullable wire variants throughout (INTN 0x26, BITN 0x68, FLTN 0x6D, MONEYN 0x6E, DATETIMN 0x6F, DECIMALN 0x6A, GUIDN 0x24) — legal for non-nullable data.
Fixed usertype 0 except rowversion's 0x50.
Specifics:

- **Nullability (fNullable)**: `SimulatedQueryResult.ColumnNullability` drives the per-column flag (first flags byte 0x09 nullable / 0x08 not).
  It's populated by the no-join SELECT projection with at most one source — the single-source shape (`BuildSqlProjection`) and the FROM-less shape (`BuildSynthesizedSqlRow`) — via the same `Expression.ResultIsNullable` inference SELECT INTO uses (direct refs preserve the base column's declaration, literals NOT NULL, other expressions claim nullable); every other producer leaves it null = claim all-nullable, including joined shapes (outer joins NULL-fill a side, so base-column nullability would over-claim NOT NULL — a real server marks inner-join base columns NOT NULL, a benign divergence).
  The FROM-less case matters because native tedious (and `SELECT 1`-style constant probes generally) reads the token name: without it a bare literal projected NOT NULL wrongly carried the N-variant (`select 1` → `IntN` not `Int` — surfaced by the tedious/Node driver, whose metadata exposes the token, where pyodbc / pymssql / JDBC's coarse type codes hid it).
  (The `usUpdateable` / `fComputed` bits real also sets on computed columns are not modeled — the sim writes a constant `0x08`/`0x09` flags byte; only fNullable is load-bearing and only fNullable is read by the standard client nullable accessor.)
  **The flag changes both the type token and the value encoding**: with fNullable=0 a fixed-width column carries the **FIXEDLENTYPE token** (INT1 `0x30` / INT2 `0x34` / INT4 `0x38` / INT8 `0x7F` / BIT `0x32` / FLT4 `0x3B` / FLT8 `0x3E` / MONEY4 `0x7A` / MONEY `0x3C` / DATETIM4 `0x3A` / DATETIME `0x3D` — a single byte, no max-length byte) rather than the nullable N-variant, and its ROW value is written raw at the declared width — no length prefix (`WriteTypeInfo(…, notNull)` via `TryFixedLenToken`, paired with `WriteRawFixedValue`).
  This matches real byte-for-byte (probe-captured against SQL Server 2025, 2026-07-22): the old always-N-variant form (`0x26` INTN, etc. with a max-length byte) desynced the native ODBC driver, which follows the TDS spec and reads a `0x26` value as length-prefixed.
  The other BYTELEN families (date / time / datetime2 / datetimeoffset, DECIMALN, GUIDN) have no FIXEDLENTYPE token, so they keep the N-variant + prefix regardless of the flag, as do all USHORTLEN / PLP forms.
  Load-bearing for DacFx bacpac export: the BCP data-file layout drops per-value prefixes on fixed-width NOT NULL columns per the *wire's* fNullable while the bacpac loader decodes per *model.xml* nullability — disagreement misaligns every exported row (`ColumnNullabilityWireTests`, `FixedLenTokenWireTests`).

- **decimal**: TYPE_INFO length by precision (5/9/13/17); value = sign byte + little-endian magnitude rescaled to the declared scale via `BigInteger` (round-half-up when the stored .NET decimal scale exceeds the declared).
- **money/smallmoney**: `SqlValue.AsMoneyScaledUnits` (scale-4 int64); money wire order is high-int32 then low-uint32.
- **datetime**: days since 1900-01-01 + 1/300-second units, computed from full-resolution ticks with round-half-up and day-carry at 25 920 000.
  The engine's internal 1/300 resolution transfers exactly; client-visible millisecond rounding then happens in SqlClient itself, same as against a real server.
- **date/time/datetime2/datetimeoffset**: scaled encodings per declared precision (3/4/5 value bytes by scale); datetimeoffset sends UTC time+date plus offset minutes.
- **Strings**: `varchar/char` value bytes use the code page implied by the advertised collation (`TdsCollationCodec.WireEncoding`) so the client's decode round-trips; `nvarchar/nchar/sysname` are UCS-2.
  MAX types and `xml` use known-length PLP (total length + single chunk + terminator); PLP NULL is the 8×0xFF sentinel, non-PLP NULL is length 0xFFFF.
- **rowversion**: BIGBINARY(8), big-endian counter bytes from `SqlValue.AsBytes`.
- **`sql_variant`** (type `0x62` SSVARIANTTYPE): COLMETADATA is the type byte + a 4-byte max length (8009 = 8000 data + the 9-byte inner-type header cap).
  Each ROW value is a 4-byte total length (**0 = NULL**, since a non-NULL variant always carries its ≥2-byte inner-type header, so SqlClient reads length 0 as NULL) followed by the MS-TDS 2.2.5.5.3 body: a 1-byte base-type token, a 1-byte property-byte count, the property bytes (5-byte collation + 2-byte max length for strings, precision + scale for decimal, scale for the fractional temporals), then the inner value's raw data bytes.
  Integer / bit / string / NULL are the SqlClient-oracle-verified subset (`SqlVariantWireTests`); the remaining base types follow the same layout.
  `TdsTypeCodec.BuildVariantBody` builds it.
  See [`catalog-views.md`](catalog-views.md) for the type model.
- **`geography` / `geometry`** (UDTTYPE `0xF0`, MS-TDS 2.2.5.5.2): COLMETADATA is a ushort max-byte-size (`0xFFFF` = max) then three B_VARCHAR names (db / schema / type — the db name goes empty because the static codec can't reach the session's current database; schema is `sys`, type is `geography`/`geometry`) and the US_VARCHAR assembly-qualified name (`Microsoft.SqlServer.Types.SqlGeography/SqlGeometry, …, Version=11.0.0.0, …, PublicKeyToken=89845dcd8080cc91`, probe-matched to SQL Server 2025).
  SqlClient surfaces the column as `SqlDbType.Udt`; with `Microsoft.SqlServer.Types` absent (the DacFx case) `GetValue` throws but `GetSqlBytes`/`GetBytes` return the raw bytes.
  The value is PLP (like `varbinary(max)`) carrying the CLR-UDT serialization synthesized from the stored WKT by `SpatialWkbEncoder` — see [`spatial.md`](spatial.md).
  Removing these from the reject list is what unblocked DacFx bacpac export of WWI's four `geography` columns.
- **`hierarchyid`** (UDTTYPE `0xF0`): the same UDT arm as spatial but with hierarchyid's fixed max byte size **892** (not the `0xFFFF` max sentinel — probe-confirmed 2026-07-16 via `GetSchemaTable` `ColumnSize`), schema `sys`, type `hierarchyid`, and the `Microsoft.SqlServer.Types.SqlHierarchyId, …, Version=11.0.0.0, …, PublicKeyToken=89845dcd8080cc91` AQN.
  Value is PLP carrying the value's canonical OrdPath bytes verbatim (`SqlValue.AsHierarchyIdBytes`, zero-copy — hierarchyid stores that byte form; see [`hierarchyid.md`](hierarchyid.md)); NULL is the PLP `0xFF…` sentinel.
  SqlClient surfaces it as `SqlDbType.Udt`; DacFx pulls raw bytes via `GetSqlBytes`/`GetBytes`.
  Removing it from the reject list unblocked DacFx export of AW's `OrganizationNode` / `DocumentNode`.
  Oracle: `HierarchyIdWireTests`.
- **`text` / `ntext` / `image`** (legacy in-band textptr form) — see [Legacy text / ntext / image](#legacy-text--ntext--image-wire-forms) below.
- Every modeled result-column type now has a wire encoding; an unmodeled one would surface as `WriteTypeInfo`'s `NotSupportedException` → ERROR 50000.

## Legacy text / ntext / image wire forms

The three deprecated large-object types stream over the wire in their pre-PLP in-band form.
Wire shapes probe-captured against SQL Server 2025 (17.0.4065.4) through a cleartext tee proxy — `Encrypt=False` leaves post-login tokens in the clear — 2026-07-19.
`TdsTypeCodec` emits the result side; `TdsRpc.DecodeImage` / `DecodeLegacyLob` read the parameter side.

**COLMETADATA TYPE_INFO.**
LONGLEN types: type byte, then a 4-byte max size — `0x7FFFFFFF` for `text` (0x23) and `image` (0x22), `0x7FFFFFFE` for `ntext` (0x63).
`text`/`ntext` then carry the 5-byte collation (`image` has none).
All three then carry the **TableName field these types uniquely carry** — a `NumParts` byte then that many US_VARCHAR (2-byte-char-count) parts.
Real sends the value's base object: a plain or aliased base-table column reports `[schema, table]` (the alias is discarded); a view column reports `[schema, view]`; an expression column (`CAST('x' AS text)`) reports **NumParts 1 with a single empty part**.
The simulator's result metadata doesn't thread per-column source identity, so it emits that empty-expression form for **every** legacy-LOB column — a documented divergence for base-table/view columns (the field surfaces only as the cosmetic `BaseTableName` in `GetSchemaTable`; SqlClient accepts the empty form real itself sends for expressions).

**ROW value.**
A 1-byte text-pointer length, the text pointer, an 8-byte timestamp, a 4-byte data length, then the raw data.
NULL is a single `0x00` text-pointer-length byte (no timestamp/length/data).
Real sends a **fixed 16-byte placeholder text pointer** (ASCII `"dummy textptr\0\0\0"`) and **8-byte timestamp** (`"dummyTS\0"`) for every value; SqlClient ignores their content, so the simulator mirrors the literal bytes.
Data bytes are CP1252 (per the column collation) for `text`, UTF-16LE for `ntext`, verbatim for `image`; an empty non-NULL value is text-pointer present + data length 0.
A value larger than one packet is written as one contiguous data block — the transport packetizes it, and the real server does the same (probed a 100 KB image / 120 KB ntext).

**Input parameters.**
`image` (0x22): 4-byte LONGLEN max size (no collation), then a 4-byte data length (`0xFFFFFFFF` = NULL) and the raw bytes — the contiguous legacy form, **not PLP**, even for a >1-packet value; binds as `DbType.Binary` and the engine coerces varbinary into the target `image` column.
`text` (0x23) / `ntext` (0x63) parameters were already decoded (`DecodeLegacyLob`, the `sp_executesql` LOB path above).
Both direct-proc RPC and `sp_executesql` bind identically.
Output-direction legacy-LOB parameters aren't a real SQL Server feature and don't arrive.

**`SET TEXTSIZE` truncates at wire egress** (shipped 2026-07-19): the session byte cap clips `text`/`ntext`/`image` and the MAX-typed trio in result columns and output parameters — the truncation rides the shared client-boundary cursor (`SimulatedQueryResult.CreateClientCursor` → `TextSizeCursor`), so the TDS row writer inherits it; see [`scalars.md`](scalars.md) for the full semantics.
**`SqlBulkCopy` into a `text`/`ntext`/`image` column** decodes (see [Bulk load](#bulk-load-sqlbulkcopy) step 3).
Oracles: `LegacyLobWireTests`, `BulkCopyTests.LegacyLobColumns_TextNtextImage_InsertAndRoundTrip`, `TextSizeWireTests`.

## Collation wire structure

COLMETADATA's 5 bytes = packed uint (LCID bits 0–19, flags 20–27, version nibble 28–31, little-endian) + sortId byte.
Derived generatively per collation (cached by interned reference), validated against a full 5540-name probe of the live reference (2026-07-13):

- Flags from name tokens: CI→bit20, AI→bit21, absent WS→bit22 (ignore-width), absent KS→bit23 (ignore-kana), BIN→bit24, BIN2→bit25.
  **Width=22/Kana=23 is load-bearing** for KS/WS-only names and contradicts MS-TDS's field-declaration order — the spec's own assembly line and SqlClient's constants agree with 22/23, probe-confirmed via `_CI_AS_KS` (0x05) / `_CI_AS_WS` (0x09).
- `_UTF8` → bit26, and it **displaces** the binary bits (`_BIN2_UTF8` reports 0x40, not 0x60); sensitivity bits are retained.
  Code page becomes 65001.
- `_SC` / `_VSS` set no wire bit — the structure is lossy (5540 names → 3987 distinct tuples).
- Version nibble from the name's number token: none/SQL_*→0, 90→1, 100→2, 140→3, **160→4**.
- LCID + ANSI code page per name-prefix from the core-layer `Collation.LcidAndCodePageByPrefix` (probe-derived, one entry per `KnownPrefixes` key); SQL_* code page comes from the CPnnn token (CP1→1252).
  Anomaly: `SQL_Latin1_General_CP1254_*` reports the Turkish LCID 0x041F (special-cased).
- Baseline `SQL_Latin1_General_CP1_CI_AS` derives to the canonical `09 04 D0 00 34` (sortId 52).

## Login response shape

ENVCHANGE(database, old `master`) → INFO 5701 → ENVCHANGE(language `us_english`) → INFO 5703 → ENVCHANGE(SQL collation, type 7: the server collation's 5-byte structure) → LOGINACK (TDS 0x74000004 big-endian on the wire, prog name `Microsoft SQL Server`, ProgVersion `17.0` with build **4065** — big-endian `0x0FE1` — in the low 16 bits) → ENVCHANGE(packet size) → DONE.
Server name in every token is `SIMULATED` (matches `SERVERPROPERTY`).
Build 4065 makes `SqlConnection.ServerVersion` report `"17.00.4065"`, and the prelogin VERSION option carries the same build — both mirror the SQL Server 2025 reference (17.0.4065.4) the simulator emulates.
A real build number is load-bearing for SSMS's per-build client feature gates (Activity Monitor / report viewer), which stop immediately on the prior build-0 identity.
**The collation ENVCHANGE is load-bearing for RPC**: SqlClient stores it as the default collation it stamps onto outbound parameter TYPE_INFO, and without it every parameterized command dies client-side in a `NullReferenceException` before any bytes hit the wire.

## Divergences / deferred

- Login INFO states are approximations.
- MARS and TDS 8.0 / `Encrypt=Strict` ship (see [MARS](#mars-multiple-active-result-sets) below and the strict paragraph up top); no plaintext sessions, no integrated auth (an SSPI/FedAuth login presents an empty SQL username, which under a non-empty registry fails as Msg 18456 rather than negotiating).
- Credential-enforcement edges not modeled: `ALTER LOGIN … DISABLE` parses but doesn't block login; password policy (`CHECK_POLICY` / expiration / lockout) never enforced; no login auditing.
- RPC parameters are gap-free in both directions: every input TYPE_INFO is accepted — TVP / UDT / `sql_variant` / `text` / `ntext` / `image` — every client value-stream column type (bulk / TVP) decodes through the shared `TdsWireValue` / `TdsColumnDecoder`, and output-direction UDT / `sql_variant` parameters write back as RETURNVALUE tokens (see [CLR-UDT / sql_variant parameters](#clr-udt--sql_variant-parameters)).
  Non-cursor well-known ProcIDs beyond the sp_execute/sp_prepare family are rejected with ERROR 50000 naming the id.
- Mid-stream attention (cancel) ships — see below.
  Residual: cancel reaction is bounded by the current statement's *materialization*, not just its streaming, because a statement's rows are materialized in one synchronous step before they stream (a cancel mid-way through a compute-heavy `SELECT`/sort waits for that materialization to finish, then discards the result at the outcome boundary; real streams-as-it-computes and stops sooner).
  The common streaming-bound drain interrupts promptly between rows.
  A single in-flight DML statement likewise runs to completion before the abort is observed (no interior row-loop cancellation), so it isn't rolled back the way real's mid-statement abort would; multi-statement batches abort at the statement boundary correctly.
- SPID in packet headers truncates to 16 bits.
- **TDS 7.1–7.4 clients connect** (the LOGINACK always answers 7.4, or 8.0 under strict); **TDS 7.0** (SQL Server 7.0 / 1998, `tds_version=7.0` in FreeTDS/pymssql) is **not modeled** — its divergent pre-modern-PRELOGIN handshake makes the session close early ("Unexpected EOF" client-side) rather than complete.
  Real SQL Server 2025 still accepts 7.0, so matching it would mean implementing a 27-year-old handshake variant no modern client (SqlClient, JDBC, ODBC 18, pymssql-default) ever requests and the managed oracle can't exercise; deferred as a legacy-protocol edge with no fidelity payoff short of full support.
- **Temp tables created by an RPC `sp_executesql` / `sp_execute` / `sp_prepexec` statement are module-scoped** (dropped when the RPC returns), matching real — so tedious's `execSql`, which routes every statement through `sp_executesql`, can re-run a `create table #t` without a Msg 2714 collision and doesn't leak the temp onto the session. The command carries `ScopeTempTablesToBatch`; the engine drops the batch's registered temps in `CreateResultSetsForCommand`'s finally. See [`temp-tables.md`](temp-tables.md).

## Mid-stream attention (cancel)

A client sends a TDS attention (packet type 6) when `SqlCommand.Cancel()` is called or `CommandTimeout` expires mid-execution/mid-drain, then waits for the server to acknowledge with a DONE carrying the `DONE_ATTN` flag before treating the connection as reusable.
The server emits **no error token** — SqlClient manufactures the surfaced exception from its own state (Msg -2 "Execution Timeout Expired" for a timeout; Msg 0 "Operation cancelled by user" for an explicit cancel), so the endpoint's whole job is to notice the attention, stop, and ack.
Semantics probed against SQL Server 2025 (2026-07-18).

**Noticing it — carry-forward concurrent read.**
The batch loop keeps exactly one inbound `ReadMessageAsync` in flight at all times.
Between requests it *is* the next request; while a SQLBatch / RPC executes it doubles as the attention watcher — in non-MARS TDS the client sends nothing but an attention until it has drained the current response, so a completed read during execution is the cancel.
A continuation fires `SimulatedDbConnection.CancelExecution()`, which the engine and the row streamer poll.
The in-flight read is **never cancelled** — it is carried across loop iterations (spent read after an attention → fresh read; otherwise it becomes the next request) — so a partially-consumed read can never corrupt packet framing.
This is why a concurrent read rather than pure safe-point polling is required: `WAITFOR DELAY` blocks the session thread, so a poll can't run while it sleeps; the watcher signals from the reader side and the wait wakes on the token's wait handle.
Bulk-insert-begin (`INSERT BULK`, whose next packet is the bulk-data type-7, not an attention) is excluded from the watcher.

**Where the engine stops (safe points).**
`SimulatedDbConnection` owns a per-execution `CancellationTokenSource`, replaced at the top of `CreateResultSetsForCommand` (so a cancel against a prior command on the same connection doesn't bleed forward) and connection-scoped so proc / UDF / dynamic-SQL bodies inherit it.
`DispatchStatementsUntil` and the `WHILE` loop poll it at statement / iteration boundaries; `WAITFOR DELAY` waits on its wait handle; `StreamOutcomesAsync` polls between outcomes and between rows (never mid-ROW-token).
On cancel the streamer returns a "cancelled" flag; the batch loop applies the transaction semantics below and writes the single `DONE_ATTN`.

**Transaction / session semantics (probed).**
Under the default `SET XACT_ABORT OFF`, a cancel leaves an open transaction **intact and usable** (`@@TRANCOUNT` unchanged, committed statements' rows preserved).
Under `SET XACT_ABORT ON`, the cancel **rolls the transaction back** (`@@TRANCOUNT` → 0).
XACT_ABORT is recorded onto the connection solely to drive this (see `SimulatedDbConnection.XactAbort`); its broader error-abort semantics stay parse-and-discard.
Batch-scoped variables go with the ended batch either way; connection-scoped temp tables persist.
`SimulatedDbCommand.Cancel()` routes to the same machinery, so an in-process `Cancel()` from another thread interrupts a running `WAITFOR` / long batch identically (the reader then drains already-materialized rows, nothing left in flight — the documented in-process reaction bound).
Oracle: `AttentionTests` (Tests.SqlClient), `WaitForDelayTests.Delay_InterruptedByInProcessCancel_ReturnsPromptly` (Tests).

**Partial response packets don't flush early (probed 2026-07-20).**
The server accumulates response tokens into a TDS packet and sends it only when the packet fills or the response ends — real SQL Server behaves identically: for `select @p; waitfor delay '00:00:30'` the one-row first result set sits in the send buffer for the full 30 seconds and the client's first `ReadAsync` blocks until the batch ends, on real and sim alike.
Two consequences: a client can't observe an early small result set to sequence a cancel after (the cancel must land while the client read is parked — `AttentionTests` retries via `CancelUntilComplete` for exactly this reason), and the attention path is the only way a blocked mid-batch client wakes early.
A `SqlCommand.Cancel()` racing execution *start* is different: a cancel before the batch begins executing is a documented client-side no-op, and one landing inside `Execute*Async`'s setup surfaces SqlClient's own `InvalidOperationException` instead of the cancel `SqlException` — client-side races independent of the server, so tests never fire a single timer-based cancel.

## MARS (Multiple Active Result Sets)

`MultipleActiveResultSets=True` lets a client run a second command while a reader is still open — the EF-lazy-load shape (iterate a parent query, touch a navigation per row).
Without it SqlClient rejects the overlap client-side ("There is already an open DataReader…").
Negotiation, SMP framing, concurrency model, and probed semantics (SQL Server 2025 + SqlClient 6.0.2 / 7.0.2, 2026-07-18) below.

**Negotiation (strictly additive).**
The prelogin MARS option is acked `1` **only when the client requested `1`** (`ParsePreloginMars`); a client that doesn't ask, or asks `0`, gets `0` and a byte-identical non-MARS session.
Prelogin, TLS, and LOGIN7 stay raw — the login response is unwrapped.
Only *post-login* TDS messages are SMP-wrapped.

**SMP framing ([MC-SMP]).**
Every post-login TDS packet rides a 16-byte SMP header: SMID `0x53`, FLAGS (SYN `0x01` / ACK `0x02` / FIN `0x04` / DATA `0x08`), SID (`ushort`), LENGTH (`uint`, header-inclusive), SEQNUM (`uint`), WNDW (`uint`) — all little-endian.
`SmpMultiplexer` owns the socket: one read loop demuxes frames into per-session `SmpSession`s, each exposing an `SmpSessionStream` (inbound via a `System.IO.Pipelines.Pipe`, outbound wrapped into DATA frames) that a per-session `TdsPacketTransport` rides — so the existing TDS batch loop (`RunMarsSessionAsync`) is unaware it is multiplexed.
Frame flow **ground-truthed against the real SQL Server 2025** (captured cleartext through a tee proxy: `Encrypt=False` encrypts only the login packet, leaving post-login SMP frames in the clear; 2026-07-18) — see the server-to-client rules below.

**Native vs managed SNI strictness (load-bearing).**
On Linux, SqlClient uses managed SNI, which is lenient about SMUX; on Windows it uses the closed-source **native SNI**, which validates SMUX strictly and drops the physical connection ("Failed to establish a MARS session … Physical connection is not usable", **SMux provider error 19**) on frames the real server never sends.
Matching *managed SNI's tolerance* is not enough — the server-to-client shape must match what the real server emits (which is what native SNI provably accepts).
Two shapes that managed SNI tolerated but native SNI rejected, both corrected: a **server-emitted SYN** (see below), and **an ACK after every received DATA**.
The `SmpFrameTests` (Tests.Internal) pin the corrected shape so a regression fails on Linux, not only on a Windows host.

Probed frame flow:

- The client opens **session 0** (the primary) with a SYN at connect, then a **new SID per concurrent command** (SYN + DATA carrying the SQLBatch/RPC).
  A reader's session **stays open** (FIN only when disposed); SqlClient **reuses a SID** for successive commands once its prior reader closes (the inner-query loop cycled one SID for all five iterations).
  SEQNUM is 0 on a SYN and increments per DATA (1, 2, 3…).
- **The server sends NO SYN.**
  A client SYN opens the session; the server's *first* server-to-client frame on it is the DATA response (the real server never SYN-acks — emitting one is exactly the native-SNI error-19 violation).
- **Window.**
  WNDW advertises the sender's receive window — *the highest SEQNUM the peer may send*.
  Advancing rules, both directions:
  - *Client → us:* the client piggybacks its window on the **WNDW field of every frame** (DATA carried wnd 4, 5, 6, 7, 8) and sends **no standalone ACKs**, so a session's send window must advance from **every inbound frame's WNDW**, not just ACK frames — updating only on ACK stalls the send window at the SYN's 4.
    `WaitForSendWindowAsync` blocks a DATA send until the window permits.
  - *Us → client:* the server advertises `received + 4` and, matching the real cadence, sends **no standalone ACK for a complete (EOM) request** — the DATA response the batch loop produces piggybacks the advanced window.
    A **standalone ACK is emitted only when consuming client data with no immediate DATA to answer it**: a mid-message (EOM-clear) packet of a multi-packet client request (else the client's send window stalls at its initial size), and an attention taken in while a command runs.
    The ACK's SEQNUM is the session's last-sent DATA sequence; its WNDW is `received + 4`.
- **FIN.**
  On the client's FIN (or connection close) the server **echoes a FIN per session**, SEQNUM = last DATA sequence sent on that session, WNDW = `received + 4`.

**Concurrency model — cooperative, never parallel.**
Real MARS multiplexes cooperatively; only one batch executes at a time, and the engine assumes one executor per connection (`CurrentExecutingThreadId`, transaction machinery).
All logical sessions share the **one** backing `SimulatedDbConnection`; a per-connection `SemaphoreSlim(1,1)` (`engineExecutionGate`) serializes engine execution.
A session acquires the gate, drives the engine, and **buffers its whole response** (the session's `TdsTokenWriter` runs in `DeferFlush` mode — intermediate per-row flushes accumulate rather than send), then releases the gate and does the single window-controlled flush.
Buffering-under-lock is what avoids the deadlock a naive "stream row-by-row while holding the lock" would hit (session A blocked on its send window while holding the gate B needs): A's engine work finishes and the gate frees before A's window-blocked send begins.
It maps cleanly onto the one-executor assumption because A's outcome enumerator is fully drained and disposed before the gate releases — no suspended-mid-iteration engine state, no two live enumerators on the engine.
The cost is a fully-materialized response per session (bounded by the largest un-drained response), matching the existing "materialize in one step" residual.

**Interleaving / DML (probed).**
A concurrent DML on a second session while a SELECT reader is open runs without deadlock (real interleaves SELECT at statement boundaries, runs DML atomically; the buffered model matches — A's SELECT fully materializes, B's DML runs).
The simulator never produces the "MARS batch interrupted" **Msg 8628/8651** family (full serialization means no interior interrupt point) — a divergence, not reachable by the lazy-load / split-query shapes.

**Shared session state (probed identical on real).**
One `@@SPID` across all sessions; temp tables and `SET` state shared; the connection-level transaction shared.
A `SqlTransaction` spanning two overlapping commands commits/rolls back both.
A command that **omits its `Transaction` property while a `SqlTransaction` is open is rejected by SqlClient client-side** (`InvalidOperationException` "requires the command to have a transaction when the connection … is in a pending local transaction") — **not** a server Msg 3997/3988, so there is nothing for the endpoint to enforce.

**Attention interplay (per session).**
A client attention (cancel/timeout) arrives as a type-6 TDS packet in a DATA frame on **one** session.
The multiplexer routes it: if that session is mid-execution (`Executing`), it fires `CancelExecution()` on the shared connection (only the executing session holds the gate, so the current scope is that session's) and leaves an atomic `AttentionState` flag; if the session is idle, it feeds the attention through the pipe so the parked read wakes.
The session loop consumes the flag with an `Interlocked.Exchange` **after** clearing `Executing`, de-duping against the pipe-delivered copy so exactly one site emits the single `DONE_ATTN` — closing the race where an attention lands just as execution completes and the cancel no-ops.
A cancel on one session never disturbs another session's reader.

**In-process contract.**
The in-process `SimulatedDbConnection` has no wire and no MARS enforcement — overlapping readers and a second command mid-reader **already work** (probe-confirmed: nested reader-per-row and two interleaved readers both return correct results), because result sets materialize before streaming, so no two live enumerators race shared engine state.
This is the deliberate contract: the in-process stand-in behaves like a MARS-enabled connection (the permissive superset EF's lazy loading needs).
See [`data-reader.md`](data-reader.md#in-process-mars-overlapping-readers).

**Divergences (all frame-shape-safe — spec-consistent frames native SNI accepts):** Msg 8628/8651 never raised; a session's response fully materializes under the gate (memory-bound for very large results, same class as the attention-materialization residual); the mid-message ACK fires once **per** EOM-clear packet whereas the real server ACKs roughly every two (both advance a monotonic `received + 4` window, so the extra ACKs are harmless); a cancel's DONE_ATTN rides one DATA packet where the real server split it across two; and the attention-during-execution ACK can trail its DATA response by a thread-scheduling race (its SEQNUM still equals the last-sent DATA sequence, so it stays spec-valid).
**Not verified on native SNI from this environment** (Linux has only managed SNI): the frame-shape corrections above are derived from the real cleartext trace — the shape native SNI provably accepts — but a Windows re-test is the final confirmation.

## Testing

`SqlServerSimulator.Tests.SqlClient` is the loopback oracle: real `Microsoft.Data.SqlClient` (direct package reference) against `ListenLocalAsync(0)` endpoints.
Where expected values are nontrivial (datetime tick rounding, money scale, collation-dependent varchar bytes), tests use the dual-read pattern — the same query through the in-process ADO surface and through the wire against the same `Simulation`, asserting equality — so the in-process behavior contract stays the single source of truth.

`SqlServerSimulator.Tests.Smo` is the second consumer oracle: real SMO (`Microsoft.SqlServer.SqlManagementObjects`, pinned 172.76.0 — the library behind SSMS Object Explorer + Script-As) connecting over loopback, the permanent home of the SSMS-shakedown surface.
One `[AssemblyInitialize]` fixture (`SmoFixture`) seeds a compact WWI-shaped schema in-code (no bacpac — repo policy) across two schemas (`Sales` / `Application`): identity clustered PKs, a cross-schema FK web plus a multi-column FK, `INCLUDE` + filtered nonclustered indexes, named + auto-named DEFAULT constraints, a CHECK, a computed column, a `rowversion`, a system-versioned temporal pair, extended properties on a table + column, an AFTER INSERT trigger, a view + a proc, and seed rows.
Coverage: Object-Explorer enumeration (`server.Databases` / `db.Tables` / a table's `Columns` / `Indexes` / `ForeignKeys` / `Triggers` / `db.Views` / `db.StoredProcedures` / `ExtendedProperties`); `table.Script(...)` CREATE output asserted line-by-line (`StringAssert`/`Assert.Contains`, not a golden match) for the header, columns, identity, PK, FK+references, INCLUDE index, DEFAULT, CHECK, and the temporal `PERIOD FOR SYSTEM_TIME` / `SYSTEM_VERSIONING = ON` clauses; and `sys.dm_exec_sessions` liveness through the SMO connection.
Graduating this surface exposed and fixed four engine gaps: named inline `CONSTRAINT n DEFAULT (expr)` parsing (the column-constraint switch handled CHECK/REFERENCES/PRIMARY/UNIQUE but not DEFAULT), a `type` / `type_desc` column pair added to `sys.views`, and the catalog views `sys.all_views` / `sys.all_sql_modules` (mirroring `sys.views` / `sys.sql_modules` like `sys.all_objects`), `sys.assemblies` (empty CLR view), and `sys.database_scoped_configurations` (fresh-database defaults) that SMO's Script-As queries read.
