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
/// <item>Fixed-width raw types (int/bigint/smallint/tinyint/bit/datetime/
/// smalldatetime/date) NOT NULL → raw bytes, no prefix.</item>
/// <item>Same types NULLABLE → 1-byte length prefix; 0xFF = NULL, otherwise
/// the type's width followed by raw bytes.</item>
/// <item>Length-prefixed fixed (uniqueidentifier/money/smallmoney/decimal/
/// datetime2/time/datetimeoffset) → always 1-byte length prefix regardless
/// of nullability; 0xFF = NULL otherwise type-width + raw bytes.</item>
/// <item>Variable-length (nvarchar/varchar/nchar/char/varbinary/binary) →
/// 2-byte LE length prefix; 0xFFFF = NULL otherwise N bytes follow.</item>
/// </list>
/// <para>MAX types and the LOB-eligible legacy text/ntext/image family use an
/// 8-byte length prefix with chunked sub-blocks; deferred until the loader
/// hits an AW table that exercises them.</para>
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
    public static SqlValue[]? TryReadRow(Stream stream, ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<bool> columnIsAlias)
    {
        // Peek one byte to detect EOF without throwing.
        var firstByte = stream.ReadByte();
        if (firstByte < 0)
            return null;
        var pushback = new PushbackStream(stream, (byte)firstByte);

        var values = new SqlValue[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            values[i] = DecodeColumn(pushback, columns[i], i < columnIsAlias.Length && columnIsAlias[i]);
        return values;
    }

    private static SqlValue DecodeColumn(PushbackStream stream, HeapColumn column, bool isAliasTyped)
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
        if (type == SqlType.Int32) return ReadFixedRaw(stream, nullable, 4, type, b => SqlValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(b)));
        if (type == SqlType.BigInt) return ReadFixedRaw(stream, nullable, 8, type, b => SqlValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(b)));
        if (type == SqlType.SmallInt) return ReadFixedRaw(stream, nullable, 2, type, b => SqlValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(b)));
        if (type == SqlType.TinyInt) return ReadFixedRaw(stream, nullable, 1, type, b => SqlValue.FromByte(b[0]));
        if (type == SqlType.Bit) return ReadFixedRaw(stream, nullable, 1, type, b => SqlValue.FromBoolean(b[0] != 0));
        if (type == SqlType.DateTime) return ReadFixedRaw(stream, nullable, 8, type, DecodeDateTime);
        if (type == SqlType.SmallDateTime) return ReadFixedRaw(stream, nullable, 4, type, DecodeSmallDateTime);
        if (type == SqlType.Date) return ReadFixedRaw(stream, nullable, 3, type, DecodeDate);
        if (type == SqlType.Money) return ReadFixedRaw(stream, nullable, 8, type, b => DecodeMoney(b, type));
        if (type == SqlType.SmallMoney) return ReadFixedRaw(stream, nullable, 4, type, b => DecodeSmallMoney(b, type));

        // Length-prefixed fixed — always 1-byte length prefix even when
        // NOT NULL. uniqueidentifier has fixed 16-byte payload but always
        // emits its 0x10 prefix per probe.
        if (type == SqlType.UniqueIdentifier) return ReadLengthPrefixed1(stream, 16, type, b => SqlValue.FromGuid(new Guid(b)));

        // Decimal/numeric — 1-byte length prefix + sign byte + LE mantissa.
        // Mantissa bytes depend on precision: 1-9 = 4 bytes, 10-19 = 8 bytes,
        // 20-28 = 12 bytes, 29-38 = 16 bytes. Prefix = sign(1) + mantissa.
        if (type is DecimalSqlType decimalType) return ReadDecimal(stream, decimalType);

        // datetime2 / time / datetimeoffset — precision-dependent fixed width
        // (probe-confirmed via AW HumanResources.Shift on 2026-05-15: time(7)
        // NOT NULL = 5 raw bytes, no prefix). Width comes from the
        // precision-specific singleton fields.
        if (type is TimeSqlType tt) return ReadFixedRaw(stream, nullable, tt.timeBytes, type, b => DecodeTime(b, type));
        if (type is DateTime2SqlType dt2t) return ReadFixedRaw(stream, nullable, dt2t.timeBytes + 3, type, b => DecodeDateTime2(b, type));
        if (type is DateTimeOffsetSqlType dtot) return ReadFixedRaw(stream, nullable, dtot.timeBytes + 5, type, b => DecodeDateTimeOffset(b, type));

        // Variable-length (text/binary) — 2-byte LE prefix, 0xFFFF = NULL.
        return type switch
        {
            VarcharSqlType => ReadVarchar2(stream, type, ansi: true),
            NVarcharSqlType => ReadVarchar2(stream, type, ansi: false),
            NCharSqlType => ReadVarchar2(stream, type, ansi: false),
            CharSqlType => ReadVarchar2(stream, type, ansi: true),
            VarbinarySqlType => ReadVarbinary2(stream, type),
            BinarySqlType => ReadVarbinary2(stream, type),
            _ => throw new NotSupportedException($"BCP decoder doesn't yet handle type {type}."),
        };
    }

    private delegate SqlValue ByteSpanDecoder(ReadOnlySpan<byte> bytes);

    private static SqlValue ReadFixedRaw(PushbackStream stream, bool nullable, int width, SqlType type, ByteSpanDecoder build)
    {
        if (nullable)
        {
            var prefix = stream.ReadOneByte();
            if (prefix == 0xFF)
                return SqlValue.Null(type);
            if (prefix != width)
                throw new InvalidDataException($"BCP: expected fixed-width prefix {width} or 0xFF, got 0x{prefix:X2}.");
        }
        var bytes = new byte[width];
        stream.ReadExact(bytes);
        return build(bytes);
    }

    private static SqlValue ReadLengthPrefixed1(PushbackStream stream, int expectedWidth, SqlType type, ByteSpanDecoder build)
    {
        var prefix = stream.ReadOneByte();
        if (prefix == 0xFF)
            return SqlValue.Null(type);
        if (prefix != expectedWidth)
            throw new InvalidDataException($"BCP: expected length-prefixed-fixed width {expectedWidth} or 0xFF, got 0x{prefix:X2}.");
        var bytes = new byte[expectedWidth];
        stream.ReadExact(bytes);
        return build(bytes);
    }

    /// <summary>
    /// Decimal/numeric BCP encoding: 1-byte prefix (= 1 sign byte + N mantissa
    /// bytes), 1 byte sign (0=negative, 1=positive), N bytes LE unsigned
    /// mantissa. The mantissa is the value times 10^scale, parsed as an
    /// arbitrarily-large integer.
    /// </summary>
    private static SqlValue ReadDecimal(PushbackStream stream, DecimalSqlType type)
    {
        var prefix = stream.ReadOneByte();
        if (prefix == 0xFF)
            return SqlValue.Null(type);
        var bytes = new byte[prefix];
        stream.ReadExact(bytes);
        var positive = bytes[0] != 0;
        // Build the mantissa as a System.Numerics.BigInteger from the LE bytes,
        // then divide by 10^scale to produce the decimal value. The .NET
        // decimal type maxes at 28-29 significant digits — values requiring
        // more are out of scope (matches the simulator's documented decimal
        // quirk).
        var mantissaSpan = bytes.AsSpan(1);
        var unsigned = new System.Numerics.BigInteger(mantissaSpan, isUnsigned: true, isBigEndian: false);
        var signed = positive ? unsigned : -unsigned;
        var scaleDivisor = System.Numerics.BigInteger.Pow(10, type.scale);
        var quotient = signed / scaleDivisor;
        var remainder = signed % scaleDivisor;
        // Compose: integer part + (remainder / 10^scale). Use decimal arithmetic
        // for the fractional piece.
        var value = (decimal)quotient + ((decimal)remainder / (decimal)scaleDivisor);
        return SqlValue.FromDecimal(type, value);
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
        var precision = ((DateTime2SqlType)type).precision;
        var ticksPerUnit = TicksPerPrecisionUnit(precision);
        var dt = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified)
            .AddTicks(ticks * ticksPerUnit);
        return SqlValue.FromDateTime2(type, dt);
    }

    /// <summary>
    /// time(N): variable-byte LE fractional-second ticks (no date part).
    /// </summary>
    private static SqlValue DecodeTime(ReadOnlySpan<byte> bytes, SqlType type)
    {
        long ticks = 0;
        for (var i = bytes.Length - 1; i >= 0; i--)
            ticks = (ticks << 8) | bytes[i];
        var precision = ((TimeSqlType)type).precision;
        return SqlValue.FromTime(type, TimeSpan.FromTicks(ticks * TicksPerPrecisionUnit(precision)));
    }

    /// <summary>
    /// datetimeoffset(N): datetime2 layout + 2-byte LE signed minutes offset
    /// from UTC.
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
        var precision = ((DateTimeOffsetSqlType)type).precision;
        var dt = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified)
            .AddTicks(ticks * TicksPerPrecisionUnit(precision));
        var offset = TimeSpan.FromMinutes(minutesOffset);
        return SqlValue.FromDateTimeOffset(type, new DateTimeOffset(dt, offset));
    }

    private static long TicksPerPrecisionUnit(int precision) => precision switch
    {
        0 => TimeSpan.TicksPerSecond,
        1 => TimeSpan.TicksPerSecond / 10,
        2 => TimeSpan.TicksPerSecond / 100,
        3 => TimeSpan.TicksPerMillisecond,
        4 => TimeSpan.TicksPerMillisecond / 10,
        5 => TimeSpan.TicksPerMillisecond / 100,
        6 => 10,
        _ => 1,
    };

    private static SqlValue ReadVarchar2(PushbackStream stream, SqlType type, bool ansi)
    {
        RejectMaxType(type);
        var prefixBytes = new byte[2];
        stream.ReadExact(prefixBytes);
        var byteLength = BinaryPrimitives.ReadUInt16LittleEndian(prefixBytes);
        if (byteLength == 0xFFFF)
            return SqlValue.Null(type);
        var data = new byte[byteLength];
        stream.ReadExact(data);
        var text = ansi ? Encoding.GetEncoding(1252).GetString(data) : Encoding.Unicode.GetString(data);
        return type switch
        {
            VarcharSqlType => SqlValue.FromVarchar(text),
            NVarcharSqlType => SqlValue.FromNVarchar(text),
            NCharSqlType => SqlValue.FromNChar(type, text),
            CharSqlType => SqlValue.FromChar(type, text),
            _ => throw new InvalidOperationException(),
        };
    }

    private static SqlValue ReadVarbinary2(PushbackStream stream, SqlType type)
    {
        RejectMaxType(type);
        var prefixBytes = new byte[2];
        stream.ReadExact(prefixBytes);
        var byteLength = BinaryPrimitives.ReadUInt16LittleEndian(prefixBytes);
        if (byteLength == 0xFFFF)
            return SqlValue.Null(type);
        var data = new byte[byteLength];
        stream.ReadExact(data);
        return type is BinarySqlType ? SqlValue.FromBinary(type, data) : SqlValue.FromVarbinary(data);
    }

    /// <summary>
    /// MAX types (varchar(MAX) / nvarchar(MAX) / varbinary(MAX)) use a
    /// different wire format than the 2-byte-prefix bounded variants —
    /// 8-byte total-length prefix with chunked sub-blocks. Decoding deferred
    /// to a follow-up bundle; raising upfront here avoids the 2-byte read
    /// consuming garbage data and corrupting downstream column boundaries.
    /// </summary>
    private static void RejectMaxType(SqlType type)
    {
        // MAX variants use length=-1 sentinel; bounded forms carry their
        // declared length (1..8000 for varchar/varbinary, 1..4000 for nvarchar).
        if (type is VarcharSqlType vc && vc.length == -1)
            throw new NotSupportedException("BCP decoder doesn't yet handle varchar(MAX).");
        if (type is NVarcharSqlType nv && nv.length == -1)
            throw new NotSupportedException("BCP decoder doesn't yet handle nvarchar(MAX).");
        if (type is VarbinarySqlType vb && vb.length == -1)
            throw new NotSupportedException("BCP decoder doesn't yet handle varbinary(MAX).");
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
        var dt = epoch.AddDays(days).AddTicks(ticks300 * (TimeSpan.TicksPerSecond / 300L));
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
    private sealed class PushbackStream(Stream baseStream, byte first)
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
