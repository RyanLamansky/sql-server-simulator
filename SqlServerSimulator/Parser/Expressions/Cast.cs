using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CAST(expr AS type)</c>: routes the source value through
/// <see cref="SqlValue.CoerceTo"/>. The target type is resolved by
/// <see cref="SqlType.GetByName"/>; a length specifier (e.g.
/// <c>varchar(10)</c>) is parsed and validated but generally not enforced as
/// a value-level cap — see the broader cast-length limitation in CLAUDE.md.
/// The one place the simulator does enforce it is the
/// <c>uniqueidentifier → char/varchar/nchar/nvarchar</c> path, where SQL
/// Server fires Msg 8170 / 8115 for sub-36-character destinations rather
/// than silently truncating.
/// </summary>
/// <remarks>
/// Cross-category coercions (string ↔ numeric) propagate
/// <see cref="NotSupportedException"/> from <c>SqlValue.CoerceTo</c>;
/// the simulator hasn't modeled them yet.
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/cast-and-convert-transact-sql
/// </remarks>
internal sealed class Cast : Expression
{
    private readonly Expression source;
    private readonly SqlType targetType;
    private readonly int? targetMaxLength;

    public Cast(ParserContext context)
    {
        this.source = Parse(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var typeName = context.GetNextRequired<Name>();
        (this.targetType, this.targetMaxLength) = ParseTargetTypeSpec(context, typeName);

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue) =>
        ApplyCoercion(source.Run(getColumnValue), this.targetType, this.targetMaxLength);

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => targetType;

    internal override string DebugDisplay() => $"CAST({source.DebugDisplay()} AS {targetType})";

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
                : context.MatchContextual(ContextualKeyword.Max)
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
    /// 8170 / 8115, then delegates to <see cref="SqlValue.CoerceTo"/> and
    /// rewraps <see cref="OverflowException"/> as the generic Msg 8115.
    /// </summary>
    internal static SqlValue ApplyCoercion(SqlValue value, SqlType targetType, int? targetMaxLength)
    {
        // uniqueidentifier → too-narrow string: SQL Server raises a target-
        // specific error rather than silently truncating. char/varchar use
        // Msg 8170 with its dedicated text; nchar/nvarchar use the generic
        // arithmetic-overflow Msg 8115 (verified against SQL Server 2025: the
        // message names "nvarchar" for both nchar and nvarchar targets).
        // NULLs pass through silently.
        if (!value.IsNull
            && value.Type == SqlType.UniqueIdentifier
            && targetMaxLength is int max
            && max < 36)
        {
            if (targetType == SqlType.Varchar || targetType is CharSqlType)
                throw SimulatedSqlException.InsufficientResultSpaceForUniqueIdentifier();
            if (targetType == SqlType.NVarchar || targetType is NCharSqlType)
                throw SimulatedSqlException.ArithmeticOverflow("nvarchar");
        }

        try
        {
            return value.CoerceTo(targetType);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(targetType.ToString()!);
        }
    }
}
