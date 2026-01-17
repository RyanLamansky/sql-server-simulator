namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Encapsulates the SQL ABS command: https://learn.microsoft.com/en-us/sql/t-sql/functions/abs-transact-sql
/// </summary>
internal sealed class AbsoluteValue(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override DataValue Run(Func<List<string>, DataValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);

        return new(value.Value switch
        {
            null => value.Value,
            int v => Math.Abs(v),
            _ => throw new NotSupportedException($"Simulation unable to to run ABS function on the provided expression."),
        }, value.Type);
    }

#if DEBUG
    public override string ToString() => $"ABS({source})";
#endif
}
