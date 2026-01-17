namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An expression that has been given a name, such as with `as`.
/// </summary>
/// <param name="expression">The expression to be named.</param>
/// <param name="name">The name of the expression, exposed via the <see cref="Name"/> property.</param>
internal sealed class NamedExpression(Expression expression, string name) : Expression
{
    private readonly Expression expression = expression;
    private readonly string name = name;

    public override string Name => this.name;

    public override byte Precedence => expression.Precedence;

    public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => this.expression.Run(getColumnValue);

#if DEBUG
    public override string ToString() => $"{expression} {name}";
#endif
}
