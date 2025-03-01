using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class AtPrefixedString(StringBuilder buffer) : StringToken(buffer)
{
    public override string ToString() => $"@{Value}";
}
