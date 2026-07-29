# Claude Working Notes — `Network/` (TDS endpoint internals)

Auto-loaded when working in this directory.
The behavior deep-dive is [`docs/claude/tds-endpoint.md`](../../../docs/claude/tds-endpoint.md) — protocol scope, response shapes, divergences, deferred items.
These notes are the local implementation contracts.

- **Session layout**: `TdsSession` is a partial — main file (prelogin/TLS/login/batch loop + `StreamOutcomesAsync`), `.Rpc.cs` (RPC dispatch + prepared-handle map + `WellKnownProcId` name/id map), `.Cursors.cs` (the `sp_cursor*` API-server-cursor family), `.TransactionManager.cs` (TM requests + the session `transaction` field), `.BulkLoad.cs` (the `SqlBulkCopy` handshake: `INSERT BULK` detect/parse-ack + BulkLoadBCP packet decode via `TdsBulkLoadReader`, feeding `Simulation.ExecuteBulkInsert`).
- **API server cursors** (`.Cursors.cs`): `sp_cursoropen/fetch/close/prepexec/execute/prepare/unprepare/option` + `sp_cursor` positioned DML.
  Each handle in the per-session `apiCursors` map wraps an engine `Cursor` **registered under an opaque `sss_apicursor_<n>` name in `connection.Cursors`** (so `WHERE CURRENT OF` resolves it) — built by synthesizing `DECLARE … CURSOR … FOR <stmt>; OPEN` and reusing the engine's sensitivity resolution.
  Fetch drives `Cursor.Fetch` directly per row over a throwaway `BatchContext` (its command needs a non-empty `CommandText` — `" "` — or `ParserContext` throws); positioned DML sets `Cursor.CurrentRid` to a buffered RID then runs synthesized `UPDATE/DELETE … WHERE CURRENT OF`.
  **Prepared/parameterized cursors freeze their bound params on the ApiCursor and re-apply them to every fetch batch** — a keyset/dynamic cursor re-runs its SELECT per fetch, so the fresh batch would otherwise miss the variables.
  Result sets append a trailing `ROWSTAT` int column.
  Concurrency options (SCROLL_LOCKS/OPTIMISTIC) are echoed but not wired (probe-confirmed API-cursor optimistic conflicts don't surface).
  Contract + probed constants in [`docs/claude/tds-endpoint.md`](../../../docs/claude/tds-endpoint.md).
  One task per socket; teardown is socket-close-driven.
  The typed catch list in `RunAsync` (`IOException` / `SocketException` / `ObjectDisposedException` / `OperationCanceledException` / `InvalidDataException` / `AuthenticationException`) + the per-handler `SimulatedSqlException` / `NotSupportedException` conversions handle every anticipated failure, so wire-format parse failures must throw `InvalidDataException`, unsupported features `NotSupportedException` (converted to ERROR tokens in handlers, never leaked).
  **Behind them are two tiers of backstop, and the difference is whether the session survives.**
  - **Statement tier** (`IsRecoverableStatementFault` + `WriteUnexpectedStatementFault`, wired into every message handler — batch, RPC, bulk load, TM): an unanticipated exception raised while executing one statement becomes **Msg 50000 / severity 16** naming the exception type (`"SqlServerSimulator: unhandled OverflowException: …"`), and the connection stays usable.
    This is the common case and the one that matters most in bulk: before it, a single unmodeled statement took the whole connection with it, so in a client test suite every later test sharing that connection failed too — one measured Django run had a single such statement account for **27 of 50** failures.
    Excluded from the tier: the transport / cancellation types (they belong to the session loop, which owns disconnect and attention), and any fault where `AtTokenBoundary` is false.
  - **Terminal tier** (`TryWriteSevereErrorAsync`): what's left — a mid-token fault, or one raised outside a statement handler — emits a best-effort **Msg 0 / severity 20** ERROR (`SevereErrorMessage`) so SqlClient surfaces a `SqlException` and marks the connection dead, then the connection closes.
    Same backstop on the MARS per-session loop, so one logical session's crash doesn't silently kill the mux.

  The forcing seam for the terminal tier is a per-`Simulation` internal hook (`NetworkBatchCrashHookForTesting`, never public) invoked *before* the handler's try, so it still escapes to the terminal boundary; the statement tier is forced by a genuinely unmodeled statement instead.
  **A new message handler needs the statement-tier catch too** — without it, its unanticipated faults fall through to the terminal tier and cost the session.
  Oracles: `CrashBoundaryTests` (both tiers), `TokenBoundaryTests`.
- **MARS / SMP layer** (`SmpMultiplexer` / `SmpSession` / `SmpSessionStream`): active only when the client requested MARS in prelogin (`ParsePreloginMars` → ack `1`, strictly additive — non-MARS stays byte-identical).
  After the raw login response, `RunAsync` hands the post-TLS stream to `SmpMultiplexer`, which owns it: one read loop demuxes 16-byte SMP frames ([MC-SMP]) into per-`SmpSession` pipes, and `RunMarsSessionAsync` runs one TDS batch loop per session over an `SmpSessionStream`-backed `TdsPacketTransport`.
  **All sessions share one `SimulatedDbConnection`**; the per-connection `engineExecutionGate` (`SemaphoreSlim(1,1)`) serializes engine execution — cooperative multiplexing, never parallel (the engine assumes one executor per connection).
  Each session **buffers its whole response under the gate** (`TdsTokenWriter.DeferFlush` skips intermediate flushes) then flushes once *after* releasing the gate, so a window-blocked send never stalls another session (the deadlock a stream-under-lock model would hit).
  **Native-SNI-safe server frame shape (ground-truthed against the real server; Windows native SNI drops the connection with SMux error 19 on shapes managed SNI on Linux tolerates):** the server sends **NO SYN** (a client SYN opens the session; our first frame on it is the DATA response), and **NO standalone ACK for a complete (EOM) request** — the DATA response piggybacks the `received + 4` window.
  A standalone ACK fires only for a mid-message (EOM-clear) packet of a multi-packet client request and for an attention taken in mid-execution.
  FIN is echoed per session (SEQNUM = last-sent DATA seq, WNDW = received + 4).
  **Window trap (client→us)**: advance a session's send window from the WNDW field of **every** inbound frame, not just ACK — SqlClient piggybacks its window on DATA frames and sends no standalone ACKs, so ACK-only updates stall the send window at the SYN's 4.
  `SmpFrameTests` (Tests.Internal) pin the server frame shape via the `ISmpHost` seam so a regression fails on Linux.
  **Attention** is per-session: the mux fires `CancelConnectionExecution()` when the target session is `Executing`, else feeds the type-6 packet through the pipe; the loop consumes an `Interlocked` `AttentionState` flag after clearing `Executing` to de-dupe against the pipe copy and emit exactly one `DONE_ATTN`.
  Contract + probed frame flow / semantics in [`docs/claude/tds-endpoint.md`](../../../docs/claude/tds-endpoint.md#mars-multiple-active-result-sets).
- **Never throw mid-token**: a `WriteTypeInfo` / `WriteValue` throw after the COLMETADATA / ROW token byte has been written leaves a partial token in the buffer, and appending anything (including the crash backstop's ERROR) desyncs the stream.
  `TdsTokenWriter.AtTokenBoundary` tracks this — true whenever the buffer ends at a complete token (self-contained token methods run to completion synchronously), and the three interleaving composites (`WriteColMetadata`, `WriteRow`, `WriteReturnValue`) bracket their body with `EnterComposite()` / `LeaveComposite()`, so a mid-body throw leaves it false and the backstop declines to append.
  **If you add a composite token writer that interleaves a throw-capable sub-write, bracket it the same way.**
  Every modeled result-column type now encodes, so a mid-COLMETADATA throw is latent; the flag is the safety net if one reappears.
  (The old `ValidateSchema` up-front pass — which only rejected `text`/`ntext`/`image` — was removed when those types gained an encoding.)
- **Client value decode is unified**: RPC parameters (`TdsRpc.cs`) and bulk / TVP row columns (`TdsColumnDecoder`) share `TdsWireValue` — the low-level primitives (`ReadPlp` / `ScaledUnitsToTicks` / `ReadCollationUtf8` / …) and the self-describing `sql_variant` body (`ReadVariantBody`) + CLR-UDT value builder (`BuildUdtValue`).
  `TdsColumnDecoder` is the single COLMETADATA-shaped value-decode home (bulk + TVP), and it now handles `sql_variant` (0x62), CLR-UDT (0xF0), and legacy `text`/`ntext`/`image` (0x23/0x63/0x22) columns.
  **Genuinely per-framing wire differences are NOT forced into one function** — RPC decimals carry their precision-implied width while the column stream always sends a fixed 17-byte decimal, and the column stream uses FIXEDLENTYPE-for-NOT-NULL where RPC uses nullable variants — so each framing keeps its own scalar switch; only the primitives and self-describing decoders are shared.
  Add a new self-describing / body-carried type to `TdsWireValue`; add a new per-framing scalar to whichever switch(es) actually receive it.
- **Legacy `text` (0x23) / `ntext` (0x63) / `image` (0x22)**: LONGLEN result columns.
  COLMETADATA = type byte + 4-byte max size (`0x7FFFFFFF` text/image, `0x7FFFFFFE` ntext) + 5-byte collation (text/ntext only) + the TableName field (`WriteLegacyLobTableName` emits real's expression form: NumParts 1 + one empty US_VARCHAR — the result metadata carries no per-column source table).
  ROW value (`WriteLegacyLob`) = text-pointer-length byte + 16-byte placeholder pointer (`"dummy textptr\0\0\0"`) + 8-byte timestamp (`"dummyTS\0"`) + 4-byte data length + data (CP1252 text / UTF-16LE ntext / verbatim image); NULL is a single `0x00`.
  `image` **input** parameters decode (`TdsRpc.DecodeImage`): 4-byte max size + 4-byte data length (`0xFFFFFFFF` = NULL) + raw bytes, contiguous (not PLP) even multi-packet, bound as `DbType.Binary`.
  **`SqlBulkCopy` into a `text`/`ntext`/`image` column decodes** (`TdsColumnDecoder`'s legacy-LOB arm): COLMETADATA LONGLEN TYPE_INFO (4-byte max size + 5-byte collation for the string pair + a two-byte zero-part TableName field SqlClient sends in a client value stream) then the in-band text-pointer ROW value (1-byte ptr length `0`=NULL, else 16-byte ptr + 8-byte timestamp — both `0xFF` from SqlClient — 4-byte data length, data).
  `SET TEXTSIZE` truncates legacy-LOB and MAX-typed result columns + output parameters at the client boundary (the shared `SimulatedQueryResult.CreateClientCursor` → `TextSizeCursor` seam, so the TDS row writer inherits it; docs/claude/scalars.md).
  Oracles: `LegacyLobWireTests`, `BulkCopyTests.LegacyLobColumns_TextNtextImage_InsertAndRoundTrip`, `TextSizeWireTests`.
- **Token writer contract**: `TdsTokenWriter` buffers synchronously; `FlushAsync(final: false)` after every row keeps memory bounded to max(row, packet).
  Only the final flush sets EOM.
  Tokens may legally split across packet boundaries.
- **Codec conventions**: nullable columns use the N-variant wire form (INTN/BITN/FLTN/MONEYN/DATETIMN/DECIMALN/GUIDN); a **NOT NULL fixed-width column carries the FIXEDLENTYPE token instead** (INT1/INT2/INT4/INT8/BIT/FLT4/FLT8/MONEY4/MONEY/DATETIM4/DATETIME — `WriteTypeInfo(…, notNull)` via `TryFixedLenToken`), matching real byte-for-byte (the old always-N-variant form desynced native ODBC, which reads a `0x26` value as length-prefixed).
  COLMETADATA fNullable comes from `SimulatedQueryResult.ColumnNullability` (populated by the single-source no-join SELECT projection via `Expression.ResultIsNullable`; null = claim all-nullable).
  **Token, flag, and value form must agree**: for those fixed-width families, fNullable=0 emits the FIXEDLENTYPE token (no max-length byte) and makes the reader take the ROW value raw at the declared width — `WriteRow` routes those columns through `WriteRawFixedValue` (no length prefix); the other BYTELEN families (date/time/datetime2/datetimeoffset, DECIMALN, GUIDN) have no FIXEDLENTYPE token, so they keep the N-variant + prefix regardless, as do USHORTLEN/PLP.
  RETURNVALUE always uses the N-variant (output params are nullable — `WriteReturnValue` passes `notNull: false`).
  Load-bearing for DacFx bacpac export (BCP data-file layout follows the wire's fNullable; the loader follows model.xml — they must match).
  PLP is written known-length (total + one chunk + terminator) but must be *read* in both known- and unknown-length (0x…FE) chunked forms — SqlClient streams large params with the latter.
- **`sql_variant` (0x62)**: a result-column type (`SqlVariantSqlType`), keyed off the per-cell `value.Type` inner (not the schema type) in `WriteValue`.
  COLMETADATA = type byte + 4-byte max length (8009).
  Each value = 4-byte total length then the MS-TDS 2.2.5.5.3 body (base-type token + cbProps + props + data).
  **NULL is total-length 0, not 0xFFFFFFFF** — a non-NULL variant is always ≥2 bytes, so SqlClient reads 0 as NULL (the one non-obvious wire rule; got this wrong first and it desynced the stream).
  `sql_variant` **input** *parameters* decode (RPC read side, `TdsRpc.DecodeSqlVariant` → `TdsWireValue.ReadVariantBody` — the read mirror of `BuildVariantBody`, into `SqlValue.FromVariant`), **output** direction writes back as a RETURNVALUE (`0x62` + ULONG maxlen 8009 + the column-form value; the engine value rides `SimulatedDbParameter.OutputSqlValue`, stamped at write-back, since the CLR `Value` loses the inner type), and **`sql_variant` columns inside a TVP** decode through the same body reader (`TdsColumnDecoder`: 0x62 + 4-byte max length, value = 4-byte total length `0`=NULL + body).
  Variant decimal bodies are always sign + 16-byte magnitude regardless of precision (probed in column and RETURNVALUE positions; SqlClient's RETURNVALUE reader hard-reads 17 data bytes).
  Oracles: `SqlVariantWireTests`, `SqlVariantRpcParameterTests`, `TvpVariantUdtColumnTests`.
- **`geography` / `geometry` (UDTTYPE `0xF0`)**: result-column-only.
  COLMETADATA = ushort max-byte-size (`0xFFFF`) + three B_VARCHAR names (db empty — the static codec can't reach the session db; schema `sys`; type name) + US_VARCHAR assembly-qualified name (`SpatialAssemblyQualifiedName`, probe-matched).
  Value is PLP carrying `SpatialWkbEncoder.Encode(wkt, isGeography, srid)` output (srid default 4326 geography / 0 geometry — `SqlValue` carries no per-value SRID).
  SqlClient reads it as `SqlDbType.Udt`; DacFx pulls raw bytes via `GetSqlBytes`/`GetBytes` (`GetValue` needs the absent `Microsoft.SqlServer.Types`).
  Oracle: `SpatialWireTests`; encoder byte-parity in `SpatialWkbEncoderTests` (Tests.Internal).
  **`hierarchyid` shares this UDT arm** — max byte size 892 (not `0xFFFF`), `SqlHierarchyId` AQN, PLP value = the stored canonical OrdPath bytes verbatim (`value.AsHierarchyIdBytes`, zero-copy; `DataLength` reads the same bytes' length).
  Oracle: `HierarchyIdWireTests`, `HierarchyIdOrdPathTests`.
  UDT **input** *parameters* decode (`TdsRpc.DecodeClrUdt` → `TdsWireValue.BuildUdtValue`): the client UDT_INFO is three B_VARCHARs (db/schema/type, no max-size, no AQN — shorter than COLMETADATA) + PLP value; hierarchyid OrdPath bytes bind verbatim, spatial WKB decodes via `SpatialWkbDecoder.TryDecode`, resolved case-insensitively into a pre-built `SqlValue` (unknown type → Msg 8064, invalid spatial bytes → Msg 8023).
  **UDT columns inside a TVP** decode through the same builder (`TdsColumnDecoder`: 0xF0 + three B_VARCHARs + PLP value).
  **Output** direction writes back as a RETURNVALUE carrying the **COLMETADATA-shaped** UDT_INFO (max byte size + db/schema/type + AQN — richer than the client request form) + PLP value, from `SimulatedDbParameter.OutputSqlValue`.
  A LOB-backed UDT TVP column (`geography`/`geometry`) round-trips via both the `sp_executesql` text path and a stored-proc READONLY parameter (the proc-parameter copy re-homes off-row values — docs/claude/table-valued-parameters.md).
  Oracles: `UdtRpcParameterTests`, `TvpVariantUdtColumnTests`.
- **Length-prefix trap**: most strings are char-counted (B_VARCHAR / US_VARCHAR), but TM-request transaction names are byte-counted — misreading them as char-counted overruns the payload on every `SqlTransaction.Save`.
  LOGIN7's password length pair is char-counted like the other LOGIN7 fields (oracle-confirmed with a surrogate-pair password); only its *bytes* are special, obfuscated per MS-TDS (client swaps nibbles then XORs 0xA5 — decode inverts the order).
- **Collation bytes**: `TdsCollationCodec` derives the 5-byte structure generatively (flags/version from name tokens, LCID/code page from the probe-built `Collation.LcidAndCodePageByPrefix` core-layer table, sortId from `Collation.SqlServerSortOrders`), cached per interned `Collation` reference.
  Probe-anchored rules: width=bit22 / kana=bit23 (MS-TDS's own field-order text is wrong), fUTF8=bit26 *displaces* the binary bits, version nibble 160→4, `SQL_Latin1_General_CP1254_*` → Turkish LCID.
  Baseline must derive to `09 04 D0 00 34`.
- **Login response**: the ENVCHANGE type 7 (server collation) is load-bearing — SqlClient NREs building any RPC without it.
  The 7.x prelogin-wrapped handshake is pinned TLS 1.2 (TLS 1.3 post-handshake tickets would be prelogin-wrapped after the client stops unwrapping); the TDS 8.0 strict path (first wire byte `0x16` → `SslStream` straight on the socket, ALPN `tds/8.0`, prelogin inside TLS, LOGINACK echoes `0x08000000`) has no pin and no framing shim.
  SqlClient ignores `TrustServerCertificate` under `Encrypt=Strict` — strict clients pin via the `ServerCertificate` connection-string keyword against the listener's exported `ServerCertificate` property.
  Oracle: `StrictEncryptionTests`.
- **Oracle**: `SqlServerSimulator.Tests.SqlClient` (real SqlClient over loopback) is the regression contract for this directory; `SqlServerSimulator.Tests.Smo` drives real SMO (`Microsoft.SqlServer.SqlManagementObjects`, the library behind SSMS Object Explorer + Script-As) against a listener over one shared WWI-shaped fixture — the permanent home of the SSMS-shakedown surface; `EFCoreOverWire` in `*.Tests.EFCore` covers vanilla `UseSqlServer`.
  For nontrivial expected values use the dual-read pattern (same query in-process and over the wire against one `Simulation`).
