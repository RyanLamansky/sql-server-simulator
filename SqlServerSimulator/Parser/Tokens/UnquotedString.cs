using System.Text;

namespace SqlServerSimulator.Parser.Tokens;

sealed class UnquotedString : Name
{
    private UnquotedString(string value) : base(value)
    {
    }

    /// <summary>
    /// Returns either an <see cref="UnquotedString"/> or <see cref="ReservedKeyword"/> depending on input.
    /// </summary>
    /// <param name="buffer">The string to consider. <see cref="StringBuilder.Clear"/> is called.</param>
    /// <returns></returns>
    public static Token CheckReserved(StringBuilder buffer)
    {
        var value = buffer.ToString();
        _ = buffer.Clear();

        return Enum.TryParse<Keyword>(value, true, out var keyword) ?
            new ReservedKeyword(keyword, value) :
            new UnquotedString(value);
    }

    public override string ToString() => Value;
}
