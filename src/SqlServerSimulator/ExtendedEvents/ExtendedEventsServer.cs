using System.Globalization;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// One server's Extended Events state: the session definitions
/// <c>sys.server_event_sessions</c> lists, the sessions running, and the
/// engine's own internal sessions <c>sys.dm_xe_sessions</c> lists beside
/// them. Created on first use by <see cref="Simulation.ExtendedEventsFor"/>,
/// which seeds real's default sessions — <c>system_health</c> running,
/// <c>AlwaysOn_health</c> stopped.
/// </summary>
internal sealed class ExtendedEventsServer
{
    private readonly Lock gate = new();

    private readonly Dictionary<string, EventSessionDefinition> sessions = new(BuiltInToken.Comparer);

    private readonly Dictionary<int, RunningEventSession> running = [];

    /// <summary>
    /// The last <c>event_session_id</c> handed out. Real numbers sessions from
    /// 65536 server-wide, never reuses one, and spends one on every
    /// <c>CREATE EVENT SESSION</c> that gets past its permission check, a
    /// duplicate name and a refused definition included (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    private int lastSessionId;

    /// <summary>How many sessions have started, which numbers each run's stand-in address.</summary>
    private int starts;

    /// <summary>
    /// The running sessions' events, by the event they fire on, published
    /// whole on every change so a firing site reads them without the gate.
    /// </summary>
    private volatile Dictionary<XeObject, (RunningEventSession Session, SessionEvent Event)[]> subscriptions = [];

    /// <summary>
    /// Whether a running session takes each event the simulator fires, by
    /// <see cref="XeEventKind"/>, published with <see cref="subscriptions"/>
    /// so a firing site asks it with one array read.
    /// </summary>
    public volatile bool[] Taken = new bool[XeEventKinds.Count];

    /// <summary>
    /// The <c>.xel</c> files the event-file targets have written, by path. A
    /// file outlives its session, as one on disk does, so a dropped session's
    /// events still read back.
    /// </summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, XeEventFile> Files = new(StringComparer.Ordinal);

    /// <summary>The internal sessions, which run from the server's start and no statement reaches.</summary>
    public readonly InternalEventSession[] InternalSessions;

    public readonly DateTime StartTime;

    /// <summary>The <c>.xel</c> file the internal sessions' event-file target has written since the server started.</summary>
    public readonly string InternalEventFilePath;

