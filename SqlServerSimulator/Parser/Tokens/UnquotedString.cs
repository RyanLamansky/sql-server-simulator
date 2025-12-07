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
    /// <returns>The appropriate token.</returns>
    public static Token CheckReserved(StringBuilder buffer)
    {
        var value = buffer.ToString();
        _ = buffer.Clear();

        return Enum.TryParse<Keyword>(value, true, out var keyword) ?
            new ReservedKeyword(keyword, value) :
            new UnquotedString(value);
    }

    /// <summary>
    /// Returns either an <see cref="UnquotedString"/> or <see cref="ReservedKeyword"/> depending on input.
    /// </summary>
    /// <returns>The appropriate token.</returns>
    public static Token CheckReserved(string command, int start, int length)
    {
        return Enum.TryParse<Keyword>(command.AsSpan().Slice(start, length), true, out var keyword) ?
            new ReservedKeyword(keyword, command.Substring(start, length)) :
            new UnquotedString(command.Substring(start, length));
    }

    public override string ToString() => Value;
}
