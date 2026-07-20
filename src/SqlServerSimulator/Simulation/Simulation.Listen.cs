using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
        => this.ListenCoreAsync(IPAddress.Loopback, IPAddress.IPv6Loopback, port, suppliedCertificate: null, cancellationToken);

    /// <summary>
    /// Opens a loopback TCP endpoint like the port-only overload, with the
    /// port and TLS certificate drawn from <paramref name="options"/>. A
    /// supplied certificate stays owned by the caller and is never disposed
    /// by the listener, so one certificate can serve many listeners.
    /// </summary>
    /// <param name="options">The port and optional TLS certificate to present.</param>
    /// <param name="cancellationToken">
    /// Cancels listener setup; once the returned task completes, the
    /// listener's lifetime is governed solely by disposing it.
    /// </param>
    /// <returns>The active listener; dispose it to stop the endpoint.</returns>
    /// <exception cref="ArgumentException">
    /// The supplied certificate lacks a private key, or a bind address was
    /// set — that option belongs to the network listen method, because the
    /// loopback endpoint's accept-anyone-until-a-login-exists credential
    /// model must never face a network interface.
    /// </exception>
    /// <exception cref="SocketException">
    /// The port is unavailable, or binding failed for another reason.
    /// </exception>
    public Task<SimulatedNetworkListener> ListenLocalAsync(SimulatedNetworkListenerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.BindAddress is not null
            ? throw new ArgumentException("BindAddress applies to ListenNetworkAsync only — ListenLocalAsync always binds loopback, whose accept-anyone-until-CREATE-LOGIN credential model must never face a network interface.", nameof(options))
            : this.ListenCoreAsync(IPAddress.Loopback, IPAddress.IPv6Loopback, options.Port, options.ServerCertificate, cancellationToken);
    }

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
            ? throw NetworkListenerRequiresLogin()
            : this.ListenCoreAsync(IPAddress.Any, IPAddress.IPv6Any, port, suppliedCertificate: null, cancellationToken);

    /// <summary>
    /// Opens an all-interfaces TCP endpoint like the port-only overload, with
    /// the port, bind address, and TLS certificate drawn from
    /// <paramref name="options"/>. A bind address narrows the listener to
    /// exactly that interface (no best-effort second-family sibling). A
    /// supplied certificate stays owned by the caller and is never disposed
    /// by the listener, so one certificate can serve many listeners — and a
    /// CA-trusted certificate spares remote clients
    /// <c>TrustServerCertificate=true</c>.
    /// </summary>
    /// <param name="options">The port, optional bind address, and optional TLS certificate to present.</param>
    /// <param name="cancellationToken">
    /// Cancels listener setup; once the returned task completes, the
    /// listener's lifetime is governed solely by disposing it.
    /// </param>
    /// <returns>The active listener; dispose it to stop the endpoint.</returns>
    /// <exception cref="ArgumentException">
    /// The supplied certificate lacks a private key.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No logins are registered. Run
    /// <c>CREATE LOGIN name WITH PASSWORD = '…'</c> through any connection
    /// to this simulation first.
    /// </exception>
    /// <exception cref="SocketException">
    /// The port is unavailable, or binding failed for another reason.
    /// </exception>
    public Task<SimulatedNetworkListener> ListenNetworkAsync(SimulatedNetworkListenerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (this.Logins.IsEmpty)
            throw NetworkListenerRequiresLogin();

        var primaryAddress = options.BindAddress ?? IPAddress.Any;
        var secondaryAddress = options.BindAddress is null ? IPAddress.IPv6Any : null;
        return this.ListenCoreAsync(primaryAddress, secondaryAddress, options.Port, options.ServerCertificate, cancellationToken);
    }

    private static InvalidOperationException NetworkListenerRequiresLogin() => new(
        "A network-reachable endpoint requires authentication: register at least one login first, e.g. CREATE LOGIN dev WITH PASSWORD = '…'. (The loopback ListenLocalAsync endpoint accepts any credentials until a login exists.)");

    /// <summary>
    /// Binds <paramref name="primaryAddress"/> (whose family decides the
    /// socket family, reporting the bound port), then best-effort binds
    /// <paramref name="secondaryAddress"/> on the same port — the
    /// other-family sibling of the default loopback / all-interfaces pairs,
    /// null when an explicit bind address narrowed the listener to one
    /// interface.
    /// </summary>
    private async Task<SimulatedNetworkListener> ListenCoreAsync(IPAddress primaryAddress, IPAddress? secondaryAddress, int port, X509Certificate2? suppliedCertificate, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        if (suppliedCertificate is { HasPrivateKey: false })
            throw new ArgumentException("The supplied server certificate must include a private key — the TLS handshake cannot be completed with the public part alone.");

        var ownsCertificate = suppliedCertificate is null;
        var certificate = suppliedCertificate ?? await Task.Run(TdsServerCertificate.Create, cancellationToken).ConfigureAwait(false);
        Socket? primaryListener = null;
        Socket? secondaryListener = null;
        try
        {
            primaryListener = new Socket(primaryAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            primaryListener.Bind(new IPEndPoint(primaryAddress, port));
            primaryListener.Listen();
            var boundPort = ((IPEndPoint)primaryListener.LocalEndPoint!).Port;

            if (secondaryAddress is not null)
            {
                try
                {
                    secondaryListener = new Socket(secondaryAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    secondaryListener.Bind(new IPEndPoint(secondaryAddress, boundPort));
                    secondaryListener.Listen();
                }
                catch (SocketException)
                {
                    // The second family is best-effort: clients resolving a
                    // host name try both families, and the primary alone
                    // suffices when the same port isn't free on the other
                    // side.
                    secondaryListener?.Dispose();
                    secondaryListener = null;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new SimulatedNetworkListener(this, primaryListener, secondaryListener, certificate, ownsCertificate, boundPort);
        }
        catch
        {
            primaryListener?.Dispose();
            secondaryListener?.Dispose();
            if (ownsCertificate)
                certificate.Dispose();
            throw;
        }
    }
}
