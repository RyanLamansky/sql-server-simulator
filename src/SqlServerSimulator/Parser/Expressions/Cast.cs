using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CAST(expr AS type)</c> and <c>TRY_CAST(expr AS type)</c>: routes
/// the source value through <see cref="SqlValue.CoerceTo"/>. The target type
/// is resolved by <see cref="SqlType.GetByName"/>; a length specifier (e.g.
/// <c>varchar(10)</c>) is parsed and validated but generally not enforced as
/// a value-level cap — see the broader cast-length limitation in CLAUDE.md.
/// The one place the simulator does enforce it is the
/// <c>uniqueidentifier → char/varchar/nchar/nvarchar</c> path, where SQL
/// Server fires Msg 8170 / 8115 for sub-36-character destinations rather
/// than silently truncating.
/// </summary>
/// <remarks>
/// <para>Cross-category coercions (string ↔ numeric) propagate
/// <see cref="NotSupportedException"/> from <c>SqlValue.CoerceTo</c>;
/// the simulator hasn't modeled them yet.</para>
/// <para>The <c>TRY_CAST</c> form wraps the outer coercion in a catch that
/// converts the documented "conversion failed" error numbers (see
/// <see cref="IsConversionFailure"/>) into <c>NULL</c> of the target type.
/// Errors raised while evaluating the source expression itself (e.g.
/// divide-by-zero, an inner CAST that fails) propagate — only the cast-level
/// failure is swallowed, matching SQL Server.</para>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/cast-and-convert-transact-sql
/// </remarks>
internal sealed class Cast : Expression
{
    private readonly Expression source;
    private readonly SqlType targetType;
    private readonly int? targetMaxLength;
    private readonly bool tryMode;
    private readonly bool targetReportsNumeric;

