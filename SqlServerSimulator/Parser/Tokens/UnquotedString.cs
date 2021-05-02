using System;
using System.Text;

namespace SqlServerSimulator.Parser.Tokens
{
    class UnquotedString : StringToken
    {
        public UnquotedString(StringBuilder buffer)
            : base(buffer)
        {
        }

        public bool TryParse(out Keyword keyword) => Enum.TryParse(value, true, out keyword);

        public Keyword Parse()
        {
            try
            {
                return Enum.Parse<Keyword>(value, true);
            }
            catch (ArgumentException)
            {
                throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{value}`.");
            }
        }

#if DEBUG
        public override string ToString() => value;
#endif
    }
}
