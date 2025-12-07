namespace SqlServerSimulator.Parser.Tokens;

sealed class Asterisk(string command, int index) : Token(command, index, 1)
{
}