    public ExtendedEventsServer(EventSessionDefinition[] defaults, DateTime startTime)
    {
        this.StartTime = startTime;
        this.InternalEventFilePath = EventFileTarget.LogDirectory + "HkEngineEventFile_0_" + startTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) + ".xel";
        this.InternalSessions = InternalEventSession.All;
        foreach (var definition in defaults)
        {
            this.sessions.Add(definition.Name, definition);
            this.lastSessionId = Math.Max(this.lastSessionId, definition.Id);
            if (definition.Options.StartupState)
                _ = this.StartLocked(definition, startTime);
        }
        this.startupStarts = this.starts;
        this.Publish();
    }

    /// <summary>Every definition, in <c>event_session_id</c> order.</summary>
    public EventSessionDefinition[] Definitions()
    {
        lock (this.gate)
            return [.. this.sessions.Values.OrderBy(definition => definition.Id)];
    }

    /// <summary>
    /// The sessions <c>sys.dm_xe_sessions</c> lists, in the order real's
    /// engine made them: <c>hkenginexesession</c>, the sessions that start
    /// with the server, the <c>sp_server_diagnostics session</c>, then the
    /// rest as they started (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public (InternalEventSession? Internal, RunningEventSession? Running)[] ListOrder()
    {
        var running = this.RunningSessions();
        var order = new List<(InternalEventSession?, RunningEventSession?)> { (this.InternalSessions[0], null) };
        order.AddRange(running.Where(session => session.StartOrder <= this.startupStarts).Select(session => ((InternalEventSession?)null, (RunningEventSession?)session)));
        order.AddRange(this.InternalSessions.Skip(1).Select(session => ((InternalEventSession?)session, (RunningEventSession?)null)));
        order.AddRange(running.Where(session => session.StartOrder > this.startupStarts).Select(session => ((InternalEventSession?)null, (RunningEventSession?)session)));
        return [.. order];
    }

    /// <summary>How many sessions started with the server.</summary>
    private readonly int startupStarts;

    /// <summary>Every running session, in the order they started.</summary>
    public RunningEventSession[] RunningSessions()
    {
        lock (this.gate)
            return [.. this.running.Values.OrderBy(session => session.StartOrder)];
    }

    public EventSessionDefinition? Find(string name)
    {
        lock (this.gate)
            return this.sessions.GetValueOrDefault(name);
    }

    public bool IsRunning(int id)
    {
        lock (this.gate)
            return this.running.ContainsKey(id);
    }

    public int AllocateSessionId()
    {
        lock (this.gate)
            return ++this.lastSessionId;
    }

    /// <summary>Adds a new definition; false when its name is taken.</summary>
    public bool TryAdd(EventSessionDefinition definition)
    {
        lock (this.gate)
            return this.sessions.TryAdd(definition.Name, definition);
    }

    /// <summary>
    /// Replaces a definition, carrying a running session's events and targets
    /// over: an event or target the replacement adds starts collecting at
    /// once, one it drops stops, and a target kept keeps what it holds.
    /// </summary>
    public void Replace(EventSessionDefinition definition)
    {
        lock (this.gate)
        {
            this.sessions[definition.Name] = definition;
            if (this.running.TryGetValue(definition.Id, out var session))
                session.Redefine(definition);
            this.Publish();
        }
    }

    /// <summary>Drops a definition, stopping it first when it runs.</summary>
    public void Remove(EventSessionDefinition definition)
    {
        lock (this.gate)
        {
            _ = this.sessions.Remove(definition.Name);
            if (this.running.Remove(definition.Id, out var session))
                session.Stop();
            this.Publish();
        }
    }

    public RunningEventSession Start(EventSessionDefinition definition, DateTime utcNow)
    {
        lock (this.gate)
        {
            var started = this.StartLocked(definition, utcNow);
            this.Publish();
            return started;
        }
    }

    public void Stop(EventSessionDefinition definition)
    {
        lock (this.gate)
        {
            if (this.running.Remove(definition.Id, out var session))
                session.Stop();
            this.Publish();
        }
    }

    private RunningEventSession StartLocked(EventSessionDefinition definition, DateTime utcNow)
    {
        var order = ++this.starts;
        return this.running[definition.Id] = new RunningEventSession(definition, utcNow, order, this);
    }

    /// <summary>The running sessions' events that fire on <paramref name="event"/>, empty for none.</summary>
    public (RunningEventSession Session, SessionEvent Event)[] Subscribers(XeObject @event) =>
        this.subscriptions.GetValueOrDefault(@event) ?? [];

    private void Publish()
    {
        var byEvent = new Dictionary<XeObject, List<(RunningEventSession, SessionEvent)>>();
        foreach (var session in this.running.Values.OrderBy(session => session.StartOrder))
        {
            foreach (var sessionEvent in session.Definition.Events)
            {
                if (!byEvent.TryGetValue(sessionEvent.Event, out var list))
                    byEvent[sessionEvent.Event] = list = [];
                list.Add((session, sessionEvent));
            }
        }
        this.subscriptions = byEvent.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var taken = new bool[XeEventKinds.Count];
        foreach (var (@event, _) in byEvent)
        {
            if (XeEventKinds.Of(@event) is { } kind)
                taken[(int)kind] = true;
        }
        this.Taken = taken;
    }
}

/// <summary>
/// One of the engine's internal sessions — <c>hkenginexesession</c> and the
/// <c>sp_server_diagnostics session</c> — as SQL Server 2025's
/// <c>sys.dm_xe_sessions</c> and its companions list them (dumped 2026-10-16
/// into <c>ExtendedEvents/InternalSessions.tsv</c>). They have no definition
/// and collect nothing here.
/// </summary>
internal sealed class InternalEventSession
{
    public readonly string Name;
    public readonly int BufferPolicyFlags;
    public readonly string BufferPolicyDesc;
    public readonly int Flags;
    public readonly string FlagDesc;
    public readonly int TotalRegularBuffers;
    public readonly long RegularBufferSize;
    public readonly long TotalBufferSize;
    public readonly List<(string Name, Guid Package)> Events = [];
    public readonly List<(string Name, Guid Package)> Targets = [];
    public readonly List<(string Name, int Id, string? Value, string ObjectType, string ObjectName, Guid Package)> Columns = [];

