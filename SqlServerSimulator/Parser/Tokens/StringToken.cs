using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

abstract class StringToken : Token
{
    public readonly string Value;

    private protected StringToken(StringBuilder buffer)
    {
        this.Value = buffer.ToString();
        buffer.Clear();
    }

#if DEBUG
    public override string ToString() => $"{GetType().Name} {Value}";
#endif
}
