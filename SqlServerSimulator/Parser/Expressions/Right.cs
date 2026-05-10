using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>RIGHT(x, n)</c>: returns the rightmost <c>n</c> characters of
/// <c>x</c>. Negative <c>n</c> raises an error matching SQL Server.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/right-transact-sql</remarks>
internal sealed class Right : Expression
{
    private readonly Expression source;
    private readonly Expression count;

    public Right(ParserContext context)
    {
        this.source = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.count = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var s = source.Run(runtime);
        var n = count.Run(runtime);
        if (s.IsNull || n.IsNull)
            return SqlValue.Null(s.Type);
        if (!SqlType.IsStringCategory(s.Type))
            throw new NotSupportedException($"RIGHT expects a string first argument; got {s.Type}.");

        var len = n.CoerceTo(SqlType.Int32).AsInt32;
        if (len < 0)
            throw SimulatedSqlException.NegativeLengthNotAllowed("RIGHT");

        var input = s.AsString;
        return SqlValue.FromString(s.Type, len >= input.Length ? input : input[(input.Length - len)..]);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => source.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"RIGHT({source.DebugDisplay()}, {count.DebugDisplay()})";
}
