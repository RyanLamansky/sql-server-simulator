using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class AtPrefixedString(StringBuilder buffer) : StringToken(buffer)
{
#if DEBUG
    public override string ToString() => $"@{Value}";
#endif
}
