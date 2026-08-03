namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// A delimited identifier — <c>[foo]</c>, or <c>"foo"</c> under the default
/// <c>SET QUOTED_IDENTIFIER ON</c>. The <see cref="Value"/> carries the
/// unescaped body; the delimiters themselves are not part of it.
/// </summary>
sealed class DelimitedIdentifier(string value, string command, int index, int length) : Name(command, index, length, value)
{
    public override ReadOnlySpan<char> Span => Value.AsSpan();

    public override string Value { get; } = value;

    /// <summary>
    /// Real names the undelimited body in a message's <c>near '…'</c> slot,
    /// escapes already collapsed: <c>[x]</c> is reported as <c>x</c> and
    /// <c>[a]]b]</c> as <c>a]b</c>. No clip applies — the constructor's
    /// 128-character gate (Msg 103) rejects anything longer first.
    /// </summary>
    public override string ErrorText => this.Value;
}
