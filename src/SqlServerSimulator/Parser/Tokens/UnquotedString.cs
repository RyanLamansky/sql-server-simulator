namespace SqlServerSimulator.Parser.Tokens;

sealed class UnquotedString : Name
{
    private UnquotedString(string command, int index, int length)
        : base(command, index, length, command.AsSpan(index, length))
    {
    }

    public override ReadOnlySpan<char> Span => Source;

    /// <summary>
    /// Whether this names a <c>GOTO</c> label where it is declared: a single
    /// <c>:</c> directly after the name. Real reads <c>lbl :</c> with a space
    /// before the colon as Msg 102 (probed 2026-09-30 against SQL Server 2025),
    /// and <c>::</c> is a scope qualifier, not a label.
    /// </summary>
    public bool IsLabelDeclaration
    {
        get
        {
            var end = this.EndIndex;
            return end < this.command.Length && this.command[end] == ':' && (end + 1 == this.command.Length || this.command[end + 1] != ':');
        }
    }

    /// <summary>
    /// Whether this is the <c>$partition</c> qualifier of a partition-function
    /// call. Only the tokenizer's <c>$</c>-word path yields an unquoted token
    /// starting with <c>$</c>, so a bracketed <c>[$partition]</c> never is.
    /// </summary>
    public bool IsDollarPartition => this.Source.Length == 10 && this.Source[0] == '$' && this.Source[1..].Equals("partition", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lazily classifies this token against <see cref="Parser.ContextualKeyword"/>.
    /// First access parses <see cref="Span"/> via <see cref="EnumNameLookup{TEnum}"/>
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
                field = EnumNameLookup<ContextualKeyword>.TryParse(this.Span, out var keyword)
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
        EnumNameLookup<Keyword>.TryParse(command.AsSpan(index, length), out var keyword)
            && (keyword != Keyword.Regexp_Like || compatibilityLevel >= CompatibilityLevel.Sql170) ?
        new ReservedKeyword(keyword, command, index, length) :
        new UnquotedString(command, index, length);
}
