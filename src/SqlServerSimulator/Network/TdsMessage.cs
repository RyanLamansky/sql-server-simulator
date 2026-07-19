namespace SqlServerSimulator.Network;

/// <summary>
/// One fully-reassembled inbound TDS message: the concatenated payloads of a
/// packet sequence ending with the end-of-message status bit.
/// </summary>
internal sealed class TdsMessage(byte packetType, byte firstStatus, byte[] payload)
{
    /// <summary>The packet-header type byte shared by the sequence.</summary>
    public readonly byte PacketType = packetType;

    /// <summary>
    /// The status byte of the first packet, which carries the
    /// reset-connection bits when SqlClient recycles a pooled connection.
    /// </summary>
    public readonly byte FirstStatus = firstStatus;

    /// <summary>The concatenated packet payloads.</summary>
    public readonly byte[] Payload = payload;
}
