namespace SqlServerSimulator.Parser.Tokens;

sealed class Comma(string command, int index) : Token(command, index, 1)
{
}
