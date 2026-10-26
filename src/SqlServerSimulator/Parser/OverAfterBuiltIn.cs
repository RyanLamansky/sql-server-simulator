using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// An <c>OVER</c> written after a call to a built-in that has no windowed
/// form. Real's grammar reads the clause as part of the call, so a syntax
/// error inside it comes first, then Msg 4113 naming the function — ahead of a
/// wrong argument count, save a count the function's own grammar settles — at
/// a state sorting the function: 1 for <c>CHECKSUM(*)</c> /
/// <c>BINARY_CHECKSUM(*)</c>, 2 for <c>GROUPING</c> / <c>GROUPING_ID</c>, 4
/// for <c>STRING_AGG</c>, 5 for <c>APPROX_COUNT_DISTINCT</c> and 6 for any
/// other. A call real's grammar reads as a keyword form — <c>CAST</c>,
/// <c>CONVERT</c>, <c>COALESCE</c>, <c>IIF</c>, <c>NULLIF</c>, <c>LEFT</c>,
/// <c>RIGHT</c>, <c>PARSE</c>, the <c>TRY_</c> trio, <c>TRIM</c> with its
/// <c>FROM</c> — ends at its parenthesis, which leaves the <c>OVER</c> the
/// syntax error after it, while <c>JSON_OBJECT</c>, <c>JSON_ARRAY</c> and
/// <c>JSON_QUERY … WITH ARRAY WRAPPER</c> read the clause and discard it
/// unbound (probed 2026-10-26 against SQL Server 2025).
/// </summary>
internal static class OverAfterBuiltIn
{
    /// <summary>
    /// Reads what follows <paramref name="call"/>, a call to a built-in with
    /// no windowed form, cursor on its closing parenthesis: raises Msg 4113
    /// for an <c>OVER</c> after it, or reads one after a JSON constructor and
    /// leaves the cursor on the clause's last token. Answers <paramref name="call"/>.
    /// </summary>
    public static Expression Read(Expression call, string name, bool starArgument, ParserContext context)
    {
        var afterCall = context.SaveCheckpoint();
        if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.Over })
        {
            context.RestoreCheckpoint(afterCall);
            return call;
        }

        Span<char> uppercaseName = stackalloc char[name.Length];
        _ = name.ToUpperInvariant(uppercaseName);
        switch (uppercaseName)
        {
            case "CAST" or "COALESCE" or "CONVERT" or "IIF" or "LEFT" or "NULLIF" or "PARSE" or "RIGHT" or "TRY_CAST" or "TRY_CONVERT" or "TRY_PARSE":
            case "TRIM" when call is Trim { TrimChars: not null }:
                context.RestoreCheckpoint(afterCall);
                return call;
            case "JSON_ARRAY" or "JSON_OBJECT":
            case "JSON_QUERY" when call is JsonQuery { ArrayWrapper: true }:
                Discard(context);
                return call;
        }
        WindowExpression.ParseClauseSyntax(context);
        throw Refusal(name, State(uppercaseName, starArgument));
    }

    /// <summary>
    /// Raises Msg 4113 for an <c>OVER</c> after a call to
    /// <paramref name="uppercaseName"/> whose argument count is wrong, which
    /// real reports ahead of the count for a built-in with no windowed form —
    /// save where the function's grammar settles the count itself:
    /// <c>LEFT</c> and <c>RIGHT</c> at any count, <c>TRIM</c> and
    /// <c>JSON_QUERY</c> above their range. Cursor on the argument list's
    /// first token, where it stays when no <c>OVER</c> follows the list.
    /// </summary>
    public static void RefuseAheadOfCount(ReadOnlySpan<char> uppercaseName, string writtenName, bool belowMinimum, ParserContext context)
    {
        switch (uppercaseName)
        {
            case "AVG" or "CHECKSUM_AGG" or "COUNT" or "COUNT_BIG" or "JSON_ARRAYAGG" or "JSON_OBJECTAGG" or "LEFT" or "MAX" or "MIN" or "PERCENTILE_CONT" or "PERCENTILE_DISC" or "PRODUCT" or "RIGHT" or "STDEV" or "STDEVP" or "SUM" or "VAR" or "VARP":
            case "JSON_QUERY" or "TRIM" when !belowMinimum:
                return;
        }
        var start = context.SaveCheckpoint();
        var starArgument = context.Token is Operator { Character: '*' };
        if (BuiltInArity.CountArguments(context) >= 0 && context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Over })
        {
            WindowExpression.ParseClauseSyntax(context);
            throw Refusal(writtenName, State(uppercaseName, starArgument));
        }
        context.RestoreCheckpoint(start);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Real's Msg 4113 names the function in lowercase however it was written; this reproduces that text rather than normalizing for comparison.")]
    private static SimulatedSqlException Refusal(string name, byte state) =>
        SimulatedSqlException.FunctionNotValidForOver(name.ToLowerInvariant(), state);

    private static byte State(ReadOnlySpan<char> uppercaseName, bool starArgument) => uppercaseName switch
    {
        "APPROX_COUNT_DISTINCT" => 5,
        "BINARY_CHECKSUM" or "CHECKSUM" when starArgument => 1,
        "GROUPING" or "GROUPING_ID" => 2,
        "STRING_AGG" => 4,
        _ => 6,
    };

    /// <summary>
    /// Reads the clause after a JSON constructor for its grammar and keeps
    /// nothing it parsed, since real binds none of it: an aggregate in it
    /// leaves the query ungrouped.
    /// </summary>
    private static void Discard(ParserContext context)
    {
        var aggregates = context.AggregateCollector?.Count ?? 0;
        var windows = context.WindowCollector?.Count ?? 0;
        WindowExpression.ParseClauseSyntax(context);
        context.AggregateCollector?.RemoveRange(aggregates, context.AggregateCollector.Count - aggregates);
        context.WindowCollector?.RemoveRange(windows, context.WindowCollector.Count - windows);
    }
}
