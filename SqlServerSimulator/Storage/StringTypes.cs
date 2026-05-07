using System.Collections.Concurrent;
using System.Text;

namespace SqlServerSimulator.Storage;

internal sealed class VarcharSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => CharSqlType.Cp1252Encoder.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => CharSqlType.Cp1252Encoder.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVarchar(CharSqlType.Cp1252Encoder.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromVarchar((string)raw);

    public override string ToString() => "varchar";
}

internal sealed class NVarcharSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNVarchar(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromNVarchar((string)raw);

    public override string ToString() => "nvarchar";
}

internal sealed class SystemNameSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromSystemName(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromSystemName((string)raw);

    public override string ToString() => "sysname";
}

internal sealed class VarbinarySqlType() : SqlType(SqlTypeCategory.Other)
{
    public override Type ClrType => typeof(byte[]);

    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => value.AsBytes.Length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsBytes;
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVarbinary(source.ToArray());

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromVarbinary((byte[])raw);

    public override string ToString() => "varbinary";
}

/// <summary>
/// SQL Server's deprecated <c>text</c> type: variable-length CP1252 string,
/// stored off-row in LOB pages. Supports <c>LIKE</c>, <c>IS NULL</c>, and
/// <c>CAST</c>/<c>CONVERT</c> to <c>varchar</c>/<c>nvarchar</c>; comparison
/// (<c>=</c>, <c>&lt;&gt;</c>, etc.) raises Msg 402, and ORDER BY / GROUP BY
/// / DISTINCT raise Msg 306. Encoded identically to <c>varchar</c> (CP1252
/// bytes); the type identity is what gates the operation restrictions.
/// </summary>
internal sealed class TextSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override bool IsLob => true;

    public override int GetVariableByteCount(SqlValue value) => CharSqlType.Cp1252Encoder.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => CharSqlType.Cp1252Encoder.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromText(CharSqlType.Cp1252Encoder.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromText((string)raw);

    public override string ToString() => "text";
}

/// <summary>
/// SQL Server's deprecated <c>ntext</c> type: variable-length UTF-16 LE
/// string, stored off-row in LOB pages. Same operation restrictions as
/// <see cref="TextSqlType"/>.
/// </summary>
internal sealed class NTextSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override bool IsLob => true;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNText(Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromNText((string)raw);

    public override string ToString() => "ntext";
}

/// <summary>
/// SQL Server's deprecated <c>image</c> type: variable-length raw bytes,
/// stored off-row in LOB pages. Same operation restrictions as
/// <see cref="TextSqlType"/>.
/// </summary>
internal sealed class ImageSqlType() : SqlType(SqlTypeCategory.Other)
{
    public override Type ClrType => typeof(byte[]);

    public override bool IsFixedLength => false;

    public override bool IsLob => true;

    public override int GetVariableByteCount(SqlValue value) => value.AsBytes.Length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsBytes;
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromImage(source.ToArray());

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromImage((byte[])raw);

    public override string ToString() => "image";
}

/// <summary>
/// SQL Server's <c>char(N)</c>: fixed-length CP1252 string, declared length
/// 1-8000 bytes. Each declared length is a distinct singleton (mirroring the
/// <c>decimal(p, s)</c> pattern); reference equality flows through the type-
/// identity model used elsewhere. Stored values are right-padded with U+0020
/// to the declared length, both in memory and on disk; comparison and equality
/// strip trailing spaces via the shared collation path so <c>char(5) 'abc  '</c>
/// equals <c>varchar 'abc'</c>.
/// </summary>
internal sealed class CharSqlType(short length) : SqlType(SqlTypeCategory.String)
{
    public readonly short length = length;

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "char";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.length;

    public override int Encode(SqlValue value, Span<byte> destination) => Cp1252Encoder.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromChar(this, Cp1252Encoder.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromChar(this, (string)raw);

    public override string ToString() => $"char({this.length})";

    public static CharSqlType Get(int length) =>
        length is < 1 or > 8000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"char length must be 1-8000; got {length}.")
            : cache.GetOrAdd((short)length, l => new CharSqlType(l));

    private static readonly ConcurrentDictionary<short, CharSqlType> cache = new();

    /// <summary>Shared CP1252 encoder; identical configuration to <see cref="VarcharSqlType"/>.</summary>
    internal static readonly Encoding Cp1252Encoder = LoadCp1252();

    private static Encoding LoadCp1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            1252,
            new EncoderReplacementFallback("?"),
            DecoderFallback.ReplacementFallback);
    }
}

/// <summary>
/// SQL Server's <c>nchar(N)</c>: fixed-length UTF-16 LE string, declared
/// length 1-4000 code units (storage 2N bytes). Each declared length is a
/// distinct singleton. Padding and trailing-space-aware comparison work
/// identically to <see cref="CharSqlType"/>.
/// </summary>
internal sealed class NCharSqlType(short length) : SqlType(SqlTypeCategory.String)
{
    public readonly short length = length;

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "nchar";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.length * 2;

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNChar(this, Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromNChar(this, (string)raw);

    public override string ToString() => $"nchar({this.length})";

    public static NCharSqlType Get(int length) =>
        length is < 1 or > 4000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"nchar length must be 1-4000; got {length}.")
            : cache.GetOrAdd((short)length, l => new NCharSqlType(l));

    private static readonly ConcurrentDictionary<short, NCharSqlType> cache = new();
}

/// <summary>
/// SQL Server's <c>binary(N)</c>: fixed-length raw bytes, declared length
/// 1-8000. Each declared length is a distinct singleton. Stored payloads are
/// right-padded with <c>0x00</c> to the declared length.
/// </summary>
internal sealed class BinarySqlType(short length) : SqlType(SqlTypeCategory.Other)
{
    public readonly short length = length;

    public override Type ClrType => typeof(byte[]);

    public override string SqlServerName => "binary";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsBytes;
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromBinary(this, source.ToArray());

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromBinary(this, (byte[])raw);

    public override string ToString() => $"binary({this.length})";

    public static BinarySqlType Get(int length) =>
        length is < 1 or > 8000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"binary length must be 1-8000; got {length}.")
            : cache.GetOrAdd((short)length, l => new BinarySqlType(l));

    private static readonly ConcurrentDictionary<short, BinarySqlType> cache = new();
}
