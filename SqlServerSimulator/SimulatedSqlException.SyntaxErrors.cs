using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    internal static SimulatedSqlException MissingEndCommentMark() => new("Missing end comment mark '*/'.", 113, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 105: a string literal opened with <c>'</c> was
    /// never closed before end of input.
    /// </summary>
    internal static SimulatedSqlException UnclosedStringLiteral() =>
        new("Unclosed quotation mark after the character string.", 105, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNearKeyword(ReservedKeyword token) => new($"Incorrect syntax near the keyword '{token}'.", 156, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(ParserContext context) => new($"Incorrect syntax near '{context.Token}'.", 102, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(Token? token) => new($"Incorrect syntax near '{token}'.", 102, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(char c) => new($"Incorrect syntax near '{c}'.", 102, 15, 1);
}
