using System.Globalization;
using System.Text;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// The events the simulator fires: <c>error_reported</c> for every error and
/// numbered message a statement raises, and <c>sql_batch_starting</c> /
/// <c>sql_batch_completed</c> or <c>rpc_starting</c> / <c>rpc_completed</c>
/// around each request, each with the fields SQL Server 2025 gives it
/// (probed 2026-10-16). A firing costs one lookup when no running
/// session takes the event.
/// </summary>
internal static class XeEvents
{
    /// <summary>
    /// Each event the simulator fires, resolved against the catalog on its
    /// first firing a session takes.
    /// </summary>
    private static readonly XeObject?[] Objects = new XeObject?[XeEventKinds.Count];

    private static XeObject Resolve(XeEventKind kind)
    {
        var index = (int)kind;
        if (Volatile.Read(ref Objects[index]) is { } resolved)
            return resolved;
        var (package, name) = XeEventKinds.Names[index];
        var found = XeMetadata.Instance.Find(package, name, "event")!;
        Volatile.Write(ref Objects[index], found);
        return found;
    }

    /// <summary>
    /// Whether a running session takes <paramref name="kind"/>: before any
    /// statement made the server's Extended Events state, whether one of the
    /// sessions that start with the server lists it, its predicate unasked.
    /// One array read, so an event nothing takes costs its call site nothing
    /// more.
    /// </summary>
    public static bool Takes(Simulation simulation, XeEventKind kind) =>
        (simulation.ExtendedEventsIfMade is { } server ? server.Taken[(int)kind] : XeEventKinds.TakenAtStartup[(int)kind])
        || Traced(simulation, kind);

    /// <summary>
    /// Whether a running session or a user's trace takes
    /// <paramref name="kind"/> — anything but the default trace, whose object
    /// events a firing site can write without the Extended Events catalog
    /// (<see cref="Trace.TraceEvents.WriteDefaultObjectEvent"/>).
    /// </summary>
    public static bool TakesBeyondDefaultTrace(Simulation simulation, XeEventKind kind) =>
        (simulation.ExtendedEventsIfMade is { } server ? server.Taken[(int)kind] : XeEventKinds.TakenAtStartup[(int)kind])
        || (simulation.TraceTaken is { } traced && traced[(int)kind]);

    /// <summary>Whether a running trace — a user's, or the default trace — takes <paramref name="kind"/>.</summary>
    private static bool Traced(Simulation simulation, XeEventKind kind) =>
        (simulation.TraceTaken is { } traced && traced[(int)kind])
        || (Trace.TraceEvents.DefaultTraceKinds[(int)kind] && simulation.DefaultTraceRunning);

    /// <summary>
    /// <c>error_reported</c> for <paramref name="error"/>, intercepted when a
    /// <c>TRY</c> frame open anywhere on the session will catch it. Category
    /// and destination are the <c>SERVER</c> / <c>USER</c> pair every
    /// statement error real raises reports. A message sent as class 0 was
    /// raised at severity 10, which the event reports (probed 2026-10-16
    /// against SQL Server 2025); <c>PRINT</c>'s unnumbered message fires none.
    /// </summary>
    public static void ErrorReported(SimulatedDbConnection connection, SimulatedError error, bool intercepted)
    {
        if (error.Number != 0)
            ErrorReported(connection, error.Number, error.Class == 0 ? 10 : error.Class, error.State, error.Message, intercepted);
    }

    /// <summary><c>error_reported</c> for a message of <paramref name="number"/> sent to the client at <paramref name="severity"/>.</summary>
    public static void ErrorReported(SimulatedDbConnection connection, int number, int severity, int state, string message, bool intercepted) =>
        Fire(connection, XeEventKind.ErrorReported, @event => Data(@event,
            ("error_number", XeValue.FromSigned(number)),
            ("severity", XeValue.FromSigned(severity)),
            ("state", XeValue.FromSigned(state)),
            ("user_defined", XeValue.FromBoolean(number >= 50000)),
            ("category", XeValue.FromMap(2, "SERVER")),
            ("destination", XeValue.FromMap(2, "USER")),
            ("is_intercepted", XeValue.FromBoolean(intercepted)),
            ("message", XeValue.FromUnicode(message)),
            ("nsp", XeValue.FromUnicode(""))));

    /// <summary><c>sql_batch_starting</c>, or <c>rpc_starting</c> for a parameterized or procedure command.</summary>
    public static void RequestStarting(SimulatedDbConnection connection, SimulatedDbCommand command)
    {
        if (IsRpc(command, out var objectName))
        {
            Fire(connection, XeEventKind.RpcStarting, @event => Data(@event,
                ("connection_reset_option", XeValue.FromMap(0, "None")),
                ("object_name", XeValue.FromUnicode(objectName)),
                ("statement", XeValue.FromUnicode(RpcStatement(command))),
                ("data_stream", XeValue.FromBinary([]))));
            return;
        }
        // A text that may carry a secret its walk hasn't yet masked waits
        // for the walk, which runs before any of its statements does.
        if (!connection.Simulation.SecretsKnown(command.CommandText))
        {
            connection.Session.XePendingBatchStarting = command;
            return;
        }
        BatchStarting(connection, command);
    }

