using System.Buffers.Binary;
using System.Text;

namespace SqlServerSimulator.Storage.Bacpac;

/// <summary>
/// Decodes the per-table <c>Data/&lt;schema&gt;.&lt;table&gt;/TableData-NNN-NNNNN.BCP</c>
/// wire stream into <see cref="SqlValue"/> rows that
/// <see cref="RowEncoder.EncodeRow(ReadOnlySpan{HeapColumn}, ReadOnlySpan{SqlValue}, Heap?)"/>
/// can consume directly. The decoded rows are inserted via
/// <see cref="Heap.Insert"/>, bypassing the SQL parser entirely — required to
/// load the AdventureWorks 760K-row payload in reasonable time.
/// </summary>
/// <remarks>
/// <para>BCP wire format conventions (verified by hex-dump probing
/// AdventureWorks2025 on 2026-05-15):</para>
/// <list type="bullet">
/// <item>Fixed-width raw types (int/bigint/smallint/tinyint/datetime/
/// smalldatetime/date) NOT NULL → raw bytes, no prefix.</item>
/// <item>Same types NULLABLE → 1-byte length prefix; 0xFF = NULL, otherwise
/// the type's width followed by raw bytes.</item>
/// <item><c>bit</c> always uses a 1-byte length prefix (== 1) followed by
/// 1 raw byte, regardless of nullability. Probe-confirmed against AW's
/// Production.Document.FolderFlag (plain bit NOT NULL) on 2026-05-15: the
/// wire bytes are <c>01 01</c> for value 1, matching the UDDT-alias bit
/// shape rather than the fixed-raw shape of other 1-byte types.</item>
/// <item>Length-prefixed fixed (uniqueidentifier/money/smallmoney/decimal/
/// datetime2/time/datetimeoffset) → always 1-byte length prefix regardless
/// of nullability; 0xFF = NULL otherwise type-width + raw bytes.</item>
/// <item>Variable-length (nvarchar/varchar/nchar/char/varbinary/binary) →
/// 2-byte LE length prefix; 0xFFFF = NULL otherwise N bytes follow.</item>
/// <item>MAX types (varchar(MAX) / nvarchar(MAX) / varbinary(MAX)), xml,
/// and the CLR-UDT family (hierarchyid / geography / geometry) all use an
/// 8-byte LE length prefix followed by N bytes inline. 0xFFFFFFFFFFFFFFFF
/// (-1) = NULL. The bacpac BCP wire form is NOT the TDS PLP chunked
/// encoding — probe-confirmed by ProductPhoto's 1077-byte ThumbNailPhoto
/// flowing inline with no 4-byte chunk markers / terminator.</item>
/// </list>
/// </remarks>
internal static class BcpRowReader
{
    /// <summary>
    /// Reads one row from <paramref name="stream"/>, decoding one
    /// <see cref="SqlValue"/> per column in <paramref name="columns"/>.
    /// <paramref name="columnIsAlias"/> marks per-column whether the column
    /// was declared via a UDDT alias — those columns use the 1-byte-prefix
    /// wire format even for fixed-raw types like int/bit/etc.
    /// Returns null when the stream is at EOF (no more rows).
    /// </summary>
    public static SqlValue[]? TryReadRow(BufferedStream stream, ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<bool> columnIsAlias)
    {
        // Peek one byte to detect EOF without throwing.
        var firstByte = stream.ReadByte();
        if (firstByte < 0)
            return null;
        var pushback = new PushbackStream(stream, (byte)firstByte);

        var values = new SqlValue[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            values[i] = DecodeColumn(ref pushback, columns[i], i < columnIsAlias.Length && columnIsAlias[i]);
        return values;
    }

    private static SqlValue DecodeColumn(ref PushbackStream stream, HeapColumn column, bool isAliasTyped)
    {
        var type = column.Type;
        // UDDT-aliased columns use 1-byte-prefix wire format regardless of
        // nullability — match that by routing fixed-raw types through the
        // 1-byte-prefix path when the alias flag is set.
        var nullable = column.Nullable || isAliasTyped;

        // Fixed-width raw types — no prefix when NOT NULL, 1-byte prefix when
        // nullable (0xFF = NULL, else width). Reference-equality on the
        // simulator's type singletons. Money/smallmoney probe as fixed-raw
        // despite the prereqs-doc matrix's "length-prefixed fixed" claim
        // (probe-confirmed against AW's SpecialOffer.DiscountPct on
        // 2026-05-15: 4 raw bytes with value 0 for the first row, no prefix).
        if (type == SqlType.Int32) return ReadFixedRaw(ref stream, nullable, 4, type, b => SqlValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(b)));
        if (type == SqlType.BigInt) return ReadFixedRaw(ref stream, nullable, 8, type, b => SqlValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(b)));
        if (type == SqlType.SmallInt) return ReadFixedRaw(ref stream, nullable, 2, type, b => SqlValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(b)));
        if (type == SqlType.TinyInt) return ReadFixedRaw(ref stream, nullable, 1, type, b => SqlValue.FromByte(b[0]));
        // bit is always 1-byte length-prefixed (1 byte prefix == 1, then 1
        // raw byte for the value). Probe-confirmed via AW's plain-bit
        // Production.Document.FolderFlag on 2026-05-15.
        if (type == SqlType.Bit) return ReadLengthPrefixed1(ref stream, 1, type, b => SqlValue.FromBoolean(b[0] != 0));
        if (type == SqlType.DateTime) return ReadFixedRaw(ref stream, nullable, 8, type, DecodeDateTime);
        if (type == SqlType.SmallDateTime) return ReadFixedRaw(ref stream, nullable, 4, type, DecodeSmallDateTime);
        if (type == SqlType.Date) return ReadFixedRaw(ref stream, nullable, 3, type, DecodeDate);
        if (type == SqlType.Money) return ReadFixedRaw(ref stream, nullable, 8, type, b => DecodeMoney(b, type));
        if (type == SqlType.SmallMoney) return ReadFixedRaw(ref stream, nullable, 4, type, b => DecodeSmallMoney(b, type));
        // float / real are IEEE 754 little-endian at their storage width, on
        // the same fixed-raw prefix rule as the integer family. A float(n) with
        // n <= 24 is real and carries the 4-byte singleton, so the declared
        // type already names the width.
        if (type == SqlType.Float) return ReadFixedRaw(ref stream, nullable, 8, type, b => SqlValue.FromDouble(BinaryPrimitives.ReadDoubleLittleEndian(b)));
        if (type == SqlType.Real) return ReadFixedRaw(ref stream, nullable, 4, type, b => SqlValue.FromSingle(BinaryPrimitives.ReadSingleLittleEndian(b)));

        // Length-prefixed fixed — always 1-byte length prefix even when
        // NOT NULL. uniqueidentifier has fixed 16-byte payload but always
        // emits its 0x10 prefix per probe.
        if (type == SqlType.UniqueIdentifier) return ReadLengthPrefixed1(ref stream, 16, type, b => SqlValue.FromGuid(new Guid(b)));

        // Decimal/numeric — see ReadDecimal for the full wire layout (the
        // BCP form prepends an inline precision + scale + sign before the
        // mantissa, which the TDS wire spec does not).
        if (type is DecimalSqlType decimalType) return ReadDecimal(ref stream, decimalType);

        // datetime2 / time / datetimeoffset — precision-dependent fixed width
        // (probe-confirmed via AW HumanResources.Shift on 2026-05-15: time(7)
        // NOT NULL = 5 raw bytes, no prefix). Width comes from the
        // precision-specific singleton fields.
        // DacFx writes time / datetime2 / datetimeoffset at their *maximum*
        // width with the value scaled to 7 fractional digits, whatever the
        // column's declared precision — verified against a sqlpackage export
        // carrying precisions 0, 3 and 7 side by side. Every bacpac exercised
        // before this used precision-7 columns only, where the declared and
        // maximum widths coincide, which is why the per-precision width read
        // correctly there and nowhere else.
        if (type is TimeSqlType) return ReadFixedRaw(ref stream, nullable, 5, type, b => DecodeTime(b, type));
        if (type is DateTime2SqlType) return ReadFixedRaw(ref stream, nullable, 8, type, b => DecodeDateTime2(b, type));
        if (type is DateTimeOffsetSqlType) return ReadFixedRaw(ref stream, nullable, 10, type, b => DecodeDateTimeOffset(b, type));

        // 8-byte LE length-prefix types (MAX text/binary, xml, CLR-UDT family).
        // 0xFFFFFFFFFFFFFFFF = NULL; otherwise N bytes inline (no TDS-PLP
        // chunk markers — probe-confirmed against AW on 2026-05-15).
        if (type is XmlSqlType) return ReadEightBytePrefixed(ref stream, type, EightBytePayload.Xml);
        if (type is GeographySqlType) return ReadEightBytePrefixed(ref stream, type, EightBytePayload.Geography);
        if (type is GeometrySqlType) return ReadEightBytePrefixed(ref stream, type, EightBytePayload.Geometry);
        if (type is HierarchyIdSqlType) return ReadEightBytePrefixed(ref stream, type, EightBytePayload.HierarchyId);
        if (type is VarcharSqlType vc && vc.length == -1) return ReadEightBytePrefixed(ref stream, type, EightBytePayload.VarcharMax);
        if (type is NVarcharSqlType nv && nv.length == -1) return ReadEightBytePrefixed(ref stream, type, EightBytePayload.NVarcharMax);
        if (type is VarbinarySqlType vb && vb.length == -1) return ReadEightBytePrefixed(ref stream, type, EightBytePayload.VarbinaryMax);

        // Variable-length bounded (text/binary) — 2-byte LE prefix, 0xFFFF = NULL.
        return type switch
        {
            VarcharSqlType => ReadVarchar2(ref stream, type),
            NVarcharSqlType => ReadVarchar2(ref stream, type),
            SystemNameSqlType => ReadVarchar2(ref stream, type),
            NCharSqlType => ReadVarchar2(ref stream, type),
            CharSqlType => ReadVarchar2(ref stream, type),
            VarbinarySqlType => ReadVarbinary2(ref stream, type),
            BinarySqlType => ReadVarbinary2(ref stream, type),
            _ => throw new NotSupportedException($"BCP decoder doesn't yet handle type {type}."),
        };
    }

    /// <summary>
    /// Distinguishes how an 8-byte-prefixed payload is materialized into a
    /// <see cref="SqlValue"/> — the byte handling and length read are
    /// identical across the 8-byte-prefix family but the final coercion to
    /// a value differs by SqlType.
    /// </summary>
    private enum EightBytePayload
    {
        VarcharMax,
        NVarcharMax,
        VarbinaryMax,
        Xml,
        /// <summary>
        /// Geography: read the length + N bytes, try
        /// <see cref="Spatial.SpatialBinaryCodec.TryDecode"/> for any 2D shape
        /// (Point / LineString / Polygon / Multi* / GeometryCollection).
        /// Z/M-bearing shapes and shapes the decoder can't handle fall
        /// back to <c>SqlValue.Null</c> — the row loads without breaking
        /// column count.
        /// </summary>
        Geography,
        /// <summary>
        /// Geometry: same decode strategy as <see cref="Geography"/> but
        /// with axis order (x, y) instead of (lat, long).
        /// </summary>
        Geometry,
        /// <summary>
        /// Hierarchyid: read the length + N bytes and store them verbatim as
        /// the value's canonical OrdPath form (no decode). Any tier round-trips
        /// through storage/export opaquely, even ones the ordinal codec can't
        /// yet stringify.
        /// </summary>
        HierarchyId,
    }

    /// <summary>
    /// Reads the 8-byte LE length prefix and dispatches by
    /// <see cref="EightBytePayload"/>. <c>0xFFFFFFFFFFFFFFFF</c> (-1 signed)
    /// is the NULL sentinel; positive lengths up to <see cref="int.MaxValue"/>
    /// are read inline as a single buffer. The chunked TDS-PLP form (other
    /// negative sentinels with 4-byte chunk markers) is not seen in bacpac
    /// files and is rejected as <see cref="NotSupportedException"/> so a bad
    /// payload surfaces clearly rather than corrupting the row stream.
    /// </summary>
    private static SqlValue ReadEightBytePrefixed(ref PushbackStream stream, SqlType type, EightBytePayload kind)
    {
        Span<byte> lengthBuf = stackalloc byte[8];
        stream.ReadExact(lengthBuf);
        var length = BinaryPrimitives.ReadInt64LittleEndian(lengthBuf);
        if (length == -1L)
            return SqlValue.Null(type);
        if (length is < 0 or > int.MaxValue)
            throw new NotSupportedException($"BCP: unsupported 8-byte-prefix length 0x{length:X16} for {type} (TDS-PLP chunked form not seen in bacpac BCP).");
        var data = new byte[length];
        stream.ReadExact(data);
        return kind switch
        {
            EightBytePayload.VarcharMax => SqlValue.FromVarchar(Encoding.Unicode.GetString(data)),
            EightBytePayload.NVarcharMax => SqlValue.FromNVarchar(Encoding.Unicode.GetString(data)),
            EightBytePayload.VarbinaryMax => SqlValue.FromVarbinary(data),
            EightBytePayload.Xml => SqlValue.FromXml(Encoding.Unicode.GetString(data)),
            EightBytePayload.Geography => Spatial.SpatialBinaryCodec.TryDecode(data, isGeography: true) is { } geographyValue
                ? SqlValue.FromGeography(geographyValue)
                : SqlValue.Null(type),
            EightBytePayload.Geometry => Spatial.SpatialBinaryCodec.TryDecode(data, isGeography: false) is { } geometryValue
                ? SqlValue.FromGeometry(geometryValue)
                : SqlValue.Null(type),
            EightBytePayload.HierarchyId => SqlValue.FromHierarchyIdBytes(data),
            _ => throw new InvalidOperationException($"unknown EightBytePayload {kind}"),
        };
    }

    private delegate SqlValue ByteSpanDecoder(ReadOnlySpan<byte> bytes);

    private static SqlValue ReadFixedRaw(ref PushbackStream stream, bool nullable, int width, SqlType type, ByteSpanDecoder build)
    {
        if (nullable)
        {
            var prefix = stream.ReadOneByte();
            if (prefix == 0xFF)
                return SqlValue.Null(type);
            if (prefix != width)
                throw new InvalidDataException($"BCP: expected fixed-width prefix {width} or 0xFF, got 0x{prefix:X2}.");
        }
        Span<byte> bytes = stackalloc byte[width];
        stream.ReadExact(bytes);
        return build(bytes);
    }

    private static SqlValue ReadLengthPrefixed1(ref PushbackStream stream, int expectedWidth, SqlType type, ByteSpanDecoder build)
    {
        var prefix = stream.ReadOneByte();
        if (prefix == 0xFF)
            return SqlValue.Null(type);
        if (prefix != expectedWidth)
            throw new InvalidDataException($"BCP: expected length-prefixed-fixed width {expectedWidth} or 0xFF, got 0x{prefix:X2}.");
        Span<byte> bytes = stackalloc byte[expectedWidth];
        stream.ReadExact(bytes);
        return build(bytes);
    }

    /// <summary>
    /// Decimal/numeric BCP encoding (probed against the WideWorldImporters
    /// bacpac, distinct from the TDS-wire layout):
    /// <c>[1-byte prefix N][1-byte precision][1-byte scale][1-byte sign 0/1][N-3 byte mantissa LE]</c>.
    /// Mantissa width depends on precision per the TDS spec (4/8/12/16 bytes
    /// for precision 1-9 / 10-19 / 20-28 / 29-38) so the BCP payload total
    /// is always one of 7/11/15/19 bytes; the simulator trusts the
    /// destination <see cref="DecimalSqlType"/> for the storage scale and
    /// uses the on-disk scale only for the value calculation.
    /// </summary>
    private static SqlValue ReadDecimal(ref PushbackStream stream, DecimalSqlType type)
    {
        var prefix = stream.ReadOneByte();
        if (prefix == 0xFF)
            return SqlValue.Null(type);
        Span<byte> bytes = stackalloc byte[prefix];
        stream.ReadExact(bytes);
        var onDiskScale = bytes[1];
        var positive = bytes[2] != 0;
        var mantissaSpan = bytes[3..];

        // The mantissa is the value's own digits at the on-disk scale, so the
        // whole width reads straight into the storage form — a 38-digit column
        // round-trips with no arithmetic at all.
        Span<byte> padded = stackalloc byte[16];
        mantissaSpan[..Math.Min(16, mantissaSpan.Length)].CopyTo(padded);
        var magnitude = BinaryPrimitives.ReadUInt128LittleEndian(padded);
        return SqlValue.FromDecimal(type, Decimal38.FromParts(magnitude, isNegative: !positive, onDiskScale));
    }

    /// <summary>
    /// datetime2(N): 3-byte LE days since 0001-01-01 + variable-byte LE
    /// fractional-second ticks. Payload width = 6/7/8 depending on precision.
    /// </summary>
    private static SqlValue DecodeDateTime2(ReadOnlySpan<byte> bytes, SqlType type)
    {
        // Last 3 bytes = days since 0001-01-01; remaining bytes = ticks at
        // the type's precision unit (LE unsigned).
        var dateOffset = bytes.Length - 3;
        var days = bytes[dateOffset] | (bytes[dateOffset + 1] << 8) | (bytes[dateOffset + 2] << 16);
        long ticks = 0;
        for (var i = dateOffset - 1; i >= 0; i--)
            ticks = (ticks << 8) | bytes[i];
        var dt = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddTicks(ticks);
        return SqlValue.FromDateTime2(type, dt);
    }

    /// <summary>
    /// time(N): 5-byte LE count of 100-nanosecond units since midnight, with
    /// no date part. Always the maximum width — see the dispatch above.
    /// </summary>
    private static SqlValue DecodeTime(ReadOnlySpan<byte> bytes, SqlType type)
    {
        long ticks = 0;
        for (var i = bytes.Length - 1; i >= 0; i--)
            ticks = (ticks << 8) | bytes[i];
        return SqlValue.FromTime(type, TimeSpan.FromTicks(ticks));
    }

    /// <summary>
    /// datetimeoffset(N): datetime2 layout + 2-byte LE signed minutes offset
    /// from UTC. The date and time carry the instant in <b>UTC</b>, not in the
    /// stored offset's local time — probe-confirmed against a DacFx export,
    /// where <c>2024-03-15 13:45:12 +05:30</c> writes 08:15:12 with offset
    /// +330.
    /// </summary>
    private static SqlValue DecodeDateTimeOffset(ReadOnlySpan<byte> bytes, SqlType type)
    {
        var minutesOffset = BinaryPrimitives.ReadInt16LittleEndian(bytes[^2..]);
        var datetime2Bytes = bytes[..^2];
        var dateOffset = datetime2Bytes.Length - 3;
        var days = datetime2Bytes[dateOffset] | (datetime2Bytes[dateOffset + 1] << 8) | (datetime2Bytes[dateOffset + 2] << 16);
        long ticks = 0;
        for (var i = dateOffset - 1; i >= 0; i--)
            ticks = (ticks << 8) | datetime2Bytes[i];
        var utc = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified).AddTicks(ticks);
        return SqlValue.FromDateTimeOffset(type, new DateTimeOffset(utc, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(minutesOffset)));
    }

    private static SqlValue ReadVarchar2(ref PushbackStream stream, SqlType type)
    {
        Span<byte> prefixBytes = stackalloc byte[2];
        stream.ReadExact(prefixBytes);
        var byteLength = BinaryPrimitives.ReadUInt16LittleEndian(prefixBytes);
        if (byteLength == 0xFFFF)
            return SqlValue.Null(type);
        var data = new byte[byteLength];
        stream.ReadExact(data);
        // DACFx writes BCP payloads for every character column as UTF-16-LE
        // regardless of varchar vs nvarchar declaration (probe-confirmed
        // against AW2025 Person.Password where the column declares
        // varchar(128) but every byte pair in the BCP file is UTF-16-LE).
        // The decoded .NET string is then handed to the matching FromX
        // factory; DATALENGTH semantics still derive from the SqlType, so
        // a varchar(128) decoded from "++bTDOq..." reports 43 bytes the
        // way SQL Server does, not 86.
        var text = Encoding.Unicode.GetString(data);
        return type switch
        {
            VarcharSqlType => SqlValue.FromVarchar(text),
            NVarcharSqlType => SqlValue.FromNVarchar(text),
            SystemNameSqlType => SqlValue.FromSystemName(text),
            NCharSqlType => SqlValue.FromNChar(type, text),
            CharSqlType => SqlValue.FromChar(type, text),
            _ => throw new InvalidOperationException(),
        };
    }

    private static SqlValue ReadVarbinary2(ref PushbackStream stream, SqlType type)
    {
        Span<byte> prefixBytes = stackalloc byte[2];
        stream.ReadExact(prefixBytes);
        var byteLength = BinaryPrimitives.ReadUInt16LittleEndian(prefixBytes);
        if (byteLength == 0xFFFF)
            return SqlValue.Null(type);
        var data = new byte[byteLength];
        stream.ReadExact(data);
        return type is BinarySqlType ? SqlValue.FromBinary(type, data) : SqlValue.FromVarbinary(data);
    }

    /// <summary>
    /// SQL Server <c>datetime</c>: 4-byte int32 (days since 1900-01-01) + 4-byte
    /// uint32 (1/300-second ticks since midnight).
    /// </summary>
    private static SqlValue DecodeDateTime(ReadOnlySpan<byte> bytes)
    {
        var days = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]);
        var ticks300 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
        var epoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var dt = epoch.AddDays(days).AddTicks(ticks300 * TimeSpan.TicksPerSecond / 300L);
        return SqlValue.FromDateTime(dt);
    }

    /// <summary>
    /// SQL Server <c>smalldatetime</c>: 2-byte uint16 (days since 1900-01-01) +
    /// 2-byte uint16 (minutes since midnight).
    /// </summary>
    private static SqlValue DecodeSmallDateTime(ReadOnlySpan<byte> bytes)
    {
        var days = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
        var minutes = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..4]);
        var dt = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .AddDays(days)
            .AddMinutes(minutes);
        return SqlValue.FromSmallDateTime(dt);
    }

    /// <summary>
    /// SQL Server <c>date</c>: 3-byte unsigned LE days since 0001-01-01.
    /// </summary>
    private static SqlValue DecodeDate(ReadOnlySpan<byte> bytes)
    {
        var days = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return SqlValue.FromDate(DateOnly.FromDayNumber(days));
    }


    /// <summary>
    /// SQL Server <c>money</c>: 8-byte signed LE integer scaled by 10000
    /// (so the bytes encode the value times 10000).
    /// </summary>
    private static SqlValue DecodeMoney(ReadOnlySpan<byte> bytes, SqlType type)
    {
        // money's wire form is two 32-bit halves: bytes[0..4] = high 32 bits,
        // bytes[4..8] = low 32 bits, each LE. Combine to a signed int64.
        var high = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]);
        var low = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
        var scaled = ((long)high << 32) | low;
        return SqlValue.FromMoney(type, scaled / 10000m);
    }

    /// <summary>
    /// SQL Server <c>smallmoney</c>: 4-byte signed LE integer scaled by 10000.
    /// </summary>
    private static SqlValue DecodeSmallMoney(ReadOnlySpan<byte> bytes, SqlType type)
    {
        var scaled = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return SqlValue.FromMoney(type, scaled / 10000m);
    }

    /// <summary>
    /// One-byte pushback over a base stream. The BCP decoder peeks the first
    /// byte of each row to detect EOF; pushback puts the byte back so the
    /// first column's decoder reads it as normal.
    /// </summary>
    private struct PushbackStream(BufferedStream baseStream, byte first)
    {
        private readonly int pushed = first;
        private bool hasPushed = true;

        public byte ReadOneByte()
        {
            if (this.hasPushed)
            {
                this.hasPushed = false;
                return (byte)this.pushed;
            }
            var v = baseStream.ReadByte();
            return v < 0
                ? throw new EndOfStreamException("BCP: unexpected end of stream.")
                : (byte)v;
        }

        public void ReadExact(Span<byte> dest)
        {
            var i = 0;
            if (this.hasPushed)
            {
                dest[0] = (byte)this.pushed;
                this.hasPushed = false;
                i = 1;
            }
            while (i < dest.Length)
            {
                var n = baseStream.Read(dest[i..]);
                if (n <= 0)
                    throw new EndOfStreamException("BCP: unexpected end of stream.");
                i += n;
            }
        }
    }
}
