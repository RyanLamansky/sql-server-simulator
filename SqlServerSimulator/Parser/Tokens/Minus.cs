namespace SqlServerSimulator.Parser.Tokens;

sealed class Minus(string command, int index) : Token(command, index, 1)
{
}
