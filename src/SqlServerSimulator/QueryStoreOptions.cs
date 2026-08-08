namespace SqlServerSimulator;

/// <summary>
/// A database's Query Store configuration — everything
/// <c>ALTER DATABASE … SET QUERY_STORE</c> can set and
/// <c>sys.database_query_store_options</c> projects. The simulator never
/// captures a query, so nothing here changes how a statement runs; the values
/// are retained so a database describes its configuration the way real does,
/// which is what a management tool reads back after configuring it.
/// </summary>
/// <remarks>
/// Field defaults are a fresh SQL Server 2025 user database's (probe-confirmed
/// 2026-08-08), which that release inherits from <c>model</c> — Query Store on
/// in READ_WRITE. <c>master</c> / <c>tempdb</c> / <c>msdb</c> are seeded OFF in
/// the <see cref="Simulation"/> constructor. Every value survives
/// <c>SET QUERY_STORE = OFF</c>: real reports the last-configured sub-options
/// on a disabled store, and re-enabling restores them.
/// </remarks>
internal sealed class QueryStoreOptions
{
    public QueryStoreState DesiredState = QueryStoreState.ReadWrite;
    public long FlushIntervalSeconds = 900;
    public long IntervalLengthMinutes = 60;
    public long MaxStorageSizeMb = 1000;
    public long StaleQueryThresholdDays = 30;
    public long MaxPlansPerQuery = 200;
    public QueryStoreCaptureMode CaptureMode = QueryStoreCaptureMode.Auto;

    /// <summary><c>SIZE_BASED_CLEANUP_MODE</c> — <c>size_based_cleanup_mode</c> 1 / AUTO when set, 0 / OFF when clear.</summary>
    public bool SizeBasedCleanupAuto = true;

    /// <summary><c>WAIT_STATS_CAPTURE_MODE</c> — <c>wait_stats_capture_mode</c> 1 / ON when set, 0 / OFF when clear.</summary>
    public bool WaitStatsCaptureOn = true;

    // QUERY_CAPTURE_POLICY sub-options. Real retains these independently of the
    // capture mode but projects all four NULL unless the mode is CUSTOM, and
    // restores them when the mode returns to CUSTOM — so they are stored
    // unconditionally and the projection applies the mask. The seeds are real's
    // own defaults for a store first switched to CUSTOM with no policy block.
    public int CapturePolicyExecutionCount = 30;
    public long CapturePolicyTotalCompileCpuTimeMs = 1000;
    public long CapturePolicyTotalExecutionCpuTimeMs = 100;
    public int CapturePolicyStaleThresholdHours = 24;

    /// <summary>
    /// A copy carrying the same values, so a <c>SET QUERY_STORE</c> options
    /// block can accumulate into a scratch instance and swap in only once the
    /// whole block has parsed. A block that raises partway through must leave
    /// the database's configuration untouched, which is what real does.
    /// </summary>
    public QueryStoreOptions Copy() => new()
    {
        DesiredState = this.DesiredState,
        FlushIntervalSeconds = this.FlushIntervalSeconds,
        IntervalLengthMinutes = this.IntervalLengthMinutes,
        MaxStorageSizeMb = this.MaxStorageSizeMb,
        StaleQueryThresholdDays = this.StaleQueryThresholdDays,
        MaxPlansPerQuery = this.MaxPlansPerQuery,
        CaptureMode = this.CaptureMode,
        SizeBasedCleanupAuto = this.SizeBasedCleanupAuto,
        WaitStatsCaptureOn = this.WaitStatsCaptureOn,
        CapturePolicyExecutionCount = this.CapturePolicyExecutionCount,
        CapturePolicyTotalCompileCpuTimeMs = this.CapturePolicyTotalCompileCpuTimeMs,
        CapturePolicyTotalExecutionCpuTimeMs = this.CapturePolicyTotalExecutionCpuTimeMs,
        CapturePolicyStaleThresholdHours = this.CapturePolicyStaleThresholdHours,
    };
}

/// <summary>
/// Query Store operational state, as <c>desired_state</c> / <c>actual_state</c>
/// encode it. The simulator's two states never disagree — real's do only while
/// a store it is transitioning or has forced read-only, neither of which
/// happens here.
/// </summary>
internal enum QueryStoreState
{
    Off = 0,

    /// <summary><c>OPERATION_MODE = READ_ONLY</c> — real serves captured data but records none.</summary>
    ReadOnly = 1,

    /// <summary><c>OPERATION_MODE = READ_WRITE</c>, and what a bare <c>SET QUERY_STORE = ON</c> selects.</summary>
    ReadWrite = 2,

    /// <summary>Real's failed-store state. Never reached here; carried so the encoding is complete.</summary>
    Error = 3,
}

/// <summary>
/// <c>QUERY_CAPTURE_MODE</c>, as <c>query_capture_mode</c> encodes it.
/// <see cref="Custom"/> is the only value under which real projects the four
/// <c>capture_policy_*</c> columns non-NULL.
/// </summary>
internal enum QueryStoreCaptureMode
{
    All = 1,
    Auto = 2,
    None = 3,
    Custom = 4,
}
