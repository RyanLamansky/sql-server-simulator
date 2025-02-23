using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class UnquotedString(StringBuilder buffer) : Name(buffer)
{
#if DEBUG
    public override string ToString() => Value;
#endif
}
