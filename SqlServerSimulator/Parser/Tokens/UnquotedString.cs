using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class UnquotedString(StringBuilder buffer) : Name(buffer)
{
    public override string ToString() => Value;
}
