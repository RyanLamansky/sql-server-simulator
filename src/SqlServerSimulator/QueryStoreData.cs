namespace SqlServerSimulator;

/// <summary>
/// What a database's Query Store has captured: the query texts, queries,
/// plans, context settings, runtime-statistics intervals and rows, hints and
/// forcing locations the <c>sys.query_store_*</c> views project, plus the
/// execution tally a threshold capture mode (AUTO / CUSTOM) keeps for a
/// query it hasn't captured yet. Fed by
/// <c>Simulation.RecordQueryStoreExecution</c> as statements complete, and
/// read and trimmed by the views and the <c>sp_query_store_*</c> procedures.
/// </summary>
/// <remarks>
/// Every member is guarded by <see cref="Gate"/>: sessions record
/// concurrently, and a view snapshots its rows under the same lock. Ids are
/// allocated per database from 1 and never reused, as real's are — a removed
/// query leaves a gap. Real registers a new query asynchronously, so a view
/// read right after a first execution can miss it there for a moment; here
/// it is visible as soon as the statement completes.
/// </remarks>
internal sealed class QueryStoreData
{
    public readonly Lock Gate = new();

    public readonly List<QueryStoreText> Texts = [];
    public readonly Dictionary<string, QueryStoreText> TextsByText = new(StringComparer.Ordinal);
    public readonly List<QueryStoreContextSettings> ContextSettings = [];
    public readonly List<QueryStoreQuery> Queries = [];
    public readonly Dictionary<QueryStoreQueryKey, QueryStoreQuery> QueriesByKey = [];
    public readonly List<QueryStorePlan> Plans = [];
    public readonly List<QueryStoreInterval> Intervals = [];
    public readonly List<QueryStoreRuntimeStats> RuntimeStats = [];
    public readonly Dictionary<(long PlanId, long IntervalId, byte ExecutionType), QueryStoreRuntimeStats> RuntimeStatsByKey = [];
    public readonly List<QueryStoreWaitStats> WaitStats = [];
    public readonly Dictionary<(long PlanId, long IntervalId, byte ExecutionType), QueryStoreWaitStats> WaitStatsByKey = [];
    public readonly List<QueryStoreHint> Hints = [];
    public readonly List<QueryStoreForcingLocation> ForcingLocations = [];

    /// <summary>
    /// The executions a threshold capture mode has seen of each query it
    /// hasn't captured yet. Bounded by <see cref="PendingCapacity"/>: a
    /// workload of distinct texts empties it rather than growing it without
    /// limit, which costs a query only the executions it had tallied.
    /// </summary>
    public readonly Dictionary<QueryStoreQueryKey, QueryStorePending> Pending = [];

    /// <summary>How many uncaptured queries <see cref="Pending"/> tallies before it starts over.</summary>
    public const int PendingCapacity = 4096;

    public long NextTextId = 1;
    public long NextContextSettingsId = 1;
    public long NextQueryId = 1;
    public long NextPlanId = 1;
    public long NextIntervalId = 1;
    public long NextRuntimeStatsId = 1;
    public long NextWaitStatsId = 1;
    public long NextHintId = 1;
    public long NextForcingLocationId = 1;

    /// <summary>Whether anything has been captured, which a NONE capture mode keeps updating.</summary>
    public bool IsEmpty => this.Queries.Count == 0;

    /// <summary>
    /// Forgets everything captured: <c>ALTER DATABASE … SET QUERY_STORE
    /// CLEAR [ALL]</c>. The id counters restart too — real numbers a cleared
    /// store's next query 1 again.
    /// </summary>
    public void Clear()
    {
        lock (this.Gate)
        {
            this.Texts.Clear();
            this.TextsByText.Clear();
            this.ContextSettings.Clear();
            this.Queries.Clear();
            this.QueriesByKey.Clear();
            this.Plans.Clear();
            this.Intervals.Clear();
            this.RuntimeStats.Clear();
            this.RuntimeStatsByKey.Clear();
            this.WaitStats.Clear();
            this.WaitStatsByKey.Clear();
            this.Hints.Clear();
            this.ForcingLocations.Clear();
            this.Pending.Clear();
            this.NextTextId = this.NextContextSettingsId = this.NextQueryId = this.NextPlanId = 1;
            this.NextIntervalId = this.NextRuntimeStatsId = this.NextWaitStatsId = this.NextHintId = this.NextForcingLocationId = 1;
        }
    }

