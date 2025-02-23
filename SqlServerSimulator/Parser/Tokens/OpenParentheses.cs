namespace SqlServerSimulator.Parser.Tokens;

sealed class OpenParentheses : Token
{
#if DEBUG
    public override string ToString() => "(";
#endif
}
