using System.Buffers.Binary;
using System.Data;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>One RPC invocation parsed from an RPC request message (MS-TDS §2.2.6.6).</summary>
internal sealed class TdsRpcRequest
{
    private static readonly DateTime Epoch1900 = new(1900, 1, 1);

    /// <summary>The stored-procedure name; empty when the numeric ProcID form is used.</summary>
    public readonly string ProcName;

    /// <summary>The numeric well-known procedure id; zero when the name form is used.</summary>
    public readonly ushort ProcId;

    /// <summary>The RPC parameters in declaration order.</summary>
    public readonly List<TdsRpcParameter> Parameters;

    private TdsRpcRequest(string procName, ushort procId, List<TdsRpcParameter> parameters)
    {
        this.ProcName = procName;
        this.ProcId = procId;
        this.Parameters = parameters;
    }

    /// <summary>
    /// Parses a full RPC message payload: skips ALL_HEADERS (a leading uint32 LE
    /// total length covering the header block), then one or more RPC requests
    /// separated by the 0xFF batch-flag byte (TDS 7.2+).
    /// </summary>
    public static List<TdsRpcRequest> ParseMessage(byte[] payload)
    {
        var reader = new Reader(payload);

        var headerLength = reader.ReadUInt32();
        if (headerLength < 4 || headerLength > (uint)payload.Length)
            throw new InvalidDataException($"RPC ALL_HEADERS length {headerLength} is outside the {payload.Length}-byte payload.");

        reader.Position = (int)headerLength;

        var requests = new List<TdsRpcRequest>();
        while (!reader.AtEnd)
            requests.Add(ParseRequest(reader));

        return requests;
    }

    private static TdsRpcRequest ParseRequest(Reader reader)
    {
        var procName = "";
        ushort procId = 0;
        var nameLenProcId = reader.ReadUInt16();
        if (nameLenProcId == 0xFFFF)
            procId = reader.ReadUInt16();
        else
            procName = reader.ReadUcs2(nameLenProcId);

        // OptionFlags (fWithRecomp / fNoMetaData / fReuseMetaData); no behavior here.
        _ = reader.ReadUInt16();

        var parameters = new List<TdsRpcParameter>();
        while (!reader.AtEnd)
        {
            // A 0xFF at parameter-start position is the batch flag opening the
            // next RPC request; a real B_VARCHAR name length never reaches it.
            if (reader.PeekByte() == 0xFF)
            {
                _ = reader.ReadByte();
                break;
            }

            parameters.Add(ParseParameter(reader));
        }

        return new TdsRpcRequest(procName, procId, parameters);
    }

    private static TdsRpcParameter ParseParameter(Reader reader)
    {
        var name = reader.ReadUcs2(reader.ReadByte());
        var isOutput = (reader.ReadByte() & 0x01) != 0;
        var token = reader.ReadByte();
        return token switch
        {
            0x22 => throw Unsupported("image"),
            0x23 => DecodeLegacyLob(reader, name, isOutput, ansi: true),
            0x24 => DecodeGuid(reader, name, isOutput),
            0x26 => DecodeIntN(reader, name, isOutput),
            0x28 => DecodeDate(reader, name, isOutput),
            0x29 => DecodeTime(reader, name, isOutput),
            0x2A => DecodeDateTime2(reader, name, isOutput),
            0x2B => DecodeDateTimeOffset(reader, name, isOutput),
            0x62 => throw Unsupported("sql_variant"),
            0x63 => DecodeLegacyLob(reader, name, isOutput, ansi: false),
            0x68 => DecodeBit(reader, name, isOutput),
            0x6A => DecodeDecimal(reader, name, isOutput),
            0x6C => DecodeDecimal(reader, name, isOutput),
            0x6D => DecodeFloatN(reader, name, isOutput),
            0x6E => DecodeMoneyN(reader, name, isOutput),
            0x6F => DecodeDateTimeN(reader, name, isOutput),
            0xA5 => DecodeBinary(reader, name, isOutput),
            0xA7 => DecodeAnsiString(reader, name, isOutput, DbType.AnsiString),
            0xAD => DecodeBinary(reader, name, isOutput),
            0xAF => DecodeAnsiString(reader, name, isOutput, DbType.AnsiStringFixedLength),
            0xE7 => DecodeNationalString(reader, name, isOutput, DbType.String),
            0xEF => DecodeNationalString(reader, name, isOutput, DbType.StringFixedLength),
            0xF0 => throw Unsupported("CLR UDT"),
            0xF1 => DecodeXml(reader, name, isOutput),
            0xF3 => throw Unsupported("table-valued"),
            _ => throw new NotSupportedException($"Unrecognized TDS RPC parameter type token 0x{token:X2}."),
        };
    }

