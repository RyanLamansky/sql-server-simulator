namespace SqlServerSimulator.Storage;

/// <summary>
/// Classifies a column's <c>GENERATED ALWAYS AS ROW</c> declaration. Only
/// system-versioned temporal tables declare period columns; the parent table
/// has exactly one <c>Start</c> and one <c>End</c> column, named in its
/// <c>PERIOD FOR SYSTEM_TIME (start, end)</c> clause. The history table's
/// matching columns mirror the parent's types and the same flags but aren't
/// engine-populated (history rows carry the parent's chosen values).
/// </summary>
internal enum GeneratedAlwaysAsRow
{
    None = 0,
    Start,
    End,
}
