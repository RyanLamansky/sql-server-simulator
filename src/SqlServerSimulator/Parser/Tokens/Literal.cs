using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// A typed literal value embedded in the SQL text: <c>'foo'</c> (varchar),
/// <c>N'foo'</c> (nvarchar), and <c>0xHEX</c> (varbinary). The surrounding
/// parser treats these uniformly as values via <see cref="Value"/>; numeric
/// literals retain their own <see cref="Numeric"/> token because they
/// participate in the integer-promotion path differently.
/// </summary>
internal sealed class Literal(SqlValue value, string command, int index, int length) : Token(command, index, length)
{
    public readonly SqlValue Value = value;
}
