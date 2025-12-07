namespace SqlServerSimulator.Parser.Tokens;

sealed class UnquotedString : Name
{
    private UnquotedString(string command, int index, int length)
        : base(command, index, length)
    {
    }

    public override ReadOnlySpan<char> Span => Source;

    /// <summary>
    /// Returns either an <see cref="UnquotedString"/> or <see cref="ReservedKeyword"/> depending on input.
    /// </summary>
    /// <returns>The appropriate token.</returns>
    public static Token CheckReserved(string command, int index, int length) =>
        Enum.TryParse<Keyword>(command.AsSpan(index, length), true, out var keyword) ?
        new ReservedKeyword(keyword, command, index, length) :
        new UnquotedString(command, index, length);
}