    private static void BatchStarting(SimulatedDbConnection connection, SimulatedDbCommand command) =>
        Fire(connection, XeEventKind.SqlBatchStarting, @event => Data(@event, ("batch_text", XeValue.FromUnicode(connection.Simulation.MaskedBatchText(command.CommandText) ?? command.CommandText))));

    /// <summary>
    /// Fires the <c>sql_batch_starting</c> <see cref="RequestStarting"/> held
    /// back for a text that may carry a secret, once the batch's walk has
    /// masked it — as the batch's statements begin, or as it ends unrun.
    /// </summary>
    public static void BatchWalked(SimulatedDbConnection connection)
    {
        if (connection.Session.XePendingBatchStarting is not { } command)
            return;
        connection.Session.XePendingBatchStarting = null;
        BatchStarting(connection, command);
    }

    /// <summary>
    /// <c>sql_batch_completed</c> or <c>rpc_completed</c>: <c>result</c> is
    /// <c>Error</c> for a request an error ended and <c>OK</c> otherwise —
    /// a statement error the batch went on past included (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    public static void RequestCompleted(SimulatedDbConnection connection, SimulatedDbCommand command, long durationMicroseconds, bool ended)
    {
        BatchWalked(connection);
        var result = ended ? XeValue.FromMap(1, "Error") : XeValue.FromMap(0, "OK");
        if (IsRpc(command, out var objectName))
        {
            Fire(connection, XeEventKind.RpcCompleted, @event => Data(@event,
                ("duration", XeValue.FromUnsigned((ulong)durationMicroseconds)),
                ("result", result),
                ("connection_reset_option", XeValue.FromMap(0, "None")),
                ("object_name", XeValue.FromUnicode(objectName)),
                ("statement", XeValue.FromUnicode(RpcStatement(command))),
                ("data_stream", XeValue.FromBinary([]))));
            return;
        }
        Fire(connection, XeEventKind.SqlBatchCompleted, @event => Data(@event,
            ("duration", XeValue.FromUnsigned((ulong)durationMicroseconds)),
            ("result", result),
            ("batch_text", XeValue.FromUnicode(connection.Simulation.MaskedBatchText(command.CommandText) ?? command.CommandText))));
    }

    /// <summary>
    /// Whether real sees <paramref name="command"/> as an RPC — a procedure
    /// call, or a parameterized command SqlClient sends as
    /// <c>sp_executesql</c> — with the object it calls.
    /// </summary>
    public static bool IsRpc(SimulatedDbCommand command, out string objectName)
    {
        if (command.CommandType == System.Data.CommandType.StoredProcedure)
        {
            var name = command.CommandText.Trim();
            var dot = name.LastIndexOf('.');
            objectName = name[(dot + 1)..].Trim('[', ']');
            return true;
        }
        objectName = "sp_executesql";
        return command.Parameters.Count != 0;
    }

