using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// One event session's definition — its <c>sys.server_event_sessions</c>
/// row and the events, actions, customizable attributes and targets under
/// it. Immutable: <c>ALTER EVENT SESSION</c> builds a replacement, so a
/// statement that fails partway leaves the session as it was, as real's
/// does (probed 2026-10-16 against SQL Server 2025).
/// </summary>
internal sealed class EventSessionDefinition(int id, string name, EventSessionOptions options, SessionEvent[] events, SessionTarget[] targets, int nextObjectId)
{
    public readonly int Id = id;

    /// <summary>The name as the creating statement spelled it.</summary>
    public readonly string Name = name;

    public readonly EventSessionOptions Options = options;

    /// <summary>The events in <c>event_id</c> order.</summary>
    public readonly SessionEvent[] Events = events;

    /// <summary>The targets in <c>target_id</c> order.</summary>
    public readonly SessionTarget[] Targets = targets;

    /// <summary>
    /// The id the next event or target takes. Real numbers every object of a
    /// session from one counter that never goes back — each event, then
    /// each of its actions and customizable attributes, each target, then
    /// each of its attributes — so an event added after a drop takes a fresh
    /// id (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public readonly int NextObjectId = nextObjectId;

    /// <summary>
    /// The targets in the order the running-session views list them: the
    /// asynchronous ones other than <c>event_file</c> newest first, then the
    /// file targets, then the synchronous ones (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    public IEnumerable<SessionTarget> TargetsInListOrder()
    {
        static bool Synchronous(SessionTarget target) => target.Target.Capabilities is { } bits && (bits & 1024) != 0;
        var newestFirst = this.Targets.Reverse().ToArray();
        return newestFirst.Where(target => !Synchronous(target) && target.Target.Name != "event_file")
            .Concat(newestFirst.Where(target => !Synchronous(target) && target.Target.Name == "event_file"))
            .Concat(newestFirst.Where(Synchronous));
    }

    public EventSessionDefinition With(EventSessionOptions? options = null, SessionEvent[]? events = null, SessionTarget[]? targets = null, int? nextObjectId = null) =>
        new(this.Id, this.Name, options ?? this.Options, events ?? this.Events, targets ?? this.Targets, nextObjectId ?? this.NextObjectId);
}

/// <summary>The <c>WITH</c> options of a session, as <c>sys.server_event_sessions</c> reports them.</summary>
internal sealed class EventSessionOptions
{
    /// <summary><c>S</c>, <c>M</c> or <c>N</c>: single event loss, multiple event loss, no event loss.</summary>
    public char RetentionMode = 'S';

    /// <summary>Milliseconds; 0 for <c>INFINITE</c>.</summary>
    public int MaxDispatchLatency = 30_000;

    /// <summary>Kilobytes.</summary>
    public int MaxMemory = 4096;

    /// <summary>Kilobytes; 0 for none.</summary>
    public int MaxEventSize;

    /// <summary><c>G</c>, <c>N</c> or <c>C</c>: none, per node, per CPU.</summary>
    public char PartitionMode = 'G';

    public bool TrackCausality;

    public bool StartupState;

    /// <summary>Seconds; 0 for <c>UNLIMITED</c>.</summary>
    public long MaxDuration;

    public EventSessionOptions Clone() => (EventSessionOptions)this.MemberwiseClone();

    /// <summary>
    /// The session's buffers: three regular ones — or two and a half per CPU
    /// under <c>PER_CPU</c> — splitting <c>MAX_MEMORY</c>, each rounded up to
    /// whole 64 KB pages less a 205-byte header, and two large ones holding
    /// <c>MAX_EVENT_SIZE</c> when it's set, as real sizes them (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    public (int Regular, long RegularSize, int Large, long LargeSize) Buffers()
    {
        if (this.MaxMemory == 0)
            return (0, 0, 0, 0);
        var regular = this.PartitionMode == 'C' ? Environment.ProcessorCount * 5 / 2 : 3;
        static long Size(long bytes, int count) => (((bytes / count / 65536) + 1) * 65536) - 205;
        var regularSize = Size(this.MaxMemory * 1024L, regular);
        return this.MaxEventSize == 0
            ? (regular, regularSize, 0, 0)
            : (regular, regularSize, 2, Size(this.MaxEventSize * 1024L, 2));
    }

    public string RetentionModeDesc => this.RetentionMode switch
    {
        'M' => "ALLOW_MULTIPLE_EVENT_LOSS",
        'N' => "NO_EVENT_LOSS",
        _ => "ALLOW_SINGLE_EVENT_LOSS",
    };

    public string PartitionModeDesc => this.PartitionMode switch
    {
        'C' => "PER_CPU",
        'N' => "PER_NODE",
        _ => "NONE",
    };
}

/// <summary>An event added to a session, with its actions, its customizable attributes set and its predicate.</summary>
internal sealed class SessionEvent(int id, string name, XeObject @event, XeObject[] actions, SessionField[] fields, string? predicateText, XePredicate? predicate)
{
    public readonly int Id = id;

    /// <summary>The event's name as the statement spelled it, which <c>sys.server_event_session_events.name</c> keeps.</summary>
    public readonly string Name = name;

    public readonly XeObject Event = @event;

    /// <summary>The actions in the order written, a repeat kept as real keeps it.</summary>
    public readonly XeObject[] Actions = actions;

    public readonly SessionField[] Fields = fields;

    public readonly string? PredicateText = predicateText;

    public readonly XePredicate? Predicate = predicate;

    /// <summary>The value of the customizable column <paramref name="column"/>: the one set, else its default.</summary>
    public string? FieldText(XeObjectColumn column) => SessionField.TextFor(this.Fields, column);
}

/// <summary>A target added to a session, with its customizable attributes set.</summary>
internal sealed class SessionTarget(int id, string name, XeObject target, SessionField[] fields)
{
    public readonly int Id = id;

    /// <summary>The target's name as the statement spelled it.</summary>
    public readonly string Name = name;

    public readonly XeObject Target = target;

    public readonly SessionField[] Fields = fields;

    /// <summary>The value of the customizable column named <paramref name="columnName"/>: the one set, else its default.</summary>
    public string? FieldText(string columnName)
    {
        foreach (var column in this.Target.Columns)
        {
            if (column.ColumnType == "customizable" && column.Name == columnName)
                return SessionField.TextFor(this.Fields, column);
        }
        return null;
    }
}

/// <summary>
/// One customizable attribute a <c>SET</c> clause set: its name as written,
/// the column it names, the constant as written — which
/// <c>sys.server_event_session_fields.value</c> keeps with its literal's type,
/// so <c>0x10</c> stays <c>varbinary</c> and <c>'a.xel'</c> <c>varchar</c>
/// (probed 2026-10-16 against SQL Server 2025) — and the value it converts to.
/// </summary>
internal sealed class SessionField(string name, XeObjectColumn column, SqlValue literal, XeValue value)
{
    public readonly string Name = name;
    public readonly XeObjectColumn Column = column;
    public readonly SqlValue Literal = literal;
    public readonly XeValue Value = value;

    /// <summary>The text <c>sys.dm_xe_session_object_columns.column_value</c> shows for <paramref name="column"/>.</summary>
    public static string? TextFor(SessionField[] fields, XeObjectColumn column)
    {
        foreach (var field in fields)
        {
            if (ReferenceEquals(field.Column, column))
                return field.Value.XmlText;
        }
        return column.ColumnValue;
    }
}
