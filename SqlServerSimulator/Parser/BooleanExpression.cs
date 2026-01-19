using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// A specific type of expression used in WHERE clauses and similar branching scenarios.
/// </summary>
internal abstract class BooleanExpression
{
    protected readonly Expression left, right;

    private protected BooleanExpression(Expression left, Expression right)
    {
        this.left = left;
        this.right = right;
    }

    private protected BooleanExpression(Expression left, ParserContext context)
    {
        this.left = left;
        context.MoveNextRequired();
        this.right = Expression.Parse(context);
    }

    public static BooleanExpression Parse(Expression left, ParserContext context) => context.Token switch
    {
        Operator { Character: '=' } => new EqualityExpression(left, context),
        Operator { Character: '>' } => context.GetNextRequired() switch
        {
            Operator { Character: '=' } => new GreaterThanOrEqualExpression(left, context),
            _ => new GreaterThanExpression(left, Expression.Parse(context))
        },
        Operator { Character: '<' } => context.GetNextRequired() switch
        {
            Operator { Character: '=' } => new LessThanOrEqualExpression(left, context),
            Operator { Character: '>' } => new InequalityExpression(left, context),
            _ => new LessThanExpression(left, Expression.Parse(context)),
        },
        Operator { Character: '!' } => context.GetNextRequired() switch
        {
            Operator { Character: '=' } => new InequalityExpression(left, context),
            Operator { Character: '>' } => new LessThanOrEqualExpression(left, context),
            Operator { Character: '<' } => new GreaterThanOrEqualExpression(left, context),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context)
        },
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    /// <summary>
    /// Runs the expression, returning its result.
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    /// <returns>The result of the expression.</returns>
    public abstract bool Run(Func<List<string>, DataValue> getColumnValue);

#if DEBUG
    public abstract override string ToString();
#endif

    private sealed class EqualityExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            (left.Run(getColumnValue).Value?.Equals(right.Run(getColumnValue).Value)).GetValueOrDefault();

#if DEBUG
        public override string ToString() => $"{left} = {right}";
#endif
    }

    private sealed class InequalityExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            !(left.Run(getColumnValue).Value?.Equals(right.Run(getColumnValue).Value)).GetValueOrDefault();

#if DEBUG
        public override string ToString() => $"{left} <> {right}";
#endif
    }

    private sealed class GreaterThanExpression(Expression left, Expression right) : BooleanExpression(left, right)
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) > 0;

#if DEBUG
        public override string ToString() => $"{left} > {right}";
#endif
    }

    private sealed class GreaterThanOrEqualExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) >= 0;

#if DEBUG
        public override string ToString() => $"{left} >= {right}";
#endif
    }

    private sealed class LessThanExpression(Expression left, Expression right) : BooleanExpression(left, right)
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) < 0;

#if DEBUG
        public override string ToString() => $"{left} < {right}";
#endif
    }

    private sealed class LessThanOrEqualExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) <= 0;

#if DEBUG
        public override string ToString() => $"{left} <= {right}";
#endif
    }
}
