using System.Text;

namespace SqlServerSimulator.Parser.Tokens
{
    class DoubleAtPrefixedString : StringToken
    {
        public DoubleAtPrefixedString(StringBuilder buffer)
            : base(buffer)
        {
        }

#if DEBUG
        public override string ToString() => $"@@{value}";
#endif
    }
}
