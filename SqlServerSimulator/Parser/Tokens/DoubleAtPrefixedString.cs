using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class DoubleAtPrefixedString : StringToken
{
    public DoubleAtPrefixedString(StringBuilder buffer)
        : base(buffer)
    {
    }
    public AtAtKeyword Parse()
    {
        if (!Enum.TryParse<AtAtKeyword>(Value, true, out var result))
            throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{Value}`.");

        return result;
    }

    public override string ToString() => $"@@{Value}";
}

