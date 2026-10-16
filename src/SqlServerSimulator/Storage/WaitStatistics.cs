namespace SqlServerSimulator.Storage;

/// <summary>
/// The waits one scope has finished — the server's, behind
/// <c>sys.dm_os_wait_stats</c>, or one session's, behind
/// <c>sys.dm_exec_session_wait_stats</c> — counted per wait type as each
/// wait ends. The simulator waits only where a statement blocks on a lock
/// (<c>LCK_M_*</c>), sleeps in a <c>WAITFOR</c>, or holds its rows for a
/// client to read (<c>ASYNC_NETWORK_IO</c>), so those are the only types that
/// ever count; a wait the scheduler signals is never queued for a CPU here,
/// so no signal time accrues.
/// </summary>
internal sealed class WaitStatistics
{
    /// <summary>The totals of one wait type.</summary>
    public struct Totals
    {
        public long WaitingTasks;
        public long WaitMilliseconds;
        public long MaxWaitMilliseconds;
    }

    private readonly Dictionary<string, Totals> byType = new(StringComparer.Ordinal);

    /// <summary>Counts one finished wait of <paramref name="milliseconds"/> on <paramref name="waitType"/>.</summary>
    public void Record(string waitType, long milliseconds)
    {
        milliseconds = Math.Max(0, milliseconds);
        lock (this.byType)
        {
            ref var totals = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(this.byType, waitType, out _);
            totals.WaitingTasks++;
            totals.WaitMilliseconds += milliseconds;
            totals.MaxWaitMilliseconds = Math.Max(totals.MaxWaitMilliseconds, milliseconds);
        }
    }

    /// <summary>The totals of <paramref name="waitType"/>, zero when it never waited.</summary>
    public Totals Of(string waitType)
    {
        lock (this.byType)
            return this.byType.GetValueOrDefault(waitType);
    }

    /// <summary>A copy of every wait type that has waited, with its totals.</summary>
    public KeyValuePair<string, Totals>[] Snapshot()
    {
        lock (this.byType)
            return [.. this.byType];
    }

    /// <summary>Forgets every wait, as <c>DBCC SQLPERF('sys.dm_os_wait_stats', CLEAR)</c> does.</summary>
    public void Clear()
    {
        lock (this.byType)
            this.byType.Clear();
    }

    /// <summary>
    /// Real's wait types in the order <c>sys.dm_os_wait_stats</c> lists them,
    /// read from SQL Server 2025 (probed 2026-10-16): four end in a blank,
    /// kept as real spells them.
    /// </summary>
    public static readonly string[] WaitTypes = ReadNames("SqlServerSimulator.WaitTypes.txt");

    /// <summary>Real's latch classes in the order <c>sys.dm_os_latch_stats</c> lists them (probed 2026-10-16 against SQL Server 2025).</summary>
    public static readonly string[] LatchClasses = ReadNames("SqlServerSimulator.LatchClasses.txt");

    private static string[] ReadNames(string resource)
    {
        using var stream = typeof(WaitStatistics).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource {resource} is missing.");
        using var reader = new StreamReader(stream);
        var names = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length != 0)
                names.Add(line);
        }
        return [.. names];
    }
}
