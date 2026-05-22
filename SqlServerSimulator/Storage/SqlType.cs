using SqlServerSimulator.Parser.Tokens;
using System.Data;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Type system for the storage layer. Each <see cref="SqlType"/> owns its own
/// byte representation via <see cref="Encode"/> and <see cref="Decode"/>, so
/// the row-level encoder does not need to know per-type layout details.
/// </summary>
internal abstract partial class SqlType
{
    private protected SqlType(SqlTypeCategory category)
    {
        this.Category = category;
    }

    /// <summary>
    /// Coarse classification used by <see cref="Promote"/> and the binary-
    /// expression dispatchers to dispatch in a single jump-table-friendly
    /// step instead of repeated per-call category checks. Each concrete
    /// type pins its category at construction.
    /// </summary>
    public readonly SqlTypeCategory Category;

    /// <summary>
    /// CLR type that an out-of-the-box, untyped accessor on the data reader
    /// returns for this SQL type. Used by <see cref="System.Data.Common.DbDataReader.GetFieldType"/>
    /// to mirror SqlClient's per-column type metadata. The reader is the
    /// only consumer that has to satisfy linker-aware annotations
    /// (<c>DynamicallyAccessedMembers</c>); the concrete types here are a
    /// closed set of well-known BCL types, so the suppression lives at the
    /// reader and concrete <c>ClrType</c> overrides stay annotation-free.
    /// </summary>
    public abstract Type ClrType { get; }

    /// <summary>
    /// Bare SQL Server type name, without parameterization (e.g. <c>"decimal"</c>
    /// not <c>"decimal(18,2)"</c>; <c>"varchar"</c> not <c>"varchar(50)"</c>).
    /// Used by <see cref="System.Data.Common.DbDataReader.GetDataTypeName"/>,
    /// which mirrors SqlClient's documented behavior of returning the
    /// catalog type name with no decoration. Defaults to <see cref="object.ToString"/>;
    /// parameterized types override to drop the parens.
    /// </summary>
    public virtual string SqlServerName => this.ToString()!;

    /// <summary>
    /// True for types whose stored bytes are a constant width, regardless of value.
    /// </summary>
    public abstract bool IsFixedLength { get; }

    /// <summary>
    /// The collation associated with this type instance, or <see langword="null"/>
    /// for non-string types. Set when a string type is bound to a column
    /// declaration's <c>COLLATE</c> clause, after a <c>COLLATE</c> postfix on
    /// an expression, or any other time the simulator pins the comparison
    /// rules. <see langword="null"/> on the default-singleton path means
    /// "fall through to <see cref="Collation.Baseline"/>"
    /// at comparison time — matching the simulator's historical behavior
    /// before per-type collation was wired through.
    /// </summary>
    public virtual Collation? Collation => null;

    /// <summary>
    /// SQL Server's collation-precedence rank for this type instance. Drives
    /// Msg 468 / Msg 457 resolution when two string operands meet with
    /// different collations. Non-string types and string types whose
    /// collation hasn't been pinned report <see cref="Coercibility.CoercibleDefault"/>.
    /// </summary>
    public virtual Coercibility Coercibility => Coercibility.CoercibleDefault;

    /// <summary>
    /// Returns the interned variant of this string type carrying the given
    /// collation and coercibility. Non-string types and string types whose
    /// instance shape doesn't intern per collation (sysname, text, ntext)
    /// return <see langword="this"/> unchanged — the simulator doesn't model
    /// per-collation variants for those types. Used by <c>CollateExpression</c>
    /// to apply an explicit COLLATE postfix, by column resolution to pin
    /// declared collation onto a column's type, and by expression-result-type
    /// propagation to forward a resolved collation.
    /// </summary>
    public virtual SqlType WithCollation(Collation collation, Coercibility coercibility) => this;

    /// <summary>
    /// True for types that always store their content off-row in a LOB page
    /// chain — currently <c>text</c>, <c>ntext</c>, <c>image</c>. The MAX
    /// variants of <c>varchar</c>/<c>nvarchar</c>/<c>varbinary</c> are LOB-
    /// eligible at the <em>column</em> level (when <c>HeapColumn.MaxLength</c>
    /// is the <see cref="MaxLengthSentinel"/>) but the <see cref="SqlType"/>
    /// instance itself isn't always-LOB; row-level decisions consult both
    /// signals via <c>HeapColumn.IsLob</c>.
    /// </summary>
    public virtual bool IsLob => false;

    /// <summary>
    /// Sentinel value used in <see cref="HeapColumn.MaxLength"/> /
    /// <c>declaredMaxLength</c> / cast <c>targetMaxLength</c> to signal MAX
    /// length for <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c> /
    /// <c>varbinary(MAX)</c>. <c>text</c>, <c>ntext</c>, and <c>image</c> are
    /// always-LOB and don't accept a length spec; their <see cref="HeapColumn.MaxLength"/>
    /// is also set to the sentinel for symmetry, even though the column
    /// declaration didn't carry an explicit MAX.
    /// </summary>
    public const int MaxLengthSentinel = -1;

    /// <summary>
    /// Byte width for fixed-length types. Throws for variable-length types.
    /// </summary>
    public virtual int FixedLength => throw new NotSupportedException($"{this} is variable-length; FixedLength is undefined.");

    /// <summary>
    /// Bytes a non-NULL value contributes to a row's variable-length data area.
    /// Throws for fixed-length types (whose values live in the fixed section instead).
    /// </summary>
    public virtual int GetVariableByteCount(SqlValue value) => throw new NotSupportedException($"{this} is fixed-length; GetVariableByteCount is undefined.");

    /// <summary>
    /// Writes a non-NULL value's bytes to <paramref name="destination"/>.
    /// NULL handling is the row encoder's responsibility (via the row-level NULL bitmap).
    /// Virtual so types whose row representation isn't a contiguous byte run
    /// (currently only <see cref="BitSqlType"/>, which is bit-packed by
    /// <c>RowEncoder</c>) can opt out — the throwing default acts as a
    /// tripwire if anyone ever routes such a type through the standard path.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public virtual int Encode(SqlValue value, Span<byte> destination) =>
        throw new NotSupportedException($"{this} doesn't participate in standalone byte encoding (e.g. bit values are packed by RowEncoder).");

