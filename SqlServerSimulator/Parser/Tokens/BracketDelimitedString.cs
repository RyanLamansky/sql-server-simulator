using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class BracketDelimitedString : Name
{
    public BracketDelimitedString(StringBuilder buffer)
        : base(buffer)
    {
    }

    public override string ToString() => $"[{Value}]";
}
