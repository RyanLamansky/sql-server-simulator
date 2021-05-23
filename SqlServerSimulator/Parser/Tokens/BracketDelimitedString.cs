using System.Text;

namespace SqlServerSimulator.Parser.Tokens
{
    class BracketDelimitedString : Name
    {
        public BracketDelimitedString(StringBuilder buffer)
            : base(buffer)
        {
        }

#if DEBUG
        public override string ToString() => $"[{value}]";
#endif
    }
}
