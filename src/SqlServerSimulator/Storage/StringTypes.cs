using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>varchar(N)</c>: variable-length string in the collation's
/// own ANSI code page, declared length 1-8000 <em>bytes</em> (so a DBCS or
/// UTF-8 code page fits fewer than N characters).
/// Each <c>(length, collation, coercibility)</c> trio is a distinct interned singleton via <see cref="Get(int, SqlServerSimulator.Collation, SqlServerSimulator.Coercibility)"/>;
/// the length-unspecified sentinel is <c>Get(0, …)</c> (returned from
/// arithmetic / column resolution paths that haven't pinned a length) and the
/// LOB <c>varchar(MAX)</c> form is <c>Get(-1, …)</c>.
/// </summary>
internal sealed class VarcharSqlType : SqlType
{
    public readonly short length;

    private readonly Collation collation;

    private readonly Coercibility coercibility;

    private VarcharSqlType(short length, Collation collation, Coercibility coercibility)
        : base(SqlTypeCategory.String)
    {
        // Runs once per interned (length, collation, coercibility) triple, so
        // the Msg 459 gate costs nothing on the cache-hit path.
        collation.RejectIfUnicodeOnly();
        this.length = length;
        this.collation = collation;
        this.coercibility = coercibility;
    }

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "varchar";

    public override bool IsFixedLength => false;

    public override Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(Collation collation, Coercibility coercibility) => Get(this.length, collation.ForVarcharStorage(), coercibility);

