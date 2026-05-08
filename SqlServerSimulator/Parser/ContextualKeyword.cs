namespace SqlServerSimulator.Parser;

/// <summary>
/// Identifiers the parser treats as keywords in specific positions but that
/// aren't on SQL Server's official reserved-keyword list (see
/// <see cref="Keyword"/>, sourced verbatim from MS docs). Kept in a separate
/// enum so the reserved set stays tied to documentation while parser sites
/// still get typed dispatch — the alternative was scattered case-insensitive
/// span comparisons against string literals at every call site.
/// </summary>
/// <remarks>
/// Recognition happens at parse time, not tokenization time: tokenizer keeps
/// emitting <see cref="Tokens.UnquotedString"/> for any unquoted identifier,
/// and <see cref="ParserContext.MatchContextual"/> /
/// <see cref="ParserContext.AsContextual"/> classify on demand. This keeps
/// column references whose names happen to match a contextual keyword
/// (e.g. <c>create table t (Output int)</c>) working without special casing
/// — identifier positions never invoke the contextual lookup.
/// </remarks>
enum ContextualKeyword
{
    _ = 0, // Default — current token isn't a contextual keyword.
    Apply,
    Compatibility_Level,
    Configuration,
    First,
    Matched,
    Max,
    Next,
    Offset,
    Only,
    Output,
    Partition,
    Persisted,
    Row,
    Rows,
    Scoped,
    TraceOff,
    TraceOn,
    Using,
    Verbose_Truncation_Warnings,
}
