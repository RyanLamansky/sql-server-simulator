using System.Net;

namespace SqlServerSimulator.Network;

/// <summary>
/// The physical connection behind a session, as <c>sys.dm_exec_connections</c>
/// reports it: the two TCP endpoints and the TDS version a TDS-endpoint
/// client's LOGIN7 named, and the packet traffic every transport carrying the
/// session — the physical one and each MARS logical session — counts into.
/// An in-process connection has one with no endpoints, standing in for real's
/// shared-memory transport. <c>sp_reset_connection</c> hands it to the fresh
/// session, since the physical connection survives the reset.
/// </summary>
internal sealed class ConnectionTransport(IPEndPoint? client, IPEndPoint? local, uint protocolVersion, int packetSize)
{
    public readonly IPEndPoint? Client = client;
    public readonly IPEndPoint? Local = local;
    public readonly uint ProtocolVersion = protocolVersion;
    public readonly int PacketSize = packetSize;
    public readonly Guid ConnectionId = Guid.NewGuid();
    public readonly DateTime ConnectTimeUtc = DateTime.UtcNow;
    public int PacketsRead;
    public int PacketsWritten;
    public long LastReadTicks;
    public long LastWriteTicks;

    /// <summary>Counts one inbound packet.</summary>
    public void CountRead()
    {
        _ = Interlocked.Increment(ref this.PacketsRead);
        _ = Interlocked.Exchange(ref this.LastReadTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Counts one outbound packet.</summary>
    public void CountWrite()
    {
        _ = Interlocked.Increment(ref this.PacketsWritten);
        _ = Interlocked.Exchange(ref this.LastWriteTicks, DateTime.UtcNow.Ticks);
    }
}
