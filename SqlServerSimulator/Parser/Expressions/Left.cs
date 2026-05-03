using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>LEFT(x, n)</c>: returns the leftmost <c>n</c> characters of
/// <c>x</c>. Negative <c>n</c> raises an error matching SQL Server.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/left-transact-sql</remarks>
internal sealed class Left : Expression
{
    private readonly Expression source;
    private readonly Expression count;

    public Left(ParserContext context)
    {
        this.source = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.count = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
    {
        var s = source.Run(getColumnValue);
        var n = count.Run(getColumnValue);
        if (s.IsNull || n.IsNull)
            return SqlValue.Null(s.Type);
        if (!SqlType.IsStringCategory(s.Type))
            throw new NotSupportedException($"LEFT expects a string first argument; got {s.Type}.");

        var len = n.CoerceTo(SqlType.Int32).AsInt32;
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowed("LEFT");

        var input = s.AsString;
        return SqlValue.FromString(s.Type, len >= input.Length ? input : input[..len]);
    }

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => source.GetSqlType(resolveColumnType);

#if DEBUG
    public override string ToString() => $"LEFT({source}, {count})";
#endif
}
