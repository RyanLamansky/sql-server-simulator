namespace SqlServerSimulator.Parser;

/// <summary>
/// Describes a single token in a SQL command.
/// </summary>
abstract class Token
{
    /// <summary>
    /// The original SQL command that contains this token.
    /// </summary>
    private readonly string command;

    private readonly int index, length;

    private protected Token(string command, int index, int length)
    {
        System.Diagnostics.Debug.Assert(index >= 0);
        System.Diagnostics.Debug.Assert(length > 0);
        System.Diagnostics.Debug.Assert(index + length <= command.Length);

        this.command = command;
        this.index = index;
        this.length = length;
    }

    /// <summary>
    /// Returns a span containing the portion of the original command this token is based upon.
    /// </summary>
    public ReadOnlySpan<char> Source => command.AsSpan(index, length);

    // This is used for various error messages even though tokens are not directly accessible to user code.
    public sealed override string ToString() => command.Substring(index, length);

#if DEBUG
    /// <summary>
    /// Identifies this token within the scope of the full command by wrapping it with '»' and '«';
    /// </summary>
    public void Highlight(Span<char> result)
    {
        var command = this.command.AsSpan();

        command[..this.index].CopyTo(result);
        result[index] = '»';
        this.Source.CopyTo(result[(index + 1)..]);
        result[index + 1 + this.length] = '«';
        command[(index + length)..].CopyTo(result[(index + length + 2)..]);
    }
#endif
}
