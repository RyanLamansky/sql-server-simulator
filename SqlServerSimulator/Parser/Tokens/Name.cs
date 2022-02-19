using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

abstract class Name : StringToken
{
    private protected Name(StringBuilder buffer)
        : base(buffer)
    {
    }

    public bool TryParse(out Keyword keyword) => Enum.TryParse(Value, true, out keyword);

    public Keyword Parse()
    {
        if (!Enum.TryParse<Keyword>(Value, true, out var result))
            throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{Value}`.");

        return result;
    }
}
