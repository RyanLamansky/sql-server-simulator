namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Encapsulates the SQL DATALENGTH command: https://learn.microsoft.com/en-us/sql/t-sql/functions/datalength-transact-sql
/// </summary>
internal sealed class DataLength(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override DataValue Run(Func<List<string>, DataValue> getColumnValue)
    {
        var value = source.Run(getColumnValue);
        return value.Value is null ? default : new(value.Type.DataLength(value));
    }

#if DEBUG
    public override string ToString() => $"DATALENGTH({source})";
#endif
}
