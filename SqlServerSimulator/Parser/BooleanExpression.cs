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
            case Operator { Character: '>' }:
                if (context.GetNextRequired() is Operator { Character: '=' })
                {
                    context.MoveNextRequired();
                    return new GreaterThanOrEqualExpression(left, Expression.Parse(context));
                }
                return new GreaterThanExpression(left, Expression.Parse(context));
            case Operator { Character: '<' }:
                switch (context.GetNextRequired())
                {
                    case Operator { Character: '=' }:
                        context.MoveNextRequired();
                        return new LessThanOrEqualExpression(left, Expression.Parse(context));
                    case Operator { Character: '>' }:
                        context.MoveNextRequired();
                        return new InequalityExpression(left, Expression.Parse(context));
                }

                if (context.GetNextRequired() is Operator { Character: '=' })
                {
                    context.MoveNextRequired();
                    return new LessThanOrEqualExpression(left, Expression.Parse(context));
                }
                return new LessThanExpression(left, Expression.Parse(context));
            case Operator { Character: '!' }:
                switch (context.GetNextRequired())
                {
                    case Operator { Character: '=' }:
                        context.MoveNextRequired();
                        return new InequalityExpression(left, Expression.Parse(context));
                    case Operator { Character: '>' }:
                        context.MoveNextRequired();
                        return new LessThanOrEqualExpression(left, Expression.Parse(context));
                    case Operator { Character: '<' }:
                        context.MoveNextRequired();
                        return new GreaterThanOrEqualExpression(left, Expression.Parse(context));
                }
                break;
        }

        throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Runs the expression, returning its result.
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    /// <returns>The result of the expression.</returns>
    public abstract bool Run(Func<List<string>, DataValue> getColumnValue);

#if DEBUG
    public abstract override string ToString();
#endif

    private sealed class EqualityExpression(Expression left, Expression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            (left.Run(getColumnValue).Value?.Equals(right.Run(getColumnValue).Value)).GetValueOrDefault();

#if DEBUG
        public override string ToString() => $"{left} = {right}";
#endif
    }

    private sealed class InequalityExpression(Expression left, Expression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            !(left.Run(getColumnValue).Value?.Equals(right.Run(getColumnValue).Value)).GetValueOrDefault();

#if DEBUG
        public override string ToString() => $"{left} <> {right}";
#endif
    }

    private sealed class GreaterThanExpression(Expression left, Expression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) > 0;

#if DEBUG
        public override string ToString() => $"{left} > {right}";
#endif
    }

    private sealed class GreaterThanOrEqualExpression(Expression left, Expression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) >= 0;

#if DEBUG
        public override string ToString() => $"{left} >= {right}";
#endif
    }

    private sealed class LessThanExpression(Expression left, Expression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) < 0;

#if DEBUG
        public override string ToString() => $"{left} < {right}";
#endif
    }

    private sealed class LessThanOrEqualExpression(Expression left, Expression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, DataValue> getColumnValue) =>
            left.Run(getColumnValue).CompareTo(right.Run(getColumnValue)) <= 0;

#if DEBUG
        public override string ToString() => $"{left} <= {right}";
#endif
    }
}
