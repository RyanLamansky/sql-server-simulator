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
    public readonly DateTime ConnectTimeUtc = DateTime.UtcNow;

    /// <summary>
    /// The <c>connection_id</c>, drawn the first time something reports it:
    /// a draw reads the operating system's random source, which every open
    /// of an in-process connection would otherwise pay for an id that only
    /// the connection DMVs and an audit record ever show. Boxed so that
    /// racing first reads publish one id whole.
    /// </summary>
    public Guid ConnectionId
    {
        get
        {
            if (Volatile.Read(ref this.connectionId) is { } drawn)
                return drawn.Value;
            _ = Interlocked.CompareExchange(ref this.connectionId, new(Guid.NewGuid()), null);
            return this.connectionId.Value;
        }
    }

    private System.Runtime.CompilerServices.StrongBox<Guid>? connectionId;
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
