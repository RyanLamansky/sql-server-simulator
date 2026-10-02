using System.Buffers.Binary;
using System.Text;

namespace SqlServerSimulator.Network;

/// <summary>
/// The fields of a client LOGIN7 message the listener acts on. The session
/// validates <see cref="UserName"/> / <see cref="Password"/> against
/// <see cref="Simulation.Logins"/> when that registry is non-empty (Msg 18456
/// on mismatch); an empty registry accepts any credentials.
/// </summary>
internal sealed class Login7Request
{
    public readonly uint TdsVersion;
    public readonly int PacketSize;
    public readonly string HostName;
    public readonly string UserName;
    public readonly string Password;
    public readonly string AppName;
    public readonly string Database;

    /// <summary>
    /// The JSON support version the client's feature extension asked for
    /// (<c>FEATUREEXT_JSONSUPPORT</c>, 0x0D), 0 when it asked for none.
    /// </summary>
    public byte JsonSupportVersion;

    /// <summary>
    /// The vector support version the client's feature extension asked for
    /// (<c>FEATUREEXT_VECTORSUPPORT</c>, 0x0E), 0 when it asked for none.
    /// </summary>
    public byte VectorSupportVersion;

    private Login7Request(uint tdsVersion, int packetSize, string hostName, string userName, string password, string appName, string database)
    {
        this.TdsVersion = tdsVersion;
        this.PacketSize = packetSize;
        this.HostName = hostName;
        this.UserName = userName;
        this.Password = password;
        this.AppName = appName;
        this.Database = database;
    }

    /// <summary>
    /// Parses a LOGIN7 payload. Offsets in the variable section are relative
    /// to the start of the structure, which is the start of the payload.
    /// </summary>
    public static Login7Request Parse(ReadOnlySpan<byte> payload)
    {
        // Fixed part: Length(4) TDSVersion(4) PacketSize(4) ClientProgVer(4)
        // ClientPID(4) ConnectionID(4) OptionFlags1/2, TypeFlags,
        // OptionFlags3 (4×1) ClientTimeZone(4) ClientLCID(4) = 36 bytes,
        // followed by the offset/length pairs.
        if (payload.Length < 36 + (4 * 9))
            throw new InvalidDataException($"LOGIN7 payload is {payload.Length} bytes, shorter than the fixed portion.");

        var tdsVersion = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var packetSize = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);

        var hostName = ReadField(payload, 36);
        var userName = ReadField(payload, 40);
        var password = ReadPasswordField(payload, 44);
        var appName = ReadField(payload, 48);
        var database = ReadField(payload, 68);

        var request = new Login7Request(tdsVersion, packetSize, hostName, userName, password, appName, database);
        ReadFeatureExtension(payload, request);
        return request;
    }

    /// <summary>
    /// Reads the feature extension block a client flags with OptionFlags3's
    /// fExtension bit: the offset pair at 56 locates a DWORD holding the
    /// block's own offset, and the block is a run of feature id, DWORD
    /// length and data, ended by 0xFF (MS-TDS 2.2.6.4). Only the JSON and
    /// vector support versions are kept; every other feature goes
    /// unacknowledged.
    /// </summary>
    private static void ReadFeatureExtension(ReadOnlySpan<byte> payload, Login7Request request)
    {
        if ((payload[27] & 0x10) == 0)
            return;
        var pointerOffset = BinaryPrimitives.ReadUInt16LittleEndian(payload[56..]);
        if (pointerOffset + 4 > payload.Length)
            throw new InvalidDataException("LOGIN7 feature extension pointer extends past the end of the payload.");
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[pointerOffset..]);
        while (offset < payload.Length && payload[offset] != 0xFF)
        {
            if (offset + 5 > payload.Length)
                throw new InvalidDataException("LOGIN7 feature extension entry extends past the end of the payload.");
            var featureId = payload[offset];
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 1)..]);
            var data = payload.Slice(offset + 5, length);
            switch (featureId)
            {
                case 0x0D when length >= 1:
                    request.JsonSupportVersion = data[0];
                    break;
                case 0x0E when length >= 1:
                    request.VectorSupportVersion = data[0];
                    break;
            }
            offset += 5 + length;
        }
    }

    /// <summary>
    /// Reads the password field, undoing the client-side obfuscation MS-TDS
    /// mandates: each byte was nibble-swapped and then XORed with 0xA5, so
    /// decoding XORs first and swaps back before the UCS-2 decode.
    /// </summary>
    private static string ReadPasswordField(ReadOnlySpan<byte> payload, int pairOffset)
    {
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(payload[pairOffset..]);
        var chars = BinaryPrimitives.ReadUInt16LittleEndian(payload[(pairOffset + 2)..]);
        if (chars == 0)
            return "";

        var byteCount = chars * 2;
        if (offset + byteCount > payload.Length)
            throw new InvalidDataException("LOGIN7 variable-section field extends past the end of the payload.");
        var clear = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        payload.Slice(offset, byteCount).CopyTo(clear);
        for (var i = 0; i < clear.Length; i++)
        {
            var b = (byte)(clear[i] ^ 0xA5);
            clear[i] = (byte)((b >> 4) | (b << 4));
        }
        return Encoding.Unicode.GetString(clear);
    }

    private static string ReadField(ReadOnlySpan<byte> payload, int pairOffset)
    {
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(payload[pairOffset..]);
        var chars = BinaryPrimitives.ReadUInt16LittleEndian(payload[(pairOffset + 2)..]);
        if (chars == 0)
            return "";

        var byteCount = chars * 2;
        return offset + byteCount > payload.Length
            ? throw new InvalidDataException("LOGIN7 variable-section field extends past the end of the payload.")
            : Encoding.Unicode.GetString(payload.Slice(offset, byteCount));
    }
}
