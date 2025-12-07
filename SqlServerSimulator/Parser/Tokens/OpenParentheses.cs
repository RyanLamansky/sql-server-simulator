namespace SqlServerSimulator.Parser.Tokens;

sealed class OpenParentheses(string command, int index) : Token(command, index, 1)
{
}
