namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An expression that's wrapped in parentheses, potentially affecting the order of operations.
/// </summary>
internal sealed class Parenthesized(Expression wrapped) : Expression
{
    private readonly Expression wrapped = wrapped;

    public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => wrapped.Run(getColumnValue);

#if DEBUG
    public override string ToString() => $"( {wrapped} )";
#endif
}
