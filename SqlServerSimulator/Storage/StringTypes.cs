using System.Collections.Concurrent;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>varchar(N)</c>: variable-length CP1252 string, declared
/// length 1-8000 bytes. Each <c>(length, collation, coercibility)</c> trio is
/// a distinct interned singleton via <see cref="Get(int, SqlServerSimulator.Collation, SqlServerSimulator.Coercibility)"/>;
/// the length-only <see cref="Get(int)"/> overload returns the
/// (length, <see cref="SqlServerSimulator.Collation.Default"/>, <see cref="SqlServerSimulator.Coercibility.CoercibleDefault"/>)
/// variant used by literal / CAST / parameter contexts that haven't pinned a
/// collation. <see cref="Unspecified"/> (length 0) is the length-unspecified
/// sentinel returned from arithmetic / column resolution paths that haven't
/// pinned a length, and <see cref="MaxForm"/> (length -1) is the LOB
/// <c>varchar(MAX)</c> form.
/// </summary>
internal sealed class VarcharSqlType : SqlType
{
    public readonly short length;

    private readonly SqlServerSimulator.Collation collation;

    private readonly Coercibility coercibility;

    private VarcharSqlType(short length, SqlServerSimulator.Collation collation, Coercibility coercibility)
        : base(SqlTypeCategory.String)
    {
        this.length = length;
        this.collation = collation;
        this.coercibility = coercibility;
    }

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "varchar";

    public override bool IsFixedLength => false;

    public override SqlServerSimulator.Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(SqlServerSimulator.Collation collation, Coercibility coercibility) => Get(this.length, collation, coercibility);

