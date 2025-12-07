namespace SqlServerSimulator.Parser.Tokens;

internal sealed class ReservedKeyword(Keyword keyword, string command, int index, int length) : Token(command, index, length)
{
    public readonly Keyword Keyword = keyword;
}
