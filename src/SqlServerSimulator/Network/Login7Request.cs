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

        return new Login7Request(tdsVersion, packetSize, hostName, userName, password, appName, database);
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
