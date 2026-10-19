using System.Globalization;
using System.Text;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// What one target of a running session has collected, and the
/// <c>target_data</c> XML <c>sys.dm_xe_session_targets</c> shows for it, in
/// SQL Server 2025's shapes (probed 2026-10-16).
/// </summary>
internal abstract class XeTargetState(SessionTarget target)
{
    public readonly SessionTarget Target = target;

    /// <summary>How many buffers the target has been handed — one per event here, which real batches.</summary>
    public long ExecutionCount;

    public static XeTargetState For(SessionTarget target, RunningEventSession session, ExtendedEventsServer server) => target.Target.Name switch
    {
        "event_counter" => new EventCounterTarget(target),
        "event_file" => new EventFileTarget(target, session, server),
        "histogram" => new HistogramTarget(target),
        "pair_matching" => new PairMatchingTarget(target),
        "ring_buffer" => new RingBufferTarget(target),
        _ => new SilentTarget(target),
    };

    /// <summary>Takes one event the session's predicate passed, already rendered as XML.</summary>
    public abstract void Accept(XeFiring firing, SessionEvent sessionEvent, XeEventRecord record);

    /// <summary>The target's <c>target_data</c>, NULL for one that reports none.</summary>
    public abstract string? TargetData();

    public virtual long BytesWritten => 0;

    public virtual long UsedMemory => 0;

    public virtual long MaxMemory => 0;

    /// <summary>Called as the session stops: a file target closes its file, which outlives the session.</summary>
    public virtual void Close()
    {
    }

