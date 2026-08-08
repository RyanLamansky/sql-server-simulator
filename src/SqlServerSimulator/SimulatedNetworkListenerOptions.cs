using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace SqlServerSimulator;

/// <summary>
/// Configuration for the listen methods on <see cref="Simulation"/>, accepted
/// by the options overloads of
/// <see cref="Simulation.ListenLocalAsync(SimulatedNetworkListenerOptions, CancellationToken)"/>
/// and
/// <see cref="Simulation.ListenNetworkAsync(SimulatedNetworkListenerOptions, CancellationToken)"/>.
/// </summary>
public sealed class SimulatedNetworkListenerOptions
{
    /// <summary>
    /// The TCP port to bind, 1433 by default. Pass 0 to let the operating
    /// system assign an ephemeral port, reported by the listener's port
    /// property — the right choice for parallel test runs.
    /// </summary>
    public int Port { get; init; } = 1433;

    /// <summary>
    /// A single interface address for
    /// <see cref="Simulation.ListenNetworkAsync(SimulatedNetworkListenerOptions, CancellationToken)"/>
    /// to bind instead of all interfaces — exactly this address, with no
    /// best-effort second-family sibling. Null (the default) keeps each
    /// method's standard binding: the loopback pair for the local method,
    /// the all-interfaces pair for the network method. Only the network
    /// method honors it;
    /// <see cref="Simulation.ListenLocalAsync(SimulatedNetworkListenerOptions, CancellationToken)"/>
    /// raises <see cref="ArgumentException"/> for a non-null value, because
    /// loopback-only is its contract — its accept-anyone-until-a-login-exists
    /// credential model must never face a network interface.
    /// </summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>
    /// The TLS certificate the listener presents during the handshake, which
    /// must include a private key (a certificate without one raises
    /// <see cref="ArgumentException"/>). The caller retains ownership — the
    /// listener never disposes a supplied certificate — so one certificate
    /// can serve many listeners: created once at suite setup and, for
    /// <c>Encrypt=Strict</c> clients, exported once to a file that connection
    /// strings pin via the <c>ServerCertificate</c> keyword. Null (the
    /// default) presents a self-signed certificate generated on first use and
    /// shared by every listener in the process that supplied none — generating
    /// one costs an RSA key pair, far more than standing up the listener
    /// around it, so a suite with a listener per test would otherwise pay that
    /// price per case.
    /// </summary>
    public X509Certificate2? ServerCertificate { get; init; }
}
