using System.Net;
using System.Net.Sockets;
using SqlServerSimulator.Network;

namespace SqlServerSimulator;

public sealed partial class Simulation
{
    /// <summary>
    /// Opens a loopback TCP endpoint speaking the SQL Server wire protocol,
    /// so unmodified SQL Server clients can connect to this simulation with
    /// only a connection-string change. The endpoint presents an ephemeral
    /// self-signed TLS certificate, so connection strings must include
    /// <c>TrustServerCertificate=true</c>. While no logins have been created,
    /// any credentials are accepted; once <c>CREATE LOGIN</c> has registered
    /// one or more logins (through any connection to this simulation), the
    /// endpoint requires a matching username and password and rejects
    /// mismatches with SQL Server's login-failed error 18456. A requested
    /// database that does not exist raises the same error a direct
    /// connection would.
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
    public async Task<SimulatedNetworkListener> ListenAsync(int port = 1433, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

        var certificate = await Task.Run(TdsServerCertificate.Create, cancellationToken).ConfigureAwait(false);
        Socket? listenerV4 = null;
        Socket? listenerV6 = null;
        try
        {
            listenerV4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenerV4.Bind(new IPEndPoint(IPAddress.Loopback, port));
            listenerV4.Listen();
            var boundPort = ((IPEndPoint)listenerV4.LocalEndPoint!).Port;

            try
            {
                listenerV6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                listenerV6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, boundPort));
                listenerV6.Listen();
            }
            catch (SocketException)
            {
                // IPv6 loopback is best-effort: clients resolving
                // "localhost" try both families, and IPv4 alone suffices
                // when the same port isn't free on ::1.
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
