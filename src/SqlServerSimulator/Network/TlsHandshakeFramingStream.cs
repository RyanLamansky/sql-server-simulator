namespace SqlServerSimulator.Network;

/// <summary>
/// The TDS 7.x TLS seam: during the handshake, TLS records travel wrapped
/// inside TDS packets of type PRELOGIN, so this stream strips packet headers
/// on read and adds them on write while an <see
/// cref="System.Net.Security.SslStream"/> performs the handshake on top of
/// it. Once the handshake completes the wrapping stops (TDS packets then flow
/// entirely inside TLS), so <see cref="EnablePassthrough"/> flips the stream
/// to a transparent proxy of the socket.
/// </summary>
internal sealed class TlsHandshakeFramingStream(Stream inner) : Stream
{
    private readonly Stream inner = inner;
    private bool passthrough;

    /// <summary>Unserved payload bytes of the current inbound wrapped packet.</summary>
    private int pendingPayload;

    /// <summary>
    /// Reusable outbound packet header. Safe as an instance field because the
    /// TLS handshake serializes its writes, and passthrough mode never touches
    /// it.
    /// </summary>
    private readonly byte[] writeHeader = [Tds.PacketPrelogin, 0, 0, 0, 0, 0, 1, 0];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            this.inner.Dispose();

        base.Dispose(disposing);
    }

    /// <summary>Stops wrapping; all further I/O passes straight through.</summary>
    public void EnablePassthrough() => this.passthrough = true;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => this.inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => this.inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    // SslStream drives the handshake through the asynchronous members; the
    // synchronous pair Stream requires runs them to completion.
    public override int Read(byte[] buffer, int offset, int count) =>
        this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.passthrough)
            return await this.inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        while (this.pendingPayload == 0)
        {
            var header = new byte[Tds.HeaderSize];
            var read = await this.inner.ReadAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return 0;

            if (read < Tds.HeaderSize)
                await this.inner.ReadExactlyAsync(header.AsMemory(read, Tds.HeaderSize - read), cancellationToken).ConfigureAwait(false);

            this.pendingPayload = ((header[2] << 8) | header[3]) - Tds.HeaderSize;
        }

        var take = Math.Min(buffer.Length, this.pendingPayload);
        await this.inner.ReadExactlyAsync(buffer[..take], cancellationToken).ConfigureAwait(false);
        this.pendingPayload -= take;
        return take;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.passthrough)
        {
            await this.inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        while (buffer.Length > 0)
        {
            var chunk = Math.Min(buffer.Length, Tds.DefaultPacketSize - Tds.HeaderSize);
            this.FillHeader(chunk, endOfMessage: chunk == buffer.Length);
            await this.inner.WriteAsync(this.writeHeader, cancellationToken).ConfigureAwait(false);
            await this.inner.WriteAsync(buffer[..chunk], cancellationToken).ConfigureAwait(false);
            buffer = buffer[chunk..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void FillHeader(int payloadLength, bool endOfMessage)
    {
        var total = Tds.HeaderSize + payloadLength;
        this.writeHeader[1] = endOfMessage ? Tds.StatusEndOfMessage : (byte)0;
        this.writeHeader[2] = (byte)(total >> 8);
        this.writeHeader[3] = (byte)total;
    }
}
