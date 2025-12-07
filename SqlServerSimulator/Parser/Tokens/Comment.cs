namespace SqlServerSimulator.Parser.Tokens;

sealed class Comment(string command, int index, int length) : Token(command, index, length)
{
}