    /// <summary>
    /// <c>current_storage_size_mb</c>: a rough byte count of what is held,
    /// rounded up to whole megabytes, so an empty store reads 0 and any
    /// captured query at least 1 — the two readings real's small stores give.
    /// Caller holds <see cref="Gate"/>.
    /// </summary>
    public long StorageSizeMb()
    {
        if (this.Queries.Count == 0 && this.Texts.Count == 0)
            return 0;
        long bytes = 0;
        foreach (var text in this.Texts)
            bytes += 256 + (2L * text.Text.Length);
        foreach (var plan in this.Plans)
            bytes += 512 + (2L * plan.PlanXml.Length);
        bytes += (512L * this.Queries.Count) + (768L * this.RuntimeStats.Count) + (128L * this.Intervals.Count);
        return (bytes + (1024 * 1024) - 1) / (1024 * 1024);
    }

    /// <summary>
    /// Removes a query with its plans, their runtime statistics, its hints
    /// and forcing locations: <c>sp_query_store_remove_query</c>. The text
    /// goes too once no other query shares it. Caller holds <see cref="Gate"/>.
    /// </summary>
    public void RemoveQuery(QueryStoreQuery query)
    {
        _ = this.Queries.Remove(query);
        _ = this.QueriesByKey.Remove(query.Key);
        foreach (var plan in this.Plans.Where(plan => plan.Query == query).ToArray())
            this.RemovePlan(plan);
        _ = this.Hints.RemoveAll(hint => hint.QueryId == query.QueryId);
        if (!this.Queries.Exists(other => other.Text == query.Text))
        {
            _ = this.Texts.Remove(query.Text);
            _ = this.TextsByText.Remove(query.Text.Text);
        }
    }

    /// <summary>
    /// Removes a plan with its runtime statistics and forcing locations:
    /// <c>sp_query_store_remove_plan</c>, which leaves the query standing.
    /// Caller holds <see cref="Gate"/>.
    /// </summary>
    public void RemovePlan(QueryStorePlan plan)
    {
        _ = this.Plans.Remove(plan);
        this.ResetRuntimeStats(plan);
        _ = this.ForcingLocations.RemoveAll(location => location.PlanId == plan.PlanId);
        if (plan.Query.Plan == plan)
            plan.Query.Plan = null;
    }

    /// <summary>
    /// Drops a plan's runtime and wait statistics:
    /// <c>sp_query_store_reset_exec_stats</c>. Caller holds <see cref="Gate"/>.
    /// </summary>
    public void ResetRuntimeStats(QueryStorePlan plan)
    {
        foreach (var stats in this.RuntimeStats.Where(stats => stats.PlanId == plan.PlanId).ToArray())
        {
            _ = this.RuntimeStats.Remove(stats);
            _ = this.RuntimeStatsByKey.Remove((stats.PlanId, stats.IntervalId, stats.ExecutionType));
        }
        foreach (var waits in this.WaitStats.Where(waits => waits.PlanId == plan.PlanId).ToArray())
        {
            _ = this.WaitStats.Remove(waits);
            _ = this.WaitStatsByKey.Remove((waits.PlanId, waits.IntervalId, waits.ExecutionType));
        }
    }

    /// <summary>The query with <paramref name="queryId"/>, or null. Caller holds <see cref="Gate"/>.</summary>
    public QueryStoreQuery? FindQuery(long queryId) => this.Queries.Find(query => query.QueryId == queryId);

    /// <summary>The plan with <paramref name="planId"/>, or null. Caller holds <see cref="Gate"/>.</summary>
    public QueryStorePlan? FindPlan(long planId) => this.Plans.Find(plan => plan.PlanId == planId);
}

/// <summary>
/// What makes two executions the same Query Store query: the text as stored
/// (a parameterized form or a declaration prefix included), the context
/// settings, the containing module, how it was parameterized, and — for a
/// statement reading a table variable, which is scoped to its batch — the
/// batch's text.
/// </summary>
internal readonly struct QueryStoreQueryKey(string text, QueryStoreContextKey context, long objectId, byte parameterizationType, string? batchText)
    : IEquatable<QueryStoreQueryKey>
{
    public readonly string Text = text;
    public readonly QueryStoreContextKey Context = context;
    public readonly long ObjectId = objectId;
    public readonly byte ParameterizationType = parameterizationType;
    public readonly string? BatchText = batchText;

    public bool Equals(QueryStoreQueryKey other) =>
        this.ObjectId == other.ObjectId
        && this.ParameterizationType == other.ParameterizationType
        && this.Context.Equals(other.Context)
        && string.Equals(this.Text, other.Text, StringComparison.Ordinal)
        && string.Equals(this.BatchText, other.BatchText, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is QueryStoreQueryKey other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.Text, this.Context, this.ObjectId, this.ParameterizationType, this.BatchText);
}

