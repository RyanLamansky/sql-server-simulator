namespace SqlServerSimulator.Parser.Tokens;

sealed class StatementTerminator : Token
{

#if DEBUG
    public override string ToString() => ";";
#endif
}
