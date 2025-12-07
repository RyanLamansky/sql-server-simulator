
namespace SqlServerSimulator.Parser.Tokens;

sealed class AtPrefixedString(string command, int index, int length) : StringToken(command, index, length)
{
    public override ReadOnlySpan<char> Span => Source[1..];
}
