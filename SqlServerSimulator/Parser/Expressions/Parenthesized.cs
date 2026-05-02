namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// An expression that's wrapped in parentheses, potentially affecting the order of operations.
/// </summary>
internal sealed class Parenthesized(ParserContext context) : Expression
{
    private readonly Expression wrapped = Parse(context.MoveNextRequiredReturnSelf());

    public override Storage.SqlValue Run(Func<List<string>, Storage.SqlValue> getColumnValue) => wrapped.Run(getColumnValue);

    public override Storage.SqlType GetSqlType(Func<List<string>, Storage.SqlType> resolveColumnType) => wrapped.GetSqlType(resolveColumnType);

#if DEBUG
    public override string ToString() => $"( {wrapped} )";
#endif
}
