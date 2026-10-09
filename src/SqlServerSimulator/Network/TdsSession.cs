using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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
    /// The connection's number for the request the non-MARS attention watcher
    /// guards; replaced only once that watcher has settled.
    /// </summary>
    private long watchedRequest;

    /// <summary>
    /// Serializes engine execution across all SMP logical sessions on a MARS
    /// connection: real MARS is cooperative multiplexing, never parallel
    /// execution, and the engine assumes one executor per connection
    /// (<c>CurrentExecutingThreadId</c>, transaction machinery). A session
    /// holds it while its request runs, buffering what each statement
    /// produces, and steps out between two statements only while what it
    /// buffered waits on the client's window, so the session's other
    /// requests run there and never inside a statement. Unused by non-MARS
    /// sessions.
    /// </summary>
    private readonly SemaphoreSlim engineExecutionGate = new(1, 1);

    private SmpMultiplexer? multiplexer;
    private int marsPacketSize = Tds.DefaultPacketSize;

    /// <summary>The JSON support version acknowledged at login, 0 for none.</summary>
    private byte jsonSupportVersion;

    /// <summary>The vector support version acknowledged at login, 0 for none.</summary>
    private byte vectorSupportVersion;

    /// <summary>
    /// Closes the socket; the session task observes the closure at its next
    /// I/O operation and runs its normal cleanup.
    /// </summary>
    public void Abort() => socket.Dispose();

    /// <summary>
    /// Ends the connection an idle session's <c>KILL</c> severs, closing it in
    /// order so the client finds it dead at its next command as it does after
    /// real's kill, rather than meeting a reset.
    /// </summary>
    public void CloseGracefully()
    {
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // Already gone: the dispose below is all that's left to do.
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        socket.Dispose();
    }

    /// <summary>
    /// Tears down the session's backing connection and the MARS machinery.
    /// Called by the listener after <see cref="RunAsync"/> returns; the
    /// connection's own teardown rolls back open transactions and drops temp
    /// tables.
    /// </summary>
    public void Dispose()
    {
        this.connection?.Dispose();
        this.engineExecutionGate.Dispose();
        this.multiplexer?.Dispose();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        CultureScope.SetEngine();
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

            // The native json and vector types go to a client that asked for
            // them, at the highest version modeled (captured 2026-10-02 against
            // SQL Server 2025 through SqlClient 7.0.2).
            this.jsonSupportVersion = Math.Min(login.JsonSupportVersion, (byte)1);
            this.vectorSupportVersion = Math.Min(login.VectorSupportVersion, (byte)1);
            writer = new TdsTokenWriter(transport) { NativeJson = this.jsonSupportVersion > 0, NativeVector = this.vectorSupportVersion > 0 };
            if (simulation.RefuseLogin(login.UserName, login.Password) is { } refusal)
            {
                // Probe-confirmed shape: one error at severity 14 state 1 — Msg
                // 18456 with identical wording for a wrong password, an unknown
                // login, an empty password and a login without CONNECT SQL (the
                // real server masks the detailed state from clients), Msg 18470
                // for a disabled one — then the connection closes.
                writer.WriteErrorOrInfo(Tds.TokenError, refusal.Number, refusal.State, refusal.Class, refusal.Message, "SIMULATED", "", 1);
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
            this.connection.Transport = transport.Counters = new ConnectionTransport(
                Ipv4Form(socket.RemoteEndPoint), Ipv4Form(socket.LocalEndPoint), login.TdsVersion, transport.PacketSize);
            if (!this.TryFireLogonTriggers(writer))
            {
                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                return;
            }
            this.connection.TransactionEvents = [];
            this.connection.RunsMars = marsRequested;
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
                this.connection.ScopesTransactionsToBatch = true;
                this.connection.PublishedSettings = SessionSettings.Capture(this.connection);
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

                // A WRITETEXT BULK waiting for its data takes whatever comes
                // next, in place of what that message asks.
                if (this.connection!.ParkedBulkText is { } parkedBulkText)
                {
                    if (!await this.ResumeBulkTextAsync(parkedBulkText, message, writer, cancellationToken).ConfigureAwait(false))
                        return;
                    pendingRead = transport.ReadMessageAsync(cancellationToken).AsTask();
                    continue;
                }

                if (message.PacketType == Tds.PacketAttention)
                {
                    // Attention with nothing executing: the session was idle, or
                    // the attention raced a response that already completed
                    // naturally. Either way, acknowledge it — SqlClient waits for
                    // the DONE_ATTN before declaring the connection reusable — and
                    // keep the session alive.
                    await this.WriteAttentionAcknowledgmentAsync(writer, interrupted: false, cancellationToken).ConfigureAwait(false);
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
                    this.watchedRequest = this.connection!.BeginRequest();
                    watcher = transport.ReadMessageAsync(cancellationToken).AsTask();
                    _ = watcher.ContinueWith(
                        static (read, state) =>
                        {
                            if (read.IsCompletedSuccessfully)
                            {
                                if (read.Result?.PacketType == Tds.PacketAttention && state is TdsSession { connection: { } target } session)
                                    target.CancelRequest(session.watchedRequest);
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
                if (attentionConsumed && this.connection!.Killed)
                {
                    // Another session's KILL ended this one mid-command: its
                    // error tokens and the DONE that carries the server-error
                    // bit stand where an attention's acknowledgment would, and
                    // the connection closes behind them.
                    var killed = SimulatedSqlException.SessionKilled();
                    WriteErrors(writer, killed);
                    writer.WriteDoneToken(Tds.TokenDone, ErrorDoneStatus(killed), 0, StatementDoneKind.Batch);
                }
                else if (attentionConsumed)
                {
                    // The engine settled the cancelled batch as it unwound
                    // (SimulatedDbConnection.SettleCancelledExecution); send
                    // what that announces, then the single DONE_ATTN the
                    // client is waiting for.
                    await this.WriteAttentionAcknowledgmentAsync(writer, interrupted: true, cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);

                // A command whose error ended the session closed its connection;
                // real drops the client there too.
                if (this.connection!.State == System.Data.ConnectionState.Closed)
                    return;

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

    private bool TryOpenConnection(Login7Request login, TdsTokenWriter writer)
    {
        var requestedDatabase = login.Database;
        var userName = login.UserName;
        var opened = simulation.CreateDbConnection();
        opened.OpenSession();
        // LOGIN7 carries the client's workstation and application names; the
        // session keeps them for HOST_NAME() / APP_NAME(),
        // sys.dm_exec_sessions and the sp_who family.
        opened.ClientHostName = login.HostName;
        opened.ClientApplicationName = login.AppName;
        opened.OriginalDatabaseName = requestedDatabase;
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

        opened.FramesEveryStatement = true;
        opened.AbortTransport = this.CloseGracefully;
        this.connection = opened;
        return true;
    }

    /// <summary>
    /// Runs the server's logon triggers for the login just settled. A refusal
    /// reaches the client after the login's database and language notices, as
    /// real sends them (probed 2026-09-28 against SQL Server 2025: SqlClient
    /// reports Msg 17892 with Msg 5701 and 5703 beside it), and closes the
    /// connection without a LOGINACK.
    /// </summary>
    private bool TryFireLogonTriggers(TdsTokenWriter writer)
    {
        var opened = this.connection!;
        try
        {
            simulation.FireLogonTriggers(opened, isPooled: false);
            return true;
        }
        catch (SimulatedSqlException ex)
        {
            var database = opened.Database;
            writer.WriteEnvChange(Tds.EnvDatabase, database, "master");
            writer.WriteErrorOrInfo(Tds.TokenInfo, 5701, 2, 0, $"Changed database context to '{database}'.", ServerName, "", 1);
            writer.WriteEnvChange(Tds.EnvLanguage, "us_english", "");
            writer.WriteErrorOrInfo(Tds.TokenInfo, 5703, 1, 0, "Changed language setting to us_english.", ServerName, "", 1);
            WriteErrors(writer, ex);
            writer.WriteDone(Tds.DoneError, 0);
            this.connection = null;
            opened.Dispose();
            return false;
        }
    }

    private static bool FailLogin(TdsTokenWriter writer, string databaseName, string userName, SimulatedDbConnection opened)
    {
        writer.WriteErrorOrInfo(Tds.TokenError, 4060, 1, 11, $"Cannot open database \"{databaseName}\" requested by the login. The login failed.", "SIMULATED", "", 1);
        writer.WriteErrorOrInfo(Tds.TokenError, 18456, 1, 14, $"Login failed for user '{userName}'.", "SIMULATED", "", 1);
        writer.WriteDone(Tds.DoneError, 0);
        opened.Dispose();
        return false;
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
        // Real acknowledges a requested feature after the packet-size change.
        if (this.jsonSupportVersion > 0 || this.vectorSupportVersion > 0)
        {
            Span<(byte, byte)> features = stackalloc (byte, byte)[2];
            var count = 0;
            if (this.jsonSupportVersion > 0)
                features[count++] = (0x0D, this.jsonSupportVersion);
            if (this.vectorSupportVersion > 0)
                features[count++] = (0x0E, this.vectorSupportVersion);
            writer.WriteFeatureExtAck(features[..count]);
        }
        writer.WriteDone(Tds.DoneFinal, 0);
    }

    /// <summary>
    /// Cancels the request <paramref name="session"/> is serving. Called by
    /// the multiplexer when a client attention targets a session serving one,
    /// running or parked between its statements; the request object is that
    /// request's alone, so the cancel can't reach the next.
    /// </summary>
    public void CancelConnectionExecution(SmpSession session) => Volatile.Read(ref session.Request)?.Cancel();

    /// <summary>
    /// Serves one request of a MARS logical session: runs it under the
    /// connection's execution gate into the session's deferred-flush writer —
    /// stepping out between statements while what it produced waits on the
    /// client (see <see cref="SendWhatTheClientTakesAsync"/>) — then acknowledges
    /// any attention and sends the rest of the response.
    /// </summary>
    private async Task ServeMarsRequestAsync(SmpSession session, SessionRequest request, TdsMessage message, string? batchText, bool isBulkInsertBegin, TdsTokenWriter writer, CancellationToken cancellationToken)
    {
        bool cancelled;
        if (message.PacketType is Tds.PacketSqlBatch or Tds.PacketRpc or Tds.PacketTransactionManager)
            request.OutstandingRequestCount = ReadOutstandingRequestCount(message.Payload);
        Volatile.Write(ref session.Request, request);
        session.Executing = true;
        try
        {
            // Another request sending a DML statement's OUTPUT rows holds the
            // session: this one waits beside it, outside the gate so that
            // request can drain, until Msg 3980 refuses it.
            await this.connection!.AwaitSessionAsync(request, timeout: null, stop: () => request.AttentionReceived).ConfigureAwait(false);
            await this.engineExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            session.Executing = false;
            throw;
        }
        this.connection!.ResumeRequest(request);
        try
        {
            // Another request still pending can refuse this one outright.
            if (this.connection.RefuseNewRequest() is { } refused)
            {
                WriteErrors(writer, AtLineOne(refused));
                writer.WriteDone(Tds.DoneError, 0);
            }
            else
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
            }

            // Read under the lock: the engine settled a cancelled batch (its
            // XACT_ABORT rollback included) as it unwound here.
            cancelled = this.connection!.ExecutionCancellationRequested;
        }
        finally
        {
            this.connection!.SuspendRequest(request, finished: true);
            session.Executing = false;
            _ = this.engineExecutionGate.Release();
        }

        // Consume any attention the multiplexer signalled. Reading the flag
        // with an exchange AFTER clearing Executing closes the race where the
        // attention lands just as execution finishes: the multiplexer either
        // saw Executing and left the flag for this exchange, or saw it cleared
        // and fed the pipe — the exchange de-dupes so exactly one site emits
        // the DONE_ATTN.
        var attention = Interlocked.Exchange(ref session.AttentionState, 0) == 1;
        if (cancelled || attention)
            await this.WriteAttentionAcknowledgmentAsync(writer, interrupted: cancelled, cancellationToken).ConfigureAwait(false);

        await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the TDS batch loop for one SMP logical session. Mirrors the
    /// non-MARS loop but over a per-session transport riding the session's
    /// demuxed stream, guards engine execution with the per-connection
    /// execution gate, and buffers each statement's output (deferred flush)
    /// so a window-blocked send happens outside the gate. All logical
    /// sessions share this session's <see cref="SimulatedDbConnection"/>.
    /// </summary>
    public async Task RunMarsSessionAsync(SmpSession session, CancellationToken cancellationToken)
    {
        CultureScope.SetEngine();
        using var logicalStream = new SmpSessionStream(session);
        var transport = new TdsPacketTransport(logicalStream)
        {
            PacketSize = this.marsPacketSize,
            Spid = unchecked((ushort)this.connection!.Spid),
            Counters = this.connection.Transport,
        };
        var writer = new TdsTokenWriter(transport) { DeferFlush = true, MarsSession = session, NativeJson = this.jsonSupportVersion > 0, NativeVector = this.vectorSupportVersion > 0 };
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
                        await this.WriteAttentionAcknowledgmentAsync(writer, interrupted: false, cancellationToken).ConfigureAwait(false);
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

                var serving = this.connection!;
                var request = serving.BeginSessionRequest(inProcess: false);
                request.RequestId = session.Sid + 1;
                // The request is done once its whole response has gone out,
                // which for a large result waits on the client: it ends as its
                // last packet goes, or on the way out if no packet does. A
                // rollback another request made of the transaction it works on
                // meanwhile ends its response with Msg 3998.
                var ended = false;
                void End()
                {
                    if (ended)
                        return;
                    ended = true;
                    if (request.TransactionEndedUnder && writer.ReopenFinalDone())
                    {
                        var uncommittable = AtLineOne(SimulatedSqlException.UncommittableTransactionAtEndOfBatch());
                        WriteErrors(writer, uncommittable);
                        writer.WriteDoneToken(Tds.TokenDone, ErrorDoneStatus(uncommittable), 0, StatementDoneKind.Batch);
                    }
                    EndRequest();
                }
                // A reset moves the request to the fresh connection.
                void EndRequest() => (this.connection ?? serving).EndSessionRequest(request);
                writer.BeforeEndOfMessage = End;
                writer.Request = request;
                try
                {
                    await this.ServeMarsRequestAsync(session, request, message, batchText, isBulkInsertBegin, writer, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    writer.BeforeEndOfMessage = null;
                    writer.Request = null;
                    if (!ended)
                    {
                        ended = true;
                        EndRequest();
                    }
                }
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
        if (this.ResetRequested(message))
        {
            if (!this.TryResetConnection(writer))
                return;
            writer.WriteResetConnectionAck();
        }

        // Test-only: force an exception the typed catches below don't handle, to
        // exercise the terminal crash boundary. No-op in production (hook null).
        simulation.NetworkBatchCrashHookForTesting?.Invoke();
        await this.ExecuteBatchTextAsync(ExtractBatchText(message.Payload), writer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a SQL batch's text and streams its response, converting an error
    /// that escapes it into the response's closing tokens.
    /// </summary>
    private async ValueTask ExecuteBatchTextAsync(string batchText, TdsTokenWriter writer, CancellationToken cancellationToken)
    {
        this.databaseAtMessageStart = this.connection!.Database;
        try
        {
            using var command = this.connection.CreateCommand();
#pragma warning disable CA2100 // This IS a SQL endpoint: the batch text is the client's query by design.
            command.CommandText = batchText;
#pragma warning restore CA2100
            command.YieldsBetweenStatements = this.multiplexer is not null;
            command.StreamsResultRows = true;
            // A cancelled batch (return value true) leaves the DONE_ATTN
            // acknowledgment to the session loop; nothing more to emit here.
            _ = await this.StreamOutcomesAsync(command, writer, Tds.TokenDone, trailingTokensFollow: false, cancellationToken).ConfigureAwait(false);
        }
        catch (SimulatedSqlException ex)
        {
            if (!this.FlushInfoMessages(writer))
                TakeBackDoneAheadOfKill(writer, ex);
            WriteErrors(writer, ex);
            this.WriteSessionEnvChangesIfAny(writer);
            writer.WriteDoneToken(Tds.TokenDone, ErrorDoneStatus(ex), 0, StatementDoneKind.Batch);
        }
        catch (NotSupportedException ex)
        {
            _ = this.FlushInfoMessages(writer);
            writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, $"SqlServerSimulator: {ex.Message}", "SIMULATED", "", 1);
            this.WriteSessionEnvChangesIfAny(writer);
            writer.WriteDoneToken(Tds.TokenDone, Tds.DoneError, 0, StatementDoneKind.Batch);
        }
#pragma warning disable CA1031 // Deliberate: an unmodeled statement must not cost the whole session — see IsRecoverableStatementFault.
        catch (Exception ex) when (IsRecoverableStatementFault(ex, writer))
        {
            _ = this.FlushInfoMessages(writer);
            WriteUnexpectedStatementFault(writer, ex);
            this.WriteSessionEnvChangesIfAny(writer);
            writer.WriteDoneToken(Tds.TokenDone, Tds.DoneError, 0, StatementDoneKind.Batch);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// The session database when the current batch / RPC message began, for
    /// detecting a mid-message <c>USE</c>. Emitted as ENVCHANGE type 1 via
    /// <see cref="WriteSessionEnvChangesIfAny"/>, which must run
    /// BEFORE the response's final DONE: SqlClient's token reader stalls
    /// until command timeout on an ENVCHANGE that arrives after the last
    /// DONE (probe-confirmed 2026-07-15 — the SSMS freeze on
    /// <c>use [master]</c>; go-mssqldb tolerates the late position, which is
    /// how the ordering shipped unnoticed).
    /// </summary>
    private string? databaseAtMessageStart;

    /// <summary>
    /// The error that ended the statements an RPC ran, which closes the RPC's
    /// DONEPROC rather than a DONEINPROC of its own; set by
    /// <see cref="StreamOutcomesAsync"/> and taken by the RPC handler.
    /// </summary>
    private SimulatedSqlException? rpcEndingError;

    /// <summary>The language the session last announced with ENVCHANGE type 2.</summary>
    private string announcedLanguage = "us_english";

    /// <summary>
    /// Writes the session-state ENVCHANGEs a message may have earned: the
    /// database-change ENVCHANGE when the session database differs from
    /// <see cref="databaseAtMessageStart"/> (the <c>USE</c> statement's own
    /// INFO 5701 is the engine's, and precedes it here where real sends it
    /// after), and
    /// the transaction-ended ENVCHANGE when the engine ended the session's
    /// transaction underneath the TM layer. Idempotent — each arm records its
    /// new baseline, so the several call sites (per-final-DONE seams and error
    /// paths) emit at most once per change.
    /// </summary>
    private void WriteSessionEnvChangesIfAny(TdsTokenWriter writer)
    {
        this.WriteTransactionEnvChanges(writer);
        this.WriteDatabaseEnvChange(writer);
    }

    /// <summary>
    /// The database ENVCHANGE, when the session database differs from the one
    /// last announced.
    /// </summary>
    private void WriteDatabaseEnvChange(TdsTokenWriter writer)
    {
        var current = this.connection!.Database;
        if (this.databaseAtMessageStart is null || string.Equals(current, this.databaseAtMessageStart, StringComparison.Ordinal))
            return;
        writer.WriteEnvChange(Tds.EnvDatabase, current, this.databaseAtMessageStart);
        this.databaseAtMessageStart = current;
    }

    /// <summary>
    /// Writes a transaction ENVCHANGE for each transaction the session began or
    /// ended since the last call — by SQL text, a transaction-manager request,
    /// an implicit transaction or the engine (an error that rolls it back, a
    /// deadlock victim, a trigger's <c>ROLLBACK</c>) — as real does at the
    /// point it happens: begin in the new-value field, commit or rollback in
    /// the old-value field, under a descriptor unique across the server's
    /// sessions — the transaction's own id (probed 2026-09-28 against SQL
    /// Server 2025). Each call site sits
    /// ahead of the next token the response writes, so an event lands where
    /// real sends it, less the DONE real gives the statement that caused it.
    /// </summary>
    private void WriteTransactionEnvChanges(TdsTokenWriter writer, int limit = int.MaxValue)
    {
        if (this.connection?.TransactionEvents is not { Count: > 0 } events)
            return;
        var count = Math.Min(limit, events.Count);
        for (var i = 0; i < count; i++)
        {
            var (transactionEvent, transaction, _) = events[i];
            writer.WriteEnvChangeTransaction(
                transactionEvent switch
                {
                    TransactionEvent.Begin => Tds.EnvBeginTransaction,
                    TransactionEvent.Commit => Tds.EnvCommitTransaction,
                    _ => Tds.EnvRollbackTransaction,
                },
                (ulong)transaction.TransactionId);
        }
        events.RemoveRange(0, count);
    }

    /// <summary>
    /// How many transaction events are pending — taken before a lookahead
    /// runs the next statement, so that statement's events wait for its own
    /// tokens rather than going out ahead of the DONE of the one before it.
    /// </summary>
    private int PendingTransactionEventCount => this.connection?.TransactionEvents?.Count ?? 0;

    /// <summary>
    /// The pending transaction events the statement that produced
    /// <paramref name="outcome"/> had recorded by the time it finished, which
    /// go out ahead of its DONE; <paramref name="unstamped"/> for an outcome
    /// carrying no mark.
    /// </summary>
    private int EventsBefore(SimulatedStatementOutcome outcome, int unstamped)
    {
        if (outcome.TransactionEventMark < 0 || this.connection?.TransactionEvents is not { } events)
            return unstamped;
        var count = 0;
        while (count < events.Count && events[count].Serial < outcome.TransactionEventMark)
            count++;
        return count;
    }

    /// <summary>
    /// The pending transaction events that go out ahead of an outcome's first
    /// token: all of them, except that a rollback the outcome's own error
    /// caused goes out after that error, as real sends it (probed 2026-09-28
    /// against SQL Server 2025).
    /// </summary>
    private int TransactionEventsAheadOf(SimulatedStatementOutcome outcome)
    {
        var count = this.EventsBefore(outcome, this.PendingTransactionEventCount);
        return count > 0
            && outcome is SimulatedErrorOutcome or SimulatedSqlResultSet { EndedByError: true, ErrorCaught: false }
            && this.connection!.TransactionEvents![count - 1].Event == TransactionEvent.Rollback
                ? count - 1
                : count;
    }

    /// <summary>
    /// An outcome as the response renders it: whether its DONE is a
    /// DONEINPROC, which it is inside a procedure, trigger or dynamic-SQL
    /// scope and throughout an RPC.
    /// </summary>
    private readonly struct RenderedOutcome(SimulatedStatementOutcome outcome, bool inProc, bool withoutStatus = false)
    {
        public readonly SimulatedStatementOutcome Outcome = outcome;
        public readonly bool InProc = inProc;

        /// <summary>
        /// Whether a scope's exit, rendered as a DONEPROC, sends no
        /// RETURNSTATUS ahead of it: a nested one a resumed response renders
        /// at batch level (see <see cref="ResponseScopes.Resumed"/>).
        /// </summary>
        public readonly bool WithoutStatus = withoutStatus;
    }

    /// <summary>
    /// The procedure and dynamic-SQL scopes a response's outcomes are inside.
    /// </summary>
    private sealed class ResponseScopes
    {
        /// <summary>The scopes the response's outcomes entered and haven't left.</summary>
        public int Entered;

        /// <summary>
        /// The scopes the batch was inside when a bulk form suspended it,
        /// which the response resuming it is inside until it leaves them. Real
        /// renders that response at batch level throughout — a DONE where a
        /// DONEINPROC would go, a nested scope's exit a DONEPROC with no
        /// RETURNSTATUS, <c>NOCOUNT</c> dropping no DONE — until the outermost
        /// call returns (probed 2026-10-07 against SQL Server 2025).
        /// </summary>
        public int Resumed;
    }

    /// <summary>
    /// The scopes a suspended batch was inside, for the response resuming it
    /// (<see cref="ResponseScopes.Resumed"/>); 0 outside one.
    /// </summary>
    private int resumedScopes;

    /// <summary>
    /// The outcomes that put a token on the wire, each with where it renders.
    /// Scope entry markers write nothing, and inside a scope <c>NOCOUNT</c>
    /// drops every DONEINPROC that would carry neither a result set nor an
    /// error — a DML statement's, a <c>SET</c>'s, a nested call's — where at
    /// batch level it only clears the count bit (probed 2026-09-28 against
    /// SQL Server 2025). Filtering ahead of the writer is what lets the more
    /// bit read "another token follows".
    /// </summary>
    private static IEnumerable<RenderedOutcome> RenderedOutcomes(IEnumerable<SimulatedStatementOutcome> outcomes, bool rpc, ResponseScopes scopes)
    {
        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case SimulatedProcScopeBoundary { IsEnter: true }:
                    scopes.Entered++;
                    continue;
                case SimulatedProcScopeBoundary { Unsent: true }:
                    scopes.Entered = Math.Max(scopes.Entered - 1, 0);
                    continue;
                case SimulatedProcScopeBoundary when scopes.Resumed > 0:
                    if (scopes.Entered > 0)
                        scopes.Entered--;
                    else
                        scopes.Resumed--;
                    yield return scopes.Resumed > 0 || scopes.Entered > 0
                        ? new(outcome, inProc: false, withoutStatus: true)
                        : new(outcome, outcome.InModule || rpc);
                    continue;
                case SimulatedProcScopeBoundary exit:
                    scopes.Entered = Math.Max(scopes.Entered - 1, 0);
                    var nested = scopes.Entered > 0 || exit.InModule || rpc;
                    if (nested && exit.CountSuppressed == true && !exit.EndedByError)
                        continue;
                    yield return new(outcome, nested);
                    continue;
                case SimulatedReturnStatus when scopes.Resumed > 0:
                    yield return new(outcome, scopes.Entered > 0 || scopes.Resumed > 1);
                    continue;
                case SimulatedReturnStatus:
                    // Rendered where the exit it precedes would be.
                    yield return new(outcome, scopes.Entered > 1 || outcome.InModule || rpc);
                    continue;
            }
            if (scopes.Resumed > 0)
            {
                yield return new(outcome, inProc: false);
                continue;
            }
            var inProc = scopes.Entered > 0 || outcome.InModule || rpc;
            if (inProc && outcome is SimulatedNonQuery { CountSuppressed: true })
                continue;
            yield return new(outcome, inProc);
        }
    }

    /// <summary>
    /// Executes a command and streams its outcomes as result-set and DONE
    /// tokens, every statement closing with a DONE of its own that names its
    /// kind (<see cref="StatementDoneKind"/>) — a plain DONE at batch level,
    /// DONEINPROC inside a scope — and every procedure or dynamic-SQL scope
    /// with RETURNSTATUS + DONEPROC, or a DONEINPROC when it is nested in
    /// another. RPC responses pass <paramref name="trailingTokensFollow"/>,
    /// because RETURNVALUE / RETURNSTATUS / DONEPROC still follow and every
    /// DONEINPROC must carry the more bit. Fully drains the outcome
    /// enumerator, which is what triggers the engine's output-parameter
    /// writeback.
    /// </summary>
    private async ValueTask<bool> StreamOutcomesAsync(SimulatedDbCommand command, TdsTokenWriter writer, byte doneToken, bool trailingTokensFollow, CancellationToken cancellationToken)
    {
        var scopes = new ResponseScopes { Resumed = this.resumedScopes };
        this.resumedScopes = 0;
        using var outcomes = RenderedOutcomes(simulation.CreateResultSetsForCommand(command, continueOnError: true), trailingTokensFollow, scopes).GetEnumerator();

        var hasOutcome = outcomes.MoveNext();
        // Whether the last token written closes with a DONE; when it doesn't,
        // the batch closes with one of its own.
        var closed = false;
        var unclosedError = false;

        // An error that escapes the outcome stream while it looks ahead — one
        // ending the batch, or an RPC's procedure — is raised once the outcome
        // just written has its DONE, which carries the more bit since the
        // error's tokens follow it, as real sends them.
        ExceptionDispatchInfo? escaped = null;
        bool Advance()
        {
            try
            {
                return outcomes.MoveNext();
            }
            catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
            {
                escaped = ExceptionDispatchInfo.Capture(ex);
                return true;
            }
        }

        // Steps past the outcome just written, first writing any message that
        // closes it (Msg 8153, Msg 3621) ahead of its DONE, where real sends it.
        bool AdvancePastClosingMessages()
        {
            var more = Advance();
            while (more && escaped is null && outcomes.Current.Outcome is SimulatedInfoOutcome { FollowsRows: true } closing)
            {
                var message = closing.Message;
                writer.WriteErrorOrInfo(Tds.TokenInfo, message.Number, message.State, message.Class, message.Message, ServerName, message.Procedure, message.LineNumber);
                more = Advance();
            }
            return more;
        }

        while (hasOutcome)
        {
            // Client attention (SqlCommand.Cancel / CommandTimeout) observed at
            // an outcome boundary: stop producing, leaving the DONE_ATTN ack to
            // the caller. Disposing the enumerator here unwinds the engine's
            // batch (its finally runs; output-parameter writeback does not).
            if (this.connection!.ExecutionCancellationToken.IsCancellationRequested)
                return true;

            escaped?.Throw();
            var rendered = outcomes.Current;
            var outcome = rendered.Outcome;
            var effectiveDoneToken = rendered.InProc ? Tds.TokenDoneInProc : doneToken;

            // A streamed result set's marker, which its rows' writer reads.
            if (outcome is SimulatedRowsProduced)
            {
                hasOutcome = Advance();
                continue;
            }

            // Between two of a MARS request's statements: what the ones before
            // went out, the request stepping aside while it waits on the
            // client.
            if (outcome is SimulatedStatementBoundary)
            {
                if (this.FlushInfoMessages(writer))
                    closed = false;
                await this.SendWhatTheClientTakesAsync(writer, cancellationToken).ConfigureAwait(false);
                if (this.connection.ExecutionCancellationRequested)
                    return true;
                hasOutcome = Advance();
                continue;
            }

            // An informational message is an INFO token ahead of whatever the
            // next outcome writes, and carries no DONE of its own. A USE's
            // Msg 5701 follows the database ENVCHANGE and precedes the new
            // database's collation, as real sends them.
            if (outcome is SimulatedInfoOutcome info)
            {
                if (info.Message.Number == 5701)
                {
                    // Every USE announces its database, the current one included.
                    _ = this.FlushInfoMessages(writer);
                    writer.WriteEnvChange(Tds.EnvDatabase, this.connection.Database, this.databaseAtMessageStart ?? this.connection.Database);
                    this.databaseAtMessageStart = this.connection.Database;
                    var message = info.Message;
                    writer.WriteErrorOrInfo(Tds.TokenInfo, message.Number, message.State, message.Class, message.Message, ServerName, message.Procedure, message.LineNumber);
                    var collation = TdsCollationCodec.For(this.connection.CurrentDatabase.Collation);
                    writer.WriteEnvChangeSqlCollation(collation.Info, collation.SortId);
                    closed = false;
                }
                // A SET LANGUAGE's Msg 5703 follows the language ENVCHANGE.
                else if (info.Message.Number == 5703)
                {
                    _ = this.FlushInfoMessages(writer);
                    writer.WriteEnvChange(Tds.EnvLanguage, this.connection.Language.Name, this.announcedLanguage);
                    this.announcedLanguage = this.connection.Language.Name;
                    this.pendingInfoMessages.Enqueue(info.Message);
                }
                else
                {
                    this.pendingInfoMessages.Enqueue(info.Message);
                }
                hasOutcome = Advance();
                continue;
            }

            this.WriteTransactionEnvChanges(writer, this.TransactionEventsAheadOf(outcome));
            _ = this.FlushInfoMessages(writer);

            // A return status sent ahead of the error that closes its scope,
            // or on its own (DROP LOGIN's pair), which leaves the batch to
            // close with a DONE of its own.
            if (outcome is SimulatedReturnStatus returned)
            {
                if (!rendered.InProc)
                {
                    writer.WriteReturnStatus(returned.Status);
                    closed = false;
                }
                hasOutcome = Advance();
                continue;
            }

            // A scope's exit closes it with RETURNSTATUS + DONEPROC, or a
            // DONEINPROC when nested — neither carrying a status when an error
            // abandoned the scope, the DONEPROC then carrying the error bit.
            if (outcome is SimulatedProcScopeBoundary exit)
            {
                var exitEvents = this.EventsBefore(exit, this.PendingTransactionEventCount);
                hasOutcome = Advance();
                var exitStatus = this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow);
                if (exit.EndedByError)
                    exitStatus |= Tds.DoneError;
                this.WriteTransactionEnvChanges(writer, exitEvents);
                if ((exitStatus & Tds.DoneMore) == 0)
                    this.WriteSessionEnvChangesIfAny(writer);
                if (!rendered.InProc && !rendered.WithoutStatus && exit.ReturnStatus is int returnStatus)
                    writer.WriteReturnStatus(returnStatus);
                writer.WriteDoneToken(rendered.InProc ? Tds.TokenDoneInProc : Tds.TokenDoneProc, exitStatus, 0, StatementDoneKind.Execute);
                closed = true;
                unclosedError = false;
                continue;
            }

            if (outcome is SimulatedQueryResult query)
            {
                // A statement still producing its rows produces more as they
                // go out (see ResultStream), each window one advance of the
                // outcome stream.
                var streamed = query is SimulatedSqlResultSet { Stream: { Complete: false } open } ? open : null;
                if (streamed is { } stream)
                {
                    // A MARS request's packets wait in the writer for the
                    // client's window, which runs the statement's four packets
                    // ahead of the client itself (see SendWhatTheClientTakesAsync).
                    if (writer.MarsSession is not null)
                        stream.LeadPackets = 1;
                    stream.Pull = () =>
                    {
                        if (Advance() && escaped is null && outcomes.Current.Outcome is not SimulatedRowsProduced)
                            throw new InvalidOperationException("A streamed result set's statement sent an outcome before its rows ended.");
                    };
                    // A KILL ends a request waiting on its client by dropping
                    // the connection under it, as real does.
                    stream.AbandonByConsumer = this.connection.AbortTransport;
                }
                writer.MarkResultStart();
                // A DML statement's OUTPUT rows going out as it produces them
                // hold a MARS session from their first.
                if (query is SimulatedSqlResultSet { Stream.PendingWrite: not null })
                    writer.HoldSessionFromResultStart();
                TdsTypeCodec.WriteColMetadata(writer, query.Schema, query.ColumnNames, query.ColumnNullability, query.ColumnReportsNumeric, query.HiddenColumnCount, query.ColumnWireFlags, this.connection!.CurrentDatabase.Name, query.Browse);
                long rows = 0;
                using (var cursor = query.CreateClientCursor())
                {
                    while (cursor.MoveNext())
                    {
                        TdsTypeCodec.WriteRow(writer, query.Schema, cursor, query.ColumnNullability);
                        rows++;
                        await writer.FlushAsync(final: false, cancellationToken).ConfigureAwait(false);
                        // A MARS request's statement suspended on its client
                        // steps aside for the session's other requests while
                        // the client's window is shut, as real's does.
                        if (streamed is { Complete: false } && writer.MarsSession is not null)
                            await this.SendWhatTheClientTakesAsync(writer, cancellationToken).ConfigureAwait(false);
                        // Mid-result-set attention: stop between rows (never
                        // mid-row — the flush above closed the last ROW token).
                        // The partial rows already sent are discarded client-side
                        // once it reads the DONE_ATTN the caller emits.
                        if (this.connection!.ExecutionCancellationToken.IsCancellationRequested)
                            return true;
                    }
                }

                var queryEvents = this.EventsBefore(query, this.PendingTransactionEventCount);
                // An error that escaped while the rows were produced follows
                // their DONE.
                hasOutcome = escaped is not null || AdvancePastClosingMessages();
                // A statement whose own error cut its rows short sends that
                // error ahead of the result set's DONE, as real does.
                var cutShort = false;
                if (query is SimulatedSqlResultSet { EndedByError: true, ErrorCaught: false } && hasOutcome && escaped is null && outcomes.Current.Outcome is SimulatedErrorOutcome cutShortError)
                {
                    WriteErrors(writer, cutShortError.Exception);
                    queryEvents = this.EventsBefore(cutShortError, this.PendingTransactionEventCount);
                    hasOutcome = AdvancePastClosingMessages();
                    cutShort = true;
                }
                var queryStatus = this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow);
                // A statement its own error cut short reports no count at all,
                // not even for the rows it sent first — DONE_ERROR with a zero
                // count (captured from SQL Server 2025, 2026-09-26) — so
                // SqlClient raises no StatementCompleted for it.
                if (cutShort)
                {
                    queryStatus |= Tds.DoneError;
                    rows = 0;
                }
                // Real reports a result set's row count under DONE_COUNT and
                // drops the flag (keeping the count itself) under NOCOUNT.
                else
                {
                    // A caught error's statement counts nothing either, but
                    // still reports that count (probed 2026-09-26).
                    if (query is SimulatedSqlResultSet { ErrorCaught: true })
                        rows = 0;
                    else if (query is SimulatedSqlResultSet { ReportedRowCount: >= 0 and var reported })
                        rows = reported;
                    if (query.CountSuppressed != true)
                        queryStatus |= Tds.DoneCount;
                }
                this.WriteTransactionEnvChanges(writer, queryEvents);
                if ((queryStatus & Tds.DoneMore) == 0)
                    this.WriteSessionEnvChangesIfAny(writer);
                // CurCmd tells the client whether that count is rows returned
                // or rows affected. A DML statement's OUTPUT clause makes the
                // statement tabular without making its count a SELECT's.
                writer.WriteDoneToken(effectiveDoneToken, queryStatus, rows, DoneKindOf(query));
                // A DML statement's OUTPUT rows hold a MARS session while
                // they go out.
                if (!query.CountsRowsReturned)
                    writer.HoldSession();
                closed = true;
                unclosedError = false;
            }
            else if (outcome is SimulatedErrorOutcome { RaisedWhileCompiling: true } compiling)
            {
                // A compile's non-aborting error has no DONE of its own; the
                // next DONE carries its error bit.
                WriteErrors(writer, compiling.Exception);
                writer.CarryErrorToNextDone();
                closed = false;
                hasOutcome = Advance();
            }
            else if (outcome is SimulatedErrorOutcome errorOutcome)
            {
                // Statement-terminating error the engine chose to continue past
                // (continueOnError): its error token(s), then the statement's
                // DONE carrying DONE_ERROR — or none, when the procedure scope
                // it ended closes with a DONEPROC carrying the bit, and the
                // batch's closing DONE when it ended the batch.
                TakeBackDoneAheadOfKill(writer, errorOutcome.Exception);
                WriteErrors(writer, errorOutcome.Exception);
                // The rollback the error caused goes out ahead of the Msg 3621
                // closing its statement (probed 2026-10-07 against SQL Server
                // 2025).
                this.WriteTransactionEnvChanges(writer, this.EventsBefore(errorOutcome, this.PendingTransactionEventCount));
                hasOutcome = AdvancePastClosingMessages();
                if (errorOutcome.DoneKind == StatementDoneKind.ClosedByScope)
                {
                    closed = false;
                    unclosedError = true;
                    continue;
                }
                // An error that ended the batch closes it with the batch's own
                // DONE, whatever scope it was raised in — or, in an RPC, with
                // the RPC's DONEPROC.
                var errorKind = errorOutcome.DoneKind == StatementDoneKind.NoDone ? StatementDoneKind.Batch : errorOutcome.DoneKind;
                if (trailingTokensFollow && errorKind == StatementDoneKind.Batch)
                {
                    this.rpcEndingError = errorOutcome.Exception;
                    closed = false;
                    continue;
                }
                var status = (ushort)(this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow) | ErrorDoneStatus(errorOutcome.Exception));
                if ((status & Tds.DoneMore) == 0)
                    this.WriteSessionEnvChangesIfAny(writer);
                writer.WriteDoneToken(errorKind == StatementDoneKind.Batch ? doneToken : effectiveDoneToken, status, 0, errorKind);
                closed = true;
                unclosedError = false;
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
                var statementEvents = this.EventsBefore(outcome, this.PendingTransactionEventCount);
                hasOutcome = AdvancePastClosingMessages();
                var status = this.OutcomeDoneStatus(hasOutcome, trailingTokensFollow);
                if (affected >= 0 && !suppressCount)
                    status |= Tds.DoneCount;

                this.WriteTransactionEnvChanges(writer, statementEvents);
                if ((status & Tds.DoneMore) == 0)
                    this.WriteSessionEnvChangesIfAny(writer);
                // Real keeps the row count in the token and only clears the
                // flag, so a suppressed count still goes out as the number.
                writer.WriteDoneToken(effectiveDoneToken, status, Math.Max(affected, 0), DoneKindOf(outcome));
                closed = true;
                unclosedError = false;
            }
        }

        // A response whose last statement sent no DONE of its own — a batch
        // ending in a DECLARE, an empty one, one an error ended — or that ends
        // in messages (INFO may never follow the final DONE) closes with the
        // batch's own DONE, which carries the error bit of an error left
        // without one (probed 2026-09-28 against SQL Server 2025). A mid-batch
        // USE's ENVCHANGE must likewise precede the final DONE.
        var flushedTrailing = this.FlushInfoMessages(writer);
        // A bulk form suspended the batch: the response resuming it starts
        // inside the scopes this one ended in.
        var suspendedInScope = false;
        if (this.connection!.ParkedBulkText is { } parked)
        {
            parked.OpenScopes = scopes.Entered + scopes.Resumed;
            suspendedInScope = parked.OpenScopes > 0;
        }
        if (!trailingTokensFollow && (flushedTrailing || !closed))
        {
            this.WriteSessionEnvChangesIfAny(writer);
            writer.WriteDoneToken(doneToken, unclosedError ? Tds.DoneError : Tds.DoneFinal, 0, StatementDoneKind.Batch);
        }
        // A statement's DONE went out expecting the next statement to send
        // something, which it didn't: that DONE ends the response — a
        // DONEINPROC as the DONE, or the DONEPROC of a scope's exit, real ends
        // a response a bulk form suspended inside a call with.
        else if (!trailingTokensFollow)
        {
            if (suspendedInScope)
                writer.EndFinalDoneInScope();
            _ = writer.TryCloseFinalDone(this.WriteSessionEnvChangesIfAny);
        }

        return false;
    }

    /// <summary>
    /// Between two statements of a MARS request, or between two rows of a
    /// <c>SELECT</c> it is sending as its client reads them: the packets it
    /// filled go out — all but its last DONE, whose more bit the next
    /// statement settles. When the client's window can't take them yet, the
    /// request steps out of the execution gate until it has, so the session's
    /// other requests run meanwhile — a suspended statement holding what its
    /// position holds — and only then runs on: real runs a batch's statement
    /// once its client has read all but about 32 KB of what the batch sent
    /// before it, and suspends a <c>SELECT</c> about 40 KB ahead of its client
    /// (probed 2026-10-05 and 2026-10-06 against SQL Server 2025).
    /// </summary>
    private async ValueTask SendWhatTheClientTakesAsync(TdsTokenWriter writer, CancellationToken cancellationToken)
    {
        if (writer is not { MarsSession: { } session, Request: { } request })
            return;
        var packets = writer.CompletePacketsPending();
        if (packets == 0)
            return;
        if (packets <= session.SendWindowRemaining())
        {
            await writer.FlushCompletePacketsAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        var connection = this.connection!;
        var database = this.databaseAtMessageStart;
        connection.SuspendRequest(request);
        _ = this.engineExecutionGate.Release();
        try
        {
            await writer.FlushCompletePacketsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The batch's own unwinding, an error's included, runs on its
            // state under the gate.
            await this.engineExecutionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            connection.ResumeRequest(request);
            this.databaseAtMessageStart = database;
        }
    }

    /// <summary>
    /// The kind an outcome's DONE names: what its statement stamped, else a
    /// result set's SELECT.
    /// </summary>
    private static ushort DoneKindOf(SimulatedStatementOutcome outcome) =>
        outcome.DoneKind != StatementDoneKind.NoDone ? outcome.DoneKind
            : outcome.CountsRowsReturned ? StatementDoneKind.Select
            : (ushort)0;

    /// <summary>
    /// DONE status for a completed outcome: more tokens follow when another
    /// outcome exists, the response is an RPC (RETURNSTATUS / DONEPROC still
    /// come), or queued info messages remain to be written — the trailing-
    /// PRINT case, whose INFO must precede the batch's final DONE.
    /// </summary>
    private ushort OutcomeDoneStatus(bool hasOutcome, bool trailingTokensFollow) =>
        hasOutcome || trailingTokensFollow || this.pendingInfoMessages.Count > 0 ? Tds.DoneMore : Tds.DoneFinal;

    /// <summary>
    /// Acknowledges an attention as real does (captured 2026-09-30 against
    /// SQL Server 2025 through a cleartext tee). When it interrupted the
    /// batch, the batch's response ends first: the transaction ENVCHANGEs its
    /// unwinding recorded, Msg 3621 when it ended a write (see
    /// <see cref="SimulatedDbConnection.AttentionEndedWrite"/>), and a DONE
    /// carrying <c>DONE_ERROR</c>, which takes the place — and the statement
    /// kind — of a completed statement's DONE nothing has followed yet. The
    /// <c>DONE_ATTN</c> the client waits for before reusing the connection
    /// follows as a message of its own, and is the whole acknowledgment of an
    /// attention that found nothing running.
    /// </summary>
    private async ValueTask WriteAttentionAcknowledgmentAsync(TdsTokenWriter writer, bool interrupted, CancellationToken cancellationToken)
    {
        var connection = this.connection!;
        var curCmd = StatementDoneKind.Batch;
        if (interrupted && writer.TryTakeTrailingDone(out var pending))
            curCmd = pending;
        this.WriteTransactionEnvChanges(writer);
        if (interrupted)
        {
            if (connection.AttentionEndedWrite)
            {
                connection.AttentionEndedWrite = false;
                var message = SimulatedSqlException.AttentionStatementTerminatedMessage(connection);
                writer.WriteErrorOrInfo(Tds.TokenInfo, message.Number, message.State, message.Class, message.Message, ServerName, message.Procedure, message.LineNumber);
            }
            writer.WriteDoneToken(Tds.TokenDone, Tds.DoneError, 0, curCmd);
            await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
        }
        writer.WriteDoneToken(Tds.TokenDone, Tds.DoneAttention, 0, StatementDoneKind.Batch);
    }

    /// <summary>
    /// An endpoint as <c>sys.dm_exec_connections</c> prints it: a dual-mode
    /// socket's IPv4-mapped address in its IPv4 form.
    /// </summary>
    private static IPEndPoint? Ipv4Form(EndPoint? endPoint) =>
        endPoint is IPEndPoint { Address.IsIPv4MappedToIPv6: true } mapped
            ? new IPEndPoint(mapped.Address.MapToIPv4(), mapped.Port)
            : endPoint as IPEndPoint;

    /// <summary>
    /// Resets the session for a pooled connection's reuse, which is a login
    /// again as far as logon triggers go: they fire with <c>IsPooled</c> 1
    /// (probed 2026-09-28 against SQL Server 2025). A refusal kills the
    /// session — Msg 17892 then Msg 596 fail the request that carried the
    /// reset, and the connection closes — and returns false.
    /// </summary>
    private bool TryResetConnection(TdsTokenWriter writer)
    {
        this.ResetConnection();
        try
        {
            simulation.FireLogonTriggers(this.connection!, isPooled: true);
            this.connection!.TransactionEvents = [];
            return true;
        }
        catch (SimulatedSqlException ex)
        {
            // DONE_SRVERROR marks the request as killed, which SqlClient
            // reports as its own severe error after these two.
            WriteErrors(writer, ex);
            WriteErrors(writer, SimulatedSqlException.SessionInKillState());
            writer.WriteDone(Tds.DoneError | Tds.DoneServerError, 0);
            this.connection!.Close();
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="message"/> asks for a pooled connection's reset.
    /// The skip-transaction form asks to keep the transaction as it is —
    /// SqlClient sends it on a connection enlisted in an ambient transaction —
    /// so it resets only a session with no transaction open; with one, the
    /// session carries on unreset rather than losing the transaction.
    /// </summary>
    private bool ResetRequested(TdsMessage message) =>
        (message.FirstStatus & Tds.StatusResetConnection) != 0
        || ((message.FirstStatus & Tds.StatusResetConnectionSkipTran) != 0 && this.connection!.CurrentTransaction is null);

    private void ResetConnection()
    {
        var previous = this.connection!;
        var database = previous.Database;
        var clientHostName = previous.ClientHostName;
        var clientApplicationName = previous.ClientApplicationName;
        var loginName = previous.Security.OriginalLoginName;
        previous.Dispose();

        var fresh = new SimulatedDbConnection(simulation, previous.Spid);
        fresh.OpenSession();
        // The SPID, the physical connection and the client identity LOGIN7
        // reported outlive the reset.
        fresh.Transport = previous.Transport;
        fresh.ClientHostName = clientHostName;
        fresh.ClientApplicationName = clientApplicationName;
        fresh.OriginalDatabaseName = previous.OriginalDatabaseName;
        this.pendingInfoMessages.Clear();
        if (!string.Equals(fresh.Database, database, StringComparison.Ordinal))
            fresh.ChangeDatabase(database);

        // Re-stamp the session principal from the original login so a reset
        // connection keeps its mapped-user identity (the reset preserves the
        // login, only the session state is cleared).
        if (Simulation.TryMapLoginToDatabaseUser(simulation, fresh.CurrentDatabase, loginName, out var principal))
            fresh.Security = Simulation.BuildAuthenticatedSecurityContext(principal, loginName);

        fresh.FramesEveryStatement = true;
        fresh.ScopesTransactionsToBatch = this.multiplexer is not null;
        if (this.multiplexer is not null)
        {
            fresh.PublishedSettings = SessionSettings.Capture(fresh);
            if (previous.ExecutingRequest is { } resetting)
                fresh.AdoptRequest(resetting);
        }
        fresh.AbortTransport = this.CloseGracefully;
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

    /// <summary>
    /// A statement ending the session with nothing ahead of its Msg 596 takes
    /// the DONE before it down unsent: real holds a statement's DONE until the
    /// next token says whether more follows (probed 2026-10-06 against SQL
    /// Server 2025, where a <c>RAISERROR</c>'s own messages ahead of its 596
    /// send it).
    /// </summary>
    private static void TakeBackDoneAheadOfKill(TdsTokenWriter writer, SimulatedSqlException exception)
    {
        if (exception is { EndsSession: true, Number: 596 })
            _ = writer.TryTakeTrailingDone(out _);
    }

    private static void WriteErrors(TdsTokenWriter writer, SimulatedSqlException exception)
    {
        foreach (var error in exception.Errors)
        {
            if ((exception.EndsSession || exception.RaisedByClient) && error.Number == 0)
                continue;
            var sessionKilled = exception.EndsSession && error.Number == 596;
            // An informational entry riding with the errors (Msg 2724 after a
            // parameter's Msg 2715) goes out as the INFO token real sends.
            writer.WriteErrorOrInfo(error.Class <= 10 ? Tds.TokenInfo : Tds.TokenError, error.Number, error.State, error.Class, error.Message, ServerName, sessionKilled ? "" : error.Procedure, sessionKilled ? 0 : error.LineNumber);
        }
    }

    /// <summary>The DONE status bits an error's closing DONE carries.</summary>
    private static ushort ErrorDoneStatus(SimulatedSqlException exception) =>
        exception.EndsSession ? (ushort)(Tds.DoneError | Tds.DoneServerError) : Tds.DoneError;

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

    /// <summary>
    /// The <c>OutstandingRequestCount</c> of a request's transaction-descriptor
    /// header (ALL_HEADERS type 2): how many requests the client has
    /// outstanding on the connection, this one included, a reader counting
    /// until it has read past its response's end. 1 when the header is absent.
    /// </summary>
    internal static int ReadOutstandingRequestCount(byte[] payload)
    {
        var end = SkipAllHeaders(payload);
        var offset = 4;
        while (offset + 6 <= end)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            if (length < 6 || offset + length > end)
                break;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 4)) == 2 && length >= 18)
                return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + 14));
            offset += length;
        }
        return 1;
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
