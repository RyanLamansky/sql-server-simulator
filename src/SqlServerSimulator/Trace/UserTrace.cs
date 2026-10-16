using System.Globalization;
using System.Text;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Trace;

/// <summary>
/// One trace <c>sp_trace_create</c> made: its options and file, the events
/// and columns <c>sp_trace_setevent</c> chose, the filters
/// <c>sp_trace_setfilter</c> set, and while it runs what it has written.
/// </summary>
internal sealed class UserTrace(int id, int options, string stem, long maxSizeMegabytes, DateTime? stopTime, int maxFiles, string creator)
{
    /// <summary>The meta rows real's file carries around the events: its header, a start, a stop, and a rollover file's opening (probed 2026-10-16 against SQL Server 2025).</summary>
    public const int HeaderClass = 65528, StartClass = 65534, StopClass = 65533, RolloverClass = 65527;

    public readonly int Id = id;

    /// <summary><c>TRACE_FILE_ROLLOVER</c> (2) and <c>SHUTDOWN_ON_ERROR</c> (4), as created.</summary>
    public readonly int Options = options;

    /// <summary>The file name as given, which <c>.trc</c> — and for each file after the first <c>_n</c> — follow.</summary>
    public readonly string Stem = stem;

    public readonly long MaxSizeMegabytes = maxSizeMegabytes;
    public readonly DateTime? StopTime = stopTime;

    /// <summary><c>sys.traces.max_files</c>: the rollover file count, 0 for no limit, 1 without rollover.</summary>
    public readonly int MaxFiles = maxFiles;

    /// <summary>The login that created the trace, which the error log names when the trace stops by itself.</summary>
    public readonly string Creator = creator;

    private readonly Lock gate = new();

    /// <summary>Each event's columns as <c>sp_trace_setevent</c> set them, <c>SPID</c> and <c>ServerName</c> aside.</summary>
    private readonly SortedDictionary<int, SortedSet<int>> events = [];

    /// <summary>
    /// Whether the trace collects <c>ServerName</c>, which real keeps for the
    /// whole trace: set on one event, every event lists it, and cleared on
    /// one, none does (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private bool serverName;

    private readonly List<TraceFilter> filters = [];

    /// <summary>Whether the trace runs, read by the firing sites without the gate.</summary>
    public volatile bool IsRunning;

    /// <summary>Per <see cref="XeEventKind"/>, whether the trace takes it, settled as it starts.</summary>
    public bool[] TakenKinds = new bool[XeEventKinds.Count];

    /// <summary>The events the trace takes, settled as it starts, so a firing site asks without the gate.</summary>
    private bool[] takenEvents = [];

    private readonly List<TraceFile> files = [];
    private int fileNumber;

    public DateTime? StartTime;
    public DateTime? LastEventTime;
    public long EventCount;

    public bool IsRollover => (this.Options & 2) != 0;

    public bool IsShutdown => (this.Options & 4) != 0;

    /// <summary>The file the trace writes, which <c>sys.traces.path</c> names.</summary>
    public string CurrentPath => FilePath(this.Stem, this.fileNumber);

    /// <summary>The <paramref name="number"/>th file of a trace written to <paramref name="stem"/>: <c>stem.trc</c>, then <c>stem_1.trc</c> on.</summary>
    public static string FilePath(string stem, int number) =>
        number == 0 ? stem + ".trc" : string.Create(CultureInfo.InvariantCulture, $"{stem}_{number}.trc");

    /// <summary><c>sys.traces.file_position</c>: the bytes written, in the whole megabytes real reports it by.</summary>
    public long FilePosition
    {
        get
        {
            lock (this.gate)
            {
                var size = this.files.Count == 0 ? 0 : this.files[^1].Size;
                return Math.Max(1, (size + (1 << 20) - 1) >> 20) << 20;
            }
        }
    }

    /// <summary>Opens the trace's first file, which <c>sp_trace_create</c> creates empty.</summary>
    public void CreateFile(SqlTraceServer server)
    {
        var file = new TraceFile(this.CurrentPath, FilePath(this.Stem, this.fileNumber + 1));
        this.files.Add(file);
        server.Files[file.Path] = file;
    }

