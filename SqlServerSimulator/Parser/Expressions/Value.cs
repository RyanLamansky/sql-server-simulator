using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser.Expressions;

internal sealed class Value : Expression
{
    private readonly DataValue value;

    public Value()
    {
    }

    public Value(DataValue value) => this.value = value;

    public Value(AtPrefixedString atPrefixed, ParserContext context)
    {
        this.value = context.GetVariableValue(atPrefixed.Value);
    }

    public Value(DoubleAtPrefixedString doubleAtPrefixedString)
    {
        switch (doubleAtPrefixedString.Parse())
        {
            case AtAtKeyword.Version:
                this.value = new("SQL Server Simulator", DataType.BuiltInDbString);
                return;
        }

        throw new NotSupportedException($"Simulator doesn't recognize {doubleAtPrefixedString}.");
    }

    public override DataValue Run(Func<List<string>, DataValue> getColumnValue) => value;

#if DEBUG
    public override string ToString() => value.Value?.ToString() ?? "null";
#endif
}
