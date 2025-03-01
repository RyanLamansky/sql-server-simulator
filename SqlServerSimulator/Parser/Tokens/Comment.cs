namespace SqlServerSimulator.Parser.Tokens;

sealed class Comment : Token
{
    public override string ToString() => "/* Comment */";
}
