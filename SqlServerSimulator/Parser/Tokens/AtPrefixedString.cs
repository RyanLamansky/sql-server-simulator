using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

class AtPrefixedString : StringToken
{
    public AtPrefixedString(StringBuilder buffer)
        : base(buffer)
    {
    }

#if DEBUG
    public override string ToString() => $"@{value}";
#endif
}