/// <summary>
/// The settings a <c>sys.query_context_settings</c> row carries: the plan-
/// affecting <c>SET</c> options as real's <c>set_options</c> bit mask, the
/// language, date format and first day, and the default schema the statement
/// resolved one-part names through (-2 when it resolved none that way).
/// </summary>
internal readonly struct QueryStoreContextKey(int setOptions, short languageId, short dateFormat, byte dateFirst, int defaultSchemaId)
    : IEquatable<QueryStoreContextKey>
{
    public readonly int SetOptions = setOptions;
    public readonly short LanguageId = languageId;
    public readonly short DateFormat = dateFormat;
    public readonly byte DateFirst = dateFirst;
    public readonly int DefaultSchemaId = defaultSchemaId;

    public bool Equals(QueryStoreContextKey other) =>
        this.SetOptions == other.SetOptions && this.LanguageId == other.LanguageId && this.DateFormat == other.DateFormat
        && this.DateFirst == other.DateFirst && this.DefaultSchemaId == other.DefaultSchemaId;

    public override bool Equals(object? obj) => obj is QueryStoreContextKey other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.SetOptions, this.LanguageId, this.DateFormat, this.DateFirst, this.DefaultSchemaId);
}

/// <summary>A <c>sys.query_store_query_text</c> row.</summary>
internal sealed class QueryStoreText(long id, string text, byte[] statementSqlHandle)
{
    public readonly long Id = id;
    public readonly string Text = text;
    public readonly byte[] StatementSqlHandle = statementSqlHandle;
}

/// <summary>A <c>sys.query_context_settings</c> row.</summary>
internal sealed class QueryStoreContextSettings(long id, QueryStoreContextKey key)
{
    public readonly long Id = id;
    public readonly QueryStoreContextKey Key = key;
}

/// <summary>A <c>sys.query_store_query</c> row, with the one plan the simulator compiles for it.</summary>
internal sealed class QueryStoreQuery(long queryId, QueryStoreQueryKey key, QueryStoreText text, QueryStoreContextSettings context, byte[] queryHash, byte[]? batchSqlHandle, DateTime firstCompile)
{
    public readonly long QueryId = queryId;
    public readonly QueryStoreQueryKey Key = key;
    public readonly QueryStoreText Text = text;
    public readonly QueryStoreContextSettings Context = context;
    public readonly byte[] QueryHash = queryHash;
    public readonly byte[]? BatchSqlHandle = batchSqlHandle;
    public readonly DateTime InitialCompileStartTime = firstCompile;
    public DateTime LastCompileStartTime = firstCompile;
    public DateTime LastExecutionTime = firstCompile;
    public byte[] LastCompileBatchSqlHandle = [];
    public long LastCompileBatchOffsetStart;
    public long LastCompileBatchOffsetEnd;
    public long CountCompiles = 1;

    /// <summary>The query's plan; null once <c>sp_query_store_remove_plan</c> took it, until the next execution compiles another.</summary>
    public QueryStorePlan? Plan;
}

/// <summary>A <c>sys.query_store_plan</c> row.</summary>
internal sealed class QueryStorePlan(long planId, QueryStoreQuery query, short compatibilityLevel, byte[] planHash, string planXml, bool isTrivial, DateTime firstCompile)
{
    public readonly long PlanId = planId;
    public readonly QueryStoreQuery Query = query;
    public readonly short CompatibilityLevel = compatibilityLevel;
    public readonly byte[] QueryPlanHash = planHash;
    public readonly string PlanXml = planXml;
    public readonly bool IsTrivial = isTrivial;
    public readonly DateTime InitialCompileStartTime = firstCompile;
    public DateTime LastExecutionTime = firstCompile;
    public bool IsForced;

    /// <summary><c>@disable_optimized_plan_forcing</c> of the forcing call.</summary>
    public bool OptimizedPlanForcingDisabled;
}