    /// <summary>A stand-in address, as the SQLOS views report.</summary>
    public readonly byte[] Address;

    private InternalEventSession(string[] fields, int index)
    {
        this.Name = fields[1];
        this.BufferPolicyFlags = int.Parse(fields[2], CultureInfo.InvariantCulture);
        this.BufferPolicyDesc = fields[3];
        this.Flags = int.Parse(fields[4], CultureInfo.InvariantCulture);
        this.FlagDesc = fields[5];
        this.TotalRegularBuffers = int.Parse(fields[7], CultureInfo.InvariantCulture);
        this.RegularBufferSize = long.Parse(fields[8], CultureInfo.InvariantCulture);
        this.TotalBufferSize = long.Parse(fields[9], CultureInfo.InvariantCulture);
        this.Address = RunningEventSession.StandInAddress(index);
    }

    public static readonly InternalEventSession[] All = Read();

    private static InternalEventSession[] Read()
    {
        using var stream = typeof(InternalEventSession).Assembly.GetManifestResourceStream("SqlServerSimulator.XeInternalSessions.tsv")
            ?? throw new InvalidOperationException("Embedded resource SqlServerSimulator.XeInternalSessions.tsv is missing.");
        using var reader = new StreamReader(stream);
        var sessions = new List<InternalEventSession>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            var fields = line.Split('\t');
            if (fields[0] == "S")
            {
                sessions.Add(new InternalEventSession(fields, sessions.Count));
                continue;
            }
            var session = sessions.First(candidate => candidate.Name == fields[1]);
            switch (fields[0])
            {
                case "C":
                    session.Columns.Add((fields[2], int.Parse(fields[3], CultureInfo.InvariantCulture), fields[4] == "\\N" ? null : fields[4], fields[5], fields[6], Guid.Parse(fields[7])));
                    break;
                case "E":
                    session.Events.Add((fields[2], Guid.Parse(fields[3])));
                    break;
                default:
                    session.Targets.Add((fields[2], Guid.Parse(fields[3])));
                    break;
            }
        }
        return [.. sessions];
    }
}

/// <summary>A session while it runs: the definition it runs under and what each of its targets holds.</summary>
internal sealed class RunningEventSession
{
    public EventSessionDefinition Definition;

    /// <summary>Guards the targets and counters, which firings on any session update.</summary>
    public readonly Lock Gate = new();

    public readonly DateTime CreateTime;

    public readonly int StartOrder;

    public readonly byte[] Address;

    /// <summary>Each target's collected state, by <c>target_id</c>.</summary>
    public readonly Dictionary<int, XeTargetState> Targets = [];

    /// <summary>The session's <c>package0.event_sequence</c> counter.</summary>
    public long EventSequence;

    /// <summary>How many times each event's predicate has been asked, by <c>event_id</c> — <c>package0.counter</c>.</summary>
    public readonly Dictionary<int, long> Counters = [];

    private readonly ExtendedEventsServer server;

    public RunningEventSession(EventSessionDefinition definition, DateTime createTime, int startOrder, ExtendedEventsServer server)
    {
        this.server = server;
        this.Definition = definition;
        this.CreateTime = createTime;
        this.StartOrder = startOrder;
        this.Address = StandInAddress(startOrder + 2);
        foreach (var target in definition.Targets)
            this.Targets[target.Id] = XeTargetState.For(target, this, server);
    }

    /// <summary>A stand-in address in the shape real's carry, distinct per session run.</summary>
    public static byte[] StandInAddress(int index)
    {
        var value = 0x0000_000C_00F0_0001UL + ((ulong)index * 0x1_0000);
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    public void Redefine(EventSessionDefinition definition)
    {
        lock (this.Gate)
        {
            this.Definition = definition;
            var kept = new HashSet<int>();
            foreach (var target in definition.Targets)
            {
                _ = kept.Add(target.Id);
                if (!this.Targets.ContainsKey(target.Id))
                    this.Targets[target.Id] = XeTargetState.For(target, this, this.server);
            }
            foreach (var id in this.Targets.Keys.Where(id => !kept.Contains(id)).ToArray())
                _ = this.Targets.Remove(id);
        }
    }

    public void Stop()
    {
        lock (this.Gate)
        {
            foreach (var (_, target) in this.Targets)
                target.Close();
        }
    }
}
