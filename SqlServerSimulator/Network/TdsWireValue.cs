using System.Buffers.Binary;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>
/// The framing-independent half of the TDS client-value codec: the low-level
/// numeric / temporal primitives, the PLP reader, and the two self-describing
/// value forms — the <c>sql_variant</c> body (MS-TDS §2.2.5.5.3) and the CLR-UDT
/// value — whose bytes carry their own type and so decode the same regardless of
/// whether they arrive as an RPC parameter (<see cref="TdsRpcRequest"/>) or as a
/// column inside a bulk-load / table-valued-parameter row
/// (<see cref="TdsColumnDecoder"/>). Both surfaces call in here so the shared
/// logic lives once; each keeps only its own outer framing (RPC reads a
/// TYPE_INFO-plus-single-value per parameter and yields a <c>DbType</c> carrier;
/// the column decoder reads COLMETADATA once then many rows and yields a
/// <see cref="SqlValue"/>).
/// </summary>
internal static class TdsWireValue
{
    public static readonly DateTime Epoch1900 = new(1900, 1, 1);

    /// <summary>Reads the 5-byte collation structure, returning whether its fUTF8 bit is set.</summary>
    public static bool ReadCollationUtf8(TdsValueReader reader)
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
    public static byte[]? ReadPlp(TdsValueReader reader)
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

    public static byte TimeValueBytes(byte scale) =>
        scale > 7
            ? throw new InvalidDataException($"Time scale {scale} exceeds the maximum of 7.")
            : scale <= 2 ? (byte)3 : scale <= 4 ? (byte)4 : (byte)5;

    public static long ScaledUnitsToTicks(long units, byte scale)
    {
        var factor = 1L;
        for (var i = scale; i < 7; i++)
            factor *= 10;

        return units * factor;
    }

    public static long AssembleLittleEndian(ReadOnlySpan<byte> bytes)
    {
        var value = 0L;
        for (var i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);

        return value;
    }

    public static int ReadThreeByteInt(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

    private static int MagnitudeBytes(byte precision) => precision <= 9 ? 4 : precision <= 19 ? 8 : precision <= 28 ? 12 : 16;

    /// <summary>
    /// Builds the <see cref="SqlValue"/> for a CLR-UDT value from its type name
    /// and PLP bytes: OrdPath bytes for <c>hierarchyid</c> (stored verbatim), the
    /// MS spatial binary for <c>geography</c> / <c>geometry</c> (decoded to WKT).
    /// An unrecognized type name raises Msg 8064; spatial bytes the decoder cannot
    /// model raise Msg 8023 — both probe-confirmed against SQL Server 2025. The
    /// <paramref name="ordinal"/> / <paramref name="parameterName"/> /
    /// <paramref name="db"/> / <paramref name="currentDatabase"/> only shape those
    /// error messages.
    /// </summary>
    public static SqlValue BuildUdtValue(string typeName, string db, string currentDatabase, int ordinal, string parameterName, byte[]? bytes)
    {
        if (typeName.Equals("hierarchyid", StringComparison.OrdinalIgnoreCase))
            return bytes is null ? SqlValue.Null(SqlType.HierarchyId) : SqlValue.FromHierarchyIdBytes(bytes);

        var isGeography = typeName.Equals("geography", StringComparison.OrdinalIgnoreCase);
        if (isGeography || typeName.Equals("geometry", StringComparison.OrdinalIgnoreCase))
        {
            SqlType type = isGeography ? SqlType.Geography : SqlType.Geometry;
            if (bytes is null)
                return SqlValue.Null(type);
            var wkt = Storage.Bacpac.SpatialWkbDecoder.TryDecode(bytes, isGeography)
                ?? throw SimulatedSqlException.RpcInvalidUdtInstance(ordinal, parameterName, typeName);
            return isGeography ? SqlValue.FromGeography(wkt) : SqlValue.FromGeometry(wkt);
        }

        throw SimulatedSqlException.RpcClrTypeDoesNotExist(ordinal, db.Length == 0 ? currentDatabase : db, typeName);
    }

    /// <summary>
    /// Reads the MS-TDS §2.2.5.5.3 <c>sql_variant</c> body — base-type token,
    /// property-byte count, per-family property bytes, then the raw inner value —
    /// into the matching inner <see cref="SqlValue"/>. The base-type tokens and
    /// property layouts mirror <c>TdsTypeCodec.BuildVariantBody</c> exactly.
    /// </summary>
    public static SqlValue ReadVariantBody(TdsValueReader reader)
    {
        var baseType = reader.ReadByte();
        var propBytes = reader.ReadByte();
#pragma warning disable SSS005 // Grouped by variant base-type family (fixed-length / temporal / decimal / string / binary), not numeric order.
        return baseType switch
        {
            0x30 => SqlValue.FromByte(reader.ReadByte()),
            0x32 => SqlValue.FromBoolean(reader.ReadByte() != 0),
            0x34 => SqlValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(reader.ReadBytes(2))),
            0x38 => SqlValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(reader.ReadBytes(4))),
            0x7F => SqlValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(reader.ReadBytes(8))),
            0x3B => SqlValue.FromSingle(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(reader.ReadBytes(4)))),
            0x3E => SqlValue.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(reader.ReadBytes(8)))),
            0x7A => SqlValue.FromMoney(SqlType.SmallMoney, BinaryPrimitives.ReadInt32LittleEndian(reader.ReadBytes(4)) / 10000m),
            0x3C => ReadVariantMoney(reader),
            0x24 => SqlValue.FromGuid(new Guid(reader.ReadBytes(16))),
            0x28 => SqlValue.FromDate(DateOnly.FromDayNumber(ReadThreeByteInt(reader.ReadBytes(3)))),
            0x3A => ReadVariantSmallDateTime(reader),
            0x3D => ReadVariantDateTime(reader),
            0x6A or 0x6C => ReadVariantDecimal(reader, propBytes),
            0x29 => ReadVariantTime(reader),
            0x2A => ReadVariantDateTime2(reader),
            0x2B => ReadVariantDateTimeOffset(reader),
            0xA7 or 0xAF => ReadVariantAnsiString(reader, propBytes),
            0xE7 or 0xEF => ReadVariantNationalString(reader, propBytes),
            0xA5 or 0xAD => ReadVariantBinary(reader, propBytes),
            _ => throw new NotSupportedException($"The network listener does not accept sql_variant values with base type token 0x{baseType:X2}."),
        };
