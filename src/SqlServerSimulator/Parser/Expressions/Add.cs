using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Add : TwoSidedExpression
{
    /// <summary>
    /// The session's <c>CONCAT_NULL_YIELDS_NULL</c> as parsing found it: when
    /// off, a NULL string operand reads as empty (probed 2026-09-24 against
    /// SQL Server 2025). The plan cache keys on the option.
    /// </summary>
    private readonly bool concatNullYieldsNull;

    internal Add(Expression left, Expression right, bool concatNullYieldsNull) : base(left, right) =>
        this.concatNullYieldsNull = concatNullYieldsNull;

    protected override SqlValue Run(SqlValue left, SqlValue right) =>
        IsStringConcatPair(left, right)
            ? StringConcatenation(left, right, this.concatNullYieldsNull)
            : left.Type.PairClass is TypePairClass.Binary or TypePairClass.Timestamp && right.Type.PairClass is TypePairClass.Binary or TypePairClass.Timestamp
                ? BinaryConcatenation(left, right)
                : AdditiveArithmetic(left, right, '+', static (a, b) => checked(a + b));

    /// <summary>
    /// Binary <c>+</c> binary concatenation: the two byte payloads are joined
    /// left-to-right. Result type is <c>binary(N+M)</c> when both operands are
    /// fixed-length binary, else <c>varbinary(N+M)</c> (capped at 8000) — the
    /// same type <see cref="SqlType.PromoteForArithmetic"/> computes for the
    /// projection schema. NULL propagates. Probe-confirmed against SQL Server
    /// 2025 (2026-07-14): <c>0x01 + 0x01 = 0x0101</c>,
    /// <c>cast(0x0102 as binary(2)) + cast(0x03 as binary(1)) = 0x010203</c>.
    /// </summary>
    private static SqlValue BinaryConcatenation(SqlValue left, SqlValue right)
    {
        var resultType = SqlType.PromoteForArithmetic(left.Type, right.Type, '+');
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(resultType);

        var leftBytes = left.AsBytes;
        var rightBytes = right.AsBytes;
        var joined = new byte[leftBytes.Length + rightBytes.Length];
        leftBytes.CopyTo(joined, 0);
        rightBytes.CopyTo(joined, leftBytes.Length);
        // Joined bytes short of a fixed result come from the unpadded empty
        // binary(N) CONVERT's style 2 makes, and stay short: that operand
        // plus a binary(1) is one byte long (probed 2026-10-17 against SQL
        // Server 2025).
        return resultType is BinarySqlType binaryResult
            ? joined.Length < binaryResult.length ? SqlValue.UnpaddedFixedLength(binaryResult, joined) : SqlValue.FromBinary(binaryResult, joined)
            : SqlValue.FromVarbinary((VarbinarySqlType)resultType, joined);
    }

    /// <summary>
    /// Detects whether <c>+</c> should run as string concatenation rather
    /// than arithmetic. Both-string operands are obvious; the mixed
    /// non-null-string + NULL case covers SQL Server's "untyped NULL inherits
    /// the string side's type" rule for bare <c>NULL</c> literals
    /// (<c>'a' + NULL</c>). The simulator can't distinguish bare NULL from
    /// <c>cast(null as int)</c> at runtime — both surface as
    /// <see cref="SqlType.Int32"/> typed NULL — so this rule minor-diverges
    /// from real SQL Server on the rare typed-null-int case (real raises
    /// Msg 245 from a string-to-int parse; the simulator returns NULL).
    /// </summary>
    private static bool IsStringConcatPair(SqlValue left, SqlValue right)
    {
        var leftIsString = left.Type.Category == SqlTypeCategory.String;
        var rightIsString = right.Type.Category == SqlTypeCategory.String;
        return (leftIsString && rightIsString)
            || (leftIsString && !left.IsNull && right.IsNull)
            || (rightIsString && !right.IsNull && left.IsNull);
    }

    /// <summary>
    /// String <c>+</c> concatenation: NULL-propagating under SQL Server's
    /// default <c>CONCAT_NULL_YIELDS_NULL ON</c>, a NULL operand reading as
    /// empty under OFF. Result type is delegated to
    /// <see cref="SqlType.PromoteForArithmetic"/>, which preserves char(N) /
    /// nchar(N) length combination for fixed-length-pair concatenation.
    /// <c>text</c> / <c>ntext</c> operands raise Msg 402 matching real SQL
    /// Server's restriction on LOB string types in arithmetic operators.
    /// Fixed-length <c>char(N)</c> / <c>nchar(N)</c> operands carry their
    /// trailing-space padding through the storage layer, so
    /// <c>cast('a' as char(5)) + cast('b' as char(5))</c> yields
    /// <c>'a    b    '</c> as a side-effect of the per-value rep — no special
    /// handling in this method.
    /// </summary>
    private static SqlValue StringConcatenation(SqlValue left, SqlValue right, bool concatNullYieldsNull)
    {
        if (left.Type == SqlType.Text || left.Type == SqlType.NText || right.Type == SqlType.Text || right.Type == SqlType.NText)
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(left.Type, right.Type, "add");

        // The string + typed-NULL fall-through settles no collation, the
        // NULL-side type being non-string per IsStringConcatPair.
        var resultType = left.Type.Category == SqlTypeCategory.String && right.Type.Category == SqlTypeCategory.String
            ? StringPairType(left.Type, right.Type)
            : ResolveResultType(left.Type, right.Type);

        // Only one NULL reads as empty: two NULLs still concatenate to NULL.
        // A bounded result past the family maximum is clipped to it.
        if (!concatNullYieldsNull && left.IsNull != right.IsNull)
            return SqlValue.FromString(resultType, StringScalars.ClipToFamilyCap((left.IsNull ? "" : TextInCodePageOf(left, resultType)) + (right.IsNull ? "" : TextInCodePageOf(right, resultType)), resultType));
        return left.IsNull || right.IsNull
            ? SqlValue.Null(resultType)
            : SqlValue.FromString(resultType, StringScalars.ClipToFamilyCap(TextInCodePageOf(left, resultType) + TextInCodePageOf(right, resultType), resultType));
    }

    /// <summary>
    /// <paramref name="operand"/>'s text as a <c>+</c> or <c>||</c> of
    /// <paramref name="resultType"/> joins it: a <c>varchar</c> operand of
    /// another code page converts into the result's first — best fit, else
    /// <c>?</c> — so <c>'ß' + s</c> over a Greek <c>s</c> holds a <c>?</c>
    /// wherever it goes next (probed 2026-10-25 against SQL Server 2025).
    /// </summary>
    internal static string TextInCodePageOf(SqlValue operand, SqlType resultType) =>
        operand.Type is VarcharSqlType or CharSqlType && resultType is VarcharSqlType or CharSqlType
            && resultType.Collation is { } result && operand.Type.Collation is { } own && own.StorageEncoding != result.StorageEncoding
            ? operand.CoerceTo(operand.Type.WithCollation(result, resultType.Coercibility)).AsString
            : operand.AsString;

    /// <summary>
    /// The type a <c>+</c> of two strings takes: <see cref="SqlType.PromoteForArithmetic"/>'s,
    /// in the higher-rank operand's collation, a same-rank conflict carried
    /// outward or reported here — <see cref="UnresolvedCollation.Settle"/>
    /// holds the whole rule. <see cref="StringAppend"/> types its appends by it.
    /// </summary>
    internal static SqlType StringPairType(SqlType left, SqlType right) =>
        UnresolvedCollation.Settle(SqlType.PromoteForArithmetic(left, right, '+'), left, right, "add");

    /// <summary>
    /// Settles on the result <see cref="SqlType"/> for a string-concat <c>+</c>.
    /// Both-string pairs delegate to <see cref="SqlType.PromoteForArithmetic"/>
    /// (which preserves char/nchar length combination). Mixed string + NULL
    /// (a non-string-typed NULL on one side, reached via the bare-NULL rule
    /// in <see cref="IsStringConcatPair"/>) reads the NULL as a one-character
    /// string of the other side's family, as <see cref="OneCharacterPartner"/>
    /// types it statically.
    /// </summary>
    private static SqlType ResolveResultType(SqlType a, SqlType b)
    {
        if (a.Category == SqlTypeCategory.String && b.Category == SqlTypeCategory.String)
            return SqlType.PromoteForArithmetic(a, b, '+');
        var stringType = a.Category == SqlTypeCategory.String ? a : b;
        var partner = OneCharacterPartner(stringType) ?? (SqlType.IsNationalStringCategory(stringType) ? SqlType.NVarchar : SqlType.Varchar);
        return a.Category == SqlTypeCategory.String
            ? SqlType.PromoteForArithmetic(a, partner, '+')
            : SqlType.PromoteForArithmetic(partner, b, '+');
    }

    /// <summary>
    /// The type a bare <c>NULL</c> takes beside <paramref name="stringType"/>
    /// in <c>+</c>: a one-character <c>varchar</c> / <c>nvarchar</c> in its
    /// collation, or null when <paramref name="stringType"/> isn't a
    /// concatenable string.
    /// </summary>
    internal static SqlType? OneCharacterPartner(SqlType stringType) => stringType switch
    {
        VarcharSqlType or CharSqlType when stringType.Collation is { } collation => VarcharSqlType.Get(1, collation, stringType.Coercibility),
        NVarcharSqlType or NCharSqlType when stringType.Collation is { } collation => NVarcharSqlType.Get(1, collation, stringType.Coercibility),
        _ => null,
    };


    protected override char Operator => '+';
}
