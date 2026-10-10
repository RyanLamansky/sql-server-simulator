namespace SqlServerSimulator;

/// <summary>
/// The <c>ALTER DATABASE … SET</c> switches a <see cref="Database"/> records
/// without modeling their behavior — the ANSI defaults, the automatic
/// statistics and file options, the cursor and parameterization defaults —
/// which <c>sys.databases</c> and <c>DATABASEPROPERTYEX</c> report.
/// </summary>
[Flags]
internal enum DatabaseSwitches
{
    None = 0,
    AnsiNullDefault = 1 << 0,
    AnsiNulls = 1 << 1,
    AnsiPadding = 1 << 2,
    AnsiWarnings = 1 << 3,
    ArithAbort = 1 << 4,
    ConcatNullYieldsNull = 1 << 5,
    NumericRoundAbort = 1 << 6,
    QuotedIdentifier = 1 << 7,
    AutoClose = 1 << 8,
    AutoShrink = 1 << 9,
    AutoCreateStatistics = 1 << 10,
    AutoCreateStatisticsIncremental = 1 << 11,
    AutoUpdateStatistics = 1 << 12,
    AutoUpdateStatisticsAsync = 1 << 13,
    CursorCloseOnCommit = 1 << 14,
    LocalCursorDefault = 1 << 15,
    ParameterizationForced = 1 << 16,
    DateCorrelationOptimization = 1 << 17,
    TemporalHistoryRetention = 1 << 18,

    /// <summary>
    /// <c>MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT</c>: a READ COMMITTED
    /// transaction reads a memory-optimized table under SNAPSHOT rather than
    /// being refused (Msg 41368). Load-bearing, unlike the rest.
    /// </summary>
    MemoryOptimizedElevateToSnapshot = 1 << 19,

    HonorBrokerPriority = 1 << 20,

    /// <summary>On in <c>master</c>, <c>model</c> and <c>msdb</c>, which pin it, and off in every other database (probed 2026-10-09 against SQL Server 2025).</summary>
    MixedPageAllocation = 1 << 21,

    SupplementalLogging = 1 << 22,

    /// <summary>What every database, system or user, starts with (probed 2026-09-26 against SQL Server 2025).</summary>
    Defaults = AutoCreateStatistics | AutoUpdateStatistics | TemporalHistoryRetention,
}