    /// <summary>
    /// Reads bytes and reconstructs a non-NULL value.
    /// For fixed-length types, <paramref name="source"/>'s length is the type's fixed width.
    /// For variable-length types, <paramref name="source"/>'s length is the value's byte count.
    /// Virtual with a throwing default for the same reason as <see cref="Encode"/>.
    /// </summary>
    public virtual SqlValue Decode(ReadOnlySpan<byte> source) =>
        throw new NotSupportedException($"{this} doesn't participate in standalone byte decoding (e.g. bit values are unpacked by RowDecoder).");

    /// <summary>
    /// Converts a non-NULL CLR parameter value into a typed <see cref="SqlValue"/>
    /// of this type. Used at the <c>DbParameter</c> boundary; NULL handling
    /// is the caller's responsibility.
    /// Virtual so types that aren't reachable through <c>DbParameter.DbType</c>
    /// (the only mapping path consumers use) can opt out — the throwing
    /// default acts as a tripwire if the parameter-binding path ever starts
    /// dispatching to one of them. The unreachable set today: <c>char(N)</c>,
    /// <c>nchar(N)</c>, <c>binary(N)</c>, <c>text</c>, <c>ntext</c>,
    /// <c>image</c>, <c>rowversion</c>, <c>sysname</c>, <c>smallmoney</c> —
    /// none of which appear in <see cref="GetByDbType"/>'s output.
    /// </summary>
    /// <exception cref="NotSupportedException">No conversion exists from <paramref name="raw"/>'s CLR type to this <see cref="SqlType"/>.</exception>
    public virtual SqlValue ConvertParameter(object raw) =>
        throw new NotSupportedException($"{this} isn't reachable as a parameter type via DbParameter.DbType; ConvertParameter is unreachable in normal binding.");

    /// <summary>
    /// SQL Server data-type precedence for implicit conversion. Higher means
    /// "wins" when a binary expression must pick a common type. The numeric
    /// values are simulator-internal; only their relative ordering matters.
    /// Within the numeric family the SQL Server chart orders
    /// <c>float &gt; real &gt; decimal/numeric &gt; money &gt; smallmoney
    /// &gt; bigint &gt; int &gt; smallint &gt; tinyint &gt; bit</c>
    /// (<c>decimal</c> and <c>numeric</c> share a slot; <c>numeric</c> is
    /// just an alias).
    /// </summary>
    /// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/data-types/data-type-precedence-transact-sql</remarks>
    public int Precedence => this switch
    {
        _ when this == HierarchyId => 17,
        XmlSqlType => 17,
        SpatialSqlType => 17,
        _ when this == UniqueIdentifier => 16,
        _ when this == SystemName => 15,
        _ when this == NText => 14,
        NVarcharSqlType => 14,
        NCharSqlType => 13,
        _ when this == Text => 12,
        VarcharSqlType => 12,
        CharSqlType => 11,
        _ when this == Float => 9,
        _ when this == Real => 8,
        DecimalSqlType => 7,
        _ when this == Money => 6,
        _ when this == SmallMoney => 5,
        _ when this == BigInt => 4,
        _ when this == Int32 => 3,
        _ when this == SmallInt => 2,
        _ when this == TinyInt => 1,
        _ when this == Bit => 0,
        _ => throw new NotSupportedException($"No precedence defined for {this}."),
    };

    /// <summary>
    /// Tinyint system-type id matching real SQL Server's <c>sys.types.system_type_id</c> /
    /// <c>sys.columns.system_type_id</c> — stable integers documented in the
    /// SQL Server catalog (e.g. <c>int = 56</c>, <c>nvarchar = 231</c>,
    /// <c>uniqueidentifier = 36</c>). <see cref="SystemName"/> shares
    /// <c>231</c> with <c>nvarchar</c> because <c>sysname</c> is implemented
    /// as an <c>nvarchar(128)</c> alias; <see cref="UserTypeId"/> distinguishes
    /// it via the alias id <c>256</c>. Probe-confirmed against SQL Server 2025.
    /// </summary>
    public byte SystemTypeId => this switch
    {
        _ when this == Image => 34,
        _ when this == Text => 35,
        _ when this == UniqueIdentifier => 36,
        _ when this == Date => 40,
        TimeSqlType => 41,
        DateTime2SqlType => 42,
        DateTimeOffsetSqlType => 43,
        _ when this == TinyInt => 48,
        _ when this == SmallInt => 52,
        _ when this == Int32 => 56,
        _ when this == SmallDateTime => 58,
        _ when this == Real => 59,
        _ when this == Money => 60,
        _ when this == DateTime => 61,
        _ when this == Float => 62,
        _ when this == NText => 99,
        _ when this == Bit => 104,
        DecimalSqlType => 106,
        _ when this == SmallMoney => 122,
        _ when this == BigInt => 127,
        VarbinarySqlType => 165,
        VarcharSqlType => 167,
        BinarySqlType => 173,
        CharSqlType => 175,
        _ when this == RowVersion => 189,
        NVarcharSqlType => 231,
        _ when this == SystemName => 231,
        NCharSqlType => 239,
        _ when this == HierarchyId => 240,
        SpatialSqlType => 240,
        XmlSqlType => 241,
        _ => throw new NotSupportedException($"No SystemTypeId defined for {this}."),
    };

    /// <summary>
    /// User-type id matching real SQL Server's <c>sys.columns.user_type_id</c>.
    /// Equal to <see cref="SystemTypeId"/> for every shipped type except
    /// <see cref="SystemName"/> (alias id <c>256</c>) and <see cref="HierarchyId"/>
    /// (well-known CLR-UDT alias id <c>128</c>, probe-confirmed against
    /// SQL Server 2025 <c>sys.types</c>).
    /// </summary>
    public int UserTypeId => this == SystemName ? 256
        : this == HierarchyId ? 128
        : this == Geometry ? 129
        : this == Geography ? 130
        : this.SystemTypeId;

