namespace SqlServerSimulator.Parser.Tokens;

sealed class BracketDelimitedString(string value, string command, int index, int length) : Name(command, index, length)
{
    public override ReadOnlySpan<char> Span => Value.AsSpan();

    public override string Value { get; } = value;
}