    public override int GetVariableByteCount(SqlValue value) => CharSqlType.Cp1252Encoder.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => CharSqlType.Cp1252Encoder.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVarchar(this, CharSqlType.Cp1252Encoder.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromVarchar((string)raw);

    public override string ToString() => this.length switch
    {
        0 => "varchar",
        -1 => "varchar(MAX)",
        _ => $"varchar({this.length})",
    };

    private static readonly ConcurrentDictionary<(short Length, SqlServerSimulator.Collation Collation, Coercibility Coercibility), VarcharSqlType> cache = new();

    internal static readonly VarcharSqlType Unspecified = Intern(0, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    internal static readonly VarcharSqlType MaxForm = Intern(-1, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    public static VarcharSqlType Get(int length) => Get(length, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    public static VarcharSqlType Get(int length, SqlServerSimulator.Collation collation, Coercibility coercibility) =>
        length is not (0 or SqlType.MaxLengthSentinel) and (< 1 or > 8000)
            ? throw new ArgumentOutOfRangeException(nameof(length), $"varchar length must be 1-8000, 0 (unspecified), or -1 (MAX); got {length}.")
            : Intern((short)length, collation, coercibility);

    private static VarcharSqlType Intern(short length, SqlServerSimulator.Collation collation, Coercibility coercibility) =>
        cache.GetOrAdd((length, collation, coercibility), static key => new VarcharSqlType(key.Length, key.Collation, key.Coercibility));
}

/// <summary>
/// SQL Server's <c>nvarchar(N)</c>: variable-length UTF-16 LE string,
/// declared length 1-4000 code units. Same intern / singleton convention as
/// <see cref="VarcharSqlType"/>; <see cref="Unspecified"/> = length 0,
/// <see cref="MaxForm"/> = length -1.
/// </summary>
internal sealed class NVarcharSqlType : SqlType
{
    public readonly short length;

    private readonly SqlServerSimulator.Collation collation;

    private readonly Coercibility coercibility;

    private NVarcharSqlType(short length, SqlServerSimulator.Collation collation, Coercibility coercibility)
        : base(SqlTypeCategory.String)
    {
        this.length = length;
        this.collation = collation;
        this.coercibility = coercibility;
    }

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "nvarchar";

    public override bool IsFixedLength => false;

    public override SqlServerSimulator.Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(SqlServerSimulator.Collation collation, Coercibility coercibility) => Get(this.length, collation, coercibility);

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNVarchar(this, Encoding.Unicode.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromNVarchar((string)raw);

    public override string ToString() => this.length switch
    {
        0 => "nvarchar",
        -1 => "nvarchar(MAX)",
        _ => $"nvarchar({this.length})",
    };

    private static readonly ConcurrentDictionary<(short Length, SqlServerSimulator.Collation Collation, Coercibility Coercibility), NVarcharSqlType> cache = new();

    internal static readonly NVarcharSqlType Unspecified = Intern(0, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    internal static readonly NVarcharSqlType MaxForm = Intern(-1, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    public static NVarcharSqlType Get(int length) => Get(length, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    public static NVarcharSqlType Get(int length, SqlServerSimulator.Collation collation, Coercibility coercibility) =>
        length is not (0 or SqlType.MaxLengthSentinel) and (< 1 or > 4000)
            ? throw new ArgumentOutOfRangeException(nameof(length), $"nvarchar length must be 1-4000, 0 (unspecified), or -1 (MAX); got {length}.")
            : Intern((short)length, collation, coercibility);

    private static NVarcharSqlType Intern(short length, SqlServerSimulator.Collation collation, Coercibility coercibility) =>
        cache.GetOrAdd((length, collation, coercibility), static key => new NVarcharSqlType(key.Length, key.Collation, key.Coercibility));
}

/// <summary>
/// SQL Server's <c>sysname</c>: <c>nvarchar(128) NOT NULL</c> alias used by
/// the system catalogs. Always carries the server-default collation
/// (<see cref="SqlServerSimulator.Collation.Default"/>) at
/// <see cref="SqlServerSimulator.Coercibility.Implicit"/> rank — sysname
/// columns don't accept a <c>COLLATE</c> clause and never coerce, so a
/// single shared instance is sufficient.
/// </summary>
internal sealed class SystemNameSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override SqlServerSimulator.Collation Collation => SqlServerSimulator.Collation.Default;

    public override Coercibility Coercibility => Coercibility.Implicit;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromSystemName(Encoding.Unicode.GetString(source));

    public override string ToString() => "sysname";
}

/// <summary>
/// SQL Server's <c>varbinary(N)</c>: variable-length raw bytes, declared
/// length 1-8000. Same singleton convention as <see cref="VarcharSqlType"/>;
/// <see cref="Unspecified"/> = length 0, <see cref="MaxForm"/> = length -1.
/// </summary>
internal sealed class VarbinarySqlType : SqlType
{
    public readonly short length;

    private VarbinarySqlType(short length) : base(SqlTypeCategory.Other) => this.length = length;

    public override Type ClrType => typeof(byte[]);

    public override string SqlServerName => "varbinary";

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

    public override string ToString() => this.length switch
    {
        0 => "varbinary",
        -1 => "varbinary(MAX)",
        _ => $"varbinary({this.length})",
    };

    private static readonly ConcurrentDictionary<short, VarbinarySqlType> cache = new();

    internal static readonly VarbinarySqlType Unspecified = new(0);

    internal static readonly VarbinarySqlType MaxForm = new(-1);

    public static VarbinarySqlType Get(int length) =>
        length is 0 ? Unspecified
        : length is SqlType.MaxLengthSentinel ? MaxForm
        : length is < 1 or > 8000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"varbinary length must be 1-8000, 0 (unspecified), or -1 (MAX); got {length}.")
            : cache.GetOrAdd((short)length, l => new VarbinarySqlType(l));
}

/// <summary>
/// SQL Server's deprecated <c>text</c> type: variable-length CP1252 string,
/// stored off-row in LOB pages. Supports <c>LIKE</c>, <c>IS NULL</c>, and
/// <c>CAST</c>/<c>CONVERT</c> to <c>varchar</c>/<c>nvarchar</c>; comparison
/// (<c>=</c>, <c>&lt;&gt;</c>, etc.) raises Msg 402, and ORDER BY / GROUP BY
/// / DISTINCT raise Msg 306. Encoded identically to <c>varchar</c> (CP1252
/// bytes); the type identity is what gates the operation restrictions.
/// Carries a collation for parity with the other string types — text columns
/// inherit the database default in real SQL Server and accept a per-column
/// <c>COLLATE</c> clause; the simulator's single shared instance models the
/// default case (the deprecated type isn't worth interning per collation).
/// </summary>
internal sealed class TextSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override bool IsLob => true;

    public override SqlServerSimulator.Collation Collation => SqlServerSimulator.Collation.Default;

    public override Coercibility Coercibility => Coercibility.Implicit;

    public override int GetVariableByteCount(SqlValue value) => CharSqlType.Cp1252Encoder.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => CharSqlType.Cp1252Encoder.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromText(CharSqlType.Cp1252Encoder.GetString(source));

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

    public override SqlServerSimulator.Collation Collation => SqlServerSimulator.Collation.Default;

    public override Coercibility Coercibility => Coercibility.Implicit;

    public override int GetVariableByteCount(SqlValue value) => Encoding.Unicode.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNText(Encoding.Unicode.GetString(source));

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

    public override string ToString() => "image";
}

/// <summary>
/// SQL Server's <c>char(N)</c>: fixed-length CP1252 string, declared length
/// 1-8000 bytes. Each <c>(length, collation, coercibility)</c> trio is a
/// distinct interned singleton; the length-only <see cref="Get(int)"/> picks
/// the (length, <see cref="SqlServerSimulator.Collation.Default"/>, <see cref="SqlServerSimulator.Coercibility.CoercibleDefault"/>)
/// variant. Stored values are right-padded with U+0020 to the declared
/// length, both in memory and on disk; comparison and equality strip
/// trailing spaces via the type's collation so <c>char(5) 'abc  '</c> equals
/// <c>varchar 'abc'</c>.
/// </summary>
internal sealed class CharSqlType : SqlType
{
    public readonly short length;

    private readonly SqlServerSimulator.Collation collation;

    private readonly Coercibility coercibility;

    private CharSqlType(short length, SqlServerSimulator.Collation collation, Coercibility coercibility)
        : base(SqlTypeCategory.String)
    {
        this.length = length;
        this.collation = collation;
        this.coercibility = coercibility;
    }

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "char";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.length;

    public override SqlServerSimulator.Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(SqlServerSimulator.Collation collation, Coercibility coercibility) => Get(this.length, collation, coercibility);

    public override int Encode(SqlValue value, Span<byte> destination) => Cp1252Encoder.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromChar(this, Cp1252Encoder.GetString(source));

    public override string ToString() => $"char({this.length})";

    public static CharSqlType Get(int length) => Get(length, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    public static CharSqlType Get(int length, SqlServerSimulator.Collation collation, Coercibility coercibility) =>
        length is < 1 or > 8000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"char length must be 1-8000; got {length}.")
            : cache.GetOrAdd(((short)length, collation, coercibility), static key => new CharSqlType(key.Item1, key.Item2, key.Item3));

    private static readonly ConcurrentDictionary<(short, SqlServerSimulator.Collation, Coercibility), CharSqlType> cache = new();

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
/// length 1-4000 code units (storage 2N bytes). Each
/// <c>(length, collation, coercibility)</c> trio is a distinct interned
/// singleton. Padding and trailing-space-aware comparison work identically
/// to <see cref="CharSqlType"/>.
/// </summary>
internal sealed class NCharSqlType : SqlType
{
    public readonly short length;

    private readonly SqlServerSimulator.Collation collation;

    private readonly Coercibility coercibility;

    private NCharSqlType(short length, SqlServerSimulator.Collation collation, Coercibility coercibility)
        : base(SqlTypeCategory.String)
    {
        this.length = length;
        this.collation = collation;
        this.coercibility = coercibility;
    }

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "nchar";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.length * 2;

    public override SqlServerSimulator.Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(SqlServerSimulator.Collation collation, Coercibility coercibility) => Get(this.length, collation, coercibility);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.Unicode.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNChar(this, Encoding.Unicode.GetString(source));

    public override string ToString() => $"nchar({this.length})";

    public static NCharSqlType Get(int length) => Get(length, SqlServerSimulator.Collation.Default, Coercibility.CoercibleDefault);

    public static NCharSqlType Get(int length, SqlServerSimulator.Collation collation, Coercibility coercibility) =>
        length is < 1 or > 4000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"nchar length must be 1-4000; got {length}.")
            : cache.GetOrAdd(((short)length, collation, coercibility), static key => new NCharSqlType(key.Item1, key.Item2, key.Item3));

    private static readonly ConcurrentDictionary<(short, SqlServerSimulator.Collation, Coercibility), NCharSqlType> cache = new();
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

    public override string ToString() => $"binary({this.length})";

    public static BinarySqlType Get(int length) =>
        length is < 1 or > 8000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"binary length must be 1-8000; got {length}.")
            : cache.GetOrAdd((short)length, l => new BinarySqlType(l));

    private static readonly ConcurrentDictionary<short, BinarySqlType> cache = new();
}
