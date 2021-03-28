namespace SqlServerSimulator.Parser.Tokens
{
    class StatementTerminator : Token
    {
#if DEBUG
        public override string ToString() => ";";
#endif
    }
}