    private static NotSupportedException Unsupported(string feature) =>
        new($"The network listener does not accept {feature} RPC parameters.");

    private static TdsRpcParameter DecodeIntN(Reader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var dbType = declaredLength switch
        {
            1 => DbType.Byte,
            2 => DbType.Int16,
            4 => DbType.Int32,
            8 => DbType.Int64,
            _ => throw new InvalidDataException($"INTN declared length {declaredLength} is not 1, 2, 4, or 8."),
        };

        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, dbType, null);

        var raw = AssembleLittleEndian(reader.ReadBytes(length));
        object value = declaredLength switch
        {
            1 => (byte)raw,
            2 => (short)raw,
            4 => (int)raw,
            _ => raw,
        };

        return new TdsRpcParameter(name, isOutput, dbType, value);
    }

    private static TdsRpcParameter DecodeBit(Reader reader, string name, bool isOutput)
    {
        _ = reader.ReadByte();
        var length = reader.ReadByte();
        return length == 0
            ? new TdsRpcParameter(name, isOutput, DbType.Boolean, null)
            : new TdsRpcParameter(name, isOutput, DbType.Boolean, reader.ReadByte() != 0);
    }

    private static TdsRpcParameter DecodeFloatN(Reader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var dbType = declaredLength == 4 ? DbType.Single : DbType.Double;
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, dbType, null);

        var payload = reader.ReadBytes(length);
        object value = length == 4
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload))
            : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload));

        return new TdsRpcParameter(name, isOutput, dbType, value);
    }

    private static TdsRpcParameter DecodeMoneyN(Reader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Currency, null);

        var payload = reader.ReadBytes(length);
        long scaled;
        if (declaredLength == 4)
        {
            scaled = BinaryPrimitives.ReadInt32LittleEndian(payload);
        }
        else
        {
            var high = BinaryPrimitives.ReadInt32LittleEndian(payload);
            var low = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            scaled = ((long)high << 32) | low;
        }

        return new TdsRpcParameter(name, isOutput, DbType.Currency, scaled / 10000m);
    }

    private static TdsRpcParameter DecodeDecimal(Reader reader, string name, bool isOutput)
    {
        _ = reader.ReadByte();
        var precision = reader.ReadByte();
        var scale = reader.ReadByte();

        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Decimal, null, precision: precision, scale: scale);

        var payload = reader.ReadBytes(length);
        var isNegative = payload[0] != 1;
        var magnitude = payload[1..];
        for (var i = 12; i < magnitude.Length; i++)
        {
            if (magnitude[i] != 0)
                throw new NotSupportedException("A decimal RPC parameter exceeds the range of System.Decimal.");
        }

        Span<byte> assembled = stackalloc byte[12];
        assembled.Clear();
        magnitude[..Math.Min(magnitude.Length, 12)].CopyTo(assembled);
        var lo = BinaryPrimitives.ReadInt32LittleEndian(assembled);
        var mid = BinaryPrimitives.ReadInt32LittleEndian(assembled[4..]);
        var hi = BinaryPrimitives.ReadInt32LittleEndian(assembled[8..]);
        var value = new decimal(lo, mid, hi, isNegative, scale);

        return new TdsRpcParameter(name, isOutput, DbType.Decimal, value, precision: precision, scale: scale);
    }

    private static TdsRpcParameter DecodeGuid(Reader reader, string name, bool isOutput)
    {
        _ = reader.ReadByte();
        var length = reader.ReadByte();
        return length == 0
            ? new TdsRpcParameter(name, isOutput, DbType.Guid, null)
            : new TdsRpcParameter(name, isOutput, DbType.Guid, new Guid(reader.ReadBytes(16)));
    }

    private static TdsRpcParameter DecodeDate(Reader reader, string name, bool isOutput)
    {
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Date, null);

        var days = ReadThreeByteInt(reader.ReadBytes(3));
        var value = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue);
        return new TdsRpcParameter(name, isOutput, DbType.Date, value);
    }

    private static TdsRpcParameter DecodeTime(Reader reader, string name, bool isOutput)
    {
        var scale = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.Time, null, scale: scale);

        var timeBytes = TimeValueBytes(scale);
        var ticks = ScaledUnitsToTicks(AssembleLittleEndian(reader.ReadBytes(timeBytes)), scale);
        return new TdsRpcParameter(name, isOutput, DbType.Time, TimeSpan.FromTicks(ticks), scale: scale);
    }

    private static TdsRpcParameter DecodeDateTime2(Reader reader, string name, bool isOutput)
    {
        var scale = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.DateTime2, null, scale: scale);

        var timeBytes = TimeValueBytes(scale);
        var payload = reader.ReadBytes(timeBytes + 3);
        var ticks = ScaledUnitsToTicks(AssembleLittleEndian(payload[..timeBytes]), scale);
        var days = ReadThreeByteInt(payload[timeBytes..]);
        var value = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).AddTicks(ticks);
        return new TdsRpcParameter(name, isOutput, DbType.DateTime2, value, scale: scale);
    }

    private static TdsRpcParameter DecodeDateTimeOffset(Reader reader, string name, bool isOutput)
    {
        var scale = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.DateTimeOffset, null, scale: scale);

        var timeBytes = TimeValueBytes(scale);
        var payload = reader.ReadBytes(timeBytes + 5);
        var ticks = ScaledUnitsToTicks(AssembleLittleEndian(payload[..timeBytes]), scale);
        var days = ReadThreeByteInt(payload[timeBytes..(timeBytes + 3)]);
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(payload[(timeBytes + 3)..]);
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var utcTicks = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).Ticks + ticks;
        var value = new DateTimeOffset(utcTicks + offset.Ticks, offset);
        return new TdsRpcParameter(name, isOutput, DbType.DateTimeOffset, value, scale: scale);
    }

    private static TdsRpcParameter DecodeDateTimeN(Reader reader, string name, bool isOutput)
    {
        var declaredLength = reader.ReadByte();
        var length = reader.ReadByte();
        if (length == 0)
            return new TdsRpcParameter(name, isOutput, DbType.DateTime, null);

        var payload = reader.ReadBytes(length);
        DateTime value;
        if (declaredLength == 4)
        {
            var days = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            var minutes = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
            value = Epoch1900.AddDays(days).AddMinutes(minutes);
        }
        else
        {
            var days = BinaryPrimitives.ReadInt32LittleEndian(payload);
            var thirds = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            var ticks = (((long)thirds * 10_000_000) + 150) / 300;
            value = Epoch1900.AddDays(days).AddTicks(ticks);
        }

        return new TdsRpcParameter(name, isOutput, DbType.DateTime, value);
    }

    private static TdsRpcParameter DecodeAnsiString(Reader reader, string name, bool isOutput, DbType dbType)
    {
        var maxLength = reader.ReadUInt16();
        var utf8 = ReadCollation(reader);
        var encoding = utf8 ? Encoding.UTF8 : CharSqlType.Cp1252Encoder;

        if (maxLength == 0xFFFF)
        {
            var bytes = ReadPlp(reader);
            var value = bytes is null ? null : encoding.GetString(bytes);
            return new TdsRpcParameter(name, isOutput, dbType, value, size: -1);
        }

        var length = reader.ReadUInt16();
        return length == 0xFFFF
            ? new TdsRpcParameter(name, isOutput, dbType, null, size: maxLength)
            : new TdsRpcParameter(name, isOutput, dbType, encoding.GetString(reader.ReadBytes(length)), size: maxLength);
    }

    private static TdsRpcParameter DecodeNationalString(Reader reader, string name, bool isOutput, DbType dbType)
    {
        var maxLength = reader.ReadUInt16();
        _ = ReadCollation(reader);

        if (maxLength == 0xFFFF)
        {
            var bytes = ReadPlp(reader);
            var value = bytes is null ? null : Encoding.Unicode.GetString(bytes);
            return new TdsRpcParameter(name, isOutput, dbType, value, size: -1);
        }

        var length = reader.ReadUInt16();
        return length == 0xFFFF
            ? new TdsRpcParameter(name, isOutput, dbType, null, size: maxLength / 2)
            : new TdsRpcParameter(name, isOutput, dbType, Encoding.Unicode.GetString(reader.ReadBytes(length)), size: maxLength / 2);
    }

    private static TdsRpcParameter DecodeBinary(Reader reader, string name, bool isOutput)
    {
        var maxLength = reader.ReadUInt16();
        if (maxLength == 0xFFFF)
        {
            var bytes = ReadPlp(reader);
            return new TdsRpcParameter(name, isOutput, DbType.Binary, bytes, size: -1);
        }

        var length = reader.ReadUInt16();
        return length == 0xFFFF
            ? new TdsRpcParameter(name, isOutput, DbType.Binary, null, size: maxLength)
            : new TdsRpcParameter(name, isOutput, DbType.Binary, reader.ReadBytes(length).ToArray(), size: maxLength);
    }

    private static TdsRpcParameter DecodeXml(Reader reader, string name, bool isOutput)
    {
        if (reader.ReadByte() == 1)
        {
            // Schema declaration: database, owning schema, and collection name.
            _ = reader.ReadUcs2(reader.ReadByte());
            _ = reader.ReadUcs2(reader.ReadByte());
            _ = reader.ReadUcs2(reader.ReadUInt16());
        }

        var bytes = ReadPlp(reader);
        var value = bytes is null ? null : Encoding.Unicode.GetString(bytes);
        // SqlClient prefixes xml parameter content with a UTF-16 BOM; the
        // server treats it as an encoding signal, not document content.
        if (value is ['\uFEFF', ..])
            value = value[1..];

        return new TdsRpcParameter(name, isOutput, DbType.Xml, value);
    }

    /// <summary>Reads the 5-byte collation structure, returning whether its fUTF8 bit is set.</summary>
    /// <summary>
    /// Decodes a legacy large-object string RPC parameter — <c>text</c> (0x23,
    /// CP1252) or <c>ntext</c> (0x63, UTF-16). SqlClient sends the
    /// <c>sp_executesql</c> statement / declaration parameters as <c>ntext</c>
    /// once they exceed nvarchar(4000) (the proc's declared parameter type),
    /// so large parameterized queries — SMO's Object-Explorer database
    /// enumeration among them — arrive this way. TYPE_INFO is a 4-byte LONGLEN
    /// max size + the 5-byte collation; the value is PLP (uint64 total length,
    /// all-ones = NULL, then length-prefixed chunks) exactly like the MAX
    /// string types.
    /// </summary>
    private static TdsRpcParameter DecodeLegacyLob(Reader reader, string name, bool isOutput, bool ansi)
    {
        _ = reader.ReadUInt32();
        var utf8 = ReadCollation(reader);
        var encoding = ansi ? (utf8 ? Encoding.UTF8 : CharSqlType.Cp1252Encoder) : Encoding.Unicode;
        var dbType = ansi ? DbType.AnsiString : DbType.String;
        var dataLength = reader.ReadUInt32();
        return dataLength == 0xFFFFFFFF
            ? new TdsRpcParameter(name, isOutput, dbType, null, size: -1)
            : new TdsRpcParameter(name, isOutput, dbType, encoding.GetString(reader.ReadBytes(checked((int)dataLength))), size: -1);
    }

    private static bool ReadCollation(Reader reader)
    {
        var info = reader.ReadUInt32();
        _ = reader.ReadByte();
        return (info & (1u << 26)) != 0;
    }

    /// <summary>
    /// Reads a PLP value: a uint64 total length (all-ones = NULL, all-ones-minus-one
    /// = unknown length) followed by length-prefixed chunks terminated by a
    /// zero-length chunk. Chunks are accumulated regardless of the declared total.
    /// </summary>
    private static byte[]? ReadPlp(Reader reader)
    {
        var total = reader.ReadUInt64();
        if (total == 0xFFFFFFFFFFFFFFFF)
            return null;

        using var accumulated = new MemoryStream();
        while (true)
        {
            var chunkLength = reader.ReadUInt32();
            if (chunkLength == 0)
                break;

            accumulated.Write(reader.ReadBytes((int)chunkLength));
        }

        return accumulated.ToArray();
    }

    private static byte TimeValueBytes(byte scale) =>
        scale > 7
            ? throw new InvalidDataException($"Time scale {scale} exceeds the maximum of 7.")
            : scale <= 2 ? (byte)3 : scale <= 4 ? (byte)4 : (byte)5;

    private static long ScaledUnitsToTicks(long units, byte scale)
    {
        var factor = 1L;
        for (var i = scale; i < 7; i++)
            factor *= 10;

        return units * factor;
    }

    private static long AssembleLittleEndian(ReadOnlySpan<byte> bytes)
    {
        var value = 0L;
        for (var i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);

        return value;
    }

    private static int ReadThreeByteInt(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

    /// <summary>A forward-only, bounds-checked cursor over an RPC message payload.</summary>
    private sealed class Reader(byte[] data)
    {
        private readonly byte[] data = data;

        /// <summary>The read offset; callers advance it by skipping the header block.</summary>
        public int Position;

        public bool AtEnd => this.Position >= this.data.Length;

        public byte PeekByte() =>
            this.Position < this.data.Length ? this.data[this.Position] : throw Truncated();

        public byte ReadByte() =>
            this.Position < this.data.Length ? this.data[this.Position++] : throw Truncated();

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || (long)this.Position + count > this.data.Length)
                throw Truncated();

            var span = this.data.AsSpan(this.Position, count);
            this.Position += count;
            return span;
        }

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(this.ReadBytes(2));

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(this.ReadBytes(4));

        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(this.ReadBytes(8));

        /// <summary>Reads <paramref name="charCount"/> UCS-2 characters as a string.</summary>
        public string ReadUcs2(int charCount) =>
            charCount == 0 ? "" : Encoding.Unicode.GetString(this.ReadBytes(charCount * 2));

        private static InvalidDataException Truncated() =>
            new("The RPC request ends before a value was fully read.");
    }
}

/// <summary>One RPC parameter: its wire declaration plus the decoded CLR value.</summary>
internal sealed class TdsRpcParameter(string name, bool isOutput, DbType dbType, object? value, int size = 0, byte precision = 0, byte scale = 0)
{
    /// <summary>The parameter name as sent, usually with a leading '@'; may be empty.</summary>
    public readonly string Name = name;

    /// <summary>True when the fByRefValue status bit marks the parameter for output.</summary>
    public readonly bool IsOutput = isOutput;

    /// <summary>The ADO.NET type inferred from the wire type token.</summary>
    public readonly DbType DbType = dbType;

    /// <summary>The decoded CLR value, or null when the wire value is NULL.</summary>
    public readonly object? Value = value;

    /// <summary>Declared maximum: characters for strings, bytes for binary; -1 for MAX; 0 when not applicable.</summary>
    public readonly int Size = size;

    /// <summary>Declared precision for decimal parameters, otherwise 0.</summary>
    public readonly byte Precision = precision;

    /// <summary>Declared scale for decimal / time / datetime2 / datetimeoffset parameters, otherwise 0.</summary>
    public readonly byte Scale = scale;
}
