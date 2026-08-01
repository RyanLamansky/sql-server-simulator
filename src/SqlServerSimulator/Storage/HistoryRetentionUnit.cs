namespace SqlServerSimulator.Storage;

/// <summary>
/// The time unit of a system-versioned table's <c>HISTORY_RETENTION_PERIOD</c>,
/// numbered the way <c>sys.tables.history_retention_period_unit</c> reports it
/// (probe-confirmed against SQL Server 2025 — the enum has no 0/1/2 members
/// because the option's grammar admits no sub-day unit). <see cref="Infinite"/>
/// pairs with a period of -1 and is the default for every system-versioned
/// table; a non-versioned table reports NULL for both catalog columns rather
/// than any value here.
/// </summary>
internal enum HistoryRetentionUnit
{
    Infinite = -1,
    Day = 3,
    Week = 4,
    Month = 5,
    Year = 6,
}
