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
/// and <see cref="Tokens.UnquotedString.ContextualKeyword"/> classifies on
/// demand with per-token caching. This keeps column references whose names
/// happen to match a contextual keyword (e.g. <c>create table t (Output int)</c>)
/// working without special casing — identifier positions never invoke the
/// contextual lookup.
/// </remarks>
enum ContextualKeyword
{
    NotChecked = 0, // Default field value — token hasn't been classified yet.
    _,              // Classified: not a contextual keyword.
    Action,
    After,
    Allow_Snapshot_Isolation,
    Always,
    Apply,
    At,
    Atomic,
    Cache,
    Catch,
    Compatibility_Level,
    Configuration,
    Cube,
    Cycle,
    Data_Consistency_Check,
    Delay,
    Disable,
    Enable,
    Encryption,
    First,
    Following,
    FullText,
    Generated,
    Grouping_Id,
    Grouping,
    Hidden,
    History_Table,
    Include,
    Increment,
    Input,
    Instead,
    Log,
    Matched,
    Max,
    MaxRecursion,
    MaxValue,
    MinValue,
    Next,
    No,
    NoWait,
    Object,
    Offset,
    Only,
    Out,
    Output,
    Partition,
    Period,
    Persisted,
    Preceding,
    Range,
    Read_Committed_Snapshot,
    ReadOnly,
    Recompile,
    Replication,
    Restart,
    Returns,
    Role,
    Rollup,
    Row,
    Rows,
    Schemabinding,
    Scoped,
    Sequence,
    Sets,
    Spatial,
    Target,
    SetError,
    Source,
    Start,
    System_Time,
    System_Versioning,
    Throw,
    Time,
    TraceOff,
    TraceOn,
    Transfer,
    Try,
    Type,
    Unbounded,
    Using,
    Value,
    Verbose_Truncation_Warnings,
    View_Metadata,
    Within,
    Work,
    Xml,
    Zone,
}
