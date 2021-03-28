using System.Text;

namespace SqlServerSimulator.Parser.Tokens
{
    abstract class StringToken : Token
    {
        public readonly string value;

        private protected StringToken(StringBuilder buffer)
        {
            this.value = buffer.ToString();
            buffer.Clear();
        }

#if DEBUG
        public override string ToString() => $"{GetType().Name} {value}";
#endif
    }
}
