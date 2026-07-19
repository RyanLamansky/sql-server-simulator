using System.Net;
using System.Net.Sockets;
using SqlServerSimulator.Network;

namespace SqlServerSimulator;

public sealed partial class Simulation
{
    /// <summary>
    /// Opens a loopback TCP endpoint speaking the SQL Server wire protocol,
    /// so unmodified SQL Server clients on the same machine can connect to
    /// this simulation with only a connection-string change. The endpoint
    /// presents an ephemeral self-signed TLS certificate, so connection
    /// strings must include <c>TrustServerCertificate=true</c>. While no
    /// logins have been created, any credentials are accepted; once
    /// <c>CREATE LOGIN</c> has registered one or more logins (through any
    /// connection to this simulation), the endpoint requires a matching
    /// username and password and rejects mismatches with SQL Server's
    /// login-failed error 18456. A requested database that does not exist
    /// raises the same error a direct connection would.
    /// </summary>
    /// <param name="port">
    /// The TCP port to bind, 1433 by default. Pass 0 to let the operating
    /// system assign an ephemeral port, reported by the listener's port
    /// property — the right choice for parallel test runs.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels listener setup; once the returned task completes, the
    /// listener's lifetime is governed solely by disposing it.
    /// </param>
    /// <returns>The active listener; dispose it to stop the endpoint.</returns>
    /// <exception cref="SocketException">
    /// The port is unavailable, or binding failed for another reason.
    /// </exception>
    public Task<SimulatedNetworkListener> ListenLocalAsync(int port = 1433, CancellationToken cancellationToken = default)
        => this.ListenCoreAsync(IPAddress.Loopback, IPAddress.IPv6Loopback, port, cancellationToken);

    /// <summary>
    /// Opens a TCP endpoint on all network interfaces speaking the SQL
    /// Server wire protocol, so unmodified SQL Server clients on other
    /// machines can connect to this simulation. Because the endpoint is
    /// reachable beyond the local machine, at least one login must already
    /// be registered via <c>CREATE LOGIN</c> — the open-until-then
    /// credential model of the loopback listener would accept anyone.
    /// Authentication is the only enforcement: the simulator has no
    /// authorization model, so every login that connects has unrestricted
    /// access to every database, and the ephemeral self-signed TLS
    /// certificate (connection strings need
    /// <c>TrustServerCertificate=true</c>) does not authenticate the server
    /// to the client. Treat the endpoint as development tooling for trusted
    /// networks, not a hardened server.
    /// </summary>
    /// <param name="port">
    /// The TCP port to bind, 1433 by default. Pass 0 to let the operating
    /// system assign an ephemeral port, reported by the listener's port
    /// property.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels listener setup; once the returned task completes, the
    /// listener's lifetime is governed solely by disposing it.
    /// </param>
    /// <returns>The active listener; dispose it to stop the endpoint.</returns>
    /// <exception cref="InvalidOperationException">
    /// No logins are registered. Run
    /// <c>CREATE LOGIN name WITH PASSWORD = '…'</c> through any connection
    /// to this simulation first.
    /// </exception>
    /// <exception cref="SocketException">
    /// The port is unavailable, or binding failed for another reason.
    /// </exception>
    public Task<SimulatedNetworkListener> ListenNetworkAsync(int port = 1433, CancellationToken cancellationToken = default)
        => this.Logins.IsEmpty
            ? throw new InvalidOperationException(
                "A network-reachable endpoint requires authentication: register at least one login first, e.g. CREATE LOGIN dev WITH PASSWORD = '…'. (The loopback ListenLocalAsync endpoint accepts any credentials until a login exists.)")
            : this.ListenCoreAsync(IPAddress.Any, IPAddress.IPv6Any, port, cancellationToken);

    private async Task<SimulatedNetworkListener> ListenCoreAsync(IPAddress bindV4, IPAddress bindV6, int port, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        var certificate = await Task.Run(TdsServerCertificate.Create, cancellationToken).ConfigureAwait(false);
        Socket? listenerV4 = null;
        Socket? listenerV6 = null;
        try
        {
            listenerV4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenerV4.Bind(new IPEndPoint(bindV4, port));
            listenerV4.Listen();
            var boundPort = ((IPEndPoint)listenerV4.LocalEndPoint!).Port;

            try
            {
                listenerV6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                listenerV6.Bind(new IPEndPoint(bindV6, boundPort));
                listenerV6.Listen();
            }
            catch (SocketException)
            {
                // IPv6 is best-effort: clients resolving a host name try both
                // families, and IPv4 alone suffices when the same port isn't
                // free on the IPv6 side.
                listenerV6?.Dispose();
                listenerV6 = null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new SimulatedNetworkListener(this, listenerV4, listenerV6, certificate, boundPort);
        }
        catch
        {
            listenerV4?.Dispose();
            listenerV6?.Dispose();
            certificate.Dispose();
            throw;
        }
    }
}
