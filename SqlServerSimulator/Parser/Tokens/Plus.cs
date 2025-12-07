namespace SqlServerSimulator.Parser.Tokens;

sealed class Plus(string command, int index) : Token(command, index, 1)
{
}
