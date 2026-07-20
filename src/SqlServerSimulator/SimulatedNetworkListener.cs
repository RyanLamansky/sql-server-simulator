using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using SqlServerSimulator.Network;

namespace SqlServerSimulator;

/// <summary>
/// A TCP endpoint speaking the SQL Server wire protocol (TDS), letting
/// unmodified SQL Server clients connect to a simulation with only a
/// connection-string change. Created by the listen methods on the simulation
/// type; accepts loopback connections only. The endpoint presents the TLS
/// certificate supplied through the listen options, or an ephemeral
/// self-signed one when none was supplied — the latter requires clients to
/// connect with <c>TrustServerCertificate=true</c>. Credentials are accepted
/// without validation.
/// </summary>
/// <remarks>
/// Disposal is immediate and waits for nothing: the listening sockets close,
/// and every active session's connection is torn down with normal session
/// semantics (open transactions roll back, temporary tables drop). Clients
/// mid-query observe an abrupt connection reset.
/// </remarks>
public sealed class SimulatedNetworkListener : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The TCP port the listener is bound to. When the listen call was made
    /// with port 0, this reports the operating-system-assigned port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The certificate the listener presents during the TLS handshake,
    /// public part only. Clients using <c>Encrypt=Strict</c> (TDS 8.0)
    /// cannot rely on <c>TrustServerCertificate</c> — SqlClient ignores it
    /// in strict mode and always validates the certificate — so they pin
    /// instead: export this certificate to a file and reference it with the
    /// connection string's <c>ServerCertificate</c> keyword. Disposed with
    /// the listener.
    /// </summary>
    public X509Certificate2 ServerCertificate { get; }

    private readonly Socket primaryListener;
    private readonly Socket? secondaryListener;
    private readonly X509Certificate2 certificate;

    /// <summary>
    /// True when the listener generated <see cref="certificate"/> itself and
    /// must dispose it; false when the certificate was supplied through the
    /// listen options, whose caller retains ownership.
    /// </summary>
    private readonly bool ownsCertificate;

    private readonly CancellationTokenSource stopSource = new();
    private readonly ConcurrentDictionary<TdsSession, byte> sessions = new();
    private int disposed;

    internal SimulatedNetworkListener(Simulation simulation, Socket primaryListener, Socket? secondaryListener, X509Certificate2 certificate, bool ownsCertificate, int port)
    {
        this.primaryListener = primaryListener;
        this.secondaryListener = secondaryListener;
        this.certificate = certificate;
        this.ownsCertificate = ownsCertificate;
        this.ServerCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        this.Port = port;

        _ = this.AcceptLoopAsync(simulation, primaryListener);
        if (secondaryListener is not null)
            _ = this.AcceptLoopAsync(simulation, secondaryListener);
    }

    /// <summary>
    /// Stops accepting connections and immediately tears down all active
    /// sessions.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.stopSource.Cancel();
        this.primaryListener.Dispose();
        this.secondaryListener?.Dispose();
        foreach (var session in this.sessions.Keys)
            session.Abort();

        if (this.ownsCertificate)
            this.certificate.Dispose();
        this.ServerCertificate.Dispose();
        this.stopSource.Dispose();
    }

    /// <summary>
    /// Equivalent to the synchronous dispose; teardown is aggressive and
    /// waits for nothing.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        this.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task AcceptLoopAsync(Simulation simulation, Socket listener)
    {
        while (true)
        {
            Socket accepted;
            try
            {
                accepted = await listener.AcceptAsync(this.stopSource.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            accepted.NoDelay = true;
            var session = new TdsSession(simulation, accepted, this.certificate);
            _ = this.sessions.TryAdd(session, 0);
            _ = this.RunSessionAsync(session);
        }
    }

    private async Task RunSessionAsync(TdsSession session)
    {
        try
        {
            await session.RunAsync(this.stopSource.Token).ConfigureAwait(false);
        }
        finally
        {
            _ = this.sessions.TryRemove(session, out _);
            session.Dispose();
        }
    }
}
