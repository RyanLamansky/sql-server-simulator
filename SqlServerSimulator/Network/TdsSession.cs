using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SqlServerSimulator.Parser.Expressions;

namespace SqlServerSimulator.Network;

/// <summary>
/// One accepted TCP connection: drives the prelogin exchange, the TLS
/// handshake, LOGIN7, and then the batch loop, mapping the session onto one
/// <see cref="SimulatedDbConnection"/>. Runs as a single task; teardown is
/// triggered by client disconnect, listener disposal, or a protocol error.
/// </summary>
internal sealed partial class TdsSession(Simulation simulation, Socket socket, X509Certificate2 certificate)
{
    private readonly Queue<SimulatedError> pendingInfoMessages = new();
    private SimulatedDbConnection? connection;

    /// <summary>
    /// Closes the socket; the session task observes the closure at its next
    /// I/O operation and runs its normal cleanup.
    /// </summary>
    public void Abort() => socket.Dispose();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Stream transportStream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var transport = new TdsPacketTransport(transportStream);

            var prelogin = await transport.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (prelogin is null || prelogin.PacketType != Tds.PacketPrelogin)
                return;

            var clientEncryption = ParsePreloginEncryption(prelogin.Payload);
            var fedAuthRequested = ParsePreloginHasOption(prelogin.Payload, Tds.PreloginFedAuthRequired);
            await transport.WritePacketAsync(Tds.PacketTabularResult, BuildPreloginResponse(fedAuthRequested), endOfMessage: true, cancellationToken).ConfigureAwait(false);
            if (clientEncryption == Tds.EncryptNotSupported)
                return;

