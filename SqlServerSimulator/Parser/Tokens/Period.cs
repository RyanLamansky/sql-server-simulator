namespace SqlServerSimulator.Parser.Tokens;

sealed class Period : Token
{
#if DEBUG
    public override string ToString() => ".";
#endif
}
