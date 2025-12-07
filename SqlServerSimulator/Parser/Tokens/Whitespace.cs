namespace SqlServerSimulator.Parser.Tokens;

internal sealed class Whitespace(string command, int index, int length) : Token(command, index, length)
{
}