    public override int GetVariableByteCount(SqlValue value) => this.collation.StorageEncoding.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => this.collation.StorageEncoding.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVarchar(this, this.collation.StorageEncoding.GetString(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromVarchar((string)raw);

    public override string ToString() => this.length switch
    {
        -1 => "varchar(MAX)",
        0 => "varchar",
        _ => $"varchar({this.length})",
    };

    private static readonly ConcurrentDictionary<(short Length, Collation Collation, Coercibility Coercibility), VarcharSqlType> cache = new();

    public static VarcharSqlType Get(int length, Collation collation, Coercibility coercibility) =>
        length is not (0 or SqlType.MaxLengthSentinel) and (< 1 or > 8000)
            ? throw new ArgumentOutOfRangeException(nameof(length), $"varchar length must be 1-8000, 0 (unspecified), or -1 (MAX); got {length}.")
            : cache.GetOrAdd(((short)length, collation, coercibility), static key => new VarcharSqlType(key.Length, key.Collation, key.Coercibility));
}

/// <summary>
/// SQL Server's <c>nvarchar(N)</c>: variable-length UTF-16 LE string,
/// declared length 1-4000 code units. Same intern / singleton convention as
/// <see cref="VarcharSqlType"/>; length 0 is the unspecified sentinel and
/// length -1 is the LOB <c>nvarchar(MAX)</c> form.
/// </summary>
internal sealed class NVarcharSqlType : SqlType
{
    public readonly short length;

    private readonly Collation collation;

    private readonly Coercibility coercibility;

    private NVarcharSqlType(short length, Collation collation, Coercibility coercibility)
        : base(SqlTypeCategory.String)
    {
        this.length = length;
        this.collation = collation;
        this.coercibility = coercibility;
    }

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "nvarchar";

    public override bool IsFixedLength => false;

    public override Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(Collation collation, Coercibility coercibility) => Get(this.length, collation, coercibility);

    public override int GetVariableByteCount(SqlValue value) => value.AsString.Length * 2;

    public override int Encode(SqlValue value, Span<byte> destination) => SystemNameSqlType.Utf16LeEncode(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNVarchar(this, SystemNameSqlType.Utf16LeDecode(source));

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromNVarchar((string)raw);

    public override string ToString() => this.length switch
    {
        -1 => "nvarchar(MAX)",
        0 => "nvarchar",
        _ => $"nvarchar({this.length})",
    };

    private static readonly ConcurrentDictionary<(short Length, Collation Collation, Coercibility Coercibility), NVarcharSqlType> cache = new();

    public static NVarcharSqlType Get(int length, Collation collation, Coercibility coercibility) =>
        length is not (0 or SqlType.MaxLengthSentinel) and (< 1 or > 4000)
            ? throw new ArgumentOutOfRangeException(nameof(length), $"nvarchar length must be 1-4000, 0 (unspecified), or -1 (MAX); got {length}.")
            : cache.GetOrAdd(((short)length, collation, coercibility), static key => new NVarcharSqlType(key.Length, key.Collation, key.Coercibility));
}

/// <summary>
/// SQL Server's <c>sysname</c>: <c>nvarchar(128) NOT NULL</c> alias used by
/// the system catalogs. Always carries the server-default collation
/// (<see cref="Collation.Baseline"/>) at
/// <see cref="Coercibility.Implicit"/> rank — sysname
/// columns don't accept a <c>COLLATE</c> clause and never coerce, so a
/// single shared instance is sufficient.
/// </summary>
internal sealed class SystemNameSqlType() : SqlType(SqlTypeCategory.String)
{
    public override Type ClrType => typeof(string);

    public override bool IsFixedLength => false;

    public override Collation Collation => SqlServerSimulator.Collation.Baseline;

    public override Coercibility Coercibility => Coercibility.Implicit;

    public override int GetVariableByteCount(SqlValue value) => value.AsString.Length * 2;

    public override int Encode(SqlValue value, Span<byte> destination) => Utf16LeEncode(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromSystemName(Utf16LeDecode(source));

    public override string ToString() => "sysname";

    /// <summary>
    /// Direct UTF-16 LE byte-copy of a .NET string, bypassing
    /// <see cref="Encoding.Unicode"/>'s <see cref="EncoderReplacementFallback"/>
    /// — which silently rewrites lone surrogates to <c>U+FFFD</c>. Real
    /// SQL Server preserves lone surrogates end-to-end (probe-confirmed:
    /// <c>SUBSTRING(N'😀X', 1, 1)</c> on a non-<c>_SC_</c> column returns
    /// the lone high surrogate, not <c>U+FFFD</c>); the simulator's
    /// nvarchar / nchar / sysname / ntext encoders all reuse this helper
    /// so the same fidelity applies to every UTF-16 storage path.
    /// </summary>
    internal static int Utf16LeEncode(string value, Span<byte> destination)
    {
        var src = MemoryMarshal.AsBytes(value.AsSpan());
        src.CopyTo(destination);
        return src.Length;
    }

    /// <summary>Inverse of <see cref="Utf16LeEncode"/>: reinterprets the byte span as <c>char</c>s without surrogate validation.</summary>
    internal static string Utf16LeDecode(ReadOnlySpan<byte> source) =>
        new(MemoryMarshal.Cast<byte, char>(source));
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
        -1 => "varbinary(MAX)",
        0 => "varbinary",
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

    public override Collation Collation => SqlServerSimulator.Collation.Baseline;

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

    public override Collation Collation => SqlServerSimulator.Collation.Baseline;

    public override Coercibility Coercibility => Coercibility.Implicit;

    public override int GetVariableByteCount(SqlValue value) => value.AsString.Length * 2;

    public override int Encode(SqlValue value, Span<byte> destination) => SystemNameSqlType.Utf16LeEncode(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNText(SystemNameSqlType.Utf16LeDecode(source));

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
/// SQL Server's <c>char(N)</c>: fixed-length string in the collation's own
/// ANSI code page, declared length 1-8000 bytes. Each <c>(length, collation, coercibility)</c> trio is a
/// distinct interned singleton. Stored values are right-padded with U+0020
/// to the declared length, both in memory and on disk; comparison and
/// equality strip trailing spaces via the type's collation so
/// <c>char(5) 'abc  '</c> equals <c>varchar 'abc'</c>.
/// </summary>
internal sealed class CharSqlType : SqlType
{
    public readonly short length;

    private readonly Collation collation;

    private readonly Coercibility coercibility;

    private CharSqlType(short length, Collation collation, Coercibility coercibility)
        : base(SqlTypeCategory.String)
    {
        // Interned per triple, so the Msg 459 gate runs once per pairing.
        collation.RejectIfUnicodeOnly();
        this.length = length;
        this.collation = collation;
        this.coercibility = coercibility;
    }

    public override Type ClrType => typeof(string);

    public override string SqlServerName => "char";

    public override bool IsFixedLength => true;

    public override int FixedLength => this.length;

    public override Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(Collation collation, Coercibility coercibility) => Get(this.length, collation.ForVarcharStorage(), coercibility);

    public override int Encode(SqlValue value, Span<byte> destination) => this.collation.StorageEncoding.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromChar(this, this.collation.StorageEncoding.GetString(source));

    public override string ToString() => $"char({this.length})";

    public static CharSqlType Get(int length, Collation collation, Coercibility coercibility) =>
        length is < 1 or > 8000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"char length must be 1-8000; got {length}.")
            : cache.GetOrAdd(((short)length, collation, coercibility), static key => new CharSqlType(key.Item1, key.Item2, key.Item3));

    private static readonly ConcurrentDictionary<(short, Collation, Coercibility), CharSqlType> cache = new();

    /// <summary>
    /// Shared CP1252 encoder — the default collation's storage encoding, and
    /// the interned instance <see cref="Collation.AnsiEncoding"/> returns for
    /// code page 1252. Also the one place the code-pages provider is
    /// registered, which every other ANSI code page depends on.
    /// </summary>
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

    private readonly Collation collation;

    private readonly Coercibility coercibility;

    private NCharSqlType(short length, Collation collation, Coercibility coercibility)
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

    public override Collation Collation => this.collation;

    public override Coercibility Coercibility => this.coercibility;

    public override SqlType WithCollation(Collation collation, Coercibility coercibility) => Get(this.length, collation, coercibility);

    public override int Encode(SqlValue value, Span<byte> destination) => SystemNameSqlType.Utf16LeEncode(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromNChar(this, SystemNameSqlType.Utf16LeDecode(source));

    public override string ToString() => $"nchar({this.length})";

    public static NCharSqlType Get(int length, Collation collation, Coercibility coercibility) =>
        length is < 1 or > 4000
            ? throw new ArgumentOutOfRangeException(nameof(length), $"nchar length must be 1-4000; got {length}.")
            : cache.GetOrAdd(((short)length, collation, coercibility), static key => new NCharSqlType(key.Item1, key.Item2, key.Item3));

    private static readonly ConcurrentDictionary<(short, Collation, Coercibility), NCharSqlType> cache = new();
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
