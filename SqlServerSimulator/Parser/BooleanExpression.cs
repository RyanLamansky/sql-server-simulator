using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// A specific type of expression used in WHERE clauses and similar branching scenarios.
/// </summary>
internal abstract class BooleanExpression
{
    private protected BooleanExpression()
    {
    }

    public static BooleanExpression Parse(ParserContext context)
    {
        var left = Expression.Parse(context);

        switch (context.Token)
        {
            case null:
                break;
            case Operator { Character: '=' }:
                context.MoveNextRequired();
                return new EqualityExpression(left, Expression.Parse(context));
        }

        throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Runs the expression, returning its result.
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    /// <returns>The result of the expression.</returns>
    public abstract bool Run(Func<List<string>, object?> getColumnValue);

#if DEBUG
    public abstract override string ToString();
#endif

    private sealed class EqualityExpression(Expression left, Expression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, object?> getColumnValue) =>
            (left.Run(getColumnValue)?.Equals(right.Run(getColumnValue))).GetValueOrDefault();

#if DEBUG
        public override string ToString() => $"{left} = {right}";
#endif
    }
}
