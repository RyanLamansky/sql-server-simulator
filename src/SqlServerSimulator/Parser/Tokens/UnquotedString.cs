namespace SqlServerSimulator.Parser.Tokens;

sealed class UnquotedString : Name
{
    private UnquotedString(string command, int index, int length)
        : base(command, index, length, command.AsSpan(index, length))
    {
    }

    public override ReadOnlySpan<char> Span => Source;

    /// <summary>
    /// Lazily classifies this token against <see cref="Parser.ContextualKeyword"/>.
    /// First access parses <see cref="Span"/> via <see cref="Enum.TryParse{TEnum}(ReadOnlySpan{char}, bool, out TEnum)"/>
    /// (case-insensitive); the result is cached on the field so repeat reads at
    /// the same token are constant-time. A miss — or a pathological identifier
    /// matching either sentinel name (<c>NotChecked</c> / <c>NotAKeyword</c>) —
    /// collapses to <see cref="ContextualKeyword.NotAKeyword"/>.
    /// </summary>
    public ContextualKeyword ContextualKeyword
    {
        get
        {
            if (field == ContextualKeyword.NotChecked)
            {
                field = Enum.TryParse<ContextualKeyword>(this.Span, ignoreCase: true, out var keyword)
                    && keyword is not (ContextualKeyword.NotChecked or ContextualKeyword.NotAKeyword)
                    ? keyword
                    : ContextualKeyword.NotAKeyword;
            }
            return field;
        }
    }

    /// <summary>
    /// Returns either an <see cref="UnquotedString"/> or <see cref="ReservedKeyword"/> depending on input.
    /// </summary>
    /// <param name="command">The command text being tokenized.</param>
    /// <param name="index">Start of the word within <paramref name="command"/>.</param>
    /// <param name="length">Length of the word.</param>
    /// <param name="compatibilityLevel">
    /// The active database's compatibility level, which decides the words
    /// reserved only from a given level. <c>REGEXP_LIKE</c> is the one such
    /// word: real reserves it at 170 (SQL Server 2025), where the native
    /// predicate ships, and leaves it usable as an identifier at 160 and below.
    /// </param>
    /// <returns>The appropriate token.</returns>
    public static Token CheckReserved(string command, int index, int length, CompatibilityLevel compatibilityLevel = CompatibilityLevel.Sql170) =>
        Enum.TryParse<Keyword>(command.AsSpan(index, length), true, out var keyword)
            && (keyword != Keyword.Regexp_Like || compatibilityLevel >= CompatibilityLevel.Sql170) ?
        new ReservedKeyword(keyword, command, index, length) :
        new UnquotedString(command, index, length);
}
