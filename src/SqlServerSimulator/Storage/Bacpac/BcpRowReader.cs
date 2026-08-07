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
    /// Resolves the wire shape of every column in <paramref name="columns"/>,
    /// once for the table's whole BCP stream. Which shape a column takes
    /// depends only on its declared type, its nullability and whether it was
    /// declared through a UDDT alias — none of which vary from row to row — so
    /// the dispatch runs once per column here instead of once per column per
    /// row.
    /// <paramref name="columnIsAlias"/> marks per-column whether the column
    /// was declared via a UDDT alias — those columns use the 1-byte-prefix
    /// wire format even for fixed-raw types like int/bit/etc.
    /// </summary>
    public static ColumnDecoder[] ResolveDecoders(ReadOnlySpan<HeapColumn> columns, ReadOnlySpan<bool> columnIsAlias)
    {
        var decoders = new ColumnDecoder[columns.Length];
        for (var i = 0; i < columns.Length; i++)
            decoders[i] = ResolveDecoder(columns[i], i < columnIsAlias.Length && columnIsAlias[i]);
        return decoders;
    }

    /// <summary>
    /// Reads one row from <paramref name="stream"/> into <paramref name="into"/>,
    /// decoding one <see cref="SqlValue"/> per entry in <paramref name="decoders"/>
    /// (as produced by <see cref="ResolveDecoders"/>). Every slot is written, so
    /// the buffer is caller-owned scratch to reuse across rows: nothing
    /// downstream retains it — the encoder copies what it needs into the row
    /// bytes — and a <see cref="SqlValue"/> is 32 bytes, so a per-row buffer is
    /// one of the larger costs of a load.
    /// Returns false when the stream is at EOF (no more rows).
    /// </summary>
    public static bool TryReadRow(BufferedStream stream, ReadOnlySpan<ColumnDecoder> decoders, SqlValue[] into)
    {
        // Peek one byte to detect EOF without throwing.
        var firstByte = stream.ReadByte();
        if (firstByte < 0)
            return false;
        var pushback = new PushbackStream(stream, (byte)firstByte);

        for (var i = 0; i < decoders.Length; i++)
            into[i] = Decode(ref pushback, decoders[i]);
        return true;
    }

    private static SqlValue Decode(ref PushbackStream stream, ColumnDecoder decoder)
    {
        var type = decoder.Type;
        return decoder.Form switch
        {
            WireForm.FixedRaw => ReadFixedRaw(ref stream, nullable: false, decoder.Width, type, decoder.Build!),
            WireForm.FixedRawNullable => ReadFixedRaw(ref stream, nullable: true, decoder.Width, type, decoder.Build!),
            WireForm.LengthPrefixed1 => ReadLengthPrefixed1(ref stream, decoder.Width, type, decoder.Build!),
            WireForm.Decimal => ReadDecimal(ref stream, type),
            WireForm.EightByte => ReadEightBytePrefixed(ref stream, type, decoder.Payload),
            WireForm.Varchar2 => ReadVarchar2(ref stream, type),
            WireForm.Varbinary2 => ReadVarbinary2(ref stream, type),
            _ => throw new InvalidOperationException($"unknown WireForm {decoder.Form}"),
        };
    }

    private static ColumnDecoder ResolveDecoder(HeapColumn column, bool isAliasTyped)
    {
        var type = column.Type;
        // UDDT-aliased columns use 1-byte-prefix wire format regardless of
        // nullability — match that by routing fixed-raw types through the
        // 1-byte-prefix path when the alias flag is set.
        var nullable = column.Nullable || isAliasTyped;

        // Every SqlType matched below is sealed and derives straight from
        // SqlType, so a type pattern is an exact match rather than a
        // hierarchy test; for the types the simulator holds as a lone
        // singleton (the integer, money, float and legacy-datetime families,
        // uniqueidentifier) it is also exactly the identity test a
        // `type == SqlType.Int32` comparison would make.
        return type switch
        {
            // Fixed-width raw types — no prefix when NOT NULL, 1-byte prefix
            // when nullable (0xFF = NULL, else width). Money/smallmoney probe
            // as fixed-raw despite the prereqs-doc matrix's "length-prefixed
            // fixed" claim (probe-confirmed against AW's
            // SpecialOffer.DiscountPct on 2026-05-15: 4 raw bytes with value 0
            // for the first row, no prefix).
            Int32SqlType => FixedRaw(type, nullable, 4, static (b, _) => SqlValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(b))),
            BigIntSqlType => FixedRaw(type, nullable, 8, static (b, _) => SqlValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(b))),
            SmallIntSqlType => FixedRaw(type, nullable, 2, static (b, _) => SqlValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(b))),
            TinyIntSqlType => FixedRaw(type, nullable, 1, static (b, _) => SqlValue.FromByte(b[0])),
            DateTimeSqlType => FixedRaw(type, nullable, 8, static (b, _) => DecodeDateTime(b)),
            SmallDateTimeSqlType => FixedRaw(type, nullable, 4, static (b, _) => DecodeSmallDateTime(b)),
            DateSqlType => FixedRaw(type, nullable, 3, static (b, _) => DecodeDate(b)),
            MoneySqlType => FixedRaw(type, nullable, 8, DecodeMoney),
            SmallMoneySqlType => FixedRaw(type, nullable, 4, DecodeSmallMoney),

            // float / real are IEEE 754 little-endian at their storage width,
            // on the same fixed-raw prefix rule as the integer family. A
            // float(n) with n <= 24 is real and carries the 4-byte singleton,
            // so the declared type already names the width.
            FloatSqlType => FixedRaw(type, nullable, 8, static (b, _) => SqlValue.FromDouble(BinaryPrimitives.ReadDoubleLittleEndian(b))),
            RealSqlType => FixedRaw(type, nullable, 4, static (b, _) => SqlValue.FromSingle(BinaryPrimitives.ReadSingleLittleEndian(b))),

            // datetime2 / time / datetimeoffset — fixed width, but DacFx writes
            // all three at their *maximum* width with the value scaled to 7
            // fractional digits, whatever the column's declared precision —
            // verified against a sqlpackage export carrying precisions 0, 3 and
            // 7 side by side. Every bacpac exercised before this used
            // precision-7 columns only, where the declared and maximum widths
            // coincide, which is why a per-precision width read correctly there
            // and nowhere else.
            TimeSqlType => FixedRaw(type, nullable, 5, DecodeTime),
            DateTime2SqlType => FixedRaw(type, nullable, 8, DecodeDateTime2),
            DateTimeOffsetSqlType => FixedRaw(type, nullable, 10, DecodeDateTimeOffset),

            // Length-prefixed fixed — 1-byte length prefix even when NOT NULL.
            // bit carries it despite being a 1-byte type (probe-confirmed via
            // AW's plain-bit Production.Document.FolderFlag on 2026-05-15), and
            // uniqueidentifier emits its 0x10 prefix ahead of a payload whose
            // width never varies.
            BitSqlType => Prefixed1(type, 1, static (b, _) => SqlValue.FromBoolean(b[0] != 0)),
            UniqueIdentifierSqlType => Prefixed1(type, 16, static (b, _) => SqlValue.FromGuid(new Guid(b))),

            // Decimal/numeric — see ReadDecimal for the full wire layout (the
            // BCP form prepends an inline precision + scale + sign before the
            // mantissa, which the TDS wire spec does not).
            DecimalSqlType => Simple(type, WireForm.Decimal),

            // 8-byte LE length-prefix types (MAX text/binary, xml, CLR-UDT
            // family). 0xFFFFFFFFFFFFFFFF = NULL; otherwise N bytes inline (no
            // TDS-PLP chunk markers — probe-confirmed against AW on
            // 2026-05-15). The MAX arms precede their bounded counterparts
            // below, which take a 2-byte prefix instead.
            XmlSqlType => EightByte(type, EightBytePayload.Xml),
            GeographySqlType => EightByte(type, EightBytePayload.Geography),
            GeometrySqlType => EightByte(type, EightBytePayload.Geometry),
            HierarchyIdSqlType => EightByte(type, EightBytePayload.HierarchyId),
            VarcharSqlType { length: -1 } => EightByte(type, EightBytePayload.VarcharMax),
            NVarcharSqlType { length: -1 } => EightByte(type, EightBytePayload.NVarcharMax),
            VarbinarySqlType { length: -1 } => EightByte(type, EightBytePayload.VarbinaryMax),

            // Variable-length bounded (text/binary) — 2-byte LE prefix,
            // 0xFFFF = NULL.
            VarcharSqlType or NVarcharSqlType or SystemNameSqlType or NCharSqlType or CharSqlType => Simple(type, WireForm.Varchar2),
            VarbinarySqlType or BinarySqlType => Simple(type, WireForm.Varbinary2),

            _ => throw new NotSupportedException($"BCP decoder doesn't yet handle type {type}."),
        };
    }

    private static ColumnDecoder FixedRaw(SqlType type, bool nullable, int width, ByteSpanDecoder build) =>
        new(type, nullable ? WireForm.FixedRawNullable : WireForm.FixedRaw, width, build, default);

    private static ColumnDecoder Prefixed1(SqlType type, int width, ByteSpanDecoder build) =>
        new(type, WireForm.LengthPrefixed1, width, build, default);

    private static ColumnDecoder EightByte(SqlType type, EightBytePayload payload) =>
        new(type, WireForm.EightByte, 0, null, payload);

    private static ColumnDecoder Simple(SqlType type, WireForm form) =>
        new(type, form, 0, null, default);

    /// <summary>
    /// The wire shape one column's bytes arrive in, resolved by
    /// <see cref="ResolveDecoder"/>.
    /// </summary>
    internal enum WireForm : byte
    {
        /// <summary>Raw bytes at the column's width, no prefix.</summary>
        FixedRaw,
        /// <summary>1-byte prefix (0xFF = NULL, else the width) then raw bytes.</summary>
        FixedRawNullable,
        /// <summary>Same bytes as <see cref="FixedRawNullable"/>, for the types that carry the prefix even when NOT NULL.</summary>
        LengthPrefixed1,
        /// <summary>1-byte prefix then a self-describing payload — see <see cref="ReadDecimal"/>.</summary>
        Decimal,
        /// <summary>8-byte LE prefix then N bytes inline, materialized per <see cref="ColumnDecoder.Payload"/>.</summary>
        EightByte,
        /// <summary>2-byte LE prefix then N bytes of UTF-16-LE text.</summary>
        Varchar2,
        /// <summary>2-byte LE prefix then N raw bytes.</summary>
        Varbinary2,
    }

    /// <summary>
    /// One column's resolved wire shape, reused for every row of the table.
    /// <see cref="Build"/> is non-null exactly for the three fixed-width forms
    /// and <see cref="Width"/> is meaningful only there; <see cref="Payload"/>
    /// is read only by <see cref="WireForm.EightByte"/>.
    /// </summary>
    internal readonly struct ColumnDecoder(SqlType type, WireForm form, int width, ByteSpanDecoder? build, EightBytePayload payload)
    {
        public readonly SqlType Type = type;
        public readonly WireForm Form = form;
        public readonly int Width = width;
        public readonly ByteSpanDecoder? Build = build;
        public readonly EightBytePayload Payload = payload;
    }

    /// <summary>
    /// Distinguishes how an 8-byte-prefixed payload is materialized into a
    /// <see cref="SqlValue"/> — the byte handling and length read are
    /// identical across the 8-byte-prefix family but the final coercion to
    /// a value differs by SqlType.
    /// </summary>
    internal enum EightBytePayload
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

    /// <summary>
    /// Materializes a fixed-width column's raw bytes. Takes the column's
    /// <see cref="SqlType"/> as an argument rather than closing over it so
    /// every implementation is non-capturing and caches into a static.
    /// </summary>
    internal delegate SqlValue ByteSpanDecoder(ReadOnlySpan<byte> bytes, SqlType type);

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
        return build(bytes, type);
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
        return build(bytes, type);
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
    private static SqlValue ReadDecimal(ref PushbackStream stream, SqlType type)
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

    /// <summary>
    /// Payload size up to which <see cref="ReadVarchar2"/> takes a bounded
    /// column's wire bytes off the stack. Those bytes die with the call —
    /// the decoded string is what survives — so a heap array per string per
    /// row buys nothing. Sized in bytes of UTF-16, so this covers a 512-char
    /// value; anything wider falls back to the heap rather than growing the
    /// frame without bound, matching <c>RowEncoder</c>'s scratch rule.
    /// </summary>
    private const int StackTextBytes = 1024;

    private static SqlValue ReadVarchar2(ref PushbackStream stream, SqlType type)
    {
        Span<byte> prefixBytes = stackalloc byte[2];
        stream.ReadExact(prefixBytes);
        var byteLength = BinaryPrimitives.ReadUInt16LittleEndian(prefixBytes);
        if (byteLength == 0xFFFF)
            return SqlValue.Null(type);
        var data = byteLength <= StackTextBytes ? stackalloc byte[byteLength] : new byte[byteLength];
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
