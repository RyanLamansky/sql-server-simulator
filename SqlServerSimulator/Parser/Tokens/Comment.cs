namespace SqlServerSimulator.Parser.Tokens;

sealed class Comment : Token
{

#if DEBUG
    public override string ToString() => "/* Comment */";
#endif
}
