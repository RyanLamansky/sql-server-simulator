namespace SqlServerSimulator.Parser.Tokens;

internal sealed class Whitespace(string command, int index, int length) : Token
{
    private readonly string command = command;
    private readonly int index = index, length = length;

    public override string ToString() => command.Substring(index, length);
}
