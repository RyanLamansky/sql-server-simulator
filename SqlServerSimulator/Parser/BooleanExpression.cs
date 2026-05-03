using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

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
        : this(left, Expression.Parse(context.MoveNextRequiredReturnSelf()))
    {
    }

    /// <summary>
    /// Parses a comparison operator and its right-hand expression. Follows
    /// the lookahead contract documented on <see cref="ParserContext"/>: on
    /// return, <see cref="ParserContext.Token"/> is the first token not
    /// consumed by the comparison.
    /// </summary>
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
    /// Evaluates the expression. Any NULL operand yields <c>false</c>
    /// (SQL UNKNOWN-in-WHERE semantics).
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    public abstract bool Run(Func<List<string>, SqlValue> getColumnValue);

#if DEBUG
    public abstract override string ToString();
#endif

    /// <summary>
    /// Evaluates both sides, applies SQL Server type promotion to a common
    /// type, and invokes the comparator. Cross-category type pairs surface as
    /// <see cref="NotSupportedException"/> via <see cref="SqlType.Promote"/>.
    /// </summary>
    private static bool ComparePromoted(Expression left, Expression right, Func<List<string>, SqlValue> getColumnValue, Func<SqlValue, SqlValue, bool> compare)
    {
        var l = left.Run(getColumnValue);
        var r = right.Run(getColumnValue);
        if (l.IsNull || r.IsNull)
            return false;

        if (l.Type == r.Type)
            return compare(l, r);

        var common = SqlType.Promote(l.Type, r.Type);
        return compare(l.CoerceTo(common), r.CoerceTo(common));
    }

    private sealed class EqualityExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, static (l, r) => l.Equals(r));

#if DEBUG
        public override string ToString() => $"{left} = {right}";
#endif
    }

    private sealed class InequalityExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, static (l, r) => !l.Equals(r));

#if DEBUG
        public override string ToString() => $"{left} <> {right}";
#endif
    }

    private sealed class GreaterThanExpression(Expression left, Expression right) : BooleanExpression(left, right)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, static (l, r) => l.CompareTo(r) > 0);

#if DEBUG
        public override string ToString() => $"{left} > {right}";
#endif
    }

    private sealed class GreaterThanOrEqualExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, static (l, r) => l.CompareTo(r) >= 0);

#if DEBUG
        public override string ToString() => $"{left} >= {right}";
#endif
    }

    private sealed class LessThanExpression(Expression left, Expression right) : BooleanExpression(left, right)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, static (l, r) => l.CompareTo(r) < 0);

#if DEBUG
        public override string ToString() => $"{left} < {right}";
#endif
    }

    private sealed class LessThanOrEqualExpression(Expression left, ParserContext context) : BooleanExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, static (l, r) => l.CompareTo(r) <= 0);

#if DEBUG
        public override string ToString() => $"{left} <= {right}";
#endif
    }
}
