namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// A delimited identifier — <c>[foo]</c>, or <c>"foo"</c> under the default
/// <c>SET QUOTED_IDENTIFIER ON</c>. The <see cref="Value"/> carries the
/// unescaped body; the delimiters themselves are not part of it.
/// </summary>
sealed class DelimitedIdentifier(string value, string command, int index, int length) : Name(command, index, length)
{
    public override ReadOnlySpan<char> Span => Value.AsSpan();

    public override string Value { get; } = value;
}
