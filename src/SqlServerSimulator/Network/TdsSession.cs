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
internal sealed partial class TdsSession(Simulation simulation, Socket socket, X509Certificate2 certificate) : IDisposable, ISmpHost
{
    /// <summary>The ALPN protocol name a TDS 8.0 strict-encryption client negotiates.</summary>
    private static readonly SslApplicationProtocol Tds8AlpnProtocol = new("tds/8.0");

    private readonly Queue<SimulatedError> pendingInfoMessages = new();
    private SimulatedDbConnection? connection;

    /// <summary>
    /// Serializes engine execution across all SMP logical sessions on a MARS
    /// connection: real MARS is cooperative multiplexing, never parallel
    /// execution, and the engine assumes one executor per connection
    /// (<c>CurrentExecutingThreadId</c>, transaction machinery). A session
    /// acquires this before driving the engine and buffers its whole response
    /// before releasing, so overlap happens only during the window-controlled
    /// send, not inside the engine. Unused by non-MARS sessions.
    /// </summary>
    private readonly SemaphoreSlim engineExecutionGate = new(1, 1);

    private SmpMultiplexer? multiplexer;
    private int marsPacketSize = Tds.DefaultPacketSize;

    /// <summary>
    /// Closes the socket; the session task observes the closure at its next
    /// I/O operation and runs its normal cleanup.
    /// </summary>
    public void Abort() => socket.Dispose();