/// <summary>A <c>sys.query_store_runtime_stats_interval</c> row: a fixed-length window aligned to its length.</summary>
internal sealed class QueryStoreInterval(long id, DateTime start, DateTime end)
{
    public readonly long Id = id;
    public readonly DateTime Start = start;
    public readonly DateTime End = end;
}

/// <summary>
/// One <c>sys.query_store_runtime_stats</c> row: a plan's executions of one
/// type within one interval, each metric's running sum, sum of squares,
/// last, minimum and maximum.
/// </summary>
internal sealed class QueryStoreRuntimeStats(long id, long planId, long intervalId, byte executionType, DateTime first)
{
    public readonly long Id = id;
    public readonly long PlanId = planId;
    public readonly long IntervalId = intervalId;
    public readonly byte ExecutionType = executionType;
    public readonly DateTime FirstExecutionTime = first;
    public DateTime LastExecutionTime = first;
    public long Count;
    public QueryStoreMetric Duration;
    public QueryStoreMetric CpuTime;
    public QueryStoreMetric LogicalIoReads;
    public QueryStoreMetric LogicalIoWrites;
    public QueryStoreMetric RowCount;
}

/// <summary>
/// One <c>sys.query_store_wait_stats</c> row: the <c>Lock</c> waits of a
/// plan's executions of one type within one interval — the one wait category
/// the simulator has — over the executions that waited.
/// </summary>
internal sealed class QueryStoreWaitStats(long id, long planId, long intervalId, byte executionType)
{
    public readonly long Id = id;
    public readonly long PlanId = planId;
    public readonly long IntervalId = intervalId;
    public readonly byte ExecutionType = executionType;
    public long Count;
    public QueryStoreMetric WaitTime;
}

/// <summary>One metric's running aggregate within a runtime-statistics row.</summary>
internal struct QueryStoreMetric
{
    public double Sum;
    public double SumOfSquares;
    public long Last;
    public long Min;
    public long Max;

    public void Add(long value, bool first)
    {
        this.Sum += value;
        this.SumOfSquares += (double)value * value;
        this.Last = value;
        this.Min = first ? value : Math.Min(this.Min, value);
        this.Max = first ? value : Math.Max(this.Max, value);
    }

    public readonly double Average(long count) => count == 0 ? 0 : this.Sum / count;

    /// <summary>The population standard deviation, which reads 0 for a single execution as real's does.</summary>
    public readonly double StandardDeviation(long count)
    {
        if (count <= 1)
            return 0;
        var mean = this.Sum / count;
        return Math.Sqrt(Math.Max(0, (this.SumOfSquares / count) - (mean * mean)));
    }
}

/// <summary>A <c>sys.query_store_query_hints</c> row, set by <c>sp_query_store_set_hints</c>.</summary>
internal sealed class QueryStoreHint(long id, long queryId, string text, Parser.Selection.OptionClause clause, bool recompiles)
{
    public readonly long Id = id;
    public readonly long QueryId = queryId;
    public readonly string Text = text;

    /// <summary>What the clause sets, parsed as a statement's own <c>OPTION</c> clause.</summary>
    public readonly Parser.Selection.OptionClause Clause = clause;

    /// <summary>The clause carries <c>RECOMPILE</c>.</summary>
    public readonly bool Recompiles = recompiles;

    /// <summary>The error that last kept the hint from applying — 8622, <c>NO_PLAN</c> — or 0.</summary>
    public int FailureReason;

    /// <summary>How many compiles the hint failed to apply to.</summary>
    public long FailureCount;
}

/// <summary>A <c>sys.query_store_plan_forcing_locations</c> row, left by <c>sp_query_store_force_plan</c>.</summary>
internal sealed class QueryStoreForcingLocation(long id, long queryId, long planId, DateTime timestamp)
{
    public readonly long Id = id;
    public readonly long QueryId = queryId;
    public readonly long PlanId = planId;
    public readonly DateTime Timestamp = timestamp;
}

/// <summary>
/// A threshold capture mode's tally of a query it hasn't captured: its
/// executions and their CPU time since <see cref="Since"/>, which a tally
/// older than the policy's stale threshold starts over from.
/// </summary>
internal sealed class QueryStorePending(DateTime since)
{
    public DateTime Since = since;
    public long Executions;
    public long CpuMicroseconds;
}
