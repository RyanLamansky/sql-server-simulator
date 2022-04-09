using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

class BracketDelimitedString : Name
{
    public BracketDelimitedString(StringBuilder buffer)
        : base(buffer)
    {
    }

    public override string ToString() => $"[{Value}]";
}