    /// <summary>True for SQL integer-family types (bit, tinyint, smallint, int, bigint).</summary>
    public static bool IsIntegerCategory(SqlType type) => type.Category == SqlTypeCategory.Integer;

    /// <summary>True for SQL exact-numeric-family types (decimal/numeric, money, smallmoney).</summary>
    public static bool IsExactNumericCategory(SqlType type) => type.Category is SqlTypeCategory.Decimal or SqlTypeCategory.Money;

    /// <summary>True for SQL approximate-numeric-family types (float, real).</summary>
    public static bool IsApproximateNumericCategory(SqlType type) => type.Category == SqlTypeCategory.Approximate;

    /// <summary>True for SQL money-family types (money, smallmoney).</summary>
    public static bool IsMoneyCategory(SqlType type) => type.Category == SqlTypeCategory.Money;

    /// <summary>
    /// SQL Server canonicalizes integer types into a decimal-equivalent
    /// (precision, scale) for arithmetic with decimal: bit→(1,0),
    /// tinyint→(3,0), smallint→(5,0), int→(10,0), bigint→(19,0). Verified
    /// against SQL Server 2025; the precision values fall out of each
    /// integer type's max representable decimal-digit count.
    /// </summary>
    public static (int Precision, int Scale) IntegerAsDecimal(SqlType type) =>
        type == Bit ? (1, 0)
        : type == TinyInt ? (3, 0)
        : type == SmallInt ? (5, 0)
        : type == Int32 ? (10, 0)
        : type == BigInt ? (19, 0)
        : throw new ArgumentException($"{type} is not an integer-family type.", nameof(type));

    /// <summary>
    /// Money / smallmoney canonicalize to <c>decimal(19, 4)</c> /
    /// <c>decimal(10, 4)</c> for arithmetic precision-promotion. Verified
    /// via probe: <c>money + decimal(5, 2) → decimal(8, 4)</c> matches the
    /// formula <c>p = max(15, 3) + max(4, 2) + 1 = 16, s = max(4, 2) = 4</c>
    /// once smallmoney's <c>(10, 4)</c> standin is plugged in.
    /// </summary>
    public static (int Precision, int Scale) MoneyAsDecimal(SqlType type) =>
        type == Money ? (19, 4)
        : type == SmallMoney ? (10, 4)
        : throw new ArgumentException($"{type} is not a money-family type.", nameof(type));

    /// <summary>True for SQL string-family types (varchar, nvarchar, sysname).</summary>
    public static bool IsStringCategory(SqlType type) => type.Category == SqlTypeCategory.String;

    public static readonly Int32SqlType Int32 = new();

    public static readonly BigIntSqlType BigInt = new();

    public static readonly SmallIntSqlType SmallInt = new();

    public static readonly TinyIntSqlType TinyInt = new();

    public static readonly BitSqlType Bit = new();

