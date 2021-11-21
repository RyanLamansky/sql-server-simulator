using System;
using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

class UnquotedString : Name
{
    public UnquotedString(StringBuilder buffer)
        : base(buffer)
    {
    }

    public bool TryParse(out Keyword keyword) => Enum.TryParse(value, true, out keyword);

    public Keyword Parse()
    {
        if (!Enum.TryParse<Keyword>(value, true, out var result))
            throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{value}`.");

        return result;
    }

#if DEBUG
    public override string ToString() => value;
#endif
}
