using System.Globalization;
using System.IO.Compression;

namespace SqlServerSimulator.Trace;

/// <summary>
/// SQL Trace's catalog — its categories, columns, events, subclass values,
/// the columns each event binds, the Extended Events each event and column
/// maps onto, and the default trace's own event set — exactly as SQL Server
/// 2025's <c>sys.trace_categories</c>, <c>sys.trace_columns</c>,
/// <c>sys.trace_events</c>, <c>sys.trace_subclass_values</c>,
/// <c>sys.trace_event_bindings</c>, <c>sys.trace_xe_event_map</c>,
/// <c>sys.trace_xe_action_map</c> and <c>sys.fn_trace_geteventinfo(1)</c>
/// list them (dumped 2026-10-16 from 17.0.4065.4 on Linux). The rows live in
/// <c>Trace/Metadata/*.tsv</c> in each view's own order, one tab-separated row
/// per line, zipped into the assembly at build time and read once per
/// process on first use.
/// </summary>
internal sealed class TraceCatalog
{
    /// <summary>The highest trace column id, which sizes the per-event column sets.</summary>
    public const int MaxColumnId = 66;

    public readonly TraceCategory[] Categories;

    /// <summary>Every column, in <c>trace_column_id</c> order; <see cref="Column"/> finds one by id.</summary>
    public readonly TraceColumn[] Columns;

    public readonly TraceEventClass[] Events;

    /// <summary><c>sys.trace_subclass_values</c>' rows: event, column, name, value.</summary>
    public readonly (short EventId, short ColumnId, string Name, short Value)[] SubclassValues;

    /// <summary><c>sys.trace_event_bindings</c>' rows, in real's order.</summary>
    public readonly (short EventId, short ColumnId)[] Bindings;

    /// <summary><c>sys.trace_xe_event_map</c>'s rows.</summary>
    public readonly (short EventId, string Package, string Name)[] XeEventMap;

    /// <summary><c>sys.trace_xe_action_map</c>'s rows.</summary>
    public readonly (short ColumnId, string Package, string Name)[] XeActionMap;

    /// <summary>The default trace's events and columns, as <c>fn_trace_geteventinfo(1)</c> lists them.</summary>
    public readonly (int EventId, int ColumnId)[] DefaultTraceEvents;

    /// <summary>Per event id, the columns it binds; null for an id that names no event.</summary>
    private readonly bool[]?[] bound;

    private TraceCatalog()
    {
        using var stream = typeof(TraceCatalog).Assembly.GetManifestResourceStream("SqlServerSimulator.TraceMetadata.zip")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.TraceMetadata.zip is missing.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        this.Categories = [.. ReadRows(archive, "Categories.tsv").Select(fields => new TraceCategory(Short(fields[0]), fields[1], byte.Parse(fields[2], CultureInfo.InvariantCulture)))];
        this.Columns = [.. ReadRows(archive, "Columns.tsv").Select(fields => new TraceColumn(Short(fields[0]), fields[1], fields[2], int.Parse(fields[3], CultureInfo.InvariantCulture),
            fields[4] == "1", fields[5] == "1", fields[6] == "1"))];
        this.Events = [.. ReadRows(archive, "Events.tsv").Select(fields => new TraceEventClass(Short(fields[0]), Short(fields[1]), fields[2]))];
        this.SubclassValues = [.. ReadRows(archive, "SubclassValues.tsv").Select(fields => (Short(fields[0]), Short(fields[1]), fields[2], Short(fields[3])))];
        this.Bindings = [.. ReadRows(archive, "EventBindings.tsv").Select(fields => (Short(fields[0]), Short(fields[1])))];
        this.XeEventMap = [.. ReadRows(archive, "XeEventMap.tsv").Select(fields => (Short(fields[0]), fields[1], fields[2]))];
        this.XeActionMap = [.. ReadRows(archive, "XeActionMap.tsv").Select(fields => (Short(fields[0]), fields[1], fields[2]))];
        this.DefaultTraceEvents = [.. ReadRows(archive, "DefaultTraceEvents.tsv").Select(fields => ((int)Short(fields[0]), (int)Short(fields[1])))];

        var maxEvent = this.Events.Max(@event => @event.Id);
        this.bound = new bool[]?[maxEvent + 1];
        foreach (var @event in this.Events)
            this.bound[@event.Id] = new bool[MaxColumnId + 1];
        foreach (var (eventId, columnId) in this.Bindings)
            this.bound[eventId]![columnId] = true;

        static short Short(string field) => short.Parse(field, CultureInfo.InvariantCulture);
    }

    /// <summary>The process-wide catalog, read on first use.</summary>
    public static TraceCatalog Instance => Holder.Value;

    private static class Holder
    {
        public static readonly TraceCatalog Value = new();
    }

    /// <summary>Whether <paramref name="eventId"/> names an event.</summary>
    public bool IsEvent(int eventId) => eventId >= 0 && eventId < this.bound.Length && this.bound[eventId] is not null;

    /// <summary>Whether <paramref name="eventId"/> binds <paramref name="columnId"/> — whether its rows can carry the column.</summary>
    public bool Binds(int eventId, int columnId) =>
        columnId is >= 1 and <= MaxColumnId && eventId >= 0 && eventId < this.bound.Length && this.bound[eventId] is { } columns && columns[columnId];

    /// <summary>The column <paramref name="columnId"/>, or null for an id that names none.</summary>
    public TraceColumn? Column(int columnId) => columnId >= 1 && columnId <= this.Columns.Length ? this.Columns[columnId - 1] : null;

    /// <summary>Each line of one file, split at its tabs.</summary>
    private static IEnumerable<string[]> ReadRows(ZipArchive archive, string entry)
    {
        using var reader = new StreamReader((archive.GetEntry(entry) ?? throw new InvalidOperationException($"{entry} is missing from the trace metadata.")).Open());
        while (reader.ReadLine() is { } line)
        {
            if (line.Length != 0)
                yield return line.Split('\t');
        }
    }
}

/// <summary>One row of <c>sys.trace_categories</c>.</summary>
internal sealed class TraceCategory(short id, string name, byte type)
{
    public readonly short Id = id;
    public readonly string Name = name;
    public readonly byte Type = type;
}

/// <summary>One row of <c>sys.trace_columns</c>.</summary>
internal sealed class TraceColumn(short id, string name, string typeName, int maxSize, bool isFilterable, bool isRepeatable, bool isRepeatedBase)
{
    public readonly short Id = id;
    public readonly string Name = name;

    /// <summary><c>text</c>, <c>image</c>, <c>int</c>, <c>bigint</c>, <c>nvarchar</c>, <c>datetime</c> or <c>uniqueidentifier</c>.</summary>
    public readonly string TypeName = typeName;
    public readonly int MaxSize = maxSize;
    public readonly bool IsFilterable = isFilterable;
    public readonly bool IsRepeatable = isRepeatable;
    public readonly bool IsRepeatedBase = isRepeatedBase;
}

/// <summary>One row of <c>sys.trace_events</c>.</summary>
internal sealed class TraceEventClass(short id, short categoryId, string name)
{
    public readonly short Id = id;
    public readonly short CategoryId = categoryId;
    public readonly string Name = name;
}
