using System.IO.Pipelines;

namespace SqlServerSimulator.Network;

/// <summary>
/// One SMP logical session: a demultiplexed bidirectional TDS stream riding a
/// MARS connection. Inbound demuxed TDS bytes arrive through a
/// <see cref="System.IO.Pipelines.Pipe"/> that the multiplexer's read loop
/// feeds; outbound TDS packets are wrapped into SMP DATA frames by the
/// multiplexer under this session's sequence/window state. An
/// <see cref="SmpSessionStream"/> over this session is what a per-session
/// <see cref="TdsPacketTransport"/> reads from and writes to, so the TDS batch
/// loop is unaware it is multiplexed.
/// </summary>
internal sealed class SmpSession(ushort sid, SmpMultiplexer multiplexer, uint peerWindow)
{
    private readonly Pipe inbound = new();
    private readonly Lock windowGate = new();
    private uint peerWindow = peerWindow;
    private TaskCompletionSource windowSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The SMP session id (SID) this logical session carries.</summary>
    public readonly ushort Sid = sid;

    /// <summary>Writer the multiplexer feeds demuxed inbound TDS bytes into.</summary>
    public PipeWriter InboundWriter => this.inbound.Writer;

    /// <summary>Reader an <see cref="SmpSessionStream"/> pulls inbound TDS bytes from.</summary>
    public PipeReader InboundReader => this.inbound.Reader;

    /// <summary>Sequence number of the last DATA frame sent to the client.</summary>
    public uint SendSequence;

    /// <summary>Count of DATA frames received from the client.</summary>
    public uint ReceivedSequence;

    /// <summary>
    /// 1 when a client attention (cancel) has targeted this session and has not
    /// yet been acknowledged. Set by the multiplexer, consumed exactly once by
    /// the session loop via <see cref="System.Threading.Interlocked.Exchange(ref int, int)"/>
    /// so an attention racing a just-completed command is neither dropped nor
    /// double-acknowledged.
    /// </summary>
    public int AttentionState;

    /// <summary>Set while this session is actively driving the engine under the execution lock.</summary>
    public volatile bool Executing;

    /// <summary>The receive window advertised to the client (received count plus slack).</summary>
    public uint ReceiveWindow => this.ReceivedSequence + Tds.SmpWindow;

    /// <summary>Wraps one outbound TDS packet as an SMP DATA frame for this session.</summary>
    public ValueTask SendOutboundAsync(ReadOnlyMemory<byte> tdsPacket, CancellationToken cancellationToken) =>
        multiplexer.SendDataAsync(this, tdsPacket, cancellationToken);

    /// <summary>Records the client's advertised send window and wakes a blocked sender.</summary>
    public void SetPeerWindow(uint window)
    {
        lock (this.windowGate)
        {
            if (window > this.peerWindow)
                this.peerWindow = window;
            var signal = this.windowSignal;
            this.windowSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = signal.TrySetResult();
        }
    }

    /// <summary>Blocks until the client's window permits the next DATA sequence number.</summary>
    public async ValueTask WaitForSendWindowAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (this.windowGate)
            {
                if (this.SendSequence + 1 <= this.peerWindow)
                    return;
                wait = this.windowSignal.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
