namespace SqlServerSimulator.Parser.Tokens;

#pragma warning disable CA1812 // False positive on "unused internal"; should be re-checked periodically.

sealed class Comma : Token
{
    public override string ToString() => ",";
}
