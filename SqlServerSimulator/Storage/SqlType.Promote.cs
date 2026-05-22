namespace SqlServerSimulator.Storage;

/// <summary>
/// Type-promotion rules: <see cref="Promote"/> for joint-envelope unification
/// (CASE / COALESCE / set ops / comparison common type) and
/// <see cref="PromoteForArithmetic"/> for the per-operator precision/scale
/// formulas SQL Server applies to <c>+</c> / <c>-</c> / <c>*</c> / <c>/</c> /
/// <c>%</c>. The two paths must agree on result type for non-decimal pairs;
/// they intentionally diverge for decimal-involving arithmetic, where each
/// operator has its own probed precision/scale rule.
/// </summary>
internal abstract partial class SqlType
{
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
    /// Integer vs string keeps the integer's specific type (probe-confirmed
    /// against SQL Server 2025: <c>tinyint + '3'</c> → tinyint,
    /// <c>bigint + '3'</c> → bigint); the string side parses through the
    /// integer's CAST path at runtime, which fails with Msg 245 for
    /// decimal-shaped strings (<c>'5.5'</c>, <c>'5.0'</c>) — SQL Server
    /// does not route through decimal even when the string represents an
    /// exact-integer value with a fractional zero. Integer vs date/time
    /// only succeeds for the legacy datetime types (datetime,
    /// smalldatetime); other date/time types raise the operand-type-clash
    /// error.
    /// </summary>
    private static SqlType PromoteFromInteger(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate => b,
        SqlTypeCategory.Decimal => PromoteDecimalPair(IntegerAsDecimalType(a), (DecimalSqlType)b),
        SqlTypeCategory.Money => b,
        SqlTypeCategory.Integer => a.Precedence >= b.Precedence ? a : b,
        SqlTypeCategory.String => a,
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
    /// promotes to uid; string vs integer keeps the integer's specific
    /// type (so <c>'5' + cast(3 as bigint)</c> → bigint).
    /// </summary>
    private static SqlType PromoteFromString(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.DateTime or SqlTypeCategory.UniqueIdentifier or SqlTypeCategory.Integer => b,
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

        // String + string is concatenation, not arithmetic. Lengths combine
        // as min(8000, N+M) for varchar/char pairs, min(4000, N+M) for any
        // pair containing nvarchar/nchar (which dominate the family choice).
        // Probe-confirmed against SQL Server 2025 (2026-05-09): `char(5) +
        // char(5)` → char(10); `varchar(10) + varchar(20)` → varchar(30);
        // `char(8000) + char(100)` → char(8000). Pure char/nchar pairs keep
        // fixed-length-ness; the moment a variable-length operand enters,
        // the result drops to var* of the combined length. Length-unspecified
        // operands (length=0 sentinel — e.g. CAST/runtime forms that haven't
        // pinned a length) treat the missing operand as length 0 in the sum,
        // mirroring the no-info-available behavior.
        if (op == '+' && a.Category == SqlTypeCategory.String && b.Category == SqlTypeCategory.String)
        {
            return (a, b) switch
            {
                (CharSqlType ca, CharSqlType cb) => GetChar(Math.Min(8000, ca.length + cb.length)),
                (NCharSqlType na, NCharSqlType nb) => GetNChar(Math.Min(4000, na.length + nb.length)),
                (CharSqlType caMix, NCharSqlType nbMix) => GetNChar(Math.Min(4000, caMix.length + nbMix.length)),
                (NCharSqlType naMix, CharSqlType cbMix) => GetNChar(Math.Min(4000, naMix.length + cbMix.length)),
                _ => StringConcatResult(a, b),
            };
        }

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

    /// <summary>
    /// Computes the result type for a string-+-string operand pair when at
    /// least one side is variable-length. National-string family wins; the
    /// declared-length sum is capped at the family's maximum (4000 for
    /// nvarchar, 8000 for varchar). LOB operands (text / ntext) drop the
    /// result to the unspecified-length form because LOB has no per-cell
    /// width to add.
    /// </summary>
    private static SqlType StringConcatResult(SqlType a, SqlType b)
    {
        var national = a is NVarcharSqlType or NCharSqlType
            || b is NVarcharSqlType or NCharSqlType
            || a == NText || b == NText;

        // Resolve the result collation via SQL Server's collation-coercibility
        // resolution over the input operands; mismatched same-rank operands
        // hand back null (resolved later by the operator's runtime path,
        // which raises Msg 468). For static-typing here we fall back to
        // the higher-rank input's collation when Resolve returns null so the
        // schema still computes; the runtime side gets the authoritative
        // error rendering with operator wording.
        var resolved = Collation.Resolve(a, b);
        var (resultCollation, resultCoercibility) = resolved
            ?? (a.Collation ?? b.Collation ?? Collation.Baseline, Coercibility.CoercibleDefault);

        if (a == Text || b == Text || a == NText || b == NText)
        {
            return national
                ? NVarcharSqlType.Get(0, resultCollation, resultCoercibility)
                : VarcharSqlType.Get(0, resultCollation, resultCoercibility);
        }

        var aLen = StringLengthForConcat(a);
        var bLen = StringLengthForConcat(b);
        if (aLen == 0 || bLen == 0)
        {
            return national
                ? NVarcharSqlType.Get(0, resultCollation, resultCoercibility)
                : VarcharSqlType.Get(0, resultCollation, resultCoercibility);
        }

        var max = national ? 4000 : 8000;
        var summed = Math.Min(max, aLen + bLen);
        return national
            ? NVarcharSqlType.Get(summed, resultCollation, resultCoercibility)
            : VarcharSqlType.Get(summed, resultCollation, resultCoercibility);
    }

    /// <summary>
    /// Returns the declared length of a string operand for use in
    /// <see cref="StringConcatResult"/>. char(N) / nchar(N) carry length on
    /// the type; varchar(N) / nvarchar(N) carry it via the per-length
    /// singleton. The unspecified-length form (length=0) and LOB families
    /// (text / ntext / sysname) report 0, which the caller interprets as
    /// "fall back to the unspecified result form."
    /// </summary>
    private static int StringLengthForConcat(SqlType type) => type switch
    {
        CharSqlType c => c.length,
        NCharSqlType nc => nc.length,
        VarcharSqlType v => v.length,
        NVarcharSqlType nv => nv.length,
        _ => 0,
    };
}
