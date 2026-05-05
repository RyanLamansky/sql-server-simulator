using SqlServerSimulator.Parser.Tokens;
using System.Data;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Type system for the storage layer. Each <see cref="SqlType"/> owns its own
/// byte representation via <see cref="Encode"/> and <see cref="Decode"/>, so
/// the row-level encoder does not need to know per-type layout details.
/// </summary>
internal abstract class SqlType
{
    private protected SqlType()
    {
    }

    /// <summary>
    /// True for types whose stored bytes are a constant width, regardless of value.
    /// </summary>
    public abstract bool IsFixedLength { get; }

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
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    public abstract int Encode(SqlValue value, Span<byte> destination);

    /// <summary>
    /// Reads bytes and reconstructs a non-NULL value.
    /// For fixed-length types, <paramref name="source"/>'s length is the type's fixed width.
    /// For variable-length types, <paramref name="source"/>'s length is the value's byte count.
    /// </summary>
    public abstract SqlValue Decode(ReadOnlySpan<byte> source);

    /// <summary>
    /// Converts a non-NULL CLR parameter value into a typed <see cref="SqlValue"/>
    /// of this type. Used at the <c>DbParameter</c> boundary; NULL handling
    /// is the caller's responsibility.
    /// </summary>
    /// <exception cref="NotSupportedException">No conversion exists from <paramref name="raw"/>'s CLR type to this <see cref="SqlType"/>.</exception>
    public abstract SqlValue ConvertParameter(object raw);

    /// <summary>
    /// SQL Server data-type precedence for implicit conversion. Higher means
    /// "wins" when a binary expression must pick a common type. The numeric
    /// values are simulator-internal; only their relative ordering matters.
    /// </summary>
    /// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/data-types/data-type-precedence-transact-sql</remarks>
    public int Precedence =>
        this == BigInt ? 5
        : this == Int32 ? 4
        : this == SmallInt ? 3
        : this == TinyInt ? 2
        : this == Bit ? 1
        : this == UniqueIdentifier ? 13
        : this == SystemName ? 12
        : this == NVarchar ? 11
        : this == Varchar ? 10
        : throw new NotSupportedException($"No precedence defined for {this}.");

