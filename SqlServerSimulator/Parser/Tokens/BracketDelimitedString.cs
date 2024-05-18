using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class BracketDelimitedString(StringBuilder buffer) : Name(buffer)
{
    public override string ToString() => $"[{Value}]";
}
