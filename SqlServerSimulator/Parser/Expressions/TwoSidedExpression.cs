namespace SqlServerSimulator.Parser.Expressions;

internal abstract class TwoSidedExpression : Expression
{
    private Expression left, right;

    public TwoSidedExpression(Expression left, ParserContext context)
    {
        this.left = left;

        context.MoveNextRequired();
        this.right = Parse(context);
    }

    public TwoSidedExpression AdjustForPrecedence()
    {
        if (this.right is not TwoSidedExpression rightTwo || rightTwo.Precedence < this.Precedence)
            return this;

        (rightTwo.left, this.right) = (this, rightTwo.left);
        return rightTwo;
    }

    public sealed override DataValue Run(Func<List<string>, DataValue> getColumnValue)
        => Run(left.Run(getColumnValue), right.Run(getColumnValue));

    protected abstract DataValue Run(DataValue left, DataValue right);
    protected abstract char Operator { get; }

#if DEBUG
    public sealed override string ToString() => $"{left} {Operator} {right}";
#endif
}
