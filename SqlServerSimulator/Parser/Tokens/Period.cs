namespace SqlServerSimulator.Parser.Tokens;

sealed class Period(string command, int index) : Token(command, index, 1)
{
}
