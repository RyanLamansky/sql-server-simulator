namespace SqlServerSimulator.Parser.Tokens;

#pragma warning disable IDE0079 // Remove unnecessary suppression -- it actually is necessary at the time of writing.
#pragma warning disable CA1812 // False positive on "unused internal"; should probably be reported as a bug...

sealed class Comma : Token
#pragma warning restore
{
    public override string ToString() => ",";
}