            var framing = new TlsHandshakeFramingStream(transportStream);
            var ssl = new SslStream(framing, leaveInnerStreamOpen: false);
            transportStream = ssl;
            // TLS 1.2 ceiling, matching SqlClient and real SQL Server for
            // prelogin-wrapped encryption: a TLS 1.3 server emits session
            // tickets at handshake completion, which would still be wrapped
            // in prelogin packets after the client has switched to reading
            // raw records. TDS 8.0 is the protocol's TLS 1.3 path.
#pragma warning disable CA5398
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12,
                },
                cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5398
            framing.EnablePassthrough();
            transport.SwitchStream(ssl);

            var loginMessage = await transport.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (loginMessage is null || loginMessage.PacketType != Tds.PacketLogin7)
                return;

            var login = Login7Request.Parse(loginMessage.Payload);
            if (login.PacketSize is >= 512 and <= 32767)
                transport.PacketSize = login.PacketSize;

            var writer = new TdsTokenWriter(transport);
            if (!ValidateCredentials(simulation, login))
            {
                // Probe-confirmed shape: Msg 18456 severity 14 state 1 with
                // identical wording for wrong-password / unknown-login /
                // empty-password (the real server masks the detailed state
                // from clients), then the connection closes.
                writer.WriteErrorOrInfo(Tds.TokenError, 18456, 1, 14, $"Login failed for user '{login.UserName}'.", "SIMULATED", "", 1);
                writer.WriteDone(Tds.DoneError, 0);
                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!this.TryOpenConnection(login.Database, login.UserName, writer))
            {
                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            transport.Spid = unchecked((ushort)this.connection!.Spid);
            this.WriteLoginResponse(writer, transport.PacketSize);
            await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);

            // One inbound read is always in flight. Between requests it is the
            // next request; while an engine request executes it doubles as the
            // attention watcher (see below). It is never cancelled mid-read —
            // it is carried forward across iterations — so packet framing can
            // never be corrupted by a partially-consumed read.
            var pendingRead = transport.ReadMessageAsync(cancellationToken).AsTask();
            while (true)
            {
                var message = await pendingRead.ConfigureAwait(false);
                if (message is null)
                    return;

                if (message.PacketType == Tds.PacketAttention)
                {
                    // Attention with nothing executing: the session was idle, or
                    // the attention raced a response that already completed
                    // naturally. Either way, acknowledge it — SqlClient waits for
                    // the DONE_ATTN before declaring the connection reusable — and
                    // keep the session alive.
                    writer.WriteDone(Tds.DoneAttention, 0);
                    await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                    pendingRead = transport.ReadMessageAsync(cancellationToken).AsTask();
                    continue;
                }

                // SqlBulkCopy sends `INSERT BULK …` as a plain SQL batch that puts
                // the session into bulk-load mode: the server sends no response and
                // consumes the BulkLoadBCP data packet (type 7) that follows — so
                // that path is NOT engine-cancellable and must not start a watcher
                // (the watcher would swallow the bulk-data packet).
                string? batchText = null;
                var isBulkInsertBegin = false;
                if (message.PacketType == Tds.PacketSqlBatch)
                {
                    batchText = ExtractBatchText(message.Payload);
                    isBulkInsertBegin = IsBulkInsertBatch(batchText);
                }

                // Only SQLBatch / RPC drive the engine and stream a cancellable
                // response. For those, start reading the next inbound packet
                // concurrently: in non-MARS TDS the client sends nothing but an
                // attention until it has drained this response, so a completed
                // read during execution is the client's cancel. The continuation
                // fires the connection's cancellation; the engine and the row
                // streamer observe it at their next safe point.
                var runsEngine = !isBulkInsertBegin && message.PacketType is Tds.PacketSqlBatch or Tds.PacketRpc;
                Task<TdsMessage?>? watcher = null;
                if (runsEngine)
                {
                    watcher = transport.ReadMessageAsync(cancellationToken).AsTask();
                    _ = watcher.ContinueWith(
                        static (read, state) =>
                        {
                            if (read.IsCompletedSuccessfully)
                            {
                                if (read.Result?.PacketType == Tds.PacketAttention)
                                    ((TdsSession)state!).connection?.CancelExecution();
                            }
                            else
                            {
                                _ = read.Exception;
                            }
                        },
                        this,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach,
                        TaskScheduler.Default);
                }

                switch (message.PacketType)
                {
                    case Tds.PacketSqlBatch:
                        if (isBulkInsertBegin)
                            this.BeginBulkInsert(batchText!, writer);
                        else
                            await this.ExecuteBatchAsync(message, writer, cancellationToken).ConfigureAwait(false);
                        break;
                    case Tds.PacketRpc:
                        await this.ExecuteRpcMessageAsync(message, writer, cancellationToken).ConfigureAwait(false);
                        break;
                    case Tds.PacketBulkLoad:
                        this.ExecuteBulkLoad(message, writer);
                        break;
                    case Tds.PacketTransactionManager:
                        this.ExecuteTransactionManagerRequest(message, writer);
                        break;
                    default:
                        writer.WriteErrorOrInfo(
                            Tds.TokenError, 50000, 1, 16,
                            $"The SqlServerSimulator network listener does not support TDS request type {message.PacketType}.",
                            "SIMULATED", "", 1);
                        writer.WriteDone(Tds.DoneError, 0);
                        break;
                }

                // A cancelled token is the definitive "watcher saw an attention"
                // signal: only the watcher's continuation cancels this
                // connection, and it does so synchronously on watcher completion,
                // so a cancelled token means the watcher is settled on an
                // attention (and its read is spent). An attention that arrives
                // after this check leaves the watcher pending; it is carried
                // forward and acknowledged on the next iteration's idle branch,
                // never lost.
                var attentionConsumed = runsEngine && this.connection!.ExecutionCancellationToken.IsCancellationRequested;
                if (attentionConsumed)
                {
                    // Roll the transaction back only under XACT_ABORT ON, then
                    // send the single DONE_ATTN the client is waiting for.
                    this.ApplyCancellationTransactionSemantics();
                    writer.WriteDone(Tds.DoneAttention, 0);
                }

                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);

                // Carry the in-flight read forward. When the watcher already
                // consumed the attention, its read is spent — start a fresh one.
                // Otherwise the same read is the next request (or still pending,
                // to be awaited next iteration).
                pendingRead = watcher is not null && !attentionConsumed
                    ? watcher
                    : transport.ReadMessageAsync(cancellationToken).AsTask();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException or AuthenticationException)
        {
            // Client disconnects, listener teardown, and malformed traffic
            // all land here; the session simply ends.
        }
        finally
        {
            this.connection?.Dispose();
            await transportStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enforces SQL-authentication credentials against
    /// <see cref="Simulation.Logins"/>. An empty registry accepts anything
    /// (the zero-configuration default); once <c>CREATE LOGIN</c> has
    /// populated it, the LOGIN7 username must resolve and the de-obfuscated
    /// password must verify against the stored PWDENCRYPT-format hash.
    /// </summary>
    private static bool ValidateCredentials(Simulation simulation, Login7Request login) =>
        simulation.Logins.IsEmpty
        || (simulation.Logins.TryGetValue(login.UserName, out var serverLogin)
            && PasswordHash.Verify(login.Password, serverLogin.PasswordHash));

    private bool TryOpenConnection(string requestedDatabase, string userName, TdsTokenWriter writer)
    {
        var opened = simulation.CreateDbConnection();
        opened.Open();
        opened.InfoMessage += this.OnInfoMessage;
        if (requestedDatabase.Length > 0)
        {
            try
            {
                opened.ChangeDatabase(requestedDatabase);
            }
            catch (SimulatedSqlException)
            {
                // Probe-confirmed shape for a login whose requested database
                // can't be opened: Msg 4060 severity 11 (database name in
                // double quotes) followed by Msg 18456 severity 14, then the
                // connection closes. The engine's Msg 911 stays the shape for
                // a mid-session USE; login gets the wrapping pair.
                writer.WriteErrorOrInfo(Tds.TokenError, 4060, 1, 11, $"Cannot open database \"{requestedDatabase}\" requested by the login. The login failed.", "SIMULATED", "", 1);
                writer.WriteErrorOrInfo(Tds.TokenError, 18456, 1, 14, $"Login failed for user '{userName}'.", "SIMULATED", "", 1);
                writer.WriteDone(Tds.DoneError, 0);
                opened.Dispose();
                return false;
            }
        }

        this.connection = opened;
        return true;
    }

    private void OnInfoMessage(object? sender, SimulatedInfoMessageEventArgs e)
    {
        foreach (var error in e.Errors)
            this.pendingInfoMessages.Enqueue(error);
    }

    private void WriteLoginResponse(TdsTokenWriter writer, int packetSize)
    {
        var database = this.connection!.Database;
        writer.WriteEnvChange(Tds.EnvDatabase, database, "master");
        writer.WriteErrorOrInfo(Tds.TokenInfo, 5701, 2, 0, $"Changed database context to '{database}'.", "SIMULATED", "", 1);
        writer.WriteEnvChange(Tds.EnvLanguage, "us_english", "");
        writer.WriteErrorOrInfo(Tds.TokenInfo, 5703, 1, 0, "Changed language setting to us_english.", "SIMULATED", "", 1);
        var serverCollation = TdsCollationCodec.For(Collation.Get(simulation.ServerCollationName));
        writer.WriteEnvChangeSqlCollation(serverCollation.Info, serverCollation.SortId);
        writer.WriteLoginAck(Tds.Version74, "Microsoft SQL Server", 17, 0);
        writer.WriteEnvChange(Tds.EnvPacketSize, packetSize.ToString(System.Globalization.CultureInfo.InvariantCulture), Tds.DefaultPacketSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteDone(Tds.DoneFinal, 0);
    }

    private async ValueTask ExecuteBatchAsync(TdsMessage message, TdsTokenWriter writer, CancellationToken cancellationToken)
    {
        if ((message.FirstStatus & (Tds.StatusResetConnection | Tds.StatusResetConnectionSkipTran)) != 0)
        {
            this.ResetConnection();
            writer.WriteResetConnectionAck();
        }

        this.databaseAtMessageStart = this.connection!.Database;
        try
        {
            using var command = this.connection.CreateCommand();
#pragma warning disable CA2100 // This IS a SQL endpoint: the batch text is the client's query by design.
            command.CommandText = ExtractBatchText(message.Payload);
#pragma warning restore CA2100
            // A cancelled batch (return value true) leaves the DONE_ATTN
            // acknowledgment to the session loop; nothing more to emit here.
            _ = await this.StreamOutcomesAsync(command, writer, Tds.TokenDone, trailingTokensFollow: false, cancellationToken).ConfigureAwait(false);
        }
        catch (SimulatedSqlException ex)
        {
            _ = this.FlushInfoMessages(writer);
            WriteErrors(writer, ex);
            this.WriteDatabaseChangeIfAny(writer);
            writer.WriteDone(Tds.DoneError, 0);
        }
        catch (NotSupportedException ex)
        {
            _ = this.FlushInfoMessages(writer);
            writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, $"SqlServerSimulator: {ex.Message}", "SIMULATED", "", 1);
            this.WriteDatabaseChangeIfAny(writer);
            writer.WriteDone(Tds.DoneError, 0);
        }
    }

    /// <summary>
    /// The session database when the current batch / RPC message began, for
    /// detecting a mid-message <c>USE</c>. Emitted as ENVCHANGE type 1 +
    /// INFO 5701 via <see cref="WriteDatabaseChangeIfAny"/>, which must run
    /// BEFORE the response's final DONE: SqlClient's token reader stalls
    /// until command timeout on an ENVCHANGE that arrives after the last
    /// DONE (probe-confirmed 2026-07-15 — the SSMS freeze on
    /// <c>use [master]</c>; go-mssqldb tolerates the late position, which is
    /// how the ordering shipped unnoticed).
    /// </summary>
    private string? databaseAtMessageStart;

    /// <summary>
    /// Writes the database-change ENVCHANGE + INFO 5701 when the session
    /// database differs from <see cref="databaseAtMessageStart"/>, matching
    /// real SQL Server's token order for <c>USE</c> (ENVCHANGE, then INFO,
    /// then the statement's DONE). Idempotent — the first call records the
    /// new baseline, so the multiple call sites (per-final-DONE seams and
    /// error paths) emit at most once per change.
    /// </summary>
    private void WriteDatabaseChangeIfAny(TdsTokenWriter writer)
    {
        var current = this.connection!.Database;
        if (this.databaseAtMessageStart is null || string.Equals(current, this.databaseAtMessageStart, StringComparison.Ordinal))
            return;
        writer.WriteEnvChange(Tds.EnvDatabase, current, this.databaseAtMessageStart);
        writer.WriteErrorOrInfo(Tds.TokenInfo, 5701, 2, 0, $"Changed database context to '{current}'.", "SIMULATED", "", 1);
        this.databaseAtMessageStart = current;
    }

    /// <summary>
    /// Executes a command and streams its outcomes as result-set and DONE
    /// tokens. Batches use the DONE token; RPC responses use DONEINPROC with
    /// <paramref name="trailingTokensFollow"/> set, because RETURNVALUE /
    /// RETURNSTATUS / DONEPROC still follow and every DONEINPROC must carry
    /// the more bit. Fully drains the outcome enumerator, which is what
    /// triggers the engine's output-parameter writeback.
    /// </summary>
    private async ValueTask<bool> StreamOutcomesAsync(SimulatedDbCommand command, TdsTokenWriter writer, byte doneToken, bool trailingTokensFollow, CancellationToken cancellationToken)
    {
        using var outcomes = simulation.CreateResultSetsForCommand(command, continueOnError: true).GetEnumerator();

        var hasOutcome = outcomes.MoveNext();
        var anyOutcome = hasOutcome;
        while (hasOutcome)
        {
            // Client attention (SqlCommand.Cancel / CommandTimeout) observed at
            // an outcome boundary: stop producing, leaving the DONE_ATTN ack to
            // the caller. Disposing the enumerator here unwinds the engine's
            // batch (its finally runs; output-parameter writeback does not).
            if (this.connection!.ExecutionCancellationToken.IsCancellationRequested)
                return true;

            var outcome = outcomes.Current;
            // Messages from a preceding no-outcome statement (PRINT,
            // severity<=10 RAISERROR): real SQL Server gives that statement
            // its own DONE after the INFO tokens. Without it SqlClient's
            // token reader stalls on the INFO/COLMETADATA adjacency until
            // command timeout (go-sqlcmd shakedown, 2026-07-14). RPC
            // responses skip the extra DONEINPROC — their per-statement
            // DONEINPROC stream is already well-formed for proc-body PRINT.
            if (this.FlushInfoMessages(writer) && !trailingTokensFollow)
                writer.WriteDoneToken(doneToken, Tds.DoneMore, 0);
            if (outcome is SimulatedQueryResult query)
            {
                TdsTypeCodec.ValidateSchema(query.Schema);
                TdsTypeCodec.WriteColMetadata(writer, query.Schema, query.ColumnNames, query.ColumnNullability);
                long rows = 0;
                using (var cursor = query.CreateCursor())
                {
                    while (cursor.MoveNext())
                    {
                        TdsTypeCodec.WriteRow(writer, query.Schema, cursor, query.ColumnNullability);
                        rows++;
                        await writer.FlushAsync(final: false, cancellationToken).ConfigureAwait(false);
                        // Mid-result-set attention: stop between rows (never
                        // mid-row — the flush above closed the last ROW token).
                        // The partial rows already sent are discarded client-side
                        // once it reads the DONE_ATTN the caller emits.
                        if (this.connection!.ExecutionCancellationToken.IsCancellationRequested)
                            return true;
                    }
                }

                hasOutcome = outcomes.MoveNext();
                var queryStatus = (ushort)(this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow) | Tds.DoneCount);
                if ((queryStatus & Tds.DoneMore) == 0)
                    this.WriteDatabaseChangeIfAny(writer);
                writer.WriteDoneToken(doneToken, queryStatus, rows);
            }
            else if (outcome is SimulatedErrorOutcome errorOutcome)
            {
                // Statement-terminating error the engine chose to continue past
                // (continueOnError). Emit its error token(s) and a DONE with
                // DONE_ERROR; the more/final bit follows the same rule as any
                // other outcome, so a mid-batch error carries DONE_MORE and a
                // trailing error closes with the final DONE. The batch loop
                // then proceeds to the next outcome — real SQL Server's
                // non-XACT_ABORT behavior for a failed statement mid-batch.
                WriteErrors(writer, errorOutcome.Exception);
                hasOutcome = outcomes.MoveNext();
                var status = (ushort)(this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow) | Tds.DoneError);
                if ((status & Tds.DoneMore) == 0)
                    this.WriteDatabaseChangeIfAny(writer);
                writer.WriteDoneToken(doneToken, status, 0);
            }
            else
            {
                var affected = outcome.RecordsAffected;
                hasOutcome = outcomes.MoveNext();
                var status = this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow);
                if (affected >= 0)
                    status |= Tds.DoneCount;

                if ((status & Tds.DoneMore) == 0)
                    this.WriteDatabaseChangeIfAny(writer);
                writer.WriteDoneToken(doneToken, status, Math.Max(affected, 0));
            }
        }

        // Trailing messages (batch ends in PRINT): INFO may never follow the
        // final DONE, so the last outcome's DONE stayed DONE_MORE (see
        // OutcomeDoneStatus) and the batch closes with its own final DONE.
        // A mid-batch USE's ENVCHANGE must likewise precede the final DONE.
        var flushedTrailing = this.FlushInfoMessages(writer);
        if (!trailingTokensFollow && (flushedTrailing || !anyOutcome))
        {
            this.WriteDatabaseChangeIfAny(writer);
            writer.WriteDoneToken(doneToken, Tds.DoneFinal, 0);
        }

        return false;
    }

    /// <summary>
    /// DONE status for a completed outcome: more tokens follow when another
    /// outcome exists, the response is an RPC (RETURNSTATUS / DONEPROC still
    /// come), or queued info messages remain to be written — the trailing-
    /// PRINT case, whose INFO must precede the batch's final DONE.
    /// </summary>
    private ushort OutcomeDoneStatus(bool hasOutcome, bool trailingTokensFollow) =>
        hasOutcome || trailingTokensFollow || this.pendingInfoMessages.Count > 0 ? Tds.DoneMore : Tds.DoneFinal;

    /// <summary>
    /// Applies the probe-confirmed transaction semantics of a cancelled batch:
    /// under <c>SET XACT_ABORT ON</c> an open transaction rolls back (the
    /// client observes <c>@@TRANCOUNT</c> 0 afterward); under the default
    /// <c>OFF</c> the transaction survives the cancel intact and the session
    /// stays usable. Variables and the aborted statements' unrun side effects
    /// go with the ended batch either way; committed statements' effects and
    /// the connection's temp tables persist.
    /// </summary>
    private void ApplyCancellationTransactionSemantics()
    {
        var connection = this.connection!;
        if (connection.XactAbort && connection.CurrentTransaction is { } tx)
        {
            tx.Rollback();
            this.transaction = null;
        }
    }

    private void ResetConnection()
    {
        var previous = this.connection!;
        var database = previous.Database;
        this.transaction = null;
        previous.Dispose();

        var fresh = simulation.CreateDbConnection();
        fresh.Open();
        fresh.InfoMessage += this.OnInfoMessage;
        this.pendingInfoMessages.Clear();
        if (!string.Equals(fresh.Database, database, StringComparison.Ordinal))
            fresh.ChangeDatabase(database);

        this.connection = fresh;
    }

    /// <summary>
    /// The server-name field carried by every ERROR / INFO token — the
    /// server's own name (<c>@@SERVERNAME</c> / <c>SERVERPROPERTY('ServerName')</c>),
    /// which a real SQL Server writes into these tokens and which token-rendering
    /// clients (sqlcmd) display. Distinct from <see cref="SimulatedError.Server"/>
    /// (the connection data source that SqlClient surfaces on
    /// <c>SqlException.Server</c>); SqlClient ignores this token field.
    /// </summary>
    internal const string ServerName = "SIMULATED";

    /// <summary>Writes all queued info messages as INFO tokens; true when any were written.</summary>
    private bool FlushInfoMessages(TdsTokenWriter writer)
    {
        var any = false;
        while (this.pendingInfoMessages.TryDequeue(out var error))
        {
            writer.WriteErrorOrInfo(Tds.TokenInfo, error.Number, error.State, error.Class, error.Message, ServerName, error.Procedure, error.LineNumber);
            any = true;
        }

        return any;
    }

    private static void WriteErrors(TdsTokenWriter writer, SimulatedSqlException exception)
    {
        foreach (var error in exception.Errors)
            writer.WriteErrorOrInfo(Tds.TokenError, error.Number, error.State, error.Class, error.Message, ServerName, error.Procedure, error.LineNumber);
    }

    /// <summary>Skips the ALL_HEADERS section and decodes the UCS-2 batch text.</summary>
    private static string ExtractBatchText(byte[] payload) =>
        Encoding.Unicode.GetString(payload.AsSpan(SkipAllHeaders(payload)));

    /// <summary>
    /// Returns the offset just past the ALL_HEADERS section, whose leading
    /// little-endian length includes itself.
    /// </summary>
    internal static int SkipAllHeaders(byte[] payload)
    {
        if (payload.Length >= 4)
        {
            var headersLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
            if (headersLength >= 4 && headersLength <= payload.Length)
                return headersLength;
        }

        return 0;
    }

    private static byte ParsePreloginEncryption(ReadOnlySpan<byte> payload)
    {
        for (var i = 0; (i + 5) <= payload.Length && payload[i] != Tds.PreloginTerminator; i += 5)
        {
            if (payload[i] != Tds.PreloginEncryption)
                continue;

            var offset = (payload[i + 1] << 8) | payload[i + 2];
            if (offset < payload.Length)
                return payload[offset];
        }

        return Tds.EncryptNotSupported;
    }

    private static bool ParsePreloginHasOption(ReadOnlySpan<byte> payload, byte option)
    {
        for (var i = 0; (i + 5) <= payload.Length && payload[i] != Tds.PreloginTerminator; i += 5)
        {
            if (payload[i] == option)
                return true;
        }

        return false;
    }

    private static byte[] BuildPreloginResponse(bool includeFedAuth)
    {
        // Options: VERSION(6) ENCRYPTION(1) INSTOPT(1) THREADID(0) MARS(1)
        // [FEDAUTHREQUIRED(1)], each with a 5-byte descriptor, then the
        // terminator, then the data region the offsets point into.
        var optionCount = includeFedAuth ? 6 : 5;
        var dataStart = (optionCount * 5) + 1;
        var data = new List<(byte Token, byte[] Value)>
        {
            (Tds.PreloginVersion, [17, 0, 0, 0, 0, 0]),
            (Tds.PreloginEncryption, [Tds.EncryptRequired]),
            (Tds.PreloginInstance, [0]),
            (Tds.PreloginThreadId, []),
            (Tds.PreloginMars, [0]),
        };
        if (includeFedAuth)
            data.Add((Tds.PreloginFedAuthRequired, [0]));

        var totalData = 0;
        foreach (var (_, value) in data)
            totalData += value.Length;

        var response = new byte[dataStart + totalData];
        var descriptor = 0;
        var cursor = dataStart;
        foreach (var (token, value) in data)
        {
            response[descriptor] = token;
            response[descriptor + 1] = (byte)(cursor >> 8);
            response[descriptor + 2] = (byte)cursor;
            response[descriptor + 3] = (byte)(value.Length >> 8);
            response[descriptor + 4] = (byte)value.Length;
            value.CopyTo(response, cursor);
            cursor += value.Length;
            descriptor += 5;
        }

        response[descriptor] = Tds.PreloginTerminator;
        return response;
    }
}
