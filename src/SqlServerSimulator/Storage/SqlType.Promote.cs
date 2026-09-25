using SqlServerSimulator.Parser;

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
    /// The common type of <paramref name="a"/> and <paramref name="b"/> — the
    /// unification CASE / COALESCE / the set operators perform, and the type a
    /// comparison converts both operands to. Raises real's own error for a
    /// pair that has none (<see cref="PairError"/>'s unification grid);
    /// otherwise the higher-<see cref="Precedence"/> operand wins, with the
    /// families that share a joint envelope (numbers, dates and times,
    /// character strings and binaries) widening to cover both.
    /// </summary>
    /// <remarks>
    /// A binary operand meeting a character string unifies as the string
    /// family's own shape of the binary's byte length — <c>binary(N)</c> as
    /// the fixed form, <c>varbinary(N)</c> as the variable one — so
    /// <c>char(3)</c> with <c>varbinary(4)</c> is <c>varchar(4)</c> and
    /// <c>nchar(3)</c> with <c>binary(2)</c> stays <c>nchar(3)</c>
    /// (probe-confirmed against SQL Server 2025). Against a number, a
    /// date or a <c>uniqueidentifier</c> the binary simply converts to the
    /// partner's type.
    /// </remarks>
    public static SqlType Promote(SqlType a, SqlType b)
    {
        if (a == b)
            return a;
        if (PairError(TypePairOperation.Unify, a, b, "") is { } error)
            throw error;
        return PromoteUnifiable(a, b);
    }

    /// <summary>
    /// <see cref="Promote"/> for operands whose diagnostic
    /// may spell a <c>decimal</c> as <c>numeric</c>.
    /// </summary>
    public static SqlType PromoteOperands(TypePairOperand a, TypePairOperand b)
    {
        if (a.Type == b.Type)
            return a.Type;
        if (OperandPairError(TypePairOperation.Unify, a, b, "") is { } error)
            throw error;
        return CapUnifiedDecimal(PromoteUnifiable(a.Type, b.Type), a.Type, b.Type);
    }

    /// <summary>
    /// The decimal a set of arms unifies to when their joint envelope — the
    /// widest integral part beside the widest scale — passes 38 digits: real
    /// keeps the integral digits and gives the scale what's left, so
    /// <c>COALESCE(&lt;decimal(38, 18)&gt;, &lt;decimal(30, 0)&gt;)</c> is
    /// <c>decimal(38, 8)</c> (probed 2026-09-25 against SQL Server 2025).
    /// Only a unified result takes this; a comparison's common type keeps the
    /// scale, so no arm's fraction is lost to it.
    /// </summary>
    private static SqlType CapUnifiedDecimal(SqlType unified, SqlType a, SqlType b)
    {
        if (unified is not DecimalSqlType || DecimalEnvelope(a) is not var (pa, sa) || DecimalEnvelope(b) is not var (pb, sb))
            return unified;
        var integral = Math.Max(pa - sa, pb - sb);
        var scale = Math.Max(sa, sb);
        return integral + scale > 38 ? GetDecimal(38, Math.Max(0, 38 - integral)) : unified;
    }

    /// <summary>The (precision, scale) an exact-numeric arm takes as a decimal, or null for any other.</summary>
    private static (int Precision, int Scale)? DecimalEnvelope(SqlType type) => type switch
    {
        DecimalSqlType d => (d.precision, d.scale),
        _ when type.Category == SqlTypeCategory.Integer => (IntegerAsDecimalType(type).precision, 0),
        _ when type == Money || type == SmallMoney => (MoneyAsDecimalType(type).precision, MoneyAsDecimalType(type).scale),
        _ => null,
    };

    private static SqlType PromoteUnifiable(SqlType a, SqlType b)
    {
        return a is SqlVariantSqlType || b is SqlVariantSqlType ? SqlVariant
            : IsNumericPairClass(a) && IsNumericPairClass(b) ? PromoteNumericPair(a, b)
            : a.Category == SqlTypeCategory.DateTime && b.Category == SqlTypeCategory.DateTime ? PromoteDateTime(a, b)
            : IsCharacterOrBinaryPairClass(a) && IsCharacterOrBinaryPairClass(b)
                ? (a.PairClass == TypePairClass.Binary && b.PairClass == TypePairClass.Binary ? PromoteBinaryFamily(a, b) : PromoteStringPair(a, b))
            : a.Precedence >= b.Precedence ? a : b;
    }

    private static bool IsNumericPairClass(SqlType type) =>
        type.PairClass is TypePairClass.Bit or TypePairClass.Integer or TypePairClass.ExactNumeric or TypePairClass.Approximate;

    private static bool IsCharacterOrBinaryPairClass(SqlType type) =>
        type.PairClass is TypePairClass.AnsiString or TypePairClass.UnicodeString or TypePairClass.Text or TypePairClass.Binary;

    /// <summary>
    /// Joint-envelope common type for a set of value branches (CASE / COALESCE /
    /// IIF / set-op columns), applying the two literal-sizing rules SQL Server
    /// uses across all of them: an untyped-NULL branch contributes nothing (it
    /// yields to any typed sibling; all-NULL falls back to <see cref="Int32"/>),
    /// and a non-negative integer-literal branch is sized
    /// <c>numeric(digit_count, 0)</c> when the set contains a decimal branch
    /// (probe-confirmed: <c>CASE … 1 … 2.5</c> → <c>numeric(2, 1)</c>,
    /// <c>SELECT 1 UNION SELECT 2.5</c> → <c>numeric(2, 1)</c>). Each branch is
    /// <c>(type, integerLiteralDigits)</c>; a digit count of <c>0</c> marks a
    /// non-literal. Callers pre-filter untyped NULLs out of the span.
    /// </summary>
    public static SqlType PromoteBranches(ReadOnlySpan<(SqlType Type, int IntegerLiteralDigits, Expression Source)> branches)
    {
        var hasDecimal = false;
        foreach (var branch in branches)
        {
            if (branch.Type.Category == SqlTypeCategory.Decimal)
            {
                hasDecimal = true;
                break;
            }
        }

        SqlType? accumulated = null;
        foreach (var branch in branches)
        {
            var effective = hasDecimal && branch.IntegerLiteralDigits > 0
                ? GetDecimal(branch.IntegerLiteralDigits, 0)
                : branch.Type;
            if (accumulated is not null && PairError(TypePairOperation.Unify, accumulated, effective, "") is { } pairError)
                throw BranchUnificationError(branches) ?? pairError;
            accumulated = accumulated is null ? effective : CapUnifiedDecimal(Promote(accumulated, effective), accumulated, effective);
        }
        return accumulated ?? Int32;
    }

    /// <summary>
    /// Real refuses a set of value arms by settling their type first — the
    /// highest <see cref="Precedence"/> among them — and then converting each
    /// arm to it in order, so the error names the first arm that can't convert,
    /// as written, against that type: <c>COALESCE(1, 2.25, @time)</c> is
    /// <c>int is incompatible with time</c>, not the <c>numeric(3, 2)</c> the
    /// first two arms unify to (probed 2026-09-25 against SQL Server 2025, which
    /// goes on to report every arm that fails; only the first is raised here).
    /// </summary>
    private static SimulatedSqlException? BranchUnificationError(ReadOnlySpan<(SqlType Type, int IntegerLiteralDigits, Expression Source)> branches)
    {
        var target = branches[0];
        foreach (var branch in branches)
        {
            if (branch.Type.Precedence > target.Type.Precedence)
                target = branch;
        }
        foreach (var branch in branches)
        {
            if (OperandPairError(TypePairOperation.Unify, new TypePairOperand(branch.Type, branch.Source), new TypePairOperand(target.Type, target.Source), "") is { } error)
                return error;
        }
        return null;
    }

    /// <summary>
    /// Binary-family unification: either operand being <c>varbinary(MAX)</c>
    /// makes the result <c>varbinary(MAX)</c> (DacFx's bacpac-export row-size
    /// sampler compares <c>varbinary(N)</c> columns against MAX-typed
    /// expressions); otherwise a mixed or variable pair widens to
    /// <c>varbinary</c> of the longer declared length, and a
    /// <c>binary</c>/<c>binary</c> pair keeps the longer fixed form.
    /// The unspecified length 0 stays unspecified so bare-<c>varbinary</c>
    /// operands keep their fall-through semantics.
    /// </summary>
    private static SqlType PromoteBinaryFamily(SqlType a, SqlType b)
    {
        var aLen = a is VarbinarySqlType va ? va.length : ((BinarySqlType)a).length;
        var bLen = b is VarbinarySqlType vb ? vb.length : ((BinarySqlType)b).length;
        return aLen == MaxLengthSentinel || bLen == MaxLengthSentinel ? VarbinaryMax
            : a is BinarySqlType && b is BinarySqlType ? (aLen >= bLen ? a : b)
            : aLen == 0 || bLen == 0 ? Varbinary
            : VarbinarySqlType.Get(Math.Max(aLen, bLen));
    }

    /// <summary>
    /// Two numbers: dispatched on the left operand's category, each arm
    /// switching on the right's, so both steps lower to jump tables.
    /// </summary>
    private static SqlType PromoteNumericPair(SqlType a, SqlType b) => a.Category switch
    {
        SqlTypeCategory.Approximate => PromoteFromApproximate(a, b),
        SqlTypeCategory.Decimal => PromoteFromDecimal(a, b),
        SqlTypeCategory.Money => PromoteFromMoney(a, b),
        _ => PromoteFromInteger(a, b),
    };

    /// <summary>
    /// Approximate (float/real) wins over every other numeric partner. When
    /// both sides are approximate, <c>float</c> wins over <c>real</c>.
    /// </summary>
    private static SqlType PromoteFromApproximate(SqlType a, SqlType b) =>
        b.Category == SqlTypeCategory.Approximate ? (a == Float || b == Float ? Float : Real) : a;

    /// <summary>
    /// Decimal vs decimal widens to the joint envelope. Decimal vs integer
    /// or money canonicalizes the partner to its decimal equivalent
    /// (bit→(1,0) … bigint→(19,0); money→(19,4); smallmoney→(10,4)) and
    /// rerun the envelope rule.
    /// </summary>
    private static SqlType PromoteFromDecimal(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate => b,
        SqlTypeCategory.Decimal => PromoteDecimalPair((DecimalSqlType)a, (DecimalSqlType)b),
        SqlTypeCategory.Integer => PromoteDecimalPair((DecimalSqlType)a, IntegerAsDecimalType(b)),
        _ => PromoteDecimalPair((DecimalSqlType)a, MoneyAsDecimalType(b)),
    };

    /// <summary>
    /// Money pairings: money vs money picks money over smallmoney; money
    /// vs integer keeps the money type; money vs decimal canonicalizes the
    /// money side and widens; money vs float/real promotes to float/real.
    /// </summary>
    private static SqlType PromoteFromMoney(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Approximate => b,
        SqlTypeCategory.Decimal => PromoteDecimalPair(MoneyAsDecimalType(a), (DecimalSqlType)b),
        SqlTypeCategory.Money => a == Money || b == Money ? Money : SmallMoney,
        _ => a,
    };

    /// <summary>
    /// Integer vs each numeric category. Integer canonicalizes to its
    /// decimal equivalent for a decimal partner; the wider integer wins an
    /// integer pair.
    /// </summary>
    private static SqlType PromoteFromInteger(SqlType a, SqlType b) => b.Category switch
    {
        SqlTypeCategory.Decimal => PromoteDecimalPair(IntegerAsDecimalType(a), (DecimalSqlType)b),
        SqlTypeCategory.Integer => a.Precedence >= b.Precedence ? a : b,
        _ => b,
    };

    /// <summary>
    /// Joint-envelope unification of two character operands — or one
    /// character operand and a binary, which unifies as the character family's
    /// own form of its byte length — for CASE / COALESCE / NULLIF / IIF /
    /// set-op / comparison common-type decisions. The result takes the
    /// <b>maximum declared width</b> of the two operands (not the sum — that's
    /// concatenation, in <see cref="PromoteForArithmetic"/>) so a projected
    /// column is wide enough for either arm's value: probe-confirmed against
    /// SQL Server 2025 (<c>CASE … 'ab' … 'wxyz'</c> → <c>varchar(4)</c>,
    /// <c>… 'ab' … N'wxyz'</c> → <c>nvarchar(4)</c>, <c>SELECT 'ab' UNION ALL
    /// SELECT 'wxyz'</c> → <c>varchar(4)</c>). National family (nvarchar /
    /// nchar / sysname) wins over the CP1252 family and the width stays
    /// measured in characters across the family change; a fixed pair (char /
    /// nchar / binary) stays fixed, and any variable operand drops the result
    /// to the variable form. Either operand at <see cref="MaxLengthSentinel"/>
    /// makes the result MAX. The length-unspecified sentinel (0) contributes 0
    /// to the max so a bare var* operand yields to a sized partner. A
    /// <c>text</c> / <c>ntext</c> operand outranks every bounded string, so the
    /// higher-ranked legacy type is the result as it stands.
    /// </summary>
    private static SqlType PromoteStringPair(SqlType a, SqlType b)
    {
        if (a.PairClass == TypePairClass.Text || b.PairClass == TypePairClass.Text)
            return a.Precedence >= b.Precedence ? a : b;

        var aLen = UnifiedStringLength(a);
        var bLen = UnifiedStringLength(b);

        // sysname is nvarchar(128) under an alias, so it carries the national
        // family into a promotion the way an nvarchar does (probe-confirmed:
        // real types `TYPE_NAME(56) + ''` as nvarchar(129)).
        var national = a.PairClass == TypePairClass.UnicodeString || b.PairClass == TypePairClass.UnicodeString;
        var fixedLength = a is CharSqlType or NCharSqlType or BinarySqlType && b is CharSqlType or NCharSqlType or BinarySqlType;

        // A binary operand has no collation to offer, so the character side's
        // stands; between two character operands it is the coercibility rule.
        var (collation, coercibility) = a.PairClass == TypePairClass.Binary ? (b.Collation ?? Collation.Baseline, b.Coercibility)
            : b.PairClass == TypePairClass.Binary ? (a.Collation ?? Collation.Baseline, a.Coercibility)
            : Collation.Resolve(a, b) ?? (a.Collation ?? b.Collation ?? Collation.Baseline, Coercibility.CoercibleDefault);

        if (aLen == MaxLengthSentinel || bLen == MaxLengthSentinel)
        {
            return national
                ? NVarcharSqlType.Get(MaxLengthSentinel, collation, coercibility)
                : VarcharSqlType.Get(MaxLengthSentinel, collation, coercibility);
        }

        var max = Math.Max(aLen, bLen);
        if (max == 0)
        {
            return national
                ? NVarcharSqlType.Get(0, collation, coercibility)
                : VarcharSqlType.Get(0, collation, coercibility);
        }

        var capped = Math.Min(national ? 4000 : 8000, max);
        return (national, fixedLength) switch
        {
            (true, true) => NCharSqlType.Get(capped, collation, coercibility),
            (true, false) => NVarcharSqlType.Get(capped, collation, coercibility),
            (false, true) => CharSqlType.Get(capped, collation, coercibility),
            (false, false) => VarcharSqlType.Get(capped, collation, coercibility),
        };
    }

    /// <summary>
    /// Declared width an operand contributes to <see cref="PromoteStringPair"/>'s
    /// max-width unification: the bounded character and binary families carry
    /// it on the type, and <c>sysname</c> is <c>nvarchar(128)</c>.
    /// </summary>
    private static int UnifiedStringLength(SqlType type) => type switch
    {
        VarcharSqlType v => v.length,
        NVarcharSqlType nv => nv.length,
        CharSqlType c => c.length,
        NCharSqlType nc => nc.length,
        VarbinarySqlType vb => vb.length,
        BinarySqlType bin => bin.length,
        _ => 128,
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
    public static SqlType PromoteForArithmetic(SqlType a, SqlType b, char op) =>
        PromoteOperandsForArithmetic(new TypePairOperand(a), new TypePairOperand(b), op);

    /// <summary>
    /// <see cref="PromoteForArithmetic"/> for callers
    /// holding the operand expressions, whose refusal names the operands the
    /// way real's does (<c>numeric</c>, a column's object for Msg 260).
    /// </summary>
    public static SqlType PromoteOperandsForArithmetic(TypePairOperand left, TypePairOperand right, char op)
    {
        var a = left.Type;
        var b = right.Type;
        if (op is '&' or '|' or '^')
            return PromoteForBitwise(left, right, op);

        if (OperandPairError(ArithmeticOperation(op), left, right, ArithmeticOperatorWord(op)) is { } error)
            throw error;

        // A legacy datetime partner makes the result that datetime: real
        // converts the other operand to it and adds or subtracts day counts.
        // Only + and - get here, the grids refusing every other operator.
        if (a.PairClass == TypePairClass.LegacyDateTime || b.PairClass == TypePairClass.LegacyDateTime)
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
        var aCharacter = a.PairClass is TypePairClass.AnsiString or TypePairClass.UnicodeString;
        var bCharacter = b.PairClass is TypePairClass.AnsiString or TypePairClass.UnicodeString;
        if (aCharacter && bCharacter)
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

        // Binary + binary (a timestamp included) concatenates.
        var aBinary = a.PairClass is TypePairClass.Binary or TypePairClass.Timestamp;
        var bBinary = b.PairClass is TypePairClass.Binary or TypePairClass.Timestamp;
        if (aBinary && bBinary)
            return BinaryConcatResultType(a, b);

        // Every other legal pair has a number on at least one side, and a
        // string, binary or timestamp operand converts to that number's type —
        // so `decimal(5,2) + 0x…` is the decimal pair's decimal(6,2), and
        // `bigint + '3'` stays bigint (probe-confirmed against SQL Server 2025).
        // bit does the same against a decimal: `bit * decimal(5,2)` is
        // decimal(11,4), the decimal(5,2) pair's own product, where every other
        // integer brings its own digits (`tinyint * decimal(5,2)` is decimal(9,2)).
        if (aCharacter || aBinary || (a == Bit && b is DecimalSqlType))
            a = b;
        else if (bCharacter || bBinary || (b == Bit && a is DecimalSqlType))
            b = a;

        // Float / real win over everything else, same as the joint-envelope
        // path; no decimal-style scale dance needed.
        if (a.Category == SqlTypeCategory.Approximate || b.Category == SqlTypeCategory.Approximate)
            return Promote(a, b);

        // Decimal-involving cases: the per-operator formula applies. Money
        // and integer canonicalize to their decimal equivalent so the
        // formula sees a uniform (precision, scale) pair on both sides.
        if (a is DecimalSqlType || b is DecimalSqlType)
        {
            var (p1, s1) = AsDecimalPrecisionScale(a);
            var (p2, s2) = AsDecimalPrecisionScale(b);
            return ComputeDecimalArithmeticResultType(p1, s1, p2, s2, op);
        }

        // Pure integer / money pairs: arithmetic result type matches the
        // joint-envelope rule (e.g., int + bigint → bigint; money + money →
        // money; int + int → int).
        return Promote(a, b);
    }

    private static TypePairOperation ArithmeticOperation(char op) => op switch
    {
        '+' => TypePairOperation.Add,
        '-' => TypePairOperation.Subtract,
        '*' => TypePairOperation.Multiply,
        '/' => TypePairOperation.Divide,
        _ => TypePairOperation.Modulo,
    };

    /// <summary>
    /// The operator as real's arithmetic diagnostics spell it: the word form
    /// for <c>+ - * / %</c> and the quoted character for the bitwise
    /// <c>&amp;</c>/<c>|</c>/<c>^</c> operators.
    /// </summary>
    internal static string ArithmeticOperatorWord(char op) => op switch
    {
        '+' => "add",
        '-' => "subtract",
        '*' => "multiply",
        '/' => "divide",
        '%' => "modulo",
        _ => $"'{op}'",
    };

    /// <summary>
    /// The bitwise operators' result type once the <see cref="TypePairOperation.Bitwise"/>
    /// grid has cleared the pair: an integer beside a string, a binary or a
    /// timestamp converts that partner to itself, and two integers promote.
    /// </summary>
    private static SqlType PromoteForBitwise(TypePairOperand left, TypePairOperand right, char op)
    {
        if (OperandPairError(TypePairOperation.Bitwise, left, right, ArithmeticOperatorWord(op)) is { } error)
            throw error;
        return left.Source is Parser.Expressions.Value { IsUntypedNull: true } ? right.Type
            : right.Source is Parser.Expressions.Value { IsUntypedNull: true } ? left.Type
            : Promote(left.Type, right.Type);
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

        // 38-precision cap, which splits by operator family.
        //
        // Multiply and divide reduce the scale by the whole excess, never
        // below min(originalScale, 6) — for division s is always ≥ 6 so the
        // floor effectively becomes 6, and for multiply it binds whenever the
        // excess would take the scale under 6 (probe-confirmed against SQL
        // Server 2025: decimal(20, 5) * decimal(23, 5) and decimal(25, 8) *
        // decimal(25, 8) both land on decimal(38, 6) where the raw excess
        // would give 4 and 3, while decimal(30, 3) / decimal(10, 4) keeps its
        // above-floor scale 7).
        //
        // Add and subtract instead give the integral part everything it needs
        // and hand the rest to the scale: the +1 carry digit is dropped first
        // and there is no 6-floor, so the result scale is whatever is left
        // beside the widest operand's integral part. Probe-confirmed:
        // decimal(38, 20) + decimal(38, 20) keeps scale 20, decimal(38, 10) +
        // decimal(38, 30) is decimal(38, 10), decimal(30, 20) + decimal(38, 30)
        // is decimal(38, 28), and decimal(38, 38) + decimal(38, 0) is
        // decimal(38, 0) — the excess rule would give 19 / 9 / 27 / 6.
        //
        // Modulo never reaches the cap: its precision is
        // min(p1-s1, p2-s2) + max(s1, s2), which 38-wide operands bound at 38.
        if (p > 38)
        {
            s = op is '+' or '-'
                ? Math.Min(s, 38 - Math.Max(p1 - s1, p2 - s2))
                : Math.Max(s - (p - 38), Math.Min(s, 6));
            p = 38;
        }
        if (s < 0) s = 0;
        return (DecimalSqlType)GetDecimal(p, s);
    }

    /// <summary>
    /// Whether a <c>*</c> or <c>/</c> over these operand types lands on a
    /// <c>decimal</c> whose scale the 38-digit cap cut below what the operator
    /// would otherwise give, which <c>SET NUMERIC_ROUNDABORT ON</c> refuses
    /// (Msg 8115 state 1) whatever the operands' values; an uncapped quotient
    /// rounds silently even when it can't be exact, and addition, subtraction
    /// and modulo never refuse (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    internal static bool DecimalScaleIsCapped(SqlType a, SqlType b, char op)
    {
        if (op is not ('*' or '/')
            || !IsDecimalArithmeticOperand(a)
            || !IsDecimalArithmeticOperand(b)
            || PromoteForArithmetic(a, b, op) is not DecimalSqlType result)
        {
            return false;
        }
        var (_, s1) = AsDecimalPrecisionScale(a);
        var (p2, s2) = AsDecimalPrecisionScale(b);
        return result.scale < (op == '*' ? s1 + s2 : Math.Max(6, s1 + p2 + 1));
    }

    private static bool IsDecimalArithmeticOperand(SqlType type) =>
        type is DecimalSqlType || IsMoneyCategory(type) || IsIntegerCategory(type);

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
    /// Date/time-category promotion: the highest-precedence family present,
    /// with a fractional precision that's the max of the two participants.
    /// Legacy <c>datetime</c> contributes scale 3 to that max (matching its
    /// 1/300-second display granularity) and <c>time</c> its own precision, so
    /// <c>time(7)</c> with <c>datetime2(3)</c> is <c>datetime2(7)</c>. The one
    /// pair with no common type — <c>date</c> with <c>time</c> — never gets
    /// here; <see cref="PairError"/> refuses it first.
    /// </summary>
    private static SqlType PromoteDateTime(SqlType a, SqlType b)
    {
        if (a is TimeSqlType ta && b is TimeSqlType tb)
            return ta.precision >= tb.precision ? a : b;

        var precision = Math.Max(PrecisionForPromotion(a), PrecisionForPromotion(b));
        return a is DateTimeOffsetSqlType || b is DateTimeOffsetSqlType ? GetDateTimeOffset(precision)
            : a is DateTime2SqlType || b is DateTime2SqlType ? GetDateTime2(precision)
            : a == DateTime || b == DateTime ? DateTime
            : a == SmallDateTime || b == SmallDateTime ? SmallDateTime
            : Date;
    }

    private static int PrecisionForPromotion(SqlType type) => type switch
    {
        DateTime2SqlType dt2 => dt2.precision,
        DateTimeOffsetSqlType dto => dto.precision,
        TimeSqlType time => time.precision,
        _ when type == DateTime => 3,
        _ => 0,
    };

    /// <summary>
    /// Concatenation result type for binary <c>+</c> binary. Both fixed-length
    /// binary operands give <c>binary(N+M)</c>; any varbinary participant gives
    /// <c>varbinary(N+M)</c>; the summed length caps at 8000, and a MAX operand
    /// makes the result MAX. Length-unspecified operands (hex literals carry no
    /// static length) fall back to the unspecified varbinary form.
    /// </summary>
    private static SqlType BinaryConcatResultType(SqlType a, SqlType b)
    {
        if (a is VarbinarySqlType { length: MaxLengthSentinel } || b is VarbinarySqlType { length: MaxLengthSentinel })
            return VarbinaryMax;

        var summed = Math.Min(8000, BinaryLength(a) + BinaryLength(b));
        return summed <= 0 ? Varbinary
            : a is BinarySqlType && b is BinarySqlType ? GetBinary(summed)
            : VarbinarySqlType.Get(summed);
    }

    /// <summary>
    /// A binary operand's declared width in a concatenation. A timestamp
    /// counts 40 rather than its 8 bytes: real types <c>varbinary(4) +
    /// timestamp</c> as <c>varbinary(44)</c> and <c>timestamp + timestamp</c>
    /// as <c>varbinary(80)</c>, though the value is the 8-byte concatenation
    /// (probe-confirmed against SQL Server 2025, 2026-09-23).
    /// </summary>
    private static int BinaryLength(SqlType type) => type switch
    {
        BinarySqlType binary => binary.length,
        VarbinarySqlType varbinary => varbinary.length > 0 ? varbinary.length : 0,
        RowVersionSqlType => 40,
        _ => 0,
    };

    /// <summary>
    /// Computes the result type for a string-+-string operand pair when at
    /// least one side is variable-length. National-string family wins; the
    /// declared-length sum is capped at the family's maximum (4000 for
    /// nvarchar, 8000 for varchar). The legacy LOB types never get here —
    /// real refuses to concatenate them (Msg 402, in the pair grids).
    /// </summary>
    private static SqlType StringConcatResult(SqlType a, SqlType b)
    {
        var national = a is NVarcharSqlType or NCharSqlType or SystemNameSqlType
            || b is NVarcharSqlType or NCharSqlType or SystemNameSqlType;

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

        var aLen = StringLengthForConcat(a);
        var bLen = StringLengthForConcat(b);
        if (aLen == MaxLengthSentinel || bLen == MaxLengthSentinel)
        {
            // Either operand being (MAX) makes the concatenation (MAX) —
            // matching real SQL Server (same max-ness propagation CONCAT
            // applies). Summing the sentinel instead produced a negative
            // length: nvarchar(max) + nvarchar(max) threw at Run time (the
            // exception killed TDS sessions as a transport error — DacFx's
            // data-phase row-size sampler builds its dynamic SQL exactly
            // that way), and (MAX) + bounded silently typed the result as
            // a tiny bounded string.
            return national
                ? NVarcharSqlType.Get(MaxLengthSentinel, resultCollation, resultCoercibility)
                : VarcharSqlType.Get(MaxLengthSentinel, resultCollation, resultCoercibility);
        }

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
    /// singleton; <c>sysname</c> is the <c>nvarchar(128)</c> it aliases
    /// (probe-confirmed: <c>sysname + varchar(10)</c> is <c>nvarchar(138)</c>).
    /// The unspecified-length form (length=0) and the LOB families (text /
    /// ntext) report 0, which the caller interprets as "fall back to the
    /// unspecified result form."
    /// </summary>
    private static int StringLengthForConcat(SqlType type) => type switch
    {
        SystemNameSqlType => 128,
        CharSqlType c => c.length,
        NCharSqlType nc => nc.length,
        VarcharSqlType v => v.length,
        NVarcharSqlType nv => nv.length,
        _ => 0,
    };

    /// <summary>
    /// True when every value of <paramref name="source"/> reaches
    /// <paramref name="target"/> unchanged — no range overflow, no rounding,
    /// no truncation — through the implicit conversion <see cref="Promote"/> /
    /// <see cref="PromoteBranches"/> inserts on an arm it didn't pick.
    /// </summary>
    /// <remarks>
    /// <para>SQL Server's projection-nullability inference asks exactly this of
    /// every CASE-family arm and GREATEST / LEAST argument: an arm whose
    /// conversion to the unified type could alter the value reads nullable.
    /// That is what makes <c>COALESCE(&lt;decimal(9, 2) column&gt;, 0)</c>
    /// nullable — the <c>int</c> literal's ten integral digits don't fit the
    /// column's seven, and the unification narrows to the column's own width
    /// because the literal's <em>value</em> is one digit wide — while
    /// <c>COALESCE(&lt;decimal(9, 2) column&gt;, 0.0)</c> is NOT NULL.
    /// Probe-confirmed against SQL Server 2025 cell for cell through both
    /// <c>sys.dm_exec_describe_first_result_set</c> and a <c>SELECT … INTO</c>
    /// destination's <c>is_nullable</c>.</para>
    /// <para>Loss, not failure, is the test real applies: <c>decimal</c> →
    /// <c>float</c> and <c>datetime</c> → <c>datetime2</c> can't raise, yet
    /// both read nullable because the target's grid doesn't carry the source's
    /// values exactly.</para>
    /// </remarks>
    public static bool ConversionPreservesEveryValue(SqlType source, SqlType target) =>
        source == target
        || (source is VarbinarySqlType or BinarySqlType ? BinaryConversionPreservesEveryValue(source, target)
        : source.Category switch
        {
            SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money =>
                ExactNumericConversionPreservesEveryValue(source, target),
            // A float carries every real; the reverse drops mantissa bits.
            SqlTypeCategory.Approximate => source == Real && target == Float,
            SqlTypeCategory.String => StringConversionPreservesEveryValue(source, target),
            SqlTypeCategory.DateTime => DateTimeConversionPreservesEveryValue(source, target),
            _ => false,
        });

    /// <summary>
    /// The integral-digit / scale shape an exact-numeric type occupies, with
    /// the integer and money families canonicalized through their
    /// decimal-equivalent precision and scale.
    /// </summary>
    private static (int Integral, int Scale) ExactNumericShape(SqlType type)
    {
        var (precision, scale) = type.Category switch
        {
            SqlTypeCategory.Integer => IntegerAsDecimal(type),
            SqlTypeCategory.Money => MoneyAsDecimal(type),
            _ => (((DecimalSqlType)type).precision, ((DecimalSqlType)type).scale),
        };
        return (precision - scale, scale);
    }

    /// <summary>
    /// Exact-numeric source: another exact numeric keeps every value when it
    /// has room for both the integral digits and the scale, and a binary
    /// mantissa keeps them only when the source is integral and its whole
    /// range lands on integers the mantissa represents exactly — every
    /// 15-digit integer fits <c>float</c>'s 2^53 and every 7-digit one fits
    /// <c>real</c>'s 2^24, which is where <c>decimal(15, 0)</c> → <c>float</c>
    /// and <c>decimal(16, 0)</c> → <c>float</c> part company.
    /// </summary>
    private static bool ExactNumericConversionPreservesEveryValue(SqlType source, SqlType target)
    {
        var (integral, scale) = ExactNumericShape(source);
        switch (target.Category)
        {
            case SqlTypeCategory.Integer:
            case SqlTypeCategory.Decimal:
            case SqlTypeCategory.Money:
                var (targetIntegral, targetScale) = ExactNumericShape(target);
                return targetIntegral >= integral && targetScale >= scale;
            case SqlTypeCategory.Approximate:
                return scale == 0 && integral <= (target == Float ? 15 : 7);
            default:
                return false;
        }
    }

    /// <summary>
    /// String source: a UTF-16 source needs a UTF-16 target (the ANSI half
    /// converts up freely), and the target has to be at least as long. An
    /// unspecified length on either side pins nothing, so it reports preserved
    /// rather than inventing a truncation.
    /// </summary>
    private static bool StringConversionPreservesEveryValue(SqlType source, SqlType target)
    {
        if (target.Category != SqlTypeCategory.String || (IsNationalStringCategory(source) && !IsNationalStringCategory(target)))
            return false;

        var sourceLength = DeclaredStringLength(source);
        var targetLength = DeclaredStringLength(target);
        return sourceLength == 0 || targetLength is 0 or MaxLengthSentinel
            || (sourceLength != MaxLengthSentinel && targetLength >= sourceLength);
    }

    /// <summary>
    /// Declared length for the value-preservation test: the bounded families
    /// carry it on the type, the legacy LOB families report the MAX sentinel,
    /// and <c>sysname</c> is <c>nvarchar(128)</c>.
    /// </summary>
    private static int DeclaredStringLength(SqlType type) => type switch
    {
        CharSqlType c => c.length,
        NCharSqlType nc => nc.length,
        VarcharSqlType v => v.length,
        NVarcharSqlType nv => nv.length,
        SystemNameSqlType => 128,
        _ => MaxLengthSentinel,
    };

    /// <summary>
    /// Binary source: preserved when the target is a binary family at least as
    /// long, with the same unspecified-length reading as the string rule.
    /// </summary>
    private static bool BinaryConversionPreservesEveryValue(SqlType source, SqlType target)
    {
        if (target is not (VarbinarySqlType or BinarySqlType))
            return false;

        var sourceLength = source is VarbinarySqlType sourceVarbinary ? sourceVarbinary.length : ((BinarySqlType)source).length;
        var targetLength = target is VarbinarySqlType targetVarbinary ? targetVarbinary.length : ((BinarySqlType)target).length;
        return sourceLength == 0 || targetLength is 0 or MaxLengthSentinel
            || (sourceLength != MaxLengthSentinel && targetLength >= sourceLength);
    }

    /// <summary>
    /// Date/time source: the target has to carry every component the source
    /// has, cover its range, and land on a grid at least as fine.
    /// <c>date</c> reaches <c>datetime</c> / <c>smalldatetime</c> only through
    /// a range both of them fall short of (they start in 1753 / 1900), and
    /// <c>datetime</c>'s 1/300-second grid lands on no other type's ticks at
    /// all — which is why <c>datetime</c> → <c>datetime2(7)</c> reads nullable
    /// while <c>smalldatetime</c> → <c>datetime</c> does not.
    /// </summary>
    private static bool DateTimeConversionPreservesEveryValue(SqlType source, SqlType target) => source switch
    {
        DateSqlType => target is DateTime2SqlType or DateTimeOffsetSqlType,
        SmallDateTimeSqlType => target is DateTimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType,
        TimeSqlType time => target switch
        {
            TimeSqlType t => t.precision >= time.precision,
            DateTime2SqlType d => d.precision >= time.precision,
            DateTimeOffsetSqlType o => o.precision >= time.precision,
            _ => false,
        },
        DateTime2SqlType datetime2 => target switch
        {
            DateTime2SqlType d => d.precision >= datetime2.precision,
            DateTimeOffsetSqlType o => o.precision >= datetime2.precision,
            _ => false,
        },
        DateTimeOffsetSqlType offset => target is DateTimeOffsetSqlType o && o.precision >= offset.precision,
        _ => false,
    };
}
