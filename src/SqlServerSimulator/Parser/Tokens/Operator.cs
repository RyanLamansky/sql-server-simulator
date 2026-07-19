namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// Represents any of the various single-character operators.
/// </summary>
/// <remarks>Multi-character operators use <see cref="ReservedKeyword"/>.</remarks>
internal sealed class Operator(string command, int index) : Token(command, index, 1)
{
    public char Character => base.Source[0];
}
