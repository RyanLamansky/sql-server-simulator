using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class DoubleAtPrefixedString(StringBuilder buffer) : StringToken(buffer)
{
    public AtAtKeyword Parse() => !Enum.TryParse<AtAtKeyword>(Value, true, out var result)
        ? throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{Value}`.")
        : result;

#if DEBUG
    public override string ToString() => $"@@{Value}";
#endif
}