    public bool TakesEvent(int eventClass) => eventClass < this.takenEvents.Length && this.takenEvents[eventClass];

    /// <summary>The trace's events and columns as <c>fn_trace_geteventinfo</c> lists them: by event, then column, <c>SPID</c> on every event.</summary>
    public List<(int EventId, int ColumnId)> EventColumns()
    {
        var list = new List<(int, int)>();
        lock (this.gate)
        {
            foreach (var (eventId, columns) in this.events)
            {
                foreach (var columnId in Columns(columns))
                    list.Add((eventId, columnId));
            }
        }
        return list;
    }

    /// <summary>An event's columns with the trace-wide ones, in id order.</summary>
    private SortedSet<int> Columns(SortedSet<int> columns)
    {
        var all = new SortedSet<int>(columns) { 12 };
        if (this.serverName)
            _ = all.Add(26);
        return all;
    }

    public TraceFilter[] Filters()
    {
        lock (this.gate)
            return [.. this.filters];
    }

    /// <summary>
    /// <c>sp_trace_setevent</c>'s change, judged already: a null column turns
    /// the event on with no columns or off with all of them; turning a
    /// column on adds the event, turning its last one off drops it.
    /// </summary>
    public void SetEvent(int eventId, int? columnId, bool on)
    {
        lock (this.gate)
        {
            if (columnId is not { } column)
            {
                if (on)
                    this.events[eventId] = [];
                else
                    _ = this.events.Remove(eventId);
                return;
            }
            // EventClass is every row's; real takes it on and lists it nowhere.
            if (column == 27)
                return;
            if (column == 26)
            {
                this.serverName = on;
                if (on && !this.events.ContainsKey(eventId))
                    this.events[eventId] = [];
                return;
            }
            if (on)
            {
                if (!this.events.TryGetValue(eventId, out var columns))
                    this.events[eventId] = columns = [];
                if (column != 12)
                    _ = columns.Add(column);
            }
            else if (this.events.TryGetValue(eventId, out var columns) && columns.Remove(column) && columns.Count == 0)
            {
                _ = this.events.Remove(eventId);
            }
        }
    }

    /// <summary>Whether the trace's filters on <paramref name="columnId"/> run together, or it has none.</summary>
    public bool FiltersGrouped(int columnId)
    {
        lock (this.gate)
            return this.filters.Count == 0 || this.filters[^1].ColumnId == columnId || this.filters.TrueForAll(filter => filter.ColumnId != columnId);
    }

    public void AddFilter(TraceFilter filter)
    {
        lock (this.gate)
            this.filters.Add(filter);
    }

    public void ClearFilters()
    {
        lock (this.gate)
            this.filters.Clear();
    }

    /// <summary>
    /// Starts the trace: its events are settled for the firing sites, the
    /// file's header and start rows written, and each open session reported
    /// as an <c>ExistingConnection</c> when the trace takes it.
    /// </summary>
    public void Start(Simulation simulation, SqlTraceServer server, DateTime now)
    {
        lock (this.gate)
        {
            var taken = new bool[TraceCatalog.Instance.Events.Max(@event => @event.Id) + 1];
            foreach (var (eventId, _) in this.events)
                taken[eventId] = true;
            this.takenEvents = taken;
            this.TakenKinds = TraceEvents.KindsTaken(taken);
            var file = this.files[^1];
            if (file.IsEmpty)
                file.Append(this.Header(HeaderClass, now));
            file.Append(MetaRow(StartClass, now));
            this.StartTime = now;
            this.IsRunning = true;
        }
        server.Publish(simulation);
        if (this.TakesEvent(17))
        {
            foreach (var connection in simulation.SnapshotConnections())
            {
                if (connection.State != System.Data.ConnectionState.Open)
                    continue;
                var values = new SqlValue?[TraceCatalog.MaxColumnId + 1];
                TraceEvents.SessionColumns(values, connection);
                values[14] = SqlValue.FromDateTime(now);
                values[26] = SqlValue.FromNVarchar(Simulation.ServerNameValue);
                TraceEvents.LoginColumns(values, 17, connection, now, pooled: false, ExistingPacketSize);
                values[51] = SqlValue.FromInt64(Interlocked.Increment(ref server.EventSequence));
                this.Write(simulation, server, connection, 17, values, now);
            }
        }
    }

