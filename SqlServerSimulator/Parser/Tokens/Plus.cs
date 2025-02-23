namespace SqlServerSimulator.Parser.Tokens;

sealed class Plus : Token
{
#if DEBUG
    public override string ToString() => "+";
#endif
}
