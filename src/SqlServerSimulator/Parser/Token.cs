namespace SqlServerSimulator.Parser;

/// <summary>
/// Describes a single token in a SQL command.
/// </summary>
abstract class Token
{
    /// <summary>
    /// The original SQL command that contains this token. Exposed to the
    /// tokens namespace so <see cref="Tokens.SynonymTypeName"/> can span
    /// several adjacent word tokens over the shared source.
    /// </summary>
    internal readonly string command;

    /// <summary>
    /// Start offset of this token in the owning command's source string.
    /// Used by <c>CREATE FUNCTION</c> to capture the body source span between
    /// the outer <c>BEGIN</c> and its matching <c>END</c> — the function's
    /// body is stored as raw text and re-tokenized per call.
    /// </summary>
    public readonly int StartIndex;

    private readonly int length;

    private protected Token(string command, int index, int length)
    {
        System.Diagnostics.Debug.Assert(index >= 0);
        System.Diagnostics.Debug.Assert(length > 0);
        System.Diagnostics.Debug.Assert(index + length <= command.Length);

        this.command = command;
        this.StartIndex = index;
        this.length = length;
    }

    /// <summary>
    /// Returns a span containing the portion of the original command this token is based upon.
    /// </summary>
    public ReadOnlySpan<char> Source => command.AsSpan(StartIndex, length);

    /// <summary>Offset just past the last character of this token in its command source.</summary>
    public int EndIndex => this.StartIndex + this.length;

    /// <summary>
    /// 1-based line number of this token within its source command. Lines are
    /// delimited by <c>\n</c> (CR before it is folded into the same line, so
    /// CRLF and LF behave the same). Used to render <c>"Line N"</c> prefixes
    /// in error messages that mirror SQL Server's parse-time errors.
    /// </summary>
    public int LineNumber => LineAt(command, StartIndex);

    /// <summary>
    /// 1-based line number of the character at <paramref name="index"/> within
    /// <paramref name="command"/>, counting <c>\n</c> delimiters (CR folded into
    /// the following LF, so CRLF and LF behave the same). Shared by
    /// <see cref="LineNumber"/> and the tokenizer's mid-token failure paths
    /// (unclosed string Msg 105 at the opening quote, unclosed block comment
    /// Msg 113 at end-of-input), whose reported line comes from the tokenizer's
    /// own position rather than the last-consumed token.
    /// </summary>
    public static int LineAt(string command, int index)
    {
        var line = 1;
        var prefix = command.AsSpan(0, index);
        foreach (var c in prefix)
        {
            if (c == '\n')
                line++;
        }
        return line;
    }

    // This is used for various error messages even though tokens are not directly accessible to user code.
    public sealed override string ToString() => command.Substring(StartIndex, length);

    /// <summary>
    /// Longest source-spelled token real keeps in a message's <c>near '…'</c>
    /// slot. Probe-confirmed against SQL Server 2025 (2026-07-31): a 200-digit
    /// numeric literal is reported clipped to its first 128 characters.
    /// </summary>
    private protected const int MaxErrorTextLength = 128;

    /// <summary>
    /// This token's text as SQL Server renders it in a message's
    /// <c>near '…'</c> slot, which is not always the way the token was
    /// spelled: <see cref="Tokens.Literal"/> and
    /// <see cref="Tokens.DelimitedIdentifier"/> override this because real
    /// names the value they denote instead. Deliberately separate from
    /// <see cref="ToString"/>, which stays source-exact for the parser's own
    /// text matching (table-hint and query-hint name lookup read it).
    /// </summary>
    public virtual string ErrorText => Clip(Source);

    /// <summary>
    /// Materializes <paramref name="text"/> clipped to
    /// <see cref="MaxErrorTextLength"/> characters.
    /// </summary>
    private protected static string Clip(ReadOnlySpan<char> text) =>
        new(text[..Math.Min(text.Length, MaxErrorTextLength)]);

#if DEBUG
    /// <summary>
    /// Identifies this token within the scope of the full command by wrapping it with '»' and '«';
    /// </summary>
    public void Highlight(Span<char> result)
    {
        var command = this.command.AsSpan();

        command[..this.StartIndex].CopyTo(result);
        result[StartIndex] = '»';
        this.Source.CopyTo(result[(StartIndex + 1)..]);
        result[StartIndex + 1 + this.length] = '«';
        command[(StartIndex + length)..].CopyTo(result[(StartIndex + length + 2)..]);
    }
#endif
}