    public Cast(ParserContext context, bool tryMode = false)
    {
        this.tryMode = tryMode;
        this.source = Parse(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var typeName = TypeNameSynonyms.TryFoldMultiWordType(context)
            ?? context.Token as Name
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        this.targetReportsNumeric = ReportsNumeric(typeName);
        (this.targetType, this.targetMaxLength) = ParseTargetTypeSpec(context, typeName);

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// True when the CAST/CONVERT target type name is named <c>numeric</c>
    /// rather than <c>decimal</c> (or its <c>dec</c> synonym) — the one input
    /// that makes the decimal-family result report the <c>numeric</c> type
    /// name at projection time. The two names are the same
    /// <see cref="SqlType"/>, so this is captured from the source token, not
    /// the resolved type. There is no numeric-named synonym, so an exact
    /// match on the bare word suffices.
    /// </summary>
    internal static bool ReportsNumeric(Name typeName) =>
        typeName.Span.Equals("numeric", StringComparison.OrdinalIgnoreCase);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var sourceValue = this.source.Run(runtime);
        var dbCollation = runtime.Batch.CurrentDatabase.Collation;
        SqlValue coerced;
        try
        {
            coerced = ApplyCoercion(sourceValue, this.targetType, this.targetMaxLength);
        }
        catch (SimulatedSqlException ex) when (this.tryMode && IsConversionFailure(ex.Number))
        {
            coerced = SqlValue.Null(this.targetType);
        }

        return RecollateStringResult(coerced, this.targetType, sourceValue.Type, dbCollation);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResultStringType(this.targetType, this.source.GetSqlType(batch, resolveColumnType), batch.CurrentDatabase.Collation) ?? this.targetType;

    internal override bool ResultReportsNumeric => this.targetReportsNumeric;

    /// <summary>
    /// Collation of a CAST/CONVERT result whose target is a character type.
    /// Real SQL Server (probe-confirmed against SQL Server 2025): a character
    /// source expression's collation and coercibility carry through; a
    /// non-character source yields the database default collation with
    /// coercibility <see cref="Coercibility.CoercibleDefault"/> (so
    /// <c>CAST(int AS varchar)</c> concatenates and compares cleanly with
    /// literals and other database-collation values rather than raising Msg 457
    /// / 468). Returns <see langword="null"/> for non-string targets (nothing to
    /// re-collate). Shared by <see cref="Cast"/> and <c>ConvertExpression</c>.
    /// </summary>
    internal static SqlType? ResultStringType(SqlType targetType, SqlType sourceType, Collation dbCollation) =>
        targetType.Collation is null
            ? null
            : sourceType.Collation is { } sourceCollation
                ? targetType.WithCollation(sourceCollation, sourceType.Coercibility)
                : targetType.WithCollation(dbCollation, Coercibility.CoercibleDefault);

    /// <summary>
    /// Applies <see cref="ResultStringType"/> to a coerced CAST/CONVERT value,
    /// re-typing it to the character result's collation. Non-string results
    /// pass through unchanged.
    /// </summary>
    internal static SqlValue RecollateStringResult(SqlValue coerced, SqlType targetType, SqlType sourceType, Collation dbCollation)
    {
        var recollated = ResultStringType(targetType, sourceType, dbCollation);
        return recollated is null ? coerced
            : coerced.IsNull ? SqlValue.Null(recollated)
            : recollated is CharSqlType && coerced.Type.Collation!.StorageEncoding != recollated.Collation!.StorageEncoding
                ? SqlValue.FromString(recollated, coerced.AsString)
                : coerced.WithType(recollated);
    }

    internal override string DebugDisplay() =>
        $"{(this.tryMode ? "TRY_CAST" : "CAST")}({source.DebugDisplay()} AS {targetType})";

    internal override void VisitColumnReferences(Action<MultiPartName> visit) => this.source.VisitColumnReferences(visit);

    internal override bool ContainsVariableReference => this.source.ContainsVariableReference;

    internal override Expression? PureConversionOperand => this.source;

    internal override bool IsRowIndependent => this.source.IsRowIndependent;

    /// <summary>
    /// Parses the optional <c>(length)</c> or <c>(precision, scale)</c> spec
    /// after a CAST/CONVERT target type name and resolves the type. The caller
    /// supplies the already-consumed type-name token; the helper advances past
    /// the spec (if any) and leaves <see cref="ParserContext.Token"/> on the
    /// first un-consumed token, ready for the wrapping function's closing
    /// paren. Errors use Msg 243 / 291 with the CAST-context wording.
    /// </summary>
    internal static (SqlType targetType, int? targetMaxLength) ParseTargetTypeSpec(ParserContext context, Name typeName)
    {
        int? declaredMaxLength = null;
        int? declaredScale = null;
        context.MoveNextRequired();
        if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    declaredScale = scaleValue.AsInt32;
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }

        // Null columnName signals CAST/CONVERT context: errors use Msg 243
        // (unknown type), 291 (length on fixed type), and the
        // "type"/"convert specification" wording for Msg 131 size errors.
        return SqlType.GetByName(typeName, declaredMaxLength, declaredScale, 1, columnName: null);
    }

    /// <summary>
    /// Runs the value-level coercion shared by CAST and CONVERT: rejects
    /// uniqueidentifier-to-too-narrow-string with the target-specific Msg
    /// 8170 / 8115, then delegates to <see cref="SqlValue.CoerceTo"/>,
    /// rewraps <see cref="OverflowException"/> as the generic Msg 8115, and
    /// finally enforces the narrow-string rules described in
    /// <see cref="EnforceTargetMaxLength"/>.
    /// </summary>
    /// <summary>
    /// Whether an explicit <c>CAST</c> / <c>CONVERT</c> out of a legacy LOB type
    /// is one real SQL Server refuses outright with Msg 529 — the payload's
    /// parseability never enters into it, so <c>CAST(&lt;text '5'&gt; AS int)</c>
    /// is rejected as firmly as a non-numeric one. The two LOB families have
    /// different allow-lists (probe-confirmed 2026-07-24):
    /// <list type="bullet">
    /// <item><c>text</c> / <c>ntext</c> convert only within the string family —
    /// <c>char</c> / <c>nchar</c> / <c>varchar</c> / <c>nvarchar</c> /
    /// <c>text</c> / <c>ntext</c> / <c>xml</c>. Since <c>xml</c> is
    /// string-category here, the category test is the whole allow-list;
    /// <c>int</c>, <c>bigint</c>, <c>decimal</c>, <c>float</c>, <c>money</c>,
    /// <c>bit</c>, <c>date</c>, <c>datetime</c>, <c>uniqueidentifier</c>,
    /// <c>varbinary</c> and <c>sql_variant</c> all raise 529.</item>
    /// <item><c>image</c> converts only within the binary family —
    /// <c>varbinary</c> / <c>binary</c> / <c>image</c>. Notably <c>xml</c> and
    /// <c>sql_variant</c> raise 529 from <c>image</c> even though <c>xml</c> is
    /// reachable from <c>text</c>.</item>
    /// </list>
    /// <c>TRY_CAST</c> / <c>TRY_CONVERT</c> raise it too rather than returning
    /// NULL — 529 is an illegal conversion, not a conversion failure, and is
    /// deliberately absent from <see cref="IsConversionFailure"/>.
    /// </summary>
    /// <remarks>
    /// This sits on the explicit-CAST seam on purpose. The <em>implicit</em>
    /// path answers with a different error entirely (Msg 206 operand-type clash
    /// for <c>textcol = 5</c>, Msg 402 for <c>textcol = 'x'</c>), so gating
    /// inside the shared <c>SqlValue.CoerceTo</c> would trade one divergence
    /// for another.
    /// </remarks>
    private static bool IsRejectedLegacyLobConversion(SqlType source, SqlType target) =>
        source == SqlType.Text || source == SqlType.NText
            ? !SqlType.IsStringCategory(target)
            : source == SqlType.Image && target is not (VarbinarySqlType or BinarySqlType or ImageSqlType);

    internal static SqlValue ApplyCoercion(SqlValue value, SqlType targetType, int? targetMaxLength)
    {
        if (IsRejectedLegacyLobConversion(value.Type, targetType))
            throw SimulatedSqlException.ExplicitConversionNotAllowed(value.Type, targetType);

        // uniqueidentifier → too-narrow string: SQL Server raises a target-
        // specific error rather than silently truncating. char/varchar use
        // Msg 8170 with its dedicated text; nchar/nvarchar use the generic
        // arithmetic-overflow Msg 8115 (verified against SQL Server 2025: the
        // message names "nvarchar" for both nchar and nvarchar targets).
        // NULLs pass through silently. A MAX target (length sentinel -1) has
        // unbounded width, so it always holds the 36-char form — the `>= 0`
        // guard keeps it out of the too-narrow branch (a plain `< 36` treats
        // the -1 sentinel as narrower than 36 and wrongly raises Msg 8115).
        if (!value.IsNull
            && value.Type == SqlType.UniqueIdentifier
            && targetMaxLength is int max
            && max is >= 0 and < 36)
        {
            if (targetType is VarcharSqlType or CharSqlType)
                throw SimulatedSqlException.InsufficientResultSpaceForUniqueIdentifier();
            if (targetType is NVarcharSqlType or NCharSqlType)
                throw SimulatedSqlException.ArithmeticOverflow("nvarchar");
        }

        var sourceType = value.Type;
        SqlValue coerced;
        try
        {
            coerced = value.CoerceTo(targetType);
        }
        catch (OverflowException)
        {
            // A tinyint/smallint/int source overflowing tinyint or smallint
            // reports Msg 220 with the source value embedded — real's %ld slot
            // holds it (probe-confirmed against SQL Server 2025, 2026-07-22).
            // A bigint source (value could exceed the 32-bit slot) and every
            // other narrowing keep the generic Msg 8115.
            if (targetType is TinyIntSqlType or SmallIntSqlType
                && SqlType.IsIntegerCategory(sourceType) && sourceType != SqlType.BigInt)
            {
                var formatted = value.CoerceTo(SqlType.BigInt).AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
                throw SimulatedSqlException.ArithmeticOverflowForDataType(targetType.SqlServerName, formatted);
            }

            throw SimulatedSqlException.ArithmeticOverflow(targetType.ToString()!);
        }

        return EnforceTargetMaxLength(coerced, targetType, targetMaxLength, sourceType);
    }

    /// <summary>
    /// Enforces the bounded-target-length rules for variable-length string
    /// (<c>varchar</c> / <c>nvarchar</c>) and binary (<c>varbinary</c>) CAST
    /// targets. The simulator's variable-length string types are stateless
    /// singletons that don't carry their declared length on the SqlType, so
    /// the length check happens here rather than in <see cref="SqlValue.CoerceTo"/>.
    /// Probe-confirmed rules against SQL Server 2025 (2026-05-09):
    /// <list type="bullet">
    /// <item>Strings, <c>varbinary</c>, and date/time-family sources →
    /// silent truncation.</item>
    /// <item><c>tinyint</c> / <c>smallint</c> / <c>int</c> source with a
    /// <c>varchar</c> target whose width can't hold the rendered value →
    /// asterisk fallback (<c>'*'</c>) — a legacy SQL Server quirk specific
    /// to <c>varchar</c>; the <c>nvarchar</c> path raises Msg 8115 instead.</item>
    /// <item><c>bigint</c> / <c>decimal</c> / <c>numeric</c> source → Msg 8115
    /// (with "expression" wording for integer sources, "numeric" wording for
    /// decimal/numeric).</item>
    /// <item><c>money</c> / <c>smallmoney</c> source → Msg 234 with its
    /// dedicated "insufficient result space" wording.</item>
    /// <item><c>float</c> / <c>real</c> source → Msg 232 embedding the
    /// formatted source value.</item>
    /// </list>
    /// Fixed-length <c>char(N)</c> / <c>nchar(N)</c> / <c>binary(N)</c>
    /// targets carry the declared length on the SqlType and are normalized
    /// inside <see cref="SqlValue.FromChar"/> / <see cref="SqlValue.FromNChar"/>
    /// / <see cref="SqlValue.FromBinary"/>; their <paramref name="targetMaxLength"/>
    /// arrives as <c>null</c> from <see cref="SqlType.GetByName"/> and they
    /// short-circuit this method.
    /// </summary>
    private static SqlValue EnforceTargetMaxLength(SqlValue coerced, SqlType targetType, int? targetMaxLength, SqlType sourceType)
    {
        if (coerced.IsNull || targetMaxLength is not int max || max <= 0)
            return coerced;

        if (targetType is VarcharSqlType or NVarcharSqlType)
        {
            var text = coerced.AsString;
            // The error wording uses the bare type-family name ("varchar" /
            // "nvarchar") regardless of the target's declared length, so
            // ToString() — which would emit "varchar(5)" — is wrong here.
            var familyName = targetType is NVarcharSqlType ? "nvarchar" : "varchar";
            return text.Length <= max ? coerced : sourceType.Category switch
            {
                SqlTypeCategory.String or SqlTypeCategory.DateTime
                    => SqlValue.FromString(targetType, text[..max]),
                SqlTypeCategory.Other when sourceType is VarbinarySqlType or BinarySqlType or ImageSqlType
                    => SqlValue.FromString(targetType, text[..max]),
                SqlTypeCategory.Integer when sourceType != SqlType.BigInt && targetType is VarcharSqlType
                    => SqlValue.FromVarchar("*"),
                SqlTypeCategory.Integer
                    => throw SimulatedSqlException.ArithmeticOverflow(familyName),
                SqlTypeCategory.Decimal
                    => throw SimulatedSqlException.ArithmeticOverflowToTarget(familyName),
                SqlTypeCategory.Money
                    => throw SimulatedSqlException.InsufficientResultSpaceForMoney(familyName),
                SqlTypeCategory.Approximate
                    => throw SimulatedSqlException.ArithmeticOverflowForType(familyName, FormatApproximateForOverflow(text)),
                _ => coerced,
            };
        }

        return targetType is VarbinarySqlType && coerced.AsBytes.Length > max
            ? SqlValue.FromVarbinary(coerced.AsBytes[..max])
            : coerced;
    }

    /// <summary>
    /// Formats a float / real value for a Msg 232 message slot. SQL Server
    /// embeds the value as a fixed-point string with six fractional digits;
    /// the runtime value here is already a coerced <c>varchar</c> string
    /// from <see cref="SqlValue.CoerceTo"/>, so we re-parse it as a double
    /// and re-format with <c>F6</c>. Bare-fail formatting falls back to the
    /// raw string so the error stays informative either way.
    /// </summary>
    private static string FormatApproximateForOverflow(string raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
            : raw;

    /// <summary>
    /// Set of <see cref="SimulatedSqlException.Number"/> values that
    /// <c>TRY_CAST</c> / <c>TRY_CONVERT</c> swallow into <c>NULL</c> — the
    /// documented "conversion failed" surface. Anything else (Msg 529
    /// explicit-cast rejection, Msg 243 unknown type, etc.) propagates so
    /// the caller still sees genuine programming errors.
    /// </summary>
    internal static bool IsConversionFailure(int number) => number is
        220    // ArithmeticOverflowForDataType (integer → tinyint/smallint)
        or 241 // ConversionFailedDateTimeFromString
        or 242 // ConversionToDateTimeOutOfRange
        or 244 // OverflowConvertingNarrowInt (INT1/INT2)
        or 245 // ConversionFailedFromString
        or 248 // OverflowConvertingToInt
        or 295 // ConversionFailedSmallDateTimeFromString
        or 8114 // ConvertingDataTypeError
        or 8115 // ArithmeticOverflow
        or 8169 // ConversionFailedFromStringToUniqueIdentifier
        or 8170 // InsufficientResultSpaceForUniqueIdentifier
        or 9807; // InputCharacterStringStyleMismatch
}
