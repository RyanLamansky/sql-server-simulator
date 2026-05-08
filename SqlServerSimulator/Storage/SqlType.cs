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
        _ when this == UniqueIdentifier => 16,
        _ when this == SystemName => 15,
        _ when this == NText => 14,
        _ when this == NVarchar => 14,
        NCharSqlType => 13,
        _ when this == Text => 12,
        _ when this == Varchar => 12,
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
    /// Returns the higher-precedence type when <paramref name="a"/> and
    /// <paramref name="b"/> share a category (both numeric or both string),
    /// the cross-family date/time rule when both are date/time, or the
    /// date/time partner when one side is a string and the other is a
    /// date/time type. Other cross-category pairs (e.g. integer ↔ string)
    /// aren't implemented; SQL Server allows those via implicit conversion
    /// but the simulator hasn't modeled them yet.
    /// </summary>
    /// <remarks>
    /// Dispatch is structured as an outer switch on the left operand's
    /// <see cref="Category"/> with each arm handing off to a helper that
    /// switches on the right operand's category. Both switches are over
    /// dense byte-typed enums, so the JIT can lower them to jump tables.
    /// </remarks>
    public static SqlType Promote(SqlType a, SqlType b) =>
        a == b ? a
        : a is RowVersionSqlType ? PromoteFromRowVersion(b)
        : b is RowVersionSqlType ? PromoteFromRowVersion(a)
        : a.Category switch
        {
            SqlTypeCategory.Approximate => PromoteFromApproximate(a, b),
            SqlTypeCategory.Decimal => PromoteFromDecimal(a, b),
            SqlTypeCategory.Money => PromoteFromMoney(a, b),
            SqlTypeCategory.Integer => PromoteFromInteger(a, b),
            SqlTypeCategory.String => PromoteFromString(a, b),
            SqlTypeCategory.DateTime => PromoteFromDateTime(a, b),
            SqlTypeCategory.UniqueIdentifier => PromoteFromUniqueIdentifier(a, b),
            _ => throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}."),
        };

    /// <summary>
    /// rowversion participates in comparison with the binary family — chiefly
    /// to support EF Core's optimistic-concurrency <c>WHERE [RowVersion] = @p</c>
    /// pattern, where <c>@p</c> binds as <c>varbinary</c>. Cross-binary
    /// promotion picks the binary side as the common type; the rowversion
    /// side coerces via its <see cref="RowVersionSqlType"/> outbound CAST.
    /// Other categories raise the operand-type-clash error.
    /// </summary>
    private static SqlType PromoteFromRowVersion(SqlType other) =>
        other == Varbinary ? Varbinary
        : other is BinarySqlType ? other
        : throw SimulatedSqlException.OperandTypeClash(RowVersion, other);

    /// <summary>
    /// Approximate (float/real) wins over every other numeric or string
    /// partner. When both sides are approximate, <c>float</c> wins over
    /// <c>real</c>; same-type pairs short-circuit at the top of
    /// <see cref="Promote"/>, so this only sees mixed approximate pairs.
    /// </summary>
    private static SqlType PromoteFromApproximate(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate => a == Float || b == Float ? Float : Real,
        SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Integer or SqlTypeCategory.String => a,
        _ => throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}."),
    };

    /// <summary>
    /// Decimal vs decimal widens to the joint envelope. Decimal vs integer
    /// or money canonicalizes the partner to its decimal equivalent
    /// (bit→(1,0) … bigint→(19,0); money→(19,4); smallmoney→(10,4)) and
    /// rerun the envelope rule. Decimal beats string (string parses).
    /// </summary>
    private static SqlType PromoteFromDecimal(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate => b,
        SqlTypeCategory.Decimal => PromoteDecimalPair((DecimalSqlType)a, (DecimalSqlType)b),
        SqlTypeCategory.Integer => PromoteDecimalPair((DecimalSqlType)a, IntegerAsDecimalType(b)),
        SqlTypeCategory.Money => PromoteDecimalPair((DecimalSqlType)a, MoneyAsDecimalType(b)),
        SqlTypeCategory.String => a,
        _ => throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}."),
    };

    /// <summary>
    /// Money pairings: money vs money picks money over smallmoney; money
    /// vs integer or string keeps the money type; money vs decimal goes
    /// through <see cref="PromoteFromDecimal"/> with the operands swapped
    /// so the decimal arm handles canonicalization; money vs float/real
    /// promotes to float/real.
    /// </summary>
    private static SqlType PromoteFromMoney(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate => b,
        SqlTypeCategory.Decimal => PromoteDecimalPair(MoneyAsDecimalType(a), (DecimalSqlType)b),
        SqlTypeCategory.Money => a == Money || b == Money ? Money : SmallMoney,
        SqlTypeCategory.Integer or SqlTypeCategory.String => a,
        _ => throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}."),
    };

    /// <summary>
    /// Integer vs each numeric / string / date-time category. Integer
    /// canonicalizes to its decimal equivalent for decimal/money partners.
    /// Integer vs date/time only succeeds for the legacy datetime types
    /// (datetime, smalldatetime); other date/time types raise the
    /// operand-type-clash error.
    /// </summary>
    private static SqlType PromoteFromInteger(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate => b,
        SqlTypeCategory.Decimal => PromoteDecimalPair(IntegerAsDecimalType(a), (DecimalSqlType)b),
        SqlTypeCategory.Money => b,
        SqlTypeCategory.Integer => a.Precedence >= b.Precedence ? a : b,
        SqlTypeCategory.DateTime => b == DateTime || b == SmallDateTime ? b : throw SimulatedSqlException.OperandTypeClash(a, b),
        _ => throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}."),
    };

    /// <summary>
    /// String vs each higher-precedence partner. The partner wins (the
    /// string parses through that partner's CAST path); same-category
    /// strings pick the higher precedence (sysname &gt; nvarchar &gt;
    /// nchar &gt; varchar &gt; char). For two parameterized siblings of
    /// the same kind (e.g. char(5) vs char(10)), the longer length wins so
    /// the shorter side doesn't truncate. String vs uniqueidentifier
    /// promotes to uid; string vs integer falls through to
    /// <see cref="NotSupportedException"/> because that promotion path
    /// isn't modeled yet.
    /// </summary>
    private static SqlType PromoteFromString(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.DateTime or SqlTypeCategory.UniqueIdentifier => b,
        SqlTypeCategory.String => (a, b) switch
        {
            (CharSqlType ca, CharSqlType cb) => ca.length >= cb.length ? a : b,
            (NCharSqlType na, NCharSqlType nb) => na.length >= nb.length ? a : b,
            _ => a.Precedence >= b.Precedence ? a : b,
        },
        _ => throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}."),
    };

    /// <summary>
    /// Date/time pairings dispatch to <see cref="PromoteDateTime"/>; date/
    /// time vs string takes the date/time side (string parses); date/time
    /// vs integer succeeds only for the legacy types.
    /// </summary>
    private static SqlType PromoteFromDateTime(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.DateTime => PromoteDateTime(a, b),
        SqlTypeCategory.String => a,
        SqlTypeCategory.Integer => a == DateTime || a == SmallDateTime ? a : throw SimulatedSqlException.OperandTypeClash(a, b),
        _ => throw new NotSupportedException($"Cross-category type promotion isn't implemented: {a} vs {b}."),
    };

    /// <summary>
    /// uniqueidentifier vs string promotes to uniqueidentifier (the string
    /// parses); every other partner raises the operand-type-clash error.
    /// </summary>
    private static SqlType PromoteFromUniqueIdentifier(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.String => a,
        _ => throw SimulatedSqlException.OperandTypeClash(a, b),
    };

    /// <summary>
    /// Joint-envelope decimal promotion used by comparison / COALESCE-style
    /// common-type decisions. Per-operator arithmetic precision/scale rules
    /// (multiplication, division, etc.) live in
    /// <see cref="PromoteForArithmetic"/>.
    /// </summary>
    private static SqlType PromoteDecimalPair(DecimalSqlType a, DecimalSqlType b)
    {
        var scale = Math.Max(a.scale, b.scale);
        var integerPart = Math.Max(a.precision - a.scale, b.precision - b.scale);
        return GetDecimal(Math.Min(38, integerPart + scale), scale);
    }

    /// <summary>
    /// Per-operator type promotion for binary arithmetic — the rules SQL
    /// Server applies to <c>+</c> / <c>-</c> / <c>*</c> / <c>/</c> / <c>%</c>
    /// when computing the result type. Differs from
    /// <see cref="Promote"/> only in the decimal-involving cases: each
    /// operator has its own precision / scale formula (probed against SQL
    /// Server 2025), and the 38-precision cap reduces scale by the excess
    /// down to <c>min(originalScale, 6)</c> — effectively "never below 6
    /// for division (which always produces scale ≥ 6 anyway)" and "never
    /// below the original scale for the other operators when the original
    /// was already ≤ 6". Non-decimal categories (integer × integer, money
    /// × money, float × *, etc.) reuse <see cref="Promote"/> since their
    /// arithmetic result type matches the joint-envelope rule.
    /// </summary>
    /// <remarks>
    /// Decimal scale formulas (verified against SQL Server 2025, 2026-05-08):
    /// <list type="bullet">
    /// <item><c>+</c>/<c>-</c>: <c>p = max(p1-s1, p2-s2) + max(s1,s2) + 1</c>,
    /// <c>s = max(s1, s2)</c></item>
    /// <item><c>*</c>: <c>p = p1+p2+1</c>, <c>s = s1+s2</c></item>
    /// <item><c>/</c>: <c>s = max(6, s1+p2+1)</c>,
    /// <c>p = p1-s1+s2+s</c></item>
    /// <item><c>%</c>: <c>p = min(p1-s1, p2-s2) + max(s1,s2)</c>,
    /// <c>s = max(s1, s2)</c></item>
    /// </list>
    /// Integer / money operands canonicalize to their decimal equivalent
    /// (bit→(1,0), tinyint→(3,0), smallint→(5,0), int→(10,0), bigint→(19,0),
    /// money→(19,4), smallmoney→(10,4)) before the formulas apply. Pure
    /// integer-pair and money-pair arithmetic skips the decimal path entirely
    /// — those produce wider integer / money results that match
    /// <see cref="Promote"/>.
    /// </remarks>
    public static SqlType PromoteForArithmetic(SqlType a, SqlType b, char op)
    {
        // Bitwise operators (&, |, ^) don't have per-operator scale rules
        // and don't accept decimal operands anyway — fall through to the
        // joint-envelope rule for type unification (decimal × bitwise will
        // raise the unsupported-numeric-pair error at runtime instead).
        if (op is '&' or '|' or '^')
            return Promote(a, b);

        // Float / real win over everything else, same as the joint-envelope
        // path; no decimal-style scale dance needed.
        if (a.Category == SqlTypeCategory.Approximate || b.Category == SqlTypeCategory.Approximate)
            return Promote(a, b);

        // Decimal-involving cases: the per-operator formula applies. Money
        // and integer canonicalize to their decimal equivalent so the
        // formula sees a uniform (precision, scale) pair on both sides.
        var aIsDecimal = a is DecimalSqlType;
        var bIsDecimal = b is DecimalSqlType;
        if (aIsDecimal || bIsDecimal)
        {
            var (p1, s1) = AsDecimalPrecisionScale(a);
            var (p2, s2) = AsDecimalPrecisionScale(b);
            return ComputeDecimalArithmeticResultType(p1, s1, p2, s2, op);
        }

        // Pure integer / money / date / string pairs: arithmetic result type
        // matches the joint-envelope rule (e.g., int + bigint → bigint;
        // money + money → money; int + int → int).
        return Promote(a, b);
    }

    /// <summary>
    /// Computes a decimal arithmetic result type from raw (p1, s1, p2, s2)
    /// quadruples. Public to <see cref="Storage"/> via
    /// <see cref="PromoteForArithmetic"/>; the binary-expression layer
    /// reuses this directly so the static (GetSqlType) and runtime
    /// (DecimalArithmetic) paths share one formula.
    /// </summary>
    internal static DecimalSqlType ComputeDecimalArithmeticResultType(int p1, int s1, int p2, int s2, char op)
    {
        int p, s;
        switch (op)
        {
            case '+' or '-':
                p = Math.Max(p1 - s1, p2 - s2) + Math.Max(s1, s2) + 1;
                s = Math.Max(s1, s2);
                break;
            case '*':
                p = p1 + p2 + 1;
                s = s1 + s2;
                break;
            case '/':
                s = Math.Max(6, s1 + p2 + 1);
                p = p1 - s1 + s2 + s;
                break;
            case '%':
                p = Math.Min(p1 - s1, p2 - s2) + Math.Max(s1, s2);
                s = Math.Max(s1, s2);
                break;
            default:
                throw new NotSupportedException($"Decimal arithmetic operator '{op}' isn't supported.");
        }

        // 38-precision cap: scale reduces by the excess, but never below
        // min(originalScale, 6). For division s is always ≥ 6 so the floor
        // effectively becomes 6; for the other operators the floor binds
        // only when the original scale was already ≤ 6, preserving it.
        if (p > 38)
        {
            s = Math.Max(s - (p - 38), Math.Min(s, 6));
            p = 38;
        }
        if (s < 0) s = 0;
        return (DecimalSqlType)GetDecimal(p, s);
    }

    /// <summary>
    /// Canonicalizes any decimal-arithmetic-eligible operand type to its
    /// (precision, scale) pair. Decimals return their declared p/s; money
    /// and integers map to documented equivalents.
    /// </summary>
    private static (int Precision, int Scale) AsDecimalPrecisionScale(SqlType type) =>
        type is DecimalSqlType d ? (d.precision, d.scale)
        : IsMoneyCategory(type) ? MoneyAsDecimal(type)
        : IntegerAsDecimal(type);

    private static DecimalSqlType IntegerAsDecimalType(SqlType integer)
    {
        var (p, s) = IntegerAsDecimal(integer);
        return DecimalSqlType.Get(p, s);
    }

    private static DecimalSqlType MoneyAsDecimalType(SqlType money)
    {
        var (p, s) = MoneyAsDecimal(money);
        return DecimalSqlType.Get(p, s);
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
    public static readonly VarcharSqlType Varchar = new();

    /// <remarks>
    /// Stored as UTF-16 LE bytes (2 bytes per BMP code unit, surrogate pairs for
    /// supplementary characters), matching SQL Server's on-disk nvarchar layout.
    /// </remarks>
    public static readonly NVarcharSqlType NVarchar = new();

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
    public static readonly VarbinarySqlType Varbinary = new();

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
    /// SQL Server's <c>char(N)</c>: fixed-length CP1252 string. Each declared
    /// length is a distinct singleton reachable through this accessor.
    /// </remarks>
    public static SqlType GetChar(int length) => CharSqlType.Get(length);

    /// <remarks>
    /// SQL Server's <c>nchar(N)</c>: fixed-length UTF-16 string. Each declared
    /// length is a distinct singleton reachable through this accessor.
    /// </remarks>
    public static SqlType GetNChar(int length) => NCharSqlType.Get(length);

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
        // of the same SqlType singleton. HeapColumn.MaxLength carries the
        // sentinel; row-level encoder uses it to route the column through LOB
        // storage when present.
        if (declaredMaxLength == MaxLengthSentinel)
            return (resolved, MaxLengthSentinel);

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
                (null, true) => SimulatedSqlException.NVarcharSizeExceedsMaximumCast("nvarchar", declared),
                (null, false) => SimulatedSqlException.SizeExceedsMaximumCast(resolved.ToString()!, declared, max),
            };
        }
        return (resolved, declared);
    }

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
            _ => null,
        },
        8 => upper switch
        {
            "SMALLINT" => SmallInt,
            "NVARCHAR" => NVarchar,
            "DATETIME" => DateTime,
            _ => null,
        },
        9 => upper switch
        {
            "TIMESTAMP" => RowVersion,
            "VARBINARY" => Varbinary,
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
