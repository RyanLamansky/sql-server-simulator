using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

class UnquotedString : Name
{
    public UnquotedString(StringBuilder buffer)
        : base(buffer)
    {
    }

    public override string ToString() => Value;
}