    /// <summary>
    /// The statement an RPC's events report, as real reconstructs it:
    /// <c>exec dbo.p @a=1</c> for a procedure (with a trailing blank when it
    /// takes no argument), <c>exec sp_executesql N'…',N'@p int',@p=1</c> for a
    /// parameterized command.
    /// </summary>
    internal static string RpcStatement(SimulatedDbCommand command)
    {
        var builder = new StringBuilder("exec ");
        var parameters = command.Parameters;
        if (command.CommandType == System.Data.CommandType.StoredProcedure)
        {
            _ = builder.Append(command.CommandText.Trim()).Append(' ');
            var first = true;
            foreach (SimulatedDbParameter parameter in parameters)
            {
                if (parameter.Direction == System.Data.ParameterDirection.ReturnValue)
                    continue;
                _ = builder.Append(first ? "" : ",").Append(ParameterName(parameter)).Append('=').Append(Literal(parameter.Value));
                first = false;
            }
            return builder.ToString();
        }
        _ = builder.Append("sp_executesql N'").Append(command.CommandText.Replace("'", "''", StringComparison.Ordinal))
            .Append("',N'").Append(Declaration(parameters).Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        foreach (SimulatedDbParameter parameter in parameters)
            _ = builder.Append(',').Append(ParameterName(parameter)).Append('=').Append(Literal(parameter.Value));
        return builder.ToString();
    }

    /// <summary>
    /// The parameters' declaration as SqlClient writes it; a parameter whose
    /// value carries no SqlClient type — a table-valued one, a value the
    /// simulator passes between its own sessions — is declared by name alone.
    /// </summary>
    private static string Declaration(SimulatedDbParameterCollection parameters)
    {
        try
        {
            return Simulation.SqlClientParameterDeclaration(parameters);
        }
        catch (ArgumentException)
        {
            return string.Join(',', parameters.Cast<SimulatedDbParameter>().Select(ParameterName));
        }
    }

    private static string ParameterName(SimulatedDbParameter parameter) =>
        parameter.ParameterName.StartsWith('@') ? parameter.ParameterName : "@" + parameter.ParameterName;

    /// <summary>A parameter's value as SqlClient's RPC text spells it.</summary>
    private static string Literal(object? value) => value switch
    {
        null or DBNull => "NULL",
        string text => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool flag => flag ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime time => "'" + time.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "'",
        Guid guid => "'" + guid.ToString("D").ToUpperInvariant() + "'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => "N'" + value.ToString()?.Replace("'", "''", StringComparison.Ordinal) + "'",
    };

    /// <summary>The event's data values in column order, from those named; the rest read as their type's zero.</summary>
    internal static XeValue[] Data(XeObject @event, params ReadOnlySpan<(string Name, XeValue Value)> values)
    {
        var columns = @event.DataColumns;
        var data = new XeValue[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            data[i] = XeContextValues.Zero(XeType.Of(columns[i].TypeName, columns[i].TypePackage));
            foreach (var (name, value) in values)
            {
                if (name == columns[i].Name)
                    data[i] = value;
            }
        }
        return data;
    }

    /// <summary>
    /// Fires <paramref name="kind"/> on every running session that takes it,
    /// <paramref name="connection"/> the session it fires on — or, under
    /// <paramref name="systemTask"/>, the session a system task's report is
    /// about — and into every running SQL Trace taking a class it maps onto
    /// (<see cref="Trace.TraceEvents"/>), <paramref name="traceText"/> the
    /// <c>TextData</c> a class reports that the event's own fields don't
    /// carry. A server whose Extended Events state no statement has
    /// made yet has only the default sessions running: an event none of them
    /// takes and no trace takes costs nothing — not even the catalog's load —
    /// and their predicates are asked before the state is made for one.
    /// </summary>
    internal static void Fire(SimulatedDbConnection connection, XeEventKind kind, Func<XeObject, XeValue[]> data, bool systemTask = false, string? traceText = null, System.Runtime.CompilerServices.StrongBox<long>? tracePairing = null)
    {
        var simulation = connection.Simulation;
        var server = simulation.ExtendedEventsIfMade;
        var sessionsTake = server is null ? XeEventKinds.TakenAtStartup[(int)kind] : server.Taken[(int)kind];
        var traced = Traced(simulation, kind);
        if (!sessionsTake && !traced)
            return;
#if DEBUG
        // What a firing reads of the session — an action, a predicate — shapes
        // nothing a cached plan keeps, even fired while a query compiles.
        using var excused = PlanCacheCaptureAudit.SuspendPrincipalWatch();
#endif
        var @event = Resolve(kind);
        XeFiring? firing = null;
        if (traced)
        {
            firing = new XeFiring(@event, data(@event), connection, DateTime.UtcNow, systemTask);
            Trace.TraceEvents.Deliver(simulation, kind, firing, traceText, tracePairing);
            if (!sessionsTake)
                return;
        }
        if (server is null)
        {
            firing ??= new XeFiring(@event, data(@event), connection, DateTime.UtcNow, systemTask);
            if (!DefaultsTake(connection, firing))
                return;
            server = simulation.ExtendedEventsFor(connection);
        }
        var subscribers = server.Subscribers(@event);
        if (subscribers.Length == 0)
            return;
        firing ??= new XeFiring(@event, data(@event), connection, DateTime.UtcNow, systemTask);
        foreach (var (session, sessionEvent) in subscribers)
            FireInto(session, sessionEvent, firing);
    }

    /// <summary>Delivers <paramref name="firing"/> to one session's event: its predicate asked, then each of its targets.</summary>
    internal static void FireInto(RunningEventSession session, SessionEvent sessionEvent, XeFiring firing)
    {
        lock (session.Gate)
        {
            firing.Session = session;
            firing.SessionEvent = sessionEvent;
            if (sessionEvent.Predicate is { } predicate && !predicate.Evaluate(firing))
                return;
            var record = XeEventXml.Render(firing, sessionEvent);
            foreach (var target in session.Definition.Targets)
            {
                if (session.Targets.TryGetValue(target.Id, out var state))
                    state.Accept(firing, sessionEvent, record);
            }
        }
    }

    /// <summary>Whether a default session that starts with the server takes <paramref name="firing"/>.</summary>
    private static bool DefaultsTake(SimulatedDbConnection connection, XeFiring firing)
    {
        foreach (var definition in DefaultEventSessions.Definitions(connection))
        {
            if (!definition.Options.StartupState)
                continue;
            foreach (var sessionEvent in definition.Events)
            {
                if (ReferenceEquals(sessionEvent.Event, firing.Event) && sessionEvent.Predicate?.Evaluate(firing) != false)
                    return true;
            }
        }
        return false;
    }
}
