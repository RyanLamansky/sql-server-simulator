using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// <c>auto_stats</c> — and through it the trace's <c>Auto Stats</c> — as a
/// query's compile creates or refreshes a statistic, with the fields SQL
/// Server 2025 gives it (probed 2026-10-16): a refresh fires <c>Loading and
/// updating stats</c> naming <c>table.statistic</c>, then status
/// <c>Other</c> with <c>Updated: table.statistic</c>, its duration, the
/// degree of parallelism and a 100% sample; a creation fires status
/// <c>Other</c> with <c>Created: column</c> under index 0, then
/// <c>Loading stats without updating</c> for the statistic it made.
/// </summary>
internal static class XeStatisticsEvents
{
    /// <summary>Whether a running session or trace takes <c>auto_stats</c>, one array read.</summary>
    public static bool Taken(Simulation simulation) => XeEvents.Takes(simulation, XeEventKind.AutoStats);

    /// <summary>A refresh of <paramref name="statistic"/> about to start.</summary>
    public static void Refreshing(Parser.BatchContext batch, HeapTable table, int statsId, string statistic) =>
        Fire(batch, table, statsId, 8, $"Loading and updating: {table.Name}.{statistic}", 0, refresh: false);

    /// <summary>A refresh of <paramref name="statistic"/> that took <paramref name="durationMicroseconds"/>.</summary>
    public static void Refreshed(Parser.BatchContext batch, HeapTable table, int statsId, string statistic, long durationMicroseconds) =>
        Fire(batch, table, statsId, 1, $"Updated: {table.Name}.{statistic}", durationMicroseconds, refresh: true);

    /// <summary>A statistic created over <paramref name="column"/>, then loaded.</summary>
    public static void Created(Parser.BatchContext batch, HeapTable table, int statsId, string statistic, string column, long durationMicroseconds)
    {
        Fire(batch, table, 0, 1, $"Created: {column}", durationMicroseconds, refresh: false);
        Fire(batch, table, statsId, 9, $"Loading without updating: {table.Name}.{statistic}", 0, refresh: false);
    }

    private static void Fire(Parser.BatchContext batch, HeapTable table, int indexId, ulong status, string list, long durationMicroseconds, bool refresh)
    {
        var connection = batch.Connection;
        var database = XeObjectEvents.DatabaseOf(batch, table);
        XeEvents.Fire(connection, XeEventKind.AutoStats, @event => XeEvents.Data(@event,
            ("database_id", XeValue.FromSigned(database.Id)),
            ("object_id", XeValue.FromSigned(table.ObjectId)),
            ("index_id", XeValue.FromUnsigned((ulong)indexId)),
            ("job_id", XeValue.FromSigned(0)),
            ("job_type", XeValue.FromMap(0, null)),
            ("status", XeValue.FromMap(status, null)),
            ("incremental", XeValue.FromBoolean(false)),
            ("async", XeValue.FromBoolean(false)),
            ("max_dop", XeValue.FromSigned(refresh ? Environment.ProcessorCount : -1)),
            ("sample_percentage", XeValue.FromSigned(refresh ? 100 : -1)),
            ("duration", XeValue.FromUnsigned((ulong)Math.Max(0, durationMicroseconds))),
            ("retries", XeValue.FromUnsigned(0)),
            ("success", XeValue.FromBoolean(true)),
            ("last_error", XeValue.FromSigned(0)),
            ("count", XeValue.FromUnsigned(1)),
            ("statistics_list", XeValue.FromUnicode(list)),
            ("database_name", XeValue.FromUnicode(""))));
    }
}
