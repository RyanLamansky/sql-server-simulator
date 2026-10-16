using System.Collections.Concurrent;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Trace;

/// <summary>
/// One server's SQL Trace state: the user traces <c>sp_trace_create</c> made,
/// the files they write, and which simulator events a running trace takes.
/// Made on first use by <see cref="Simulation.TracesFor"/>; the default trace
/// is not one of them (see <c>sys.traces</c>).
/// </summary>
internal sealed class SqlTraceServer
{
    private readonly Lock gate = new();

    private readonly SortedDictionary<int, UserTrace> traces = [];

    /// <summary>
    /// The trace files written, by path, case aside as real's Linux file
    /// layer reads one. A file outlives its trace, as one on disk does.
    /// </summary>
    public readonly ConcurrentDictionary<string, TraceFile> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The running traces, published whole on every start and stop so a firing site reads them without the gate.</summary>
    public volatile UserTrace[] Running = [];

    /// <summary>
    /// The server's event sequence, which numbers every event any trace
    /// writes, as <c>EventSequence</c> does across real's traces.
    /// </summary>
    public long EventSequence;

    /// <summary>Every trace, in id order.</summary>
    public UserTrace[] Traces()
    {
        lock (this.gate)
            return [.. this.traces.Values];
    }

    public UserTrace? Find(int id)
    {
        lock (this.gate)
            return this.traces.GetValueOrDefault(id);
    }

    /// <summary>
    /// Adds a trace under the lowest id above the default trace's that no
    /// trace holds, as real hands them out (probed 2026-10-16 against SQL
    /// Server 2025: a closed trace's id is taken again).
    /// </summary>
    public UserTrace Add(Func<int, UserTrace> create)
    {
        lock (this.gate)
        {
            var id = 2;
            while (this.traces.ContainsKey(id))
                id++;
            var trace = create(id);
            this.traces.Add(id, trace);
            return trace;
        }
    }

    /// <summary>Closes a stopped trace: it leaves the server, its files staying behind.</summary>
    public void Remove(UserTrace trace)
    {
        lock (this.gate)
            _ = this.traces.Remove(trace.Id);
    }

    /// <summary>
    /// Republishes <see cref="Running"/> and the per-event flags each firing
    /// site asks (<see cref="Simulation.TraceTaken"/>), null while no trace
    /// runs so that a site pays one null check.
    /// </summary>
    public void Publish(Simulation simulation)
    {
        lock (this.gate)
        {
            var running = this.traces.Values.Where(trace => trace.IsRunning).ToArray();
            bool[]? taken = null;
            foreach (var trace in running)
            {
                taken ??= new bool[XeEventKinds.Count];
                for (var i = 0; i < taken.Length; i++)
                    taken[i] |= trace.TakenKinds[i];
            }
            this.Running = running;
            simulation.TraceTaken = taken;
        }
    }
}

/// <summary>One filter of a trace, as <c>sp_trace_setfilter</c> set it.</summary>
internal sealed class TraceFilter(int columnId, int logicalOperator, int comparisonOperator, SqlValue value)
{
    public readonly int ColumnId = columnId;

    /// <summary>0 for <c>AND</c>, 1 for <c>OR</c>.</summary>
    public readonly int LogicalOperator = logicalOperator;

    /// <summary>0 <c>=</c>, 1 <c>&lt;&gt;</c>, 2 <c>&gt;</c>, 3 <c>&lt;</c>, 4 <c>&gt;=</c>, 5 <c>&lt;=</c>, 6 <c>LIKE</c>, 7 <c>NOT LIKE</c>.</summary>
    public readonly int ComparisonOperator = comparisonOperator;

    public readonly SqlValue Value = value;

    /// <summary>A <c>LIKE</c> filter's pattern, compiled once under the case-insensitive collation real compares by.</summary>
    public readonly Parser.LikeMatcher? Matcher = comparisonOperator is 6 or 7 && !value.IsNull
        ? Parser.LikeMatcher.Compile(value.AsString, null, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), forPatIndex: false)
        : null;
}

/// <summary>One row of a trace file: its event class and the columns it carries, in column order.</summary>
internal sealed class TraceRecord(int eventClass, (short ColumnId, SqlValue Value)[] columns)
{
    public readonly int EventClass = eventClass;
    public readonly (short ColumnId, SqlValue Value)[] Columns = columns;

    /// <summary>
    /// The bytes the row takes in its file, the simulator's estimate of real's
    /// encoding: a six-byte header, and per column a six-byte header and its
    /// data — two bytes a character, eight for a time, the rest at their width.
    /// </summary>
    public long Size
    {
        get
        {
            long size = 6;
            foreach (var (_, value) in this.Columns)
            {
                size += 6 + value.Type switch
                {
                    NTextSqlType or NVarcharSqlType => 2L * value.AsString.Length,
                    ImageSqlType or VarbinarySqlType => value.AsBytes.Length,
                    DateTimeSqlType => 16,
                    UniqueIdentifierSqlType => 16,
                    BigIntSqlType => 8,
                    _ => 4,
                };
            }
            return size;
        }
    }
}

/// <summary>
/// One <c>.trc</c> file a trace wrote: its rows, in the simulator's own form
/// — a file real's tools can't read — and its size by
/// <see cref="TraceRecord.Size"/>.
/// </summary>
internal sealed class TraceFile(string path, string next)
{
    public readonly string Path = path;

    /// <summary>The path of the rollover file after this one, which <c>fn_trace_gettable</c> reads on into.</summary>
    public readonly string Next = next;

    private readonly Lock gate = new();
    private readonly List<TraceRecord> records = [];

    private long size;

    public long Size
    {
        get
        {
            lock (this.gate)
                return this.size;
        }
    }

    public void Append(TraceRecord record)
    {
        lock (this.gate)
        {
            this.records.Add(record);
            this.size += record.Size;
        }
    }

    public TraceRecord[] Records()
    {
        lock (this.gate)
            return [.. this.records];
    }

    public bool IsEmpty
    {
        get
        {
            lock (this.gate)
                return this.records.Count == 0;
        }
    }
}