    /// <summary>
    /// Returns the higher-precedence type when <paramref name="a"/> and
    /// <paramref name="b"/> share a category (both numeric or both string),
    /// the cross-family date/time rule when both are date/time, or the
    /// date/time partner when one side is a string and the other is a
    /// date/time type. Other cross-category pairs (e.g. integer ↔ string)
    /// aren't implemented; SQL Server allows those via implicit conversion
    /// but the simulator hasn't modeled them yet.
    /// </summary>
    public static SqlType Promote(SqlType a, SqlType b)
    {
        if (a == b)
            return a;

        // Date/time category — within-family widens to higher precision;
        // cross-family follows SQL Server's precedence (datetimeoffset >
        // datetime2 > datetime > smalldatetime > date), with `time` blocked
        // from non-time partners by Msg 402 (matching the binary-comparison-
        // operator rule).
        if (IsDateTimeCategory(a) && IsDateTimeCategory(b))
            return PromoteDateTime(a, b);

        // String ↔ date/time: SQL Server's data-type precedence puts every
        // date/time type above varchar/nvarchar, so a string operand
        // implicitly parses-and-coerces to the date/time partner. Bad-format
        // strings surface from the existing CoerceTo parsers (Msg 241 / 295
        // depending on the target).
        if (IsStringCategory(a) && IsDateTimeCategory(b))
            return b;
        if (IsDateTimeCategory(a) && IsStringCategory(b))
            return a;

        // Integer ↔ date/time: only the legacy types (datetime / smalldatetime)
        // accept integers as days-since-1900-01-01. The newer types reject
        // the pair with Msg 206 — the same operand-type-clash error SQL
        // Server raises for `where date = 0` or `time + 1`.
        if (IsIntegerCategory(a) && IsDateTimeCategory(b))
            return b == DateTime || b == SmallDateTime ? b : throw SimulatedSqlException.OperandTypeClash(a, b);
        if (IsDateTimeCategory(a) && IsIntegerCategory(b))
            return a == DateTime || a == SmallDateTime ? a : throw SimulatedSqlException.OperandTypeClash(a, b);

        // String ↔ uniqueidentifier: SQL Server's data-type precedence puts
        // uniqueidentifier above the string types, so a string operand
        // implicitly parses-and-coerces to uniqueidentifier (bad-format
        // strings surface from CoerceTo as Msg 8169). uniqueidentifier
        // against any non-string partner has no implicit conversion and
        // raises the operand-type-clash error.
        if (a == UniqueIdentifier && IsStringCategory(b))
            return a;
        if (IsStringCategory(a) && b == UniqueIdentifier)
            return b;
        if (a == UniqueIdentifier || b == UniqueIdentifier)
            throw SimulatedSqlException.OperandTypeClash(a, b);

        var sameCategory = (IsIntegerCategory(a) && IsIntegerCategory(b)) || (IsStringCategory(a) && IsStringCategory(b));
        return sameCategory
            ? a.Precedence >= b.Precedence ? a : b
            : throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}.");
    }

    /// <summary>
    /// Date/time-category promotion. <c>time</c> is incompatible with any
    /// non-<c>time</c> partner (Msg 402); other pairs widen to the
    /// highest-precedence family with a precision that's the max of the two
    /// participants. Legacy <c>datetime</c> contributes scale 3 to that max
    /// (matching its 1/300-second display granularity).
    /// </summary>
    private static SqlType PromoteDateTime(SqlType a, SqlType b)
    {
        // time-vs-non-time rejection comes first so the more permissive
        // within-family rule below doesn't accidentally swallow it.
        var aIsTime = a is TimeSqlType;
        var bIsTime = b is TimeSqlType;
        if (aIsTime != bIsTime)
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(a, b, "equal to");

        if (aIsTime && bIsTime)
        {
            var ta = (TimeSqlType)a;
            var tb = (TimeSqlType)b;
            return ta.precision >= tb.precision ? a : b;
        }

        // Effective precision contributed by each side: dt2/dto carry their
        // declared precision; legacy datetime is scale 3; date contributes 0.
        var aPrecision = PrecisionForPromotion(a);
        var bPrecision = PrecisionForPromotion(b);
        var precision = Math.Max(aPrecision, bPrecision);

        // Pick the highest-precedence family present.
        if (a is DateTimeOffsetSqlType || b is DateTimeOffsetSqlType)
            return GetDateTimeOffset(precision);
        if (a is DateTime2SqlType || b is DateTime2SqlType)
            return GetDateTime2(precision);
        if (a == DateTime || b == DateTime)
            return DateTime;
        if (a == SmallDateTime || b == SmallDateTime)
            return SmallDateTime;

        // Both are `date` is handled by the `a == b` short-circuit upstream.
        return Date;
    }

    private static int PrecisionForPromotion(SqlType type) => type switch
    {
        DateTime2SqlType dt2 => dt2.precision,
        DateTimeOffsetSqlType dto => dto.precision,
        _ when type == DateTime => 3,
        _ => 0,
    };

    /// <summary>True for SQL integer-family types (bit, tinyint, smallint, int, bigint).</summary>
    public static bool IsIntegerCategory(SqlType type) =>
        type == Bit || type == TinyInt || type == SmallInt || type == Int32 || type == BigInt;

    /// <summary>True for SQL string-family types (varchar, nvarchar, sysname).</summary>
    public static bool IsStringCategory(SqlType type) =>
        type == Varchar || type == NVarchar || type == SystemName;

    public static readonly SqlType Int32 = new Int32SqlType();

    public static readonly SqlType BigInt = new BigIntSqlType();

    public static readonly SqlType SmallInt = new SmallIntSqlType();

    public static readonly SqlType TinyInt = new TinyIntSqlType();

    public static readonly SqlType Bit = new BitSqlType();

    /// <remarks>
    /// Stored as Windows-1252 bytes — matching SQL Server's default collation
    /// <c>SQL_Latin1_General_CP1_CI_AS</c>, a single-byte code page. Characters
    /// outside CP1252 (e.g. CJK, emoji) silently round-trip through SQL Server's
    /// best-fit replacement to <c>?</c>; the simulator follows that lossy
    /// behavior so authenticity is preserved even when it isn't desirable.
    /// UTF-8-enabled collations (introduced in SQL Server 2019) are an opt-in
    /// feature and aren't modeled today.
    /// </remarks>
    public static readonly SqlType Varchar = new VarcharSqlType();

    /// <remarks>
    /// Stored as UTF-16 LE bytes (2 bytes per BMP code unit, surrogate pairs for
    /// supplementary characters), matching SQL Server's on-disk nvarchar layout.
    /// </remarks>
    public static readonly SqlType NVarchar = new NVarcharSqlType();

    /// <remarks>
    /// SQL Server's <c>sysname</c> — historically <c>varchar(30)</c> in 6.5,
    /// modernly <c>nvarchar(128) NOT NULL</c>. Stored on disk identically to
    /// <see cref="NVarchar"/> (UTF-16 LE), but kept as a distinct
    /// <see cref="SqlType"/> instance because it appears with its own identity
    /// across system catalogs and in user-visible <c>sp_help</c> output.
    /// </remarks>
    public static readonly SqlType SystemName = new SystemNameSqlType();

    /// <remarks>
    /// Stored as raw bytes — no encoding, no codepage conversion. The simulator
    /// treats the contents as immutable: callers shouldn't mutate the array
    /// after passing it to <see cref="SqlValue.FromVarbinary"/> or after
    /// receiving it from <see cref="SqlValue.AsBytes"/>.
    /// </remarks>
    public static readonly SqlType Varbinary = new VarbinarySqlType();

    /// <remarks>
    /// SQL Server's <c>date</c>: 3-byte fixed-length storage representing days
    /// since 0001-01-01, range 0001-01-01 through 9999-12-31 (3652058 days).
    /// No time component. The simulator stores the day count as a 24-bit
    /// little-endian unsigned integer; SQL Server's exact byte ordering for
    /// <c>date</c> isn't publicly specified, and 24-bit LE is the natural fit
    /// for SQL Server's overall little-endian on-disk format.
    /// </remarks>
    public static readonly SqlType Date = new DateSqlType();

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
    public static readonly SqlType DateTime = new DateTimeSqlType();

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
    public static readonly SqlType SmallDateTime = new SmallDateTimeSqlType();

    /// <remarks>
    /// SQL Server's <c>uniqueidentifier</c>: 16-byte fixed-length GUID. On-disk
    /// byte layout matches <see cref="Guid.TryWriteBytes(Span{byte})"/>'s
    /// output — Data1/Data2/Data3 little-endian, final 8 bytes raw — so
    /// encode and decode delegate straight to the BCL. Comparison uses
    /// SQL Server's quirky permutation (last 6 bytes most significant) via
    /// <see cref="System.Data.SqlTypes.SqlGuid.CompareTo(System.Data.SqlTypes.SqlGuid)"/>,
    /// which is incompatible with .NET's natural <see cref="Guid.CompareTo(Guid)"/>.
    /// </remarks>
    public static readonly SqlType UniqueIdentifier = new UniqueIdentifierSqlType();

    /// <summary>True for SQL date/time-family types (date, datetime, smalldatetime, datetime2, time, datetimeoffset).</summary>
    public static bool IsDateTimeCategory(SqlType type) =>
        type == Date || type == DateTime || type == SmallDateTime || type is DateTime2SqlType || type is TimeSqlType || type is DateTimeOffsetSqlType;

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
    /// the column.
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
    public static (SqlType Type, int? MaxLength) GetByName(Name name, int? declaredMaxLength, int index, string? columnName)
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

        var resolved = resolvedName switch
        {
            3 => upper switch
            {
                "BIT" => Bit,
                "INT" => Int32,
                _ => null
            },
            4 => upper switch
            {
                "DATE" => Date,
                _ => null
            },
            6 => upper switch
            {
                "BIGINT" => BigInt,
                _ => null
            },
            7 => upper switch
            {
                "TINYINT" => TinyInt,
                "VARCHAR" => Varchar,
                _ => null
            },
            8 => upper switch
            {
                "SMALLINT" => SmallInt,
                "NVARCHAR" => NVarchar,
                "DATETIME" => DateTime,
                _ => null
            },
            9 => upper switch
            {
                "VARBINARY" => Varbinary,
                _ => null
            },
            13 => upper switch
            {
                "SMALLDATETIME" => SmallDateTime,
                _ => null
            },
            16 => upper switch
            {
                "UNIQUEIDENTIFIER" => UniqueIdentifier,
                _ => null
            },
            _ => null,
        } ?? throw (columnName is not null
            ? SimulatedSqlException.CannotFindDataType(name.Span, index)
            : SimulatedSqlException.CannotFindDataTypeInCast(name.Span));

        // SQL Server's pre-type-specific zero check: Msg 1001 fires before
        // any per-type validation (e.g. varchar(0), datetime(0)). datetime2 /
        // time / datetimeoffset are unaffected because their N=0 cases are
        // dispatched ahead of this branch (precision 0 is valid there).
        if (declaredMaxLength == 0)
            throw SimulatedSqlException.LengthOrPrecisionSpecificationInvalid(0, name.LineNumber);

        if (resolved.IsFixedLength)
        {
            return declaredMaxLength is not null
                ? throw (columnName is not null
                    ? SimulatedSqlException.CannotSpecifyColumnWidth(resolved, index)
                    : SimulatedSqlException.CannotSpecifyColumnWidthInCast(resolved))
                : (resolved, null);
        }

        // Variable-length string types are bounded per type; SQL Server defaults
        // a missing length to 1 (with a warning the simulator does not raise).
        var max = resolved == NVarchar ? 4000 : 8000;
        var declared = declaredMaxLength ?? 1;
        if (declared < 1 || declared > max)
        {
            throw (columnName, resolved == NVarchar) switch
            {
                (not null, true) => SimulatedSqlException.NVarcharSizeExceedsMaximumColumn(columnName, declared),
                (not null, false) => SimulatedSqlException.SizeExceedsMaximumColumn(columnName, declared, max),
                (null, true) => SimulatedSqlException.NVarcharSizeExceedsMaximumCast(declared),
                (null, false) => SimulatedSqlException.SizeExceedsMaximumCast(resolved, declared, max),
            };
        }
        return (resolved, declared);
    }

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
