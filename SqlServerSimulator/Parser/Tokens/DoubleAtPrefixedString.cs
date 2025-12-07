namespace SqlServerSimulator.Parser.Tokens;

sealed class DoubleAtPrefixedString(string command, int index, int length) : StringToken(command, index, length)
{
    public override ReadOnlySpan<char> Span => Source[2..];

    public AtAtKeyword Parse() => !Enum.TryParse<AtAtKeyword>(Span, true, out var result)
        ? throw new NotSupportedException($"Simulated command processor doesn't know what to do with `{Span}`.")
        : result;
}

