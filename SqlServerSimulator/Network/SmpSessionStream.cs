using System.Buffers;

namespace SqlServerSimulator.Network;

/// <summary>
/// The stream seam between a per-session <see cref="TdsPacketTransport"/> and
/// the <see cref="SmpMultiplexer"/>: reads pull demuxed inbound TDS bytes from
/// the session's pipe; writes hand a whole TDS packet to the multiplexer for
/// SMP DATA framing. Owned and disposed by the session's batch loop.
/// </summary>
internal sealed class SmpSessionStream(SmpSession session) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var reader = session.InboundReader;
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffered = result.Buffer;
            if (buffered.Length > 0)
            {
                var toCopy = (int)Math.Min(buffered.Length, buffer.Length);
                buffered.Slice(0, toCopy).CopyTo(buffer.Span[..toCopy]);
                reader.AdvanceTo(buffered.GetPosition(toCopy));
                return toCopy;
            }

            if (result.IsCompleted)
                return 0;
            reader.AdvanceTo(buffered.Start, buffered.End);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        session.SendOutboundAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
