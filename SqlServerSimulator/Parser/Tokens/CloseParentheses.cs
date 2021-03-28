namespace SqlServerSimulator.Parser.Tokens
{
    sealed class CloseParentheses : Token
    {

#if DEBUG
        public override string ToString() => ")";
#endif
    }
}