    /// <summary>The integer value of a customizable attribute, its default when unset.</summary>
    protected long Attribute(string name, long fallback) =>
        long.TryParse(this.Target.FieldText(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

/// <summary>A target the simulator keeps nothing for (<c>etw_classic_sync_target</c>).</summary>
internal sealed class SilentTarget(SessionTarget target) : XeTargetState(target)
{
    public override void Accept(XeFiring firing, SessionEvent sessionEvent, XeEventRecord record) => this.ExecutionCount++;

    public override string? TargetData() => null;
}

/// <summary>
/// <c>ring_buffer</c>: the newest events up to <c>max_events_limit</c> (1,000
/// by default), oldest dropped first.
/// </summary>
internal sealed class RingBufferTarget(SessionTarget target) : XeTargetState(target)
{
    private readonly Queue<string> events = new();
    private long processed;
    private long memoryUsed;

    public override void Accept(XeFiring firing, SessionEvent sessionEvent, XeEventRecord record)
    {
        this.ExecutionCount++;
        this.processed++;
        this.events.Enqueue(record.Xml);
        this.memoryUsed += record.Xml.Length;
        var limit = this.Attribute("max_events_limit", 1000);
        while (limit > 0 && this.events.Count > limit)
            this.memoryUsed -= this.events.Dequeue().Length;
    }

    public override string TargetData()
    {
        var builder = new StringBuilder(64 + (int)Math.Min(this.memoryUsed, int.MaxValue / 2));
        _ = builder.Append("<RingBufferTarget truncated=\"0\" processingTime=\"0\" totalEventsProcessed=\"").Append(this.processed)
            .Append("\" eventCount=\"").Append(this.events.Count).Append("\" droppedCount=\"0\" memoryUsed=\"").Append(this.memoryUsed).Append("\">");
        foreach (var xml in this.events)
            _ = builder.Append(xml);
        return builder.Append("</RingBufferTarget>").ToString();
    }

    /// <summary>The target's memory cap in bytes, or real's 4,294,967,295 for none (<c>max_memory = 0</c>).</summary>
    public override long MaxMemory => this.Attribute("max_memory", 0) is var kb and > 0 ? kb * 1024 : uint.MaxValue;

    public override long UsedMemory => this.MaxMemory;
}

/// <summary><c>event_counter</c>: how many times each of the session's events fired, under its package.</summary>
internal sealed class EventCounterTarget(SessionTarget target) : XeTargetState(target)
{
    private readonly Dictionary<SessionEvent, long> counts = [];

    public override void Accept(XeFiring firing, SessionEvent sessionEvent, XeEventRecord record)
    {
        this.ExecutionCount++;
        this.counts[sessionEvent] = this.counts.GetValueOrDefault(sessionEvent) + 1;
    }

    /// <summary>Every package in <c>sys.dm_xe_packages</c>' order, each listing the counted events it holds.</summary>
    public override string TargetData()
    {
        var builder = new StringBuilder("<CounterTarget truncated=\"0\"><Packages>");
        foreach (var package in XeMetadata.Instance.Packages)
        {
            _ = builder.Append("<Package name=\"").Append(package.Name).Append("\">");
            foreach (var (sessionEvent, count) in this.counts)
            {
                if (ReferenceEquals(sessionEvent.Event.Package, package))
                    _ = builder.Append("<Event name=\"").Append(sessionEvent.Event.Name).Append("\" count=\"").Append(count).Append("\"></Event>");
            }
            _ = builder.Append("</Package>");
        }
        return builder.Append("</Packages></CounterTarget>").ToString();
    }
}

/// <summary>
/// <c>histogram</c>: the events bucketed by one value — an action
/// (<c>source_type = 1</c>, the default) or a field of
/// <c>filtering_event_name</c> (<c>source_type = 0</c>).
/// </summary>
internal sealed class HistogramTarget(SessionTarget target) : XeTargetState(target)
{
    private readonly Dictionary<string, long> slots = new(StringComparer.Ordinal);

    public override void Accept(XeFiring firing, SessionEvent sessionEvent, XeEventRecord record)
    {
        this.ExecutionCount++;
        var source = this.Target.FieldText("source") ?? "";
        var dot = source.LastIndexOf('.');
        string? value = null;
        if (this.Attribute("source_type", 1) == 0)
        {
            var filter = this.Target.FieldText("filtering_event_name") ?? "";
            if (!filter.EndsWith("." + sessionEvent.Event.Name, StringComparison.OrdinalIgnoreCase) && !filter.Equals(sessionEvent.Event.Name, StringComparison.OrdinalIgnoreCase))
                return;
            foreach (var column in sessionEvent.Event.DataColumns)
            {
                if (column.Name.Equals(source[(dot + 1)..], StringComparison.OrdinalIgnoreCase))
                    value = firing.Field(column).XmlText;
            }
        }
        else if (dot > 0 && XeMetadata.Instance.Find(source[..dot], source[(dot + 1)..], "action") is { } action)
        {
            value = XeContextValues.Value(action.Package.Name, action.Name, firing)?.XmlText;
        }
        if (value is not null)
            this.slots[value] = this.slots.GetValueOrDefault(value) + 1;
    }

    public override string TargetData()
    {
        var builder = new StringBuilder("<HistogramTarget truncated=\"0\" buckets=\"").Append(this.Attribute("slots", 256)).Append("\">");
        foreach (var (value, count) in this.slots.OrderByDescending(slot => slot.Value))
            _ = builder.Append("<Slot count=\"").Append(count).Append("\"><value>").Append(value).Append("</value></Slot>");
        return builder.Append("</HistogramTarget>").ToString();
    }

    public override long UsedMemory => 10432;
}

/// <summary>
/// <c>pair_matching</c>: each <c>begin_event</c> kept as an orphan until an
/// <c>end_event</c> whose matching columns and actions read the values its
/// own did pairs with it, the oldest such begin first, while an end no begin
/// waits for stays as an orphan itself, in arrival order (probed 2026-10-19
/// against SQL Server 2025).
/// </summary>
internal sealed class PairMatchingTarget(SessionTarget target) : XeTargetState(target)
{
    private readonly List<(bool Begin, string Key, string Xml)> orphans = [];
    private long matched;

    public override void Accept(XeFiring firing, SessionEvent sessionEvent, XeEventRecord record)
    {
        this.ExecutionCount++;
        var name = sessionEvent.Event.Package.Name + "." + sessionEvent.Event.Name;
        if (name.Equals(this.Target.FieldText("begin_event"), StringComparison.OrdinalIgnoreCase))
        {
            this.orphans.Add((true, this.MatchingKey(firing, sessionEvent, "begin"), record.Xml));
            return;
        }
        if (!name.Equals(this.Target.FieldText("end_event"), StringComparison.OrdinalIgnoreCase))
            return;
        var key = this.MatchingKey(firing, sessionEvent, "end");
        for (var i = 0; i < this.orphans.Count; i++)
        {
            if (this.orphans[i].Begin && string.Equals(this.orphans[i].Key, key, StringComparison.Ordinal))
            {
                this.orphans.RemoveAt(i);
                this.matched++;
                return;
            }
        }
        this.orphans.Add((false, key, record.Xml));
    }

    /// <summary>The values an event's <c>&lt;side&gt;_matching_columns</c> and <c>_actions</c> name, in order.</summary>
    private string MatchingKey(XeFiring firing, SessionEvent sessionEvent, string side)
    {
        var key = new StringBuilder();
        foreach (var name in Names(this.Target.FieldText(side + "_matching_columns")))
        {
            foreach (var column in sessionEvent.Event.DataColumns)
            {
                if (column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    _ = key.Append(firing.Field(column).XmlText);
            }
            _ = key.Append('\u001F');
        }
        foreach (var name in Names(this.Target.FieldText(side + "_matching_actions")))
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0 && XeMetadata.Instance.Find(name[..dot], name[(dot + 1)..], "action") is { } action)
                _ = key.Append(XeContextValues.Value(action.Package.Name, action.Name, firing)?.XmlText);
            _ = key.Append('\u001F');
        }
        return key.ToString();

        static string[] Names(string? list) => list is null ? [] : list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public override string TargetData()
    {
        var builder = new StringBuilder("<PairingTarget truncated=\"0\" orphanCount=\"").Append(this.orphans.Count)
            .Append("\" matchedCount=\"").Append(this.matched).Append("\" droppedCount=\"0\">");
        foreach (var (_, _, xml) in this.orphans)
            _ = builder.Append(xml);
        return builder.Append("</PairingTarget>").ToString();
    }
}

/// <summary>
/// <c>event_file</c>: each run writes a file of its own under real's Linux
/// log directory, <c>name_0_ticks.xel</c>, which <c>sys.fn_xe_file_target_read_file</c>
/// reads back and which outlives the session.
/// </summary>
internal sealed class EventFileTarget : XeTargetState
{
    public readonly XeEventFile File;

    public EventFileTarget(SessionTarget target, RunningEventSession session, ExtendedEventsServer server) : base(target)
    {
        var name = target.FieldText("filename") ?? "";
        var slash = name.LastIndexOf('/');
        var directory = slash >= 0 ? name[..(slash + 1)] : LogDirectory;
        var leaf = slash >= 0 ? name[(slash + 1)..] : name;
        var stem = leaf.EndsWith(".xel", StringComparison.OrdinalIgnoreCase) ? leaf[..^4] : leaf;
        this.File = new XeEventFile(directory + stem + "_0_" + session.CreateTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) + ".xel");
        server.Files[this.File.Path] = this.File;
    }

    /// <summary>Where a relative file name lands: real's Linux server's error-log directory.</summary>
    public const string LogDirectory = "/var/opt/mssql/log/";

    public override void Accept(XeFiring firing, SessionEvent sessionEvent, XeEventRecord record)
    {
        this.ExecutionCount++;
        this.File.Append(sessionEvent.Event, firing.Timestamp, record.FileXml);
    }

    public override string TargetData() =>
        "<EventFileTarget truncated=\"0\"><Buffers logged=\"" + this.ExecutionCount.ToString(CultureInfo.InvariantCulture)
        + "\" dropped=\"0\"/><File name=\"" + this.File.Path + "\"/></EventFileTarget>";

    public override long BytesWritten => this.File.Length;
}

/// <summary>
/// One <c>.xel</c> file an <c>event_file</c> target wrote: the events in the
/// order written, each with the offset <c>sys.fn_xe_file_target_read_file</c>
/// reports — the start of the buffer it went out in, which for real is the
/// 16 KB header's end for every event of a default session's first buffer
/// (probed 2026-10-16 against SQL Server 2025). The simulator keeps its own
/// form of the file, in memory, for the life of the simulation.
/// </summary>
internal sealed class XeEventFile(string path)
{
    private const long HeaderBytes = 16384;

    /// <summary>A default session's buffer, rounded to its 64 KB pages, which the offsets step by.</summary>
    private const long BufferBytes = 1_441_792;

    public readonly string Path = path;

    /// <summary>Guards the events, which a firing appends and a read copies.</summary>
    public readonly Lock Gate = new();

    public readonly List<(XeObject Event, DateTime Timestamp, string Xml, long Offset)> Events = [];

    /// <summary>The file's size: real's 16 KB header, then each event's bytes.</summary>
    public long Length = HeaderBytes;

    public void Append(XeObject @event, DateTime timestamp, string xml)
    {
        lock (this.Gate)
        {
            this.Events.Add((@event, timestamp, xml, HeaderBytes + ((this.Length - HeaderBytes) / BufferBytes * BufferBytes)));
            this.Length += Encoding.Unicode.GetByteCount(xml);
        }
    }
}