    /// <summary>
    /// Tears down the session's backing connection and the MARS machinery.
    /// Called by the listener after <see cref="RunAsync"/> returns; the
    /// connection's own teardown rolls back open transactions and drops temp
    /// tables.
    /// </summary>
    public void Dispose()
    {
        this.transaction?.Dispose();
        this.connection?.Dispose();
        this.engineExecutionGate.Dispose();
        this.multiplexer?.Dispose();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Stream transportStream = new NetworkStream(socket, ownsSocket: true);
        TdsTokenWriter? writer = null;
        try
        {
            // TDS 8.0 (Encrypt=Strict) opens with a bare TLS ClientHello
            // negotiating ALPN "tds/8.0", and every TDS packet — prelogin
            // included — then flows inside the TLS channel. TDS 7.x opens
            // with a cleartext PRELOGIN packet and wraps the TLS handshake
            // in prelogin packets afterward. The first byte on the wire
            // routes between them.
            var peek = new byte[1];
            if (await socket.ReceiveAsync(peek, SocketFlags.Peek, cancellationToken).ConfigureAwait(false) == 0)
                return;

            var strictEncryption = peek[0] == Tds.TlsRecordHandshake;
            if (strictEncryption)
            {
                var strictSsl = new SslStream(transportStream, leaveInnerStreamOpen: false);
                transportStream = strictSsl;
                await strictSsl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ApplicationProtocols = [Tds8AlpnProtocol],
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var transport = new TdsPacketTransport(transportStream);

            var prelogin = await transport.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (prelogin is null || prelogin.PacketType != Tds.PacketPrelogin)
                return;

            var clientEncryption = ParsePreloginEncryption(prelogin.Payload);
            var fedAuthRequested = ParsePreloginHasOption(prelogin.Payload, Tds.PreloginFedAuthRequired);
            var marsRequested = ParsePreloginMars(prelogin.Payload);
            await transport.WritePacketAsync(Tds.PacketTabularResult, BuildPreloginResponse(fedAuthRequested, marsRequested), endOfMessage: true, cancellationToken).ConfigureAwait(false);
            if (!strictEncryption)
            {
                if (clientEncryption == Tds.EncryptNotSupported)
                    return;

                var framing = new TlsHandshakeFramingStream(transportStream);
                var ssl = new SslStream(framing, leaveInnerStreamOpen: false);
                transportStream = ssl;
                // TLS 1.2 ceiling, matching SqlClient and real SQL Server for
                // prelogin-wrapped encryption: a TLS 1.3 server emits session
                // tickets at handshake completion, which would still be wrapped
                // in prelogin packets after the client has switched to reading
                // raw records. The strict path above is the protocol's TLS 1.3
                // home (records flow raw, so tickets are harmless).
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
            }

            var loginMessage = await transport.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (loginMessage is null || loginMessage.PacketType != Tds.PacketLogin7)
                return;

            var login = Login7Request.Parse(loginMessage.Payload);
            if (login.PacketSize is >= 512 and <= 32767)
                transport.PacketSize = login.PacketSize;

            writer = new TdsTokenWriter(transport);
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

            if (!this.TryOpenConnection(login, writer))
            {
                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            transport.Spid = unchecked((ushort)this.connection!.Spid);
            this.WriteLoginResponse(writer, transport.PacketSize, login.TdsVersion == Tds.Version8 ? Tds.Version8 : Tds.Version74);
            await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);

            // MARS negotiated: prelogin, TLS, and LOGIN7 stayed raw (the login
            // response above is unwrapped), but every post-login TDS message is
            // wrapped in SMP frames. Hand the socket to the multiplexer, which
            // demuxes SMP sessions and drives one batch loop per session against
            // this shared connection. Non-MARS keeps the single-session loop
            // below byte-for-byte.
            if (marsRequested)
            {
                this.marsPacketSize = transport.PacketSize;
                this.multiplexer = new SmpMultiplexer(transportStream, this);
                await this.multiplexer.RunAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

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
#pragma warning disable CA1031 // Terminal backstop: every exception type must surface as an in-band severe error, not a silent transport reset.
        catch (Exception)
        {
            // Terminal crash boundary: an exception the typed handlers above
            // didn't anticipate (and never converted to an ERROR token). Rather
            // than letting the session die silently — the client seeing only a
            // raw transport reset — emit a best-effort in-band severe error so
            // SqlClient surfaces a SqlException, then let the connection close.
            await TryWriteSevereErrorAsync(writer, cancellationToken).ConfigureAwait(false);
        }
#pragma warning restore CA1031
        finally
        {
            // The connection, transaction, and MARS machinery are torn down in
            // Dispose (invoked by the listener once this returns); here only the
            // transport stream, a RunAsync-local, needs releasing.
            await transportStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The Msg 0 / severity 20 error real SQL Server sends when an internal
    /// failure aborts the current command; SqlClient treats severity ≥ 20 as
    /// fatal — it surfaces a <c>SqlException</c> and marks the connection dead.
    /// </summary>
    internal const string SevereErrorMessage = "A severe error occurred on the current command. The results, if any, should be discarded.";

    /// <summary>
    /// Whether an exception none of the per-statement typed catches anticipated
    /// can still be reported in band as a <em>statement-level</em> error,
    /// leaving the session usable — as opposed to escaping to the terminal
    /// crash boundary, which reports severity 20 and kills the connection.
    /// </summary>
    /// <remarks>
    /// <para>Worth the breadth because the alternative is disproportionate: a
    /// single unmodeled statement used to take the whole connection down with
    /// it, so in a test suite every later test sharing that connection failed
    /// too. One measured run had a single such statement account for 27 of 50
    /// failures — the cascade cost far exceeds the underlying gap.</para>
    /// <para>Two exclusions. Transport and cancellation types must keep flowing
    /// to the session loop, which owns disconnect and attention handling. And
    /// the writer must be at a token boundary: a fault that struck mid-token
    /// has already emitted a partial token, so appending ERROR would desync the
    /// stream — that case still belongs to the terminal backstop.</para>
    /// </remarks>
    private static bool IsRecoverableStatementFault(Exception ex, TdsTokenWriter writer) =>
        writer.AtTokenBoundary
        && ex is not (IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or InvalidDataException or AuthenticationException);

    /// <summary>
    /// Reports an unanticipated exception as a severity-16 statement error, so
    /// the client sees a diagnosable failure and keeps the connection. The
    /// exception type is named because these are simulator defects rather than
    /// modeled SQL Server behavior, and the type is what makes them findable.
    /// </summary>
    private static void WriteUnexpectedStatementFault(TdsTokenWriter writer, Exception ex) =>
        writer.WriteErrorOrInfo(
            Tds.TokenError, 50000, 1, 16,
            $"SqlServerSimulator: unhandled {ex.GetType().Name}: {ex.Message}", "SIMULATED", "", 1);

    /// <summary>
    /// Best-effort terminal backstop: appends a severity-20 ERROR + DONE to the
    /// response and flushes it, so an otherwise-silent session crash reaches the
    /// client as a <c>SqlException</c> rather than a bare transport reset. Only
    /// runs when the writer is at a token boundary
    /// (<see cref="TdsTokenWriter.AtTokenBoundary"/>) — a crash that struck
    /// mid-COLMETADATA / mid-ROW left a partial token buffered, and appending
    /// another token there would desync the stream, so the connection just
    /// closes. Any bytes already flushed for the current response stay
    /// well-formed: an ERROR token legally follows complete tokens (even a
    /// partial result set the client then discards).
    /// </summary>
    private static async ValueTask TryWriteSevereErrorAsync(TdsTokenWriter? writer, CancellationToken cancellationToken)
    {
        if (writer is null || !writer.AtTokenBoundary)
            return;

        try
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 0, 1, 20, SevereErrorMessage, ServerName, "", 0);
            writer.WriteDone(Tds.DoneError, 0);
            await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException or AuthenticationException)
        {
            // The connection is already going away; the backstop is best-effort.
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

    private bool TryOpenConnection(Login7Request login, TdsTokenWriter writer)
    {
        var requestedDatabase = login.Database;
        var userName = login.UserName;
        var opened = simulation.CreateDbConnection();
        opened.Open();
        opened.InfoMessage += this.OnInfoMessage;
        // LOGIN7 carries the client's workstation and application names; the
        // session keeps them for HOST_NAME() / APP_NAME(),
        // sys.dm_exec_sessions and the sp_who family.
        opened.ClientHostName = login.HostName;
        opened.ClientApplicationName = login.AppName;
        var target = opened.CurrentDatabase;
        if (requestedDatabase.Length > 0)
        {
            try
            {
                opened.ChangeDatabase(requestedDatabase);
                target = opened.CurrentDatabase;
            }
            catch (SimulatedSqlException)
            {
                // Probe-confirmed shape for a login whose requested database
                // can't be opened: Msg 4060 severity 11 (database name in
                // double quotes) followed by Msg 18456 severity 14, then the
                // connection closes. The engine's Msg 911 stays the shape for
                // a mid-session USE; login gets the wrapping pair.
                return FailLogin(writer, requestedDatabase, userName, opened);
            }
        }

        // Map the validated login to its database user in the connect-target
        // database and stamp the session principal (mapped user / guest-in-
        // master / 4060-refusal per the shared resolution). A refusal writes the
        // same 4060 + 18456 pair as a missing database.
        if (!Simulation.TryMapLoginToDatabaseUser(simulation, target, userName, out var principal))
            return FailLogin(writer, target.Name, userName, opened);
        opened.Security = Simulation.BuildAuthenticatedSecurityContext(principal, userName);

        this.connection = opened;
        return true;
    }

    private static bool FailLogin(TdsTokenWriter writer, string databaseName, string userName, SimulatedDbConnection opened)
    {
        writer.WriteErrorOrInfo(Tds.TokenError, 4060, 1, 11, $"Cannot open database \"{databaseName}\" requested by the login. The login failed.", "SIMULATED", "", 1);
        writer.WriteErrorOrInfo(Tds.TokenError, 18456, 1, 14, $"Login failed for user '{userName}'.", "SIMULATED", "", 1);
        writer.WriteDone(Tds.DoneError, 0);
        opened.Dispose();
        return false;
    }

    private void OnInfoMessage(object? sender, SimulatedInfoMessageEventArgs e)
    {
        foreach (var error in e.Errors)
            this.pendingInfoMessages.Enqueue(error);
    }

    private void WriteLoginResponse(TdsTokenWriter writer, int packetSize, uint tdsVersion)
    {
        var database = this.connection!.Database;
        writer.WriteEnvChange(Tds.EnvDatabase, database, "master");
        writer.WriteErrorOrInfo(Tds.TokenInfo, 5701, 2, 0, $"Changed database context to '{database}'.", "SIMULATED", "", 1);
        writer.WriteEnvChange(Tds.EnvLanguage, "us_english", "");
        writer.WriteErrorOrInfo(Tds.TokenInfo, 5703, 1, 0, "Changed language setting to us_english.", "SIMULATED", "", 1);
        var serverCollation = TdsCollationCodec.For(Collation.Get(simulation.ServerCollationName));
        writer.WriteEnvChangeSqlCollation(serverCollation.Info, serverCollation.SortId);
        writer.WriteLoginAck(
            tdsVersion,
            "Microsoft SQL Server",
            checked((byte)ReferenceBuild.Version.Major),
            checked((byte)ReferenceBuild.Version.Minor),
            checked((ushort)ReferenceBuild.Version.Build));
        writer.WriteEnvChange(Tds.EnvPacketSize, packetSize.ToString(System.Globalization.CultureInfo.InvariantCulture), Tds.DefaultPacketSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteDone(Tds.DoneFinal, 0);
    }

    /// <summary>
    /// Cancels whatever command currently holds the connection's execution
    /// scope. Called by the multiplexer when a client attention targets the
    /// session that is actively driving the engine; because execution is
    /// serialized, the current scope belongs to that session.
    /// </summary>
    public void CancelConnectionExecution() => this.connection?.CancelExecution();

    /// <summary>
    /// Runs the TDS batch loop for one SMP logical session. Mirrors the
    /// non-MARS loop but over a per-session transport riding the session's
    /// demuxed stream, guards engine execution with the per-connection
    /// execution gate, and buffers the whole response (deferred flush) so the
    /// window-controlled send happens outside the lock. All logical sessions
    /// share this session's <see cref="SimulatedDbConnection"/>.
    /// </summary>
    public async Task RunMarsSessionAsync(SmpSession session, CancellationToken cancellationToken)
    {
        using var logicalStream = new SmpSessionStream(session);
        var transport = new TdsPacketTransport(logicalStream)
        {
            PacketSize = this.marsPacketSize,
            Spid = unchecked((ushort)this.connection!.Spid),
        };
        var writer = new TdsTokenWriter(transport) { DeferFlush = true };
        try
        {
            while (true)
            {
                var message = await transport.ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                    return;

                if (message.PacketType == Tds.PacketAttention)
                {
                    // Idle-session attention, delivered through the pipe (the
                    // executing case is handled after the switch). Ack only if
                    // this consumes the flag — a post-execution check may already
                    // have consumed it when the attention raced completion.
                    if (Interlocked.Exchange(ref session.AttentionState, 0) == 1)
                    {
                        writer.WriteDone(Tds.DoneAttention, 0);
                        await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                string? batchText = null;
                var isBulkInsertBegin = false;
                if (message.PacketType == Tds.PacketSqlBatch)
                {
                    batchText = ExtractBatchText(message.Payload);
                    isBulkInsertBegin = IsBulkInsertBatch(batchText);
                }

                bool cancelled;
                await this.engineExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                session.Executing = true;
                try
                {
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

                    // A mid-execution cancel rolls the transaction back only under
                    // XACT_ABORT ON; captured under the lock so the shared
                    // transaction isn't touched concurrently with another session.
                    cancelled = this.connection!.ExecutionCancellationToken.IsCancellationRequested;
                    if (cancelled)
                        this.ApplyCancellationTransactionSemantics();
                }
                finally
                {
                    session.Executing = false;
                    _ = this.engineExecutionGate.Release();
                }

                // Consume any attention the multiplexer signalled. Reading the
                // flag with an exchange AFTER clearing Executing closes the race
                // where the attention lands just as execution finishes: the
                // multiplexer either saw Executing and left the flag for this
                // exchange, or saw it cleared and fed the pipe — the exchange
                // de-dupes so exactly one site emits the DONE_ATTN.
                var attention = Interlocked.Exchange(ref session.AttentionState, 0) == 1;
                if (cancelled || attention)
                    writer.WriteDone(Tds.DoneAttention, 0);

                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException or AuthenticationException)
        {
            // Client disconnect / teardown / malformed traffic ends the session.
        }
#pragma warning disable CA1031 // Terminal backstop: a MARS session's unanticipated exception must surface as a severe error, not silently kill the mux.
        catch (Exception)
        {
            // Terminal crash boundary for one MARS logical session: emit a
            // best-effort severe error rather than letting an unanticipated
            // exception fault the session loop and silently kill the whole mux.
            // The FIN in the finally still tears just this session down.
            await TryWriteSevereErrorAsync(writer, cancellationToken).ConfigureAwait(false);
        }
#pragma warning restore CA1031
        finally
        {
            try
            {
                await this.multiplexer!.SendFinAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private async ValueTask ExecuteBatchAsync(TdsMessage message, TdsTokenWriter writer, CancellationToken cancellationToken)
    {
        if ((message.FirstStatus & (Tds.StatusResetConnection | Tds.StatusResetConnectionSkipTran)) != 0)
        {
            this.ResetConnection();
            writer.WriteResetConnectionAck();
        }

        this.databaseAtMessageStart = this.connection!.Database;
        // Test-only: force an exception the typed catches below don't handle, to
        // exercise the terminal crash boundary. No-op in production (hook null).
        simulation.NetworkBatchCrashHookForTesting?.Invoke();
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
#pragma warning disable CA1031 // Deliberate: an unmodeled statement must not cost the whole session — see IsRecoverableStatementFault.
        catch (Exception ex) when (IsRecoverableStatementFault(ex, writer))
        {
            _ = this.FlushInfoMessages(writer);
            WriteUnexpectedStatementFault(writer, ex);
            this.WriteDatabaseChangeIfAny(writer);
            writer.WriteDone(Tds.DoneError, 0);
        }
#pragma warning restore CA1031
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
        // Depth of EXEC('…') / sp_executesql scopes currently open: while > 0,
        // statement outcomes render with DONEINPROC (0xFF) instead of the
        // batch/RPC done token, matching real SQL Server's nested-proc discipline.
        var procScopeDepth = 0;
        while (hasOutcome)
        {
            // Client attention (SqlCommand.Cancel / CommandTimeout) observed at
            // an outcome boundary: stop producing, leaving the DONE_ATTN ack to
            // the caller. Disposing the enumerator here unwinds the engine's
            // batch (its finally runs; output-parameter writeback does not).
            if (this.connection!.ExecutionCancellationToken.IsCancellationRequested)
                return true;

            var outcome = outcomes.Current;

            // Proc-scope markers bracket a dynamic-SQL body. Entry raises the
            // depth (no token); exit lowers it and closes the scope with
            // RETURNSTATUS + DONEPROC, exactly as real SQL Server frames an
            // EXEC('…'). The DONEPROC carries the usual more/final bit.
            if (outcome is SimulatedProcScopeBoundary boundary)
            {
                hasOutcome = outcomes.MoveNext();
                if (boundary.IsEnter)
                {
                    procScopeDepth++;
                    continue;
                }

                procScopeDepth--;
                var procStatus = this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow);
                if ((procStatus & Tds.DoneMore) == 0)
                    this.WriteDatabaseChangeIfAny(writer);
                writer.WriteReturnStatus(0);
                writer.WriteDoneToken(Tds.TokenDoneProc, procStatus, 0);
                continue;
            }

            // Inside a dynamic-SQL scope every statement uses DONEINPROC.
            var effectiveDoneToken = procScopeDepth > 0 ? Tds.TokenDoneInProc : doneToken;

            // Messages from a preceding no-outcome statement (PRINT,
            // severity<=10 RAISERROR): real SQL Server gives that statement
            // its own DONE after the INFO tokens. Without it SqlClient's
            // token reader stalls on the INFO/COLMETADATA adjacency until
            // command timeout (go-sqlcmd shakedown, 2026-07-14). RPC
            // responses skip the extra DONEINPROC — their per-statement
            // DONEINPROC stream is already well-formed for proc-body PRINT.
            if (this.FlushInfoMessages(writer) && !trailingTokensFollow)
                writer.WriteDoneToken(effectiveDoneToken, Tds.DoneMore, 0);
            if (outcome is SimulatedQueryResult query)
            {
                TdsTypeCodec.WriteColMetadata(writer, query.Schema, query.ColumnNames, query.ColumnNullability, query.ColumnReportsNumeric);
                long rows = 0;
                using (var cursor = query.CreateClientCursor())
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
                var queryStatus = this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow);
                // Real reports a result set's row count under DONE_COUNT and
                // drops the flag (keeping the count itself) under NOCOUNT.
                if (query.CountSuppressed != true)
                    queryStatus |= Tds.DoneCount;
                if ((queryStatus & Tds.DoneMore) == 0)
                    this.WriteDatabaseChangeIfAny(writer);
                // CurCmd tells the client whether that count is rows returned
                // or rows affected. A DML statement's OUTPUT clause makes the
                // statement tabular without making its count a SELECT's, so it
                // is the one result set that goes out unclassified.
                writer.WriteDoneToken(effectiveDoneToken, queryStatus, rows, query.CountsRowsReturned ? Tds.CmdSelect : (ushort)0);
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
                writer.WriteDoneToken(effectiveDoneToken, status, 0);
            }
            else
            {
                var affected = outcome.RecordsAffected;
                // SET NOCOUNT ON suppresses the rows-affected count: the DONE
                // omits DONE_COUNT so an ODBC/pyodbc driver skips this DML result
                // and advances to a trailing SELECT SCOPE_IDENTITY() (the
                // mssql-django identity pattern — without this it stalls on the
                // INSERT's rowcount). Read off the outcome rather than the live
                // session flag: the statement after this one runs on the
                // MoveNext below, and its own SET NOCOUNT would otherwise decide
                // this statement's DONE.
                var suppressCount = outcome.CountSuppressed == true;
                hasOutcome = outcomes.MoveNext();
                var status = this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow);
                if (affected >= 0 && !suppressCount)
                    status |= Tds.DoneCount;

                if ((status & Tds.DoneMore) == 0)
                    this.WriteDatabaseChangeIfAny(writer);
                // Real keeps the row count in the token and only clears the
                // flag, so a suppressed count still goes out as the number.
                writer.WriteDoneToken(
                    effectiveDoneToken,
                    status,
                    Math.Max(affected, 0),
                    outcome.CountsRowsReturned ? Tds.CmdSelect : (ushort)0);
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
        var loginName = previous.Security.OriginalLoginName;
        this.transaction = null;
        previous.Dispose();

        var fresh = simulation.CreateDbConnection();
        fresh.Open();
        fresh.InfoMessage += this.OnInfoMessage;
        this.pendingInfoMessages.Clear();
        if (!string.Equals(fresh.Database, database, StringComparison.Ordinal))
            fresh.ChangeDatabase(database);

        // Re-stamp the session principal from the original login so a reset
        // connection keeps its mapped-user identity (the reset preserves the
        // login, only the session state is cleared).
        if (Simulation.TryMapLoginToDatabaseUser(simulation, fresh.CurrentDatabase, loginName, out var principal))
            fresh.Security = Simulation.BuildAuthenticatedSecurityContext(principal, loginName);

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

    private static bool ParsePreloginMars(ReadOnlySpan<byte> payload)
    {
        for (var i = 0; (i + 5) <= payload.Length && payload[i] != Tds.PreloginTerminator; i += 5)
        {
            if (payload[i] != Tds.PreloginMars)
                continue;

            var offset = (payload[i + 1] << 8) | payload[i + 2];
            if (offset < payload.Length)
                return payload[offset] == 1;
        }

        return false;
    }

    private static byte[] BuildPreloginResponse(bool includeFedAuth, bool marsRequested)
    {
        // Options: VERSION(6) ENCRYPTION(1) INSTOPT(1) THREADID(0) MARS(1)
        // [FEDAUTHREQUIRED(1)], each with a 5-byte descriptor, then the
        // terminator, then the data region the offsets point into.
        var optionCount = includeFedAuth ? 6 : 5;
        var dataStart = (optionCount * 5) + 1;
        var data = new List<(byte Token, byte[] Value)>
        {
            // VERSION = major, minor, build (big-endian u16), subbuild (u16,
            // zero like real's prelogin); values derive from ReferenceBuild.
            (Tds.PreloginVersion,
                [
                    checked((byte)ReferenceBuild.Version.Major),
                    checked((byte)ReferenceBuild.Version.Minor),
                    (byte)(ReferenceBuild.Version.Build >> 8),
                    (byte)(ReferenceBuild.Version.Build & 0xFF),
                    0,
                    0,
                ]),
            (Tds.PreloginEncryption, [Tds.EncryptRequired]),
            (Tds.PreloginInstance, [0]),
            (Tds.PreloginThreadId, []),
            (Tds.PreloginMars, [marsRequested ? (byte)1 : (byte)0]),
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