    /// <summary>The packet size an <c>ExistingConnection</c> row reports, SqlClient's default as real's probed sessions reported it.</summary>
    private const int ExistingPacketSize = 8000;

    /// <summary>Stops the trace, writing its stop row; false when it wasn't running.</summary>
    public bool Stop(Simulation simulation, SqlTraceServer server, DateTime now, bool writeStopRow = true)
    {
        lock (this.gate)
        {
            if (!this.IsRunning)
                return false;
            this.IsRunning = false;
            this.takenEvents = [];
            this.TakenKinds = new bool[XeEventKinds.Count];
            if (writeStopRow)
                this.files[^1].Append(MetaRow(StopClass, now));
        }
        server.Publish(simulation);
        return true;
    }

    /// <summary>
    /// Writes one event's row: its filters asked, its columns the ones the
    /// trace chose for the class and the class binds, then the file's limits
    /// — a full file rolls over or, without rollover, stops and closes the
    /// trace, and a stop time passed stops and closes it — as real's do
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public void Write(Simulation simulation, SqlTraceServer server, SimulatedDbConnection connection, int eventClass, SqlValue?[] values, DateTime now)
    {
        string? stoppedBecause = null;
        lock (this.gate)
        {
            if (!this.IsRunning || !this.events.TryGetValue(eventClass, out var chosen))
                return;
            if (this.StopTime is { } stop && now >= stop)
            {
                stoppedBecause = "";
            }
            else
            {
                if (!this.Passes(values))
                    return;
                var catalog = TraceCatalog.Instance;
                var columns = new List<(short, SqlValue)>();
                foreach (var columnId in this.Columns(chosen))
                {
                    if (catalog.Binds(eventClass, columnId) && values[columnId] is { } value)
                        columns.Add(((short)columnId, value));
                }
                var record = new TraceRecord(eventClass, [.. columns]);
                var file = this.files[^1];
                if (file.Size + record.Size > this.MaxSizeMegabytes << 20)
                {
                    if (!this.IsRollover)
                    {
                        stoppedBecause = "Stopping the trace because the current trace file is full and the rollover option is not specified.";
                    }
                    else
                    {
                        file = this.Roll(server, now);
                    }
                }
                if (stoppedBecause is null)
                {
                    file.Append(record);
                    this.EventCount++;
                    this.LastEventTime = now;
                }
            }
        }
        if (stoppedBecause is null)
            return;
        // A trace stopped by its stop time writes its stop row as a stop
        // does; one stopped by an error writes none.
        _ = this.Stop(simulation, server, now, writeStopRow: stoppedBecause.Length == 0);
        server.Remove(this);
        simulation.ErrorLog.Write(now, ErrorLog.SessionProcess(connection.Spid), stoppedBecause.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"SQL Trace stopped. Trace ID = '{this.Id}'. Login Name = '{this.Creator}'.")
            : string.Create(CultureInfo.InvariantCulture, $"Trace ID '{this.Id}' was stopped because of an error. Cause: {stoppedBecause}. Restart the trace after correcting the problem."));
    }

    /// <summary>Opens the next rollover file, dropping the oldest past the trace's file count.</summary>
    private TraceFile Roll(SqlTraceServer server, DateTime now)
    {
        this.fileNumber++;
        var file = new TraceFile(this.CurrentPath, FilePath(this.Stem, this.fileNumber + 1));
        file.Append(this.Header(HeaderClass, now));
        file.Append(MetaRow(RolloverClass, now));
        this.files.Add(file);
        server.Files[file.Path] = file;
        if (this.MaxFiles > 0 && this.files.Count > this.MaxFiles)
        {
            _ = server.Files.TryRemove(this.files[0].Path, out _);
            this.files.RemoveAt(0);
        }
        return file;
    }

    /// <summary>
    /// Whether a row passes the trace's filters, read left to right, each
    /// joining what came before by its own logical operator — the first's
    /// ignored — so <c>a LIKE 'x%'</c> then <c>OR b = 1</c> takes either. A
    /// column the row lacks passes every filter but <c>&lt;&gt; NULL</c>;
    /// comparisons are case-insensitive, <c>=</c> ignores trailing spaces, and
    /// <c>LIKE</c> takes real's wildcards (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    private bool Passes(SqlValue?[] values)
    {
        var result = true;
        for (var i = 0; i < this.filters.Count; i++)
        {
            var filter = this.filters[i];
            var passes = Evaluate(filter, values[filter.ColumnId]);
            result = i == 0 ? passes : filter.LogicalOperator == 1 ? result || passes : result && passes;
        }
        return result;
    }

    private static bool Evaluate(TraceFilter filter, SqlValue? value)
    {
        var expected = filter.Value;
        if (value is not { IsNull: false } actual)
            return !(filter.ComparisonOperator == 1 && expected.IsNull);
        if (expected.IsNull)
            return filter.ComparisonOperator == 1;
        if (filter.Matcher is { } matcher)
            return matcher.IsMatch(actual.AsString, trailingSpaceSlack: false) == (filter.ComparisonOperator == 6);
        int comparison;
        if (actual.Type is NTextSqlType or NVarcharSqlType)
            comparison = Collation.Get("SQL_Latin1_General_CP1_CI_AS").Compare(actual.AsString.TrimEnd(' '), expected.AsString.TrimEnd(' '));
        else
            comparison = actual.CoerceTo(expected.Type).CompareTo(expected);
        return filter.ComparisonOperator switch
        {
            0 => comparison == 0,
            1 => comparison != 0,
            2 => comparison > 0,
            3 => comparison < 0,
            4 => comparison >= 0,
            _ => comparison <= 0,
        };
    }

    /// <summary>A meta row: the moment and the server, as real's start, stop and rollover rows carry.</summary>
    private static TraceRecord MetaRow(int eventClass, DateTime now) =>
        new(eventClass, [(14, SqlValue.FromDateTime(now)), (26, SqlValue.FromNVarchar(Simulation.ServerNameValue))]);

    /// <summary>
    /// The file's header row, whose <c>BinaryData</c> is real's own header
    /// block: its product name and version, the trace's file options, the
    /// server's name, then each event with its columns and the union of
    /// every event's columns — byte for byte what real's
    /// <c>fn_trace_gettable</c> returns (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private TraceRecord Header(int eventClass, DateTime now)
    {
        using var stream = new MemoryStream(1024);
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        writer.Write((ushort)0xFEFF);
        writer.Write((ushort)656);
        writer.Write((ushort)1);
        WritePadded(writer, "Microsoft SQL Server", 384);
        writer.Write((ushort)17);
        writer.Write((ushort)4065);
        writer.Write(this.IsRollover ? 3 : 2);
        WritePadded(writer, Simulation.ServerNameValue, 256);
        writer.Write([0x0C, 0x00, 0xED, 0xFB, 0x00]);
        var union = new SortedSet<int> { 27 };
        foreach (var (eventId, chosen) in this.events)
        {
            var columns = this.Columns(chosen);
            union.UnionWith(columns);
            writer.Write((ushort)0xFFFC);
            writer.Write((byte)(2 * (columns.Count + 1)));
            writer.Write((ushort)eventId);
            foreach (var columnId in columns)
                writer.Write((ushort)columnId);
        }
        writer.Write((ushort)0xFBFC);
        writer.Write((byte)(2 * union.Count));
        foreach (var columnId in union)
            writer.Write((ushort)columnId);
        writer.Flush();
        return new TraceRecord(eventClass,
        [
            (2, SqlValue.FromImage(stream.ToArray())),
            (14, SqlValue.FromDateTime(now)),
            (26, SqlValue.FromNVarchar(Simulation.ServerNameValue)),
        ]);

        static void WritePadded(BinaryWriter writer, string text, int bytes)
        {
            var encoded = Encoding.Unicode.GetBytes(text);
            writer.Write(encoded);
            writer.Write(new byte[bytes - encoded.Length]);
        }
    }
}
