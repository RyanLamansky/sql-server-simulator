using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class BracketDelimitedString(StringBuilder buffer) : Name(buffer)
{
#if DEBUG
    public override string ToString() => $"[{Value}]";
#endif
}
