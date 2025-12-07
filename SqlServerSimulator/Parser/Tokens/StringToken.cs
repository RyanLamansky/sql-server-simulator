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
    /// The value of the string after being parsed as a substring.
    /// <see cref="Span"/> is preferable to avoid memory allocation.
    /// </summary>
    /// <remarks>This should be overridden if the memory allocation is avoidable.</remarks>
    public virtual string Value => new(Span);
}
