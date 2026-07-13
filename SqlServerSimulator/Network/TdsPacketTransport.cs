using System.Buffers;

namespace SqlServerSimulator.Network;

/// <summary>
/// Reads and writes TDS packets over a stream, reassembling inbound packet
/// sequences into messages and splitting outbound payloads at the negotiated
/// packet size. The underlying stream is swapped after the TLS handshake so
/// the same transport carries both the plaintext prelogin exchange and the
/// encrypted remainder of the session.
/// </summary>
internal sealed class TdsPacketTransport(Stream stream)
{
    private Stream stream = stream;

    /// <summary>Negotiated packet size in bytes, including headers.</summary>
    public int PacketSize = Tds.DefaultPacketSize;

    /// <summary>Session SPID stamped into outbound packet headers.</summary>
    public ushort Spid;

    private byte nextPacketId = 1;

    /// <summary>
    /// Replaces the underlying stream; used to switch to the
    /// <see cref="System.Net.Security.SslStream"/> after the TLS handshake.
    /// </summary>
    public void SwitchStream(Stream newStream) => this.stream = newStream;

    /// <summary>
    /// Reads packets until the end-of-message bit, returning the assembled
    /// message, or null when the client disconnected cleanly between messages.
    /// </summary>
    public async ValueTask<TdsMessage?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var header = new byte[Tds.HeaderSize];
        byte packetType = 0;
        byte firstStatus = 0;
        byte[]? firstPayload = null;
        List<byte[]>? continuations = null;
        var first = true;

        while (true)
        {
            var read = await this.stream.ReadAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return first
                    ? null
                    : throw new EndOfStreamException("The client disconnected in the middle of a TDS message.");
            }

            if (read < Tds.HeaderSize)
                await this.stream.ReadExactlyAsync(header.AsMemory(read, Tds.HeaderSize - read), cancellationToken).ConfigureAwait(false);

            var length = (header[2] << 8) | header[3];
            if (length < Tds.HeaderSize)
                throw new InvalidDataException($"TDS packet header declares length {length}, below the {Tds.HeaderSize}-byte minimum.");

            var chunk = new byte[length - Tds.HeaderSize];
            await this.stream.ReadExactlyAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (first)
            {
                packetType = header[0];
                firstStatus = header[1];
                firstPayload = chunk;
                first = false;
            }
            else
            {
                continuations ??= [];
                continuations.Add(chunk);
            }

            if ((header[1] & Tds.StatusEndOfMessage) != 0)
                return new TdsMessage(packetType, firstStatus, Combine(firstPayload!, continuations));
        }
    }

    /// <summary>
    /// Sends one packet: the payload prefixed by an 8-byte header. The caller
    /// is responsible for keeping the payload within the negotiated size.
    /// </summary>
    public async ValueTask WritePacketAsync(byte packetType, ReadOnlyMemory<byte> payload, bool endOfMessage, CancellationToken cancellationToken)
    {
        var total = Tds.HeaderSize + payload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            rented[0] = packetType;
            rented[1] = endOfMessage ? Tds.StatusEndOfMessage : (byte)0;
            rented[2] = (byte)(total >> 8);
            rented[3] = (byte)total;
            rented[4] = (byte)(this.Spid >> 8);
            rented[5] = (byte)this.Spid;
            rented[6] = this.nextPacketId++;
            rented[7] = 0;
            payload.Span.CopyTo(rented.AsSpan(Tds.HeaderSize));
            await this.stream.WriteAsync(rented.AsMemory(0, total), cancellationToken).ConfigureAwait(false);
            await this.stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static byte[] Combine(byte[] firstPayload, List<byte[]>? continuations)
    {
        if (continuations is null)
            return firstPayload;

        var total = firstPayload.Length;
        foreach (var chunk in continuations)
            total += chunk.Length;

        var combined = new byte[total];
        firstPayload.CopyTo(combined, 0);
        var offset = firstPayload.Length;
        foreach (var chunk in continuations)
        {
            chunk.CopyTo(combined, offset);
            offset += chunk.Length;
        }

        return combined;
    }
}