#pragma warning restore SSS005
    }

    private static SqlValue ReadVariantMoney(TdsValueReader reader)
    {
        var payload = reader.ReadBytes(8);
        var high = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var low = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        return SqlValue.FromMoney(SqlType.Money, (((long)high << 32) | low) / 10000m);
    }

    private static SqlValue ReadVariantSmallDateTime(TdsValueReader reader)
    {
        var payload = reader.ReadBytes(4);
        var days = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var minutes = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        return SqlValue.FromSmallDateTime(Epoch1900.AddDays(days).AddMinutes(minutes));
    }

    private static SqlValue ReadVariantDateTime(TdsValueReader reader)
    {
        var payload = reader.ReadBytes(8);
        var days = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var thirds = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        var ticks = (((long)thirds * 10_000_000) + 150) / 300;
        return SqlValue.FromDateTime(Epoch1900.AddDays(days).AddTicks(ticks));
    }

    private static SqlValue ReadVariantDecimal(TdsValueReader reader, byte propBytes)
    {
        var precision = reader.ReadByte();
        var scale = reader.ReadByte();
        for (var i = 2; i < propBytes; i++)
            _ = reader.ReadByte();
        var type = SqlType.GetDecimal(precision, scale);
        var isNegative = reader.ReadByte() != 1;
        var magnitude = reader.ReadBytes(MagnitudeBytes(precision));
        for (var i = 12; i < magnitude.Length; i++)
        {
            if (magnitude[i] != 0)
                throw new NotSupportedException("A sql_variant decimal value exceeds the range of System.Decimal.");
        }

        Span<byte> assembled = stackalloc byte[12];
        assembled.Clear();
        magnitude[..Math.Min(magnitude.Length, 12)].CopyTo(assembled);
        var lo = BinaryPrimitives.ReadInt32LittleEndian(assembled);
        var mid = BinaryPrimitives.ReadInt32LittleEndian(assembled[4..]);
        var hi = BinaryPrimitives.ReadInt32LittleEndian(assembled[8..]);
        return SqlValue.FromDecimal(type, new decimal(lo, mid, hi, isNegative, scale));
    }

    private static SqlValue ReadVariantTime(TdsValueReader reader)
    {
        var scale = reader.ReadByte();
        var ticks = ScaledUnitsToTicks(AssembleLittleEndian(reader.ReadBytes(TimeValueBytes(scale))), scale);
        return SqlValue.FromTime(SqlType.GetTime(scale), TimeSpan.FromTicks(ticks));
    }

    private static SqlValue ReadVariantDateTime2(TdsValueReader reader)
    {
        var scale = reader.ReadByte();
        var timeBytes = TimeValueBytes(scale);
        var payload = reader.ReadBytes(timeBytes + 3);
        var ticks = ScaledUnitsToTicks(AssembleLittleEndian(payload[..timeBytes]), scale);
        var days = ReadThreeByteInt(payload[timeBytes..]);
        return SqlValue.FromDateTime2(SqlType.GetDateTime2(scale), DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).AddTicks(ticks));
    }

    private static SqlValue ReadVariantDateTimeOffset(TdsValueReader reader)
    {
        var scale = reader.ReadByte();
        var timeBytes = TimeValueBytes(scale);
        var payload = reader.ReadBytes(timeBytes + 5);
        var ticks = ScaledUnitsToTicks(AssembleLittleEndian(payload[..timeBytes]), scale);
        var days = ReadThreeByteInt(payload[timeBytes..(timeBytes + 3)]);
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(payload[(timeBytes + 3)..]);
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        var utcTicks = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).Ticks + ticks;
        return SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(scale), new DateTimeOffset(utcTicks + offset.Ticks, offset));
    }

    private static SqlValue ReadVariantAnsiString(TdsValueReader reader, byte propBytes)
    {
        var utf8 = ReadCollationUtf8(reader);
        var maxLength = reader.ReadUInt16();
        for (var i = 7; i < propBytes; i++)
            _ = reader.ReadByte();
        var encoding = utf8 ? Encoding.UTF8 : CharSqlType.Cp1252Encoder;
        return SqlValue.FromVarchar(encoding.GetString(reader.ReadBytes(maxLength)));
    }

    private static SqlValue ReadVariantNationalString(TdsValueReader reader, byte propBytes)
    {
        _ = ReadCollationUtf8(reader);
        var maxLength = reader.ReadUInt16();
        for (var i = 7; i < propBytes; i++)
            _ = reader.ReadByte();
        return SqlValue.FromNVarchar(Encoding.Unicode.GetString(reader.ReadBytes(maxLength)));
    }

    private static SqlValue ReadVariantBinary(TdsValueReader reader, byte propBytes)
    {
        var maxLength = reader.ReadUInt16();
        for (var i = 2; i < propBytes; i++)
            _ = reader.ReadByte();
        return SqlValue.FromVarbinary(reader.ReadBytes(maxLength).ToArray());
    }
}
