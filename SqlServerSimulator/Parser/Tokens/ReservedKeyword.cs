namespace SqlServerSimulator.Parser.Tokens;

internal sealed class ReservedKeyword(Keyword keyword, string value) : Token
{
    public readonly Keyword Keyword = keyword;

    /// <summary>
    /// The original value as provided in the command.
    /// </summary>
    /// <remarks>This preserves input casing for potential error messages.</remarks>
    public readonly string Value = value;

    public override string ToString() => this.Value;
}
