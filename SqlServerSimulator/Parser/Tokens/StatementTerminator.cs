namespace SqlServerSimulator.Parser.Tokens;

sealed class StatementTerminator(string command, int index) : Token(command, index, 1)
{
}
