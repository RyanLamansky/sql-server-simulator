using System.Text;

namespace SqlServerSimulator.Parser.Tokens
{
    class UnquotedString : StringToken
    {
        public UnquotedString(StringBuilder buffer)
            : base(buffer)
        {
        }

#if DEBUG
        public override string ToString() => value;
#endif
    }
}
