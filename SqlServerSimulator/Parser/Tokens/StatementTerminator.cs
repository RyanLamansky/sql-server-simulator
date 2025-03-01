namespace SqlServerSimulator.Parser.Tokens;

sealed class StatementTerminator : Token
{
    public override string ToString() => ";";
}
