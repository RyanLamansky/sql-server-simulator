using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using SqlServerSimulator.Network;

namespace SqlServerSimulator;

/// <summary>
/// A TCP endpoint speaking the SQL Server wire protocol (TDS), letting
/// unmodified SQL Server clients connect to a simulation with only a
/// connection-string change. Created by the listen method on the simulation
/// type; accepts loopback connections only. The endpoint presents an
/// ephemeral self-signed TLS certificate, so clients must connect with
/// <c>TrustServerCertificate=true</c>. Credentials are accepted without
/// validation.
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

    private readonly Socket listenerV4;
    private readonly Socket? listenerV6;
    private readonly X509Certificate2 certificate;
    private readonly CancellationTokenSource stopSource = new();
    private readonly ConcurrentDictionary<TdsSession, byte> sessions = new();
    private int disposed;

    internal SimulatedNetworkListener(Simulation simulation, Socket listenerV4, Socket? listenerV6, X509Certificate2 certificate, int port)
    {
        this.listenerV4 = listenerV4;
        this.listenerV6 = listenerV6;
        this.certificate = certificate;
        this.ServerCertificate = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        this.Port = port;

        _ = this.AcceptLoopAsync(simulation, listenerV4);
        if (listenerV6 is not null)
            _ = this.AcceptLoopAsync(simulation, listenerV6);
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
        this.listenerV4.Dispose();
        this.listenerV6?.Dispose();
        foreach (var session in this.sessions.Keys)
            session.Abort();

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
