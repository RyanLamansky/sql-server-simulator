namespace SqlServerSimulator.Parser.Tokens;

abstract class StringToken : Token
{
    private protected StringToken(string command, int index, int length)
        : base(command, index, length)
    {
    }

    /// <summary>
    /// The value of the string after being parsed as a read-only span.
    /// </summary>
    public abstract ReadOnlySpan<char> Span { get; }

    /// <summary>
    /// The value of the string after being parsed as a substring, cut on first
    /// read and kept: a parse reads a name several times over (a dotted
    /// member is matched against each method family in turn), and a token can
    /// be read again by every parse the token memo serves.
    /// <see cref="Span"/> is preferable where no string is needed.
    /// </summary>
    /// <remarks>This should be overridden if the memory allocation is avoidable.</remarks>
    public virtual string Value => field ??= new(this.Span);
}
