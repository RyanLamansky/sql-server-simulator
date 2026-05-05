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

        // Optional (N) or (P, S) length/precision specifier — accepted for
        // parity with SQL Server syntax. Length is generally not enforced as
        // a value-level cap; precision/scale are interpreted by the type.
        int? declaredMaxLength = null;
        int? declaredScale = null;
        context.MoveNextRequired();
        if (context.Token is Operator { Character: '(' })
        {
            if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            declaredMaxLength = numericValue.AsInt32;
            var next = context.GetNextRequired();
            switch (next)
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

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Null columnName signals CAST context: errors use Msg 243 (unknown
        // type), 291 (length on fixed type), and the "type"/"convert
        // specification" wording for Msg 131 size errors.
        var (resolved, max) = SqlType.GetByName(typeName, declaredMaxLength, declaredScale, 1, columnName: null);
        this.targetType = resolved;
        this.targetMaxLength = max;
    }

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);

        // uniqueidentifier → too-narrow string: SQL Server raises a target-
        // specific error rather than silently truncating. char/varchar use
        // Msg 8170 with its dedicated text; nchar/nvarchar use the generic
        // arithmetic-overflow Msg 8115. NULLs pass through silently.
        if (!value.IsNull
            && value.Type == SqlType.UniqueIdentifier
            && this.targetMaxLength is int max
            && max < 36)
        {
            if (this.targetType == SqlType.Varchar)
                throw SimulatedSqlException.InsufficientResultSpaceForUniqueIdentifier();
            if (this.targetType == SqlType.NVarchar)
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

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => targetType;

#if DEBUG
    public override string ToString() => $"CAST({source} AS {targetType})";
#endif
}
