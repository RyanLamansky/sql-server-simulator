namespace SqlServerSimulator.Parser.Tokens;

sealed class CloseParentheses(string command, int index) : Token(command, index, 1)
{
}
