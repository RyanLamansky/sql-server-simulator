using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Trace;

/// <summary>
/// The default trace — trace 1, writing <c>log_1.trc</c> in the log directory
/// and rolling over at 20 MB to five files, with real's own events and
/// columns (<c>DefaultTraceEvents.tsv</c>) from the simulation's start — kept
/// as the events it took, in the order they fired, each with the session
/// columns it carries; its files are written from them only as something
/// reads them (<see cref="Render"/>), so a server nobody reads the trace of
/// never loads the trace catalog and pays an event a small record.
/// </summary>
internal sealed class DefaultTraceLog
{
    /// <summary>
    /// The events kept, past which the oldest give way — about what real's
    /// five 20 MB files hold of the object events.
    /// </summary>
    private const int Capacity = 400_000;

    private readonly Lock gate = new();
    private readonly Queue<Entry> entries = new();

    /// <summary>The files as last written, by path, for <c>fn_trace_gettable</c>.</summary>
    private System.Collections.Concurrent.ConcurrentDictionary<string, TraceFile>? rendered;
    private long renderedAt = -1;
    private long version;

    /// <summary>The file the trace writes, which <c>sys.traces.path</c> names, as last written.</summary>
    public string CurrentPath = BuiltInResources.DefaultTracePath;

    /// <summary>How many events the trace has written, <c>sys.traces.event_count</c>.</summary>
    public long EventCount;

    /// <summary>
    /// One event the trace took — its columns, or the compact row they're
    /// made from — or a stop (<see cref="UserTrace.StopClass"/>) or start of
    /// the trace.
    /// </summary>
    private sealed class Entry(int eventClass, DateTime time, SqlValue?[]? values, TraceEvents.ObjectRow? row)
    {
        public readonly int EventClass = eventClass;
        public readonly DateTime Time = time;
        public readonly SqlValue?[]? Values = values;
        public readonly TraceEvents.ObjectRow? Row = row;
    }

    /// <summary>Keeps an event of <paramref name="eventClass"/> whose columns are <paramref name="values"/>, or a stop or start for none.</summary>
    public void Add(int eventClass, SqlValue?[]? values, DateTime time) => this.Add(new Entry(eventClass, time, values, null));

    /// <summary>Keeps an object event as its compact row.</summary>
    public void Add(TraceEvents.ObjectRow row) => this.Add(new Entry(row.EventClass, row.Time, null, row));

    private void Add(Entry entry)
    {
        lock (this.gate)
        {
            this.entries.Enqueue(entry);
            if (this.entries.Count > Capacity)
                _ = this.entries.Dequeue();
            if (entry.Values is not null || entry.Row is not null)
                this.EventCount++;
            this.version++;
        }
    }

    /// <summary>
    /// The trace's files, written from the events kept: the header and start
    /// rows, then each event as the trace's columns choose it, rolling over
    /// as real's does.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, TraceFile> Render(Simulation simulation, SqlTraceServer server)
    {
        Entry[] kept;
        lock (this.gate)
        {
            if (this.rendered is { } current && this.renderedAt == this.version)
                return current;
            kept = [.. this.entries];
        }
        var trace = new UserTrace(1, 2, ErrorLog.Directory + "log", 20, null, 5, "sa");
        foreach (var (eventId, columnId) in TraceCatalog.Instance.DefaultTraceEvents)
            trace.SetEvent(eventId, columnId, on: true);
        var files = new System.Collections.Concurrent.ConcurrentDictionary<string, TraceFile>(StringComparer.OrdinalIgnoreCase);
        trace.CreateFile(files, number: 1);
        trace.Start(simulation, server, simulation.SeedDate, publish: false);
        foreach (var entry in kept)
        {
            if ((entry.Row?.Values() ?? entry.Values) is { } values)
                trace.Write(simulation, server, null, entry.EventClass, values, entry.Time);
            else if (entry.EventClass == UserTrace.StopClass)
                _ = trace.Stop(simulation, server, entry.Time, publish: false);
            else
                trace.Start(simulation, server, entry.Time, publish: false);
        }
        lock (this.gate)
        {
            this.rendered = files;
            this.renderedAt = this.version;
            this.CurrentPath = trace.CurrentPath;
        }
        return files;
    }
}
