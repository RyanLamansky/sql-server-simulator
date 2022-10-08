using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class AtPrefixedString : StringToken
{
    public AtPrefixedString(StringBuilder buffer)
        : base(buffer)
    {
    }

    public override string ToString() => $"@{Value}";
}
