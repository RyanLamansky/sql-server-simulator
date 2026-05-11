namespace SqlServerSimulator.Parser.Tokens;

sealed class UnquotedString : Name
{
    private UnquotedString(string command, int index, int length)
        : base(command, index, length)
    {
    }

    public override ReadOnlySpan<char> Span => Source;

    /// <summary>
    /// Lazily classifies this token against <see cref="Parser.ContextualKeyword"/>.
    /// First access parses <see cref="Span"/> via <see cref="Enum.TryParse{TEnum}(ReadOnlySpan{char}, bool, out TEnum)"/>
    /// (case-insensitive); the result is cached on the field so repeat reads at
    /// the same token are constant-time. A miss — or a pathological identifier
    /// matching either sentinel name (<c>NotChecked</c> / <c>_</c>) — collapses
    /// to <see cref="ContextualKeyword._"/>.
    /// </summary>
    public ContextualKeyword ContextualKeyword
    {
        get
        {
            if (field == ContextualKeyword.NotChecked)
            {
                field = Enum.TryParse<ContextualKeyword>(this.Span, ignoreCase: true, out var keyword)
                    && keyword is not (ContextualKeyword.NotChecked or ContextualKeyword._)
                    ? keyword
                    : ContextualKeyword._;
            }
            return field;
        }
    }

    /// <summary>
    /// Returns either an <see cref="UnquotedString"/> or <see cref="ReservedKeyword"/> depending on input.
    /// </summary>
    /// <returns>The appropriate token.</returns>
    public static Token CheckReserved(string command, int index, int length) =>
        Enum.TryParse<Keyword>(command.AsSpan(index, length), true, out var keyword) ?
        new ReservedKeyword(keyword, command, index, length) :
        new UnquotedString(command, index, length);
}