    /// <remarks>
    /// Stored as Windows-1252 bytes — matching SQL Server's default collation
    /// <c>SQL_Latin1_General_CP1_CI_AS</c>, a single-byte code page. Characters
    /// outside CP1252 (e.g. CJK, emoji) silently round-trip through SQL Server's
    /// best-fit replacement to <c>?</c>; the simulator follows that lossy
    /// behavior so authenticity is preserved even when it isn't desirable.
    /// UTF-8-enabled collations (introduced in SQL Server 2019) are an opt-in
    /// feature and aren't modeled today.
    /// </remarks>
    public static readonly VarcharSqlType Varchar = VarcharSqlType.Get(0, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <remarks>
    /// Stored as UTF-16 LE bytes (2 bytes per BMP code unit, surrogate pairs for
    /// supplementary characters), matching SQL Server's on-disk nvarchar layout.
    /// </remarks>
    public static readonly NVarcharSqlType NVarchar = NVarcharSqlType.Get(0, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <remarks>
    /// SQL Server's <c>sysname</c> — historically <c>varchar(30)</c> in 6.5,
    /// modernly <c>nvarchar(128) NOT NULL</c>. Stored on disk identically to
    /// <see cref="NVarchar"/> (UTF-16 LE), but kept as a distinct
    /// <see cref="SqlType"/> instance because it appears with its own identity
    /// across system catalogs and in user-visible <c>sp_help</c> output.
    /// </remarks>
    public static readonly SystemNameSqlType SystemName = new();

    /// <remarks>
    /// Stored as raw bytes — no encoding, no codepage conversion. The simulator
    /// treats the contents as immutable: callers shouldn't mutate the array
    /// after passing it to <see cref="SqlValue.FromVarbinary"/> or after
    /// receiving it from <see cref="SqlValue.AsBytes"/>.
    /// </remarks>
    public static readonly VarbinarySqlType Varbinary = VarbinarySqlType.Unspecified;

    /// <remarks>
    /// SQL Server's deprecated <c>text</c> type: <c>varchar</c>-shaped CP1252
    /// storage, always off-row in a LOB chain. Encodes identically to
    /// <see cref="Varchar"/>; the distinct singleton exists so the
    /// expression layer can apply the operation restrictions Msg 402
    /// (no comparison) and Msg 306 (no sort/group/distinct).
    /// </remarks>
    public static readonly TextSqlType Text = new();

    /// <remarks>
    /// SQL Server's deprecated <c>ntext</c> type: <c>nvarchar</c>-shaped UTF-16
    /// storage, always off-row. Same restrictions as <see cref="Text"/>.
    /// </remarks>
    public static readonly NTextSqlType NText = new();

    /// <remarks>
    /// SQL Server's deprecated <c>image</c> type: <c>varbinary</c>-shaped raw
    /// bytes, always off-row. Same restrictions as <see cref="Text"/>.
    /// </remarks>
    public static readonly ImageSqlType Image = new();

    /// <remarks>
    /// SQL Server's <c>date</c>: 3-byte fixed-length storage representing days
    /// since 0001-01-01, range 0001-01-01 through 9999-12-31 (3652058 days).
    /// No time component. The simulator stores the day count as a 24-bit
    /// little-endian unsigned integer; SQL Server's exact byte ordering for
    /// <c>date</c> isn't publicly specified, and 24-bit LE is the natural fit
    /// for SQL Server's overall little-endian on-disk format.
    /// </remarks>
    public static readonly DateSqlType Date = new();

    /// <remarks>
    /// SQL Server's <c>datetime2(N)</c>: variable-precision date+time, where
    /// <c>N</c> selects fractional-second digits (0-7). Storage width depends
    /// on precision: 6 bytes for N=0-2, 7 bytes for N=3-4, 8 bytes for N=5-7.
    /// Each precision is a distinct <see cref="SqlType"/> singleton so the
    /// reference-equality pattern used for promotion and comparison
    /// continues to apply; <see cref="GetDateTime2"/> resolves precision
    /// numbers to the singleton.
    /// </remarks>
    public static SqlType GetDateTime2(int precision) =>
        precision is < 0 or > 7
            ? throw new ArgumentOutOfRangeException(nameof(precision), $"datetime2 precision must be 0-7; got {precision}.")
            : DateTime2Cache[precision];

    private static readonly DateTime2SqlType[] DateTime2Cache =
    [
        new(0), new(1), new(2), new(3), new(4), new(5), new(6), new(7),
    ];

    /// <remarks>
    /// SQL Server's <c>time(N)</c>: time-of-day with fractional-second
    /// precision (0-7). Storage width is 3/4/5 bytes for N=0-2/3-4/5-7 — no
    /// date portion. As with <see cref="GetDateTime2"/>, each precision is a
    /// distinct singleton so reference-equality flows through promotion and
    /// comparison.
    /// </remarks>
    public static SqlType GetTime(int precision) =>
        precision is < 0 or > 7
            ? throw new ArgumentOutOfRangeException(nameof(precision), $"time precision must be 0-7; got {precision}.")
            : TimeCache[precision];

    private static readonly TimeSqlType[] TimeCache =
    [
        new(0), new(1), new(2), new(3), new(4), new(5), new(6), new(7),
    ];

    /// <remarks>
    /// SQL Server's <c>datetimeoffset(N)</c>: <c>datetime2(N)</c> plus a
    /// time-zone offset stored as a signed 16-bit minute count. Storage width
    /// is 8/9/10 bytes for N=0-2/3-4/5-7 (the datetime2 width plus the 2-byte
    /// offset). Comparisons are by UTC instant (matching SQL Server), but the
    /// original offset is preserved so the value round-trips losslessly. Each
    /// precision is a distinct singleton so the reference-equality pattern
    /// used for promotion and comparison continues to apply.
    /// </remarks>
    public static SqlType GetDateTimeOffset(int precision) =>
        precision is < 0 or > 7
            ? throw new ArgumentOutOfRangeException(nameof(precision), $"datetimeoffset precision must be 0-7; got {precision}.")
            : DateTimeOffsetCache[precision];

    private static readonly DateTimeOffsetSqlType[] DateTimeOffsetCache =
    [
        new(0), new(1), new(2), new(3), new(4), new(5), new(6), new(7),
    ];

    /// <remarks>
    /// SQL Server's legacy <c>datetime</c>: 8-byte fixed-length, 1/300-second
    /// (≈3.33 ms) tick granularity, range 1753-01-01 through
    /// 9999-12-31 23:59:59.997. The simulator's on-disk layout is 4 bytes of
    /// 1/300-second tick count since midnight (uint32 LE) followed by 4 bytes
    /// of days-since-1900-01-01 (int32 LE). Time-first matches the order used
    /// by <see cref="DateTime2SqlType"/>; SQL Server's exact byte ordering
    /// for <c>datetime</c> isn't publicly specified but the engine is
    /// little-endian throughout. Inputs are rounded half-up to the nearest
    /// 1/300-second tick at construction; rounding past the type's max value
    /// raises Msg 242.
    /// </remarks>
    public static readonly DateTimeSqlType DateTime = new();

    /// <remarks>
    /// SQL Server's <c>smalldatetime</c>: 4-byte fixed-length, 1-minute
    /// granularity, range 1900-01-01 through 2079-06-06 23:59. The
    /// simulator's on-disk layout is 2 bytes of minutes-since-midnight
    /// (uint16 LE) followed by 2 bytes of days-since-1900-01-01 (uint16 LE).
    /// Time-first matches the order used by <see cref="DateTimeSqlType"/>;
    /// SQL Server's exact byte ordering for <c>smalldatetime</c> isn't
    /// publicly specified but the engine is little-endian throughout.
    /// Inputs are first quantized to legacy 1/300-second tick (matching
    /// SQL Server's internal pipeline) and then rounded half-up to the
    /// nearest minute; rounding past the type's max raises Msg 242.
    /// </remarks>
    public static readonly SmallDateTimeSqlType SmallDateTime = new();

    /// <remarks>
    /// SQL Server's <c>uniqueidentifier</c>: 16-byte fixed-length GUID. On-disk
    /// byte layout matches <see cref="Guid.TryWriteBytes(Span{byte})"/>'s
    /// output — Data1/Data2/Data3 little-endian, final 8 bytes raw — so
    /// encode and decode delegate straight to the BCL. Comparison uses
    /// SQL Server's quirky permutation (last 6 bytes most significant) via
    /// <see cref="System.Data.SqlTypes.SqlGuid.CompareTo(System.Data.SqlTypes.SqlGuid)"/>,
    /// which is incompatible with .NET's natural <see cref="Guid.CompareTo(Guid)"/>.
    /// </remarks>
    public static readonly UniqueIdentifierSqlType UniqueIdentifier = new();

    /// <remarks>
    /// SQL Server's <c>rowversion</c> (also spelled <c>timestamp</c>): 8-byte
    /// big-endian auto-generated counter. See <see cref="RowVersionSqlType"/>
    /// for the auto-generation contract.
    /// </remarks>
    public static readonly RowVersionSqlType RowVersion = new();

    /// <remarks>
    /// SQL Server's <c>hierarchyid</c>: variable-length CLR UDT for tree
    /// paths. Internal byte form is simulator-specific; CAST round-trips
    /// inside the simulator are byte-stable, cross-engine transport (BCP /
    /// SqlClient UDT wire) is deferred. See <see cref="HierarchyIdSqlType"/>
    /// for the segment-array internal representation and string-form rules.
    /// </remarks>
    public static readonly HierarchyIdSqlType HierarchyId = new();

    /// <remarks>
    /// SQL Server's <c>xml</c>: variable-length Unicode text with XPath /
    /// XQuery method dispatch layered on top. The simulator stores payload
    /// identically to <c>nvarchar(MAX)</c>; XML methods (<c>.value()</c> /
    /// <c>.nodes()</c> / <c>.query()</c> / <c>.exist()</c> / <c>.modify()</c>)
    /// raise <see cref="NotSupportedException"/> at execute time. See
    /// <c>docs/claude/xml.md</c> for the skip-with-diagnostic rationale.
    /// </remarks>
    public static readonly XmlSqlType Xml = new();

    /// <remarks>
    /// SQL Server's <c>geography</c> CLR UDT — round-earth spatial values
    /// bound to a Spatial Reference Identifier. Stored in the simulator as
    /// raw-WKT UTF-16 (degraded-mode encoding); OGC + Microsoft-extension
    /// methods parse cleanly and throw <see cref="NotSupportedException"/> at
    /// execute except <c>.ToString()</c>, which returns the stored WKT. See
    /// <see cref="GeographySqlType"/> for the skip-with-diagnostic rationale.
    /// </remarks>
    public static readonly GeographySqlType Geography = new();

    /// <remarks>
    /// SQL Server's <c>geometry</c> CLR UDT — flat-Earth spatial values.
    /// Same implementation strategy as <see cref="Geography"/>; see
    /// <see cref="GeometrySqlType"/> for details.
    /// </remarks>
    public static readonly GeometrySqlType Geometry = new();

    /// <remarks>
    /// SQL Server's <c>char(N)</c>: fixed-length CP1252 string. Each declared
    /// length is a distinct singleton reachable through this accessor.
    /// </remarks>
    public static SqlType GetChar(int length) => CharSqlType.Get(length, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <remarks>
    /// SQL Server's <c>nchar(N)</c>: fixed-length UTF-16 string. Each declared
    /// length is a distinct singleton reachable through this accessor.
    /// </remarks>
    public static SqlType GetNChar(int length) => NCharSqlType.Get(length, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <remarks>
    /// SQL Server's <c>binary(N)</c>: fixed-length raw bytes. Each declared
    /// length is a distinct singleton reachable through this accessor.
    /// </remarks>
    public static SqlType GetBinary(int length) => BinarySqlType.Get(length);

    /// <remarks>
    /// SQL Server's <c>decimal(p, s)</c> / <c>numeric(p, s)</c>: exact-precision
    /// fixed-point. Each (precision, scale) pair is a distinct singleton
    /// reachable through <see cref="GetDecimal"/>; reference equality flows
    /// the same way it does for date/time precision singletons. Precision
    /// above 28 throws <see cref="NotSupportedException"/> — .NET decimal's
    /// 28-29 digit limit doesn't extend to SQL Server's full 38, and the
    /// arbitrary-precision mantissa needed to bridge isn't modeled yet.
    /// </remarks>
    public static SqlType GetDecimal(int precision, int scale) => DecimalSqlType.Get(precision, scale);

    /// <remarks>
    /// SQL Server's <c>float</c>: 8-byte IEEE 754 double. <c>float(N)</c>
    /// with <c>N ≤ 24</c> resolves to <see cref="Real"/> instead — that
    /// dispatch lives in <see cref="GetByName"/>.
    /// </remarks>
    public static readonly FloatSqlType Float = new();

    /// <remarks>
    /// SQL Server's <c>real</c>: 4-byte IEEE 754 single. Equivalent to
    /// <c>float(24)</c>.
    /// </remarks>
    public static readonly RealSqlType Real = new();

    /// <remarks>
    /// SQL Server's <c>money</c>: 8-byte scaled signed integer with a fixed
    /// scale of 4 decimal places. Range
    /// <c>[-922337203685477.5808, 922337203685477.5807]</c> (matching
    /// <see cref="long"/>).
    /// </remarks>
    public static readonly MoneySqlType Money = new();

    /// <remarks>
    /// SQL Server's <c>smallmoney</c>: 4-byte scaled signed integer with a
    /// fixed scale of 4 decimal places. Range
    /// <c>[-214748.3648, 214748.3647]</c>.
    /// </remarks>
    public static readonly SmallMoneySqlType SmallMoney = new();

    /// <summary>True for SQL date/time-family types (date, datetime, smalldatetime, datetime2, time, datetimeoffset).</summary>
    public static bool IsDateTimeCategory(SqlType type) => type.Category == SqlTypeCategory.DateTime;

    /// <summary>
    /// Resolves an ADO.NET <see cref="DbType"/> to its corresponding
    /// <see cref="SqlType"/>. Used at the parameter boundary, where typed
    /// parameter values arrive with a <see cref="DbType"/> and need to land in
    /// the simulator's type system.
    /// </summary>
    /// <exception cref="NotSupportedException">No mapping exists for <paramref name="dbType"/>.</exception>
    public static SqlType GetByDbType(DbType dbType) => dbType switch
    {
        DbType.Boolean => Bit,
        DbType.Byte => TinyInt,
        DbType.Int16 => SmallInt,
        DbType.Int32 => Int32,
        DbType.Int64 => BigInt,
        DbType.AnsiString => Varchar,
        DbType.String => NVarchar,
        // Fixed-length string DbTypes map to their variable-length cousins at
        // the parameter boundary: the parameter carries the raw string content,
        // and the destination column's <c>char(N)</c> / <c>nchar(N)</c> type
        // drives padding/truncation at INSERT/UPDATE time via the same pipeline
        // that handles oversize varchar/nvarchar inputs. EF Core's
        // <c>SqlServerStringTypeMapping</c> sets these for properties marked
        // <c>HasColumnType("char(N)")</c> or <c>"nchar(N)"</c>.
        DbType.AnsiStringFixedLength => Varchar,
        DbType.StringFixedLength => NVarchar,
        DbType.Binary => Varbinary,
        DbType.Date => Date,
        // DbType.DateTime is the legacy 1/300-second type; explicit opt-in.
        DbType.DateTime => DateTime,
        // DbType.DateTime2 carries no explicit precision; full DateTime tick
        // resolution is precision 7. EF's DateTime mapping arrives here.
        DbType.DateTime2 => GetDateTime2(7),
        // DbType.Time likewise binds to the highest precision. EF maps both
        // TimeSpan and TimeOnly via DbType.Time.
        DbType.Time => GetTime(7),
        DbType.DateTimeOffset => GetDateTimeOffset(7),
        DbType.Guid => UniqueIdentifier,
        // DbType.Decimal / VarNumeric have no precision/scale on the
        // DbType enum; default to decimal(18, 0) — the same fallback SQL
        // Server applies when no parameters are declared.
        DbType.Decimal or DbType.VarNumeric => GetDecimal(18, 0),
        DbType.Double => Float,
        DbType.Single => Real,
        DbType.Currency => Money,
        _ => throw new NotSupportedException($"No SqlType mapping for DbType {dbType}."),
    };

    /// <summary>
    /// Resolves a SQL type name (as seen in CREATE TABLE or CAST) to its
    /// <see cref="SqlType"/> and, for variable-length string types, the
    /// validated declared maximum length.
    /// </summary>
    /// <param name="name">The type name token. Used for line attribution in error messages.</param>
    /// <param name="declaredMaxLength">
    /// The N from <c>varchar(N)</c> / <c>nvarchar(N)</c> if one was supplied,
    /// otherwise null. Reused for <c>datetime2(N)</c> / <c>time(N)</c> /
    /// <c>datetimeoffset(N)</c> as the fractional-second precision (0-7),
    /// where the integer is interpreted by the type rather than stored on
    /// the column. For <c>decimal(p, s)</c> / <c>numeric(p, s)</c> this
    /// carries the precision <c>p</c>; the scale arrives separately on
    /// <paramref name="declaredScale"/>.
    /// </param>
    /// <param name="declaredScale">
    /// The second integer in a two-arg type spec — only <c>decimal(p, s)</c>
    /// and <c>numeric(p, s)</c> use it today.
    /// </param>
    /// <param name="index">1-based column index. Used for column-context errors only.</param>
    /// <param name="columnName">
    /// Unquoted column name when called from a column declaration; null when
    /// called from a CAST/CONVERT expression. Selects the SQL Server error
    /// variant — column declarations and casts raise different Msg numbers
    /// for the same underlying mistake (e.g. unknown type → 2715 vs 243;
    /// width-on-fixed-type → 2716 vs 291; oversize varchar → 131 with
    /// "column" vs "type" wording; oversize nvarchar → 2717 vs 131).
    /// </param>
    /// <returns>
    /// The resolved <see cref="SqlType"/> and, for <c>varchar</c> / <c>nvarchar</c>,
    /// the validated max length (in bytes for <c>varchar</c>, UCS-2 code units for
    /// <c>nvarchar</c>); null for fixed-length types.
    /// </returns>
    /// <exception cref="SimulatedSqlException">
    /// The type name is unknown, a length was supplied for a fixed-length type,
    /// no length was supplied for a variable-length type, or the supplied length
    /// is out of range for the type.
    /// </exception>
    public static (SqlType Type, int? MaxLength) GetByName(Name name, int? declaredMaxLength, int? declaredScale, int index, string? columnName)
    {
        Span<char> upper = stackalloc char[name.Span.Length];
        var resolvedName = name.Span.ToUpperInvariant(upper);

        // datetime2(N) and time(N) are dispatched ahead of the fixed/variable-
        // length switch because their declared parameter is precision (chooses
        // the singleton), not a length cap on the column.
        if (resolvedName == 9 && upper.SequenceEqual("DATETIME2"))
        {
            var precision = declaredMaxLength ?? 7;
            return precision is < 0 or > 7
                ? throw SimulatedSqlException.InvalidScale(precision, name.LineNumber)
                : (GetDateTime2(precision), null);
        }

        if (resolvedName == 4 && upper.SequenceEqual("TIME"))
        {
            var precision = declaredMaxLength ?? 7;
            return precision is < 0 or > 7
                ? throw SimulatedSqlException.InvalidScale(precision, name.LineNumber)
                : (GetTime(precision), null);
        }

        if (resolvedName == 14 && upper.SequenceEqual("DATETIMEOFFSET"))
        {
            var precision = declaredMaxLength ?? 7;
            return precision is < 0 or > 7
                ? throw SimulatedSqlException.InvalidScale(precision, name.LineNumber)
                : (GetDateTimeOffset(precision), null);
        }

        // float(N) — N selects 4-byte real (N ≤ 24) or 8-byte float
        // (N 25-53). Bare <c>float</c> defaults to N=53 (8 bytes).
        if (resolvedName == 5 && upper.SequenceEqual("FLOAT"))
        {
            var mantissaBits = declaredMaxLength ?? 53;
            return mantissaBits is < 1 or > 53
                ? throw SimulatedSqlException.LengthOrPrecisionSpecificationInvalid(mantissaBits, name.LineNumber)
                : (mantissaBits <= 24 ? Real : Float, null);
        }

        // decimal(p, s) and numeric(p, s) — same backing type, parsed
        // identically. Defaults match SQL Server: precision 18, scale 0.
        if ((resolvedName == 7 && upper.SequenceEqual("DECIMAL")) || (resolvedName == 7 && upper.SequenceEqual("NUMERIC")))
        {
            var precision = declaredMaxLength ?? 18;
            var scale = declaredScale ?? 0;
            return precision is < 1 or > 38
                ? throw SimulatedSqlException.LengthOrPrecisionSpecificationInvalid(precision, name.LineNumber)
                : scale < 0 || scale > precision
                    ? throw SimulatedSqlException.InvalidScale(scale, name.LineNumber)
                    : ((SqlType, int?))(GetDecimal(precision, scale), null);
        }

        // Fixed-length char/nchar/binary parameterize on the declared length —
        // dispatched here ahead of the generic fixed/variable-length switch
        // because (a) IsFixedLength is true (so the generic "no width allowed"
        // path would reject the parameter) and (b) SQL Server picks different
        // defaults by context: 1 in column declarations, 30 in CAST.
        if (resolvedName == 4 && upper.SequenceEqual("CHAR"))
            return ResolveFixedString(declaredMaxLength, columnName, name.LineNumber, max: 8000, isNvarcharCousin: false, "char", GetChar);
        if (resolvedName == 5 && upper.SequenceEqual("NCHAR"))
            return ResolveFixedString(declaredMaxLength, columnName, name.LineNumber, max: 4000, isNvarcharCousin: true, "nchar", GetNChar);
        if (resolvedName == 6 && upper.SequenceEqual("BINARY"))
            return ResolveFixedString(declaredMaxLength, columnName, name.LineNumber, max: 8000, isNvarcharCousin: false, "binary", GetBinary);

        // Length-then-name dispatch over the simple keyword-named singletons.
        // (CHAR / NCHAR / BINARY / VARCHAR(MAX) / etc. with parameter handling
        // are dispatched ahead of this block — see the early returns above.)
        // ResolveSimpleKeyword's explicit return type bridges the disparate
        // concrete SqlType subclasses each arm produces; embedding the same
        // dispatch as a switch expression here would force per-arm casts to
        // satisfy best-common-type inference.
        var resolved = ResolveSimpleKeyword(resolvedName, upper)
            ?? throw (columnName is not null
                ? SimulatedSqlException.CannotFindDataType(name.Span, index)
                : SimulatedSqlException.CannotFindDataTypeInCast(name.Span));

        // SQL Server's pre-type-specific zero check: Msg 1001 fires before
        // any per-type validation (e.g. varchar(0), datetime(0)). datetime2 /
        // time / datetimeoffset are unaffected because their N=0 cases are
        // dispatched ahead of this branch (precision 0 is valid there).
        // declaredMaxLength == MaxLengthSentinel is the MAX path (varchar/
        // nvarchar/varbinary only), which skips the zero check.
        if (declaredMaxLength == 0)
            throw SimulatedSqlException.LengthOrPrecisionSpecificationInvalid(0, name.LineNumber);

        // text/ntext/image are always-LOB and don't accept a length spec.
        // The MaxLength field on the resulting column is set to the sentinel
        // for symmetry with the explicit-MAX path on varchar/nvarchar/varbinary.
        if (resolved.IsLob)
        {
            return declaredMaxLength is not null
                ? throw (columnName is not null
                    ? SimulatedSqlException.CannotSpecifyColumnWidth(resolved, index)
                    : SimulatedSqlException.CannotSpecifyColumnWidthInCast(resolved))
                : (resolved, MaxLengthSentinel);
        }

        if (resolved.IsFixedLength)
        {
            return declaredMaxLength is not null
                ? throw (columnName is not null
                    ? SimulatedSqlException.CannotSpecifyColumnWidth(resolved, index)
                    : SimulatedSqlException.CannotSpecifyColumnWidthInCast(resolved))
                : (resolved, null);
        }

        // varchar(MAX) / nvarchar(MAX) / varbinary(MAX): the LOB-eligible form
        // of the same SqlType family. The Type carries the MAX-form length
        // (-1) directly via the per-type Get(-1, …) factory; HeapColumn.MaxLength
        // duplicates the same sentinel for the row-level encoder's LOB-routing
        // path.
        if (declaredMaxLength == MaxLengthSentinel)
            return (ResolveVarFamilyForLength(resolved, MaxLengthSentinel), MaxLengthSentinel);

        // sysname has a fixed intrinsic length (nvarchar(128) NOT NULL — SQL
        // Server's sys-schema alias). A length spec on the keyword is grammar-
        // rejected by real SQL Server; we mirror that by raising Msg 2716 here
        // when one is supplied. The 128-character cap is enforced via
        // HeapColumn.MaxLength = 128 (the row encoder treats sysname-typed
        // columns identically to nvarchar(128) at storage).
        if (resolved is SystemNameSqlType)
        {
            return declaredMaxLength is not null
                ? throw (columnName is not null
                    ? SimulatedSqlException.CannotSpecifyColumnWidth(resolved, index)
                    : SimulatedSqlException.CannotSpecifyColumnWidthInCast(resolved))
                : (resolved, 128);
        }

        // Variable-length string types are bounded per type. SQL Server has the
        // same two-context rule as fixed-length char/nchar/binary: missing
        // length defaults to 1 in a column declaration but 30 in a CAST/CONVERT
        // expression — the columnName parameter (null in CAST context) selects
        // between them. Probe-confirmed against SQL Server 2025: `CAST('hello'
        // AS varchar)` returns the full string, which would truncate to 'h' if
        // the CAST default were 1.
        var max = resolved is NVarcharSqlType ? 4000 : 8000;
        var declared = declaredMaxLength ?? (columnName is null ? 30 : 1);
        if (declared < 1 || declared > max)
        {
            throw (columnName, resolved is NVarcharSqlType) switch
            {
                (not null, true) => SimulatedSqlException.NVarcharSizeExceedsMaximumColumn(columnName, declared),
                (not null, false) => SimulatedSqlException.SizeExceedsMaximumColumn(columnName, declared, max),
                (null, true) => SimulatedSqlException.NVarcharSizeExceedsMaximumCast("nvarchar", declared),
                (null, false) => SimulatedSqlException.SizeExceedsMaximumCast(resolved.ToString()!, declared, max),
            };
        }
        return (ResolveVarFamilyForLength(resolved, declared), declared);
    }

    /// <summary>
    /// Maps the length-unspecified <c>varchar</c> / <c>nvarchar</c> /
    /// <c>varbinary</c> singleton coming back from <see cref="ResolveSimpleKeyword"/>
    /// to its length-bearing variant via the per-type <c>Get</c> factory.
    /// Pinning the length on the SqlType (parallel to <see cref="CharSqlType"/>'s
    /// existing model) lets <see cref="PromoteForArithmetic"/>, <c>SELECT INTO</c>,
    /// and computed columns track <c>varchar(N) + varchar(M) → varchar(N+M)</c>
    /// without a parallel length channel.
    /// </summary>
    private static SqlType ResolveVarFamilyForLength(SqlType resolved, int length) => resolved switch
    {
        VarcharSqlType v => VarcharSqlType.Get(length, v.Collation, v.Coercibility),
        NVarcharSqlType nv => NVarcharSqlType.Get(length, nv.Collation, nv.Coercibility),
        VarbinarySqlType => VarbinarySqlType.Get(length),
        _ => resolved,
    };

    /// <summary>
    /// Resolves a parameterized fixed-length string/binary type
    /// (<c>char(N)</c>, <c>nchar(N)</c>, <c>binary(N)</c>) to its singleton
    /// instance. Defaults match SQL Server's two-context rule: declared length
    /// 1 in a column declaration, 30 in a CAST expression — the
    /// <paramref name="columnName"/> parameter (null in CAST context) selects
    /// between them. Validation and error variants mirror the var* siblings:
    /// 0 raises Msg 1001 first, oversize raises Msg 131 (varchar/varbinary
    /// wording) or 2717 (nchar wording).
    /// </summary>
    private static (SqlType Type, int? MaxLength) ResolveFixedString(int? declaredMaxLength, string? columnName, int line, int max, bool isNvarcharCousin, string typeName, Func<int, SqlType> factory)
    {
        if (declaredMaxLength == 0)
            throw SimulatedSqlException.LengthOrPrecisionSpecificationInvalid(0, line);
        var declared = declaredMaxLength ?? (columnName is null ? 30 : 1);
        if (declared < 1 || declared > max)
        {
            throw (columnName, isNvarcharCousin) switch
            {
                (not null, true) => SimulatedSqlException.NVarcharSizeExceedsMaximumColumn(columnName, declared),
                (not null, false) => SimulatedSqlException.SizeExceedsMaximumColumn(columnName, declared, max),
                (null, true) => SimulatedSqlException.NVarcharSizeExceedsMaximumCast(typeName, declared),
                (null, false) => SimulatedSqlException.SizeExceedsMaximumCast(typeName, declared, max),
            };
        }
        return (factory(declared), declared);
    }

    /// <summary>
    /// Length-then-name dispatch for the simple keyword-named singletons
    /// (no parameter parsing; the parameterized types like <c>varchar(N)</c>
    /// and <c>decimal(p, s)</c> are handled in <see cref="GetByName"/>'s
    /// earlier branches). Returns null when the name doesn't match a known
    /// type. The explicit <c>SqlType?</c> return type widens each arm's
    /// concrete subclass to the common <see cref="SqlType"/> via the usual
    /// reference conversion — embedding the same dispatch inline as a
    /// switch expression would require per-arm casts to satisfy
    /// best-common-type inference.
    /// </summary>
    private static SqlType? ResolveSimpleKeyword(int length, ReadOnlySpan<char> upper) => length switch
    {
        3 => upper switch
        {
            "BIT" => Bit,
            "INT" => Int32,
            "XML" => Xml,
            _ => null,
        },
        4 => upper switch
        {
            "DATE" => Date,
            "REAL" => Real,
            "TEXT" => Text,
            _ => null,
        },
        5 => upper switch
        {
            "MONEY" => Money,
            "NTEXT" => NText,
            "IMAGE" => Image,
            _ => null,
        },
        6 => upper switch
        {
            "BIGINT" => BigInt,
            _ => null,
        },
        7 => upper switch
        {
            "TINYINT" => TinyInt,
            "VARCHAR" => Varchar,
            "SYSNAME" => SystemName,
            _ => null,
        },
        8 => upper switch
        {
            "SMALLINT" => SmallInt,
            "NVARCHAR" => NVarchar,
            "DATETIME" => DateTime,
            "GEOMETRY" => Geometry,
            _ => null,
        },
        9 => upper switch
        {
            "TIMESTAMP" => RowVersion,
            "VARBINARY" => Varbinary,
            "GEOGRAPHY" => Geography,
            _ => null,
        },
        10 => upper switch
        {
            "ROWVERSION" => RowVersion,
            "SMALLMONEY" => SmallMoney,
            _ => null,
        },
        13 => upper switch
        {
            "SMALLDATETIME" => SmallDateTime,
            _ => null,
        },
        11 => upper switch
        {
            "HIERARCHYID" => HierarchyId,
            _ => null,
        },
        16 => upper switch
        {
            "UNIQUEIDENTIFIER" => UniqueIdentifier,
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// Tick count of one fractional-second unit at the given precision (e.g.
    /// precision 0 = 10_000_000 ticks per second, precision 7 = 1 tick).
    /// Shared by <see cref="DateTime2SqlType"/>, <see cref="TimeSqlType"/>,
    /// and <see cref="DateTimeOffsetSqlType"/>. <c>private protected</c> so
    /// only same-assembly derived <see cref="SqlType"/>s can use it — the
    /// helper has no meaning outside the date/time-precision family.
    /// </summary>
    private protected static long TicksPerPrecisionUnit(int precision) => precision switch
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
}
