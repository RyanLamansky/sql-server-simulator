using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CAST(expr AS type)</c>: routes the source value through
/// <see cref="SqlValue.CoerceTo"/>. The target type is resolved by
/// <see cref="SqlType.GetByName"/>; a length specifier (e.g.
/// <c>varchar(10)</c>) is parsed but not enforced — column-level max length
/// lives on <see cref="HeapColumn"/>, not on <see cref="SqlValue"/>, so a
/// cast-time length cap would need a separate carrier.
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

    public Cast(ParserContext context)
    {
        this.source = Parse(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.As })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var typeName = context.GetNextRequired<Name>();

        // Optional (N) length specifier — accepted for parity with SQL Server
        // syntax but not enforced as a value-level cap.
        int? declaredMaxLength = null;
        context.MoveNextRequired();
        if (context.Token is Operator { Character: '(' })
        {
            if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            declaredMaxLength = numericValue.AsInt32;
            if (context.GetNextRequired() is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Null columnName signals CAST context: errors use Msg 243 (unknown
        // type), 291 (length on fixed type), and the "type"/"convert
        // specification" wording for Msg 131 size errors.
        var (resolved, _) = SqlType.GetByName(typeName, declaredMaxLength, 1, columnName: null);
        this.targetType = resolved;
    }

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);
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
