using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

class UnquotedString : Name
{
    public UnquotedString(StringBuilder buffer)
        : base(buffer)
    {
    }

#if DEBUG
    public override string ToString() => Value;
#endif
}
