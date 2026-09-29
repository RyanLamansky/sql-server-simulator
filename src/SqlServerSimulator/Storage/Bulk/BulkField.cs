using System.Buffers.Binary;
using System.Text;
using SqlServerSimulator.Storage.Bacpac;

namespace SqlServerSimulator.Storage.Bulk;

/// <summary>How a data-file field's bytes are laid out and decode.</summary>
internal enum BulkHostType : byte
{
    /// <summary>Text in the file's character set: <c>SQLCHAR</c>.</summary>
    Char,

    /// <summary>UTF-16 text: <c>SQLNCHAR</c>.</summary>
    NChar,

    /// <summary>Raw bytes: <c>SQLBINARY</c>, a native <c>varbinary</c>.</summary>
    Binary,

    /// <summary>A value in its native storage form, typed by <see cref="BulkField.NativeType"/>.</summary>
    Native,
}

/// <summary>
/// One field of a data file as a format file (or a native load's table)
/// describes it: a length prefix, a fixed length or a terminator, how the
/// bytes decode, and which destination column receives it.
/// </summary>
internal sealed class BulkField(BulkHostType host, SqlType? nativeType, int prefixLength, int length, byte[] terminator, int target, string name)
{
    public readonly BulkHostType Host = host;

    /// <summary>The native value type, for <see cref="BulkHostType.Native"/>.</summary>
    public readonly SqlType? NativeType = nativeType;

    /// <summary>The length prefix's width in bytes: 0, 1, 2, 4 or 8.</summary>
    public readonly int PrefixLength = prefixLength;

    /// <summary>The field's width when neither a prefix nor a terminator bounds it; else its declared maximum.</summary>
    public readonly int Length = length;

    /// <summary>The terminator's bytes, empty when none.</summary>
    public readonly byte[] Terminator = terminator;

    /// <summary>The 0-based destination column, or -1 for a field nothing receives.</summary>
    public readonly int Target = target;

    /// <summary>The destination column's name as the format file gives it.</summary>
    public readonly string Name = name;

    /// <summary>
    /// A text field's terminator in the byte form its encoding writes:
    /// UTF-8 for <see cref="BulkHostType.Char"/>, UTF-16 for
    /// <see cref="BulkHostType.NChar"/>.
    /// </summary>
    public static byte[] EncodeTerminator(BulkHostType host, string terminator) =>
        host == BulkHostType.NChar ? Encoding.Unicode.GetBytes(terminator) : Encoding.UTF8.GetBytes(terminator);

    /// <summary>
    /// The native layout <c>bcp -n</c> writes for a column of
    /// its type (probed 2026-09-29 against SQL Server 2025's
    /// <c>bcp</c>): fixed-width values raw when NOT NULL and behind a one-byte
    /// length when nullable; <c>bit</c>, <c>uniqueidentifier</c> and
    /// <c>decimal</c> behind a one-byte length always; bounded strings and
    /// binaries behind a two-byte length and the MAX and LOB types behind an
    /// eight-byte one. Character data is the column's code page, or UTF-16
    /// under <c>widenative</c>.
    /// </summary>
    public static BulkField ForNativeColumn(HeapColumn column, int target, bool wide)
    {
        var type = column.Type;
        var nullable = column.Nullable;
        return type switch
        {
            Int32SqlType or RealSqlType or SmallMoneySqlType or SmallDateTimeSqlType => Fixed(4),
            BigIntSqlType or FloatSqlType or MoneySqlType or DateTimeSqlType => Fixed(8),
            SmallIntSqlType => Fixed(2),
            TinyIntSqlType => Fixed(1),
            DateSqlType => Fixed(3),
            TimeSqlType => Fixed(5),
            DateTime2SqlType => Fixed(8),
            DateTimeOffsetSqlType => Fixed(10),
            RowVersionSqlType => new(BulkHostType.Binary, null, 0, 8, [], target, column.Name),
            BitSqlType => new(BulkHostType.Native, type, 1, 1, [], target, column.Name),
            UniqueIdentifierSqlType => new(BulkHostType.Native, type, 1, 16, [], target, column.Name),
            DecimalSqlType => new(BulkHostType.Native, type, 1, 19, [], target, column.Name),
            VarcharSqlType { length: -1 } or TextSqlType => new(wide ? BulkHostType.NChar : BulkHostType.Char, null, 8, 0, [], target, column.Name),
            NVarcharSqlType { length: -1 } or NTextSqlType or XmlSqlType or JsonSqlType => new(BulkHostType.NChar, null, 8, 0, [], target, column.Name),
            VarbinarySqlType { length: -1 } or ImageSqlType or HierarchyIdSqlType or SpatialSqlType or ClrUdtSqlType or VectorSqlType => new(BulkHostType.Binary, null, 8, 0, [], target, column.Name),
            CharSqlType or VarcharSqlType => new(wide ? BulkHostType.NChar : BulkHostType.Char, null, 2, 0, [], target, column.Name),
            NCharSqlType or NVarcharSqlType or SystemNameSqlType => new(BulkHostType.NChar, null, 2, 0, [], target, column.Name),
            BinarySqlType or VarbinarySqlType => new(BulkHostType.Binary, null, 2, 0, [], target, column.Name),
            _ => throw new NotSupportedException($"A native bulk load of a {type.SqlServerName} column isn't modeled."),
        };

        BulkField Fixed(int width) => new(BulkHostType.Native, type, nullable ? 1 : 0, width, [], target, column.Name);
    }

    /// <summary>
    /// Decodes one field's bytes: text for the character hosts, a value of
    /// <see cref="NativeType"/> or raw bytes otherwise.
    /// </summary>
    public object Decode(ReadOnlySpan<byte> bytes, Encoding charEncoding) => this.Host switch
    {
        BulkHostType.Char => charEncoding.GetString(bytes),
        BulkHostType.NChar => Encoding.Unicode.GetString(bytes),
        BulkHostType.Binary => SqlValue.FromVarbinary(bytes.ToArray()),
        _ => DecodeNative(bytes, this.NativeType!),
    };

    private static SqlValue DecodeNative(ReadOnlySpan<byte> bytes, SqlType type)
    {
        if (type is DecimalSqlType)
        {
            if (bytes.Length < 3)
                throw SimulatedSqlException.BulkUnexpectedEndOfFile();
            var scale = bytes[1];
            Span<byte> padded = stackalloc byte[16];
            bytes[3..][..Math.Min(16, bytes.Length - 3)].CopyTo(padded);
            var magnitude = BinaryPrimitives.ReadUInt128LittleEndian(padded);
            var precision = (byte)Math.Clamp((int)bytes[0], 1, 38);
            return SqlValue.FromDecimal(SqlType.GetDecimal(precision, scale), Decimal38.FromParts(magnitude, isNegative: bytes[2] == 0 && magnitude != UInt128.Zero, scale));
        }
        var decoder = BcpRowReader.ResolveDecoders([new HeapColumn("", type, maxLength: null, nullable: true)], [])[0];
        if (decoder.Build is not { } build || bytes.Length != decoder.Width)
            throw SimulatedSqlException.BulkUnexpectedEndOfFile();
        return build(bytes, type);
    }
}
