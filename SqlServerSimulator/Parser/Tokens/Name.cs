namespace SqlServerSimulator.Parser.Tokens;

abstract class Name(string command, int index, int length) : StringToken(command, index, length)
{
}
