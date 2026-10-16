using System.Globalization;
using System.Text;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// One firing of an event: the values of its data fields, the session and
/// statement it fired in, and the moment it fired. A predicate reads its
/// fields and the predicate sources through it, and each session it reaches
/// renders it as the XML its targets keep.
/// </summary>
internal sealed class XeFiring(XeObject @event, XeValue[] data, SimulatedDbConnection connection, DateTime timestamp)
{
    public readonly XeObject Event = @event;

    /// <summary>The values of <see cref="XeObject.DataColumns"/>, position for position.</summary>
    public readonly XeValue[] Data = data;

    public readonly SimulatedDbConnection Connection = connection;


    public readonly DateTime Timestamp = timestamp;

    /// <summary>The session judging the firing, which <c>package0.counter</c> and <c>package0.event_sequence</c> count for.</summary>
    public RunningEventSession? Session;

    /// <summary>The session's event judging the firing.</summary>
    public SessionEvent? SessionEvent;

    public XeValue Field(XeObjectColumn column)
    {
        var columns = this.Event.DataColumns;
        for (var i = 0; i < columns.Length; i++)
        {
            if (ReferenceEquals(columns[i], column))
                return this.Data[i];
        }
        return default;
    }

    /// <summary>A predicate source's value; one the simulator doesn't track reads as its type's zero.</summary>
    public XeValue Global(XeObject source, XeType type)
    {
        if (source.Name == "counter" && source.Package.Name == "package0" && this.Session is { } session && this.SessionEvent is { } sessionEvent)
        {
            lock (session.Gate)
            {
                var count = session.Counters.GetValueOrDefault(sessionEvent.Id) + 1;
                session.Counters[sessionEvent.Id] = count;
                return XeValue.FromUnsigned((ulong)count);
            }
        }
        return XeContextValues.Value(source.Package.Name, source.Name, this) ?? XeContextValues.Zero(type);
    }
}

/// <summary>
/// The values the actions and predicate sources read off the session and
/// statement an event fires in. A source or action the simulator has no
/// state for reads as its type's zero; one that has nothing to report — the
/// statement text of an event raised outside a batch — is left out of the
/// event, as real leaves it out (probed 2026-10-16 against SQL Server 2025).
/// </summary>
internal static class XeContextValues
{
    public static XeValue? Value(string package, string name, XeFiring firing)
    {
        var connection = firing.Connection;
        if (package == "package0")
        {
            return name switch
            {
                "collect_current_thread_id" or "current_thread_id" => XeValue.FromUnsigned((ulong)Environment.CurrentManagedThreadId),
                "collect_system_time" => XeValue.FromAnsi(XeEventXml.Timestamp(firing.Timestamp)),
                "event_sequence" when firing.Session is { } session => XeValue.FromUnsigned((ulong)Interlocked.Increment(ref session.EventSequence)),
                _ => null,
            };
        }
        if (package != "sqlserver")
            return null;
        var security = connection.Security.Effective;
        return name switch
        {
            "client_app_name" => XeValue.FromUnicode(connection.ClientApplicationName),
            "client_hostname" => XeValue.FromUnicode(connection.ClientHostName),
            "client_pid" => XeValue.FromUnsigned((ulong)connection.ClientProcessId),
            "database_id" => XeValue.FromUnsigned((ulong)connection.CurrentDatabase.Id),
            "database_name" => XeValue.FromUnicode(connection.CurrentDatabase.Name),
            "is_system" => XeValue.FromBoolean(false),
            "server_instance_name" => XeValue.FromUnicode(SimulatedDbConnection.DataSourceName),
            "server_principal_name" or "session_server_principal_name" or "username" => XeValue.FromUnicode(security.LoginName),
            "session_id" => XeValue.FromUnsigned((ulong)connection.Spid),
            "sql_text" => connection.Session.XeSqlText is { } text ? XeValue.FromUnicode(text) : null,
            "transaction_id" => XeValue.FromSigned(connection.CurrentTransaction?.TransactionId ?? 0),
            _ => null,
        };
    }

    public static XeValue Zero(XeType type) => type.Family switch
    {
        XeTypeFamily.Signed => XeValue.FromSigned(0),
        XeTypeFamily.Float => XeValue.FromFloat(0),
        XeTypeFamily.Boolean => XeValue.FromBoolean(false),
        XeTypeFamily.UnicodeString => XeValue.FromUnicode(""),
        XeTypeFamily.AnsiString => XeValue.FromAnsi(""),
        XeTypeFamily.BinaryData => XeValue.FromBinary([]),
        XeTypeFamily.Guid => XeValue.FromGuid(Guid.Empty),
        XeTypeFamily.Map => XeValue.FromMap(0, null),
        XeTypeFamily.Unsigned or XeTypeFamily.Pointer => XeValue.FromUnsigned(0),
        _ => XeValue.FromAnsi(""),
    };
}

/// <summary>One firing as a session writes it: the XML the in-memory targets keep, and the shorter form an event file holds.</summary>
internal sealed class XeEventRecord(string xml, string fileXml)
{
    public readonly string Xml = xml;
    public readonly string FileXml = fileXml;
}

/// <summary>Writes an event as the ring buffer, the event file and the pairing target keep it.</summary>
internal static class XeEventXml
{
    /// <summary><c>2026-10-16T05:31:16.986Z</c>: a timestamp as real writes one, to the millisecond in UTC.</summary>
    public static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The event's XML: every data field in column order, then the actions
    /// in the reverse of their written order, as real lists them (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static XeEventRecord Render(XeFiring firing, SessionEvent sessionEvent)
    {
        var @event = firing.Event;
        var memory = new StringBuilder(512);
        var file = new StringBuilder(384);
        foreach (var builder in (ReadOnlySpan<StringBuilder>)[memory, file])
        {
            _ = builder.Append("<event name=\"").Append(@event.Name).Append("\" package=\"").Append(@event.Package.Name)
                .Append("\" timestamp=\"").Append(Timestamp(firing.Timestamp)).Append("\">");
        }
        var columns = @event.DataColumns;
        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            var value = Collects(sessionEvent, column) ? firing.Data[i] : XeContextValues.Zero(XeType.Of(column.TypeName, column.TypePackage));
            AppendValue(memory, "data", column.Name, column.TypeName, column.TypePackage, value, withType: true);
            AppendValue(file, "data", column.Name, column.TypeName, column.TypePackage, value, withType: false);
        }
        for (var i = sessionEvent.Actions.Length - 1; i >= 0; i--)
        {
            var action = sessionEvent.Actions[i];
            var type = XeType.Of(action.TypeName!, action.TypePackage);
            if (XeContextValues.Value(action.Package.Name, action.Name, firing) is not { } value)
            {
                if (action.Package.Name is "sqlserver" && action.Name is "sql_text")
                    continue;
                value = XeContextValues.Zero(type);
            }
            AppendValue(memory, "action", action.Name, action.TypeName!, action.TypePackage, value, withType: true, action.Package.Name);
            AppendValue(file, "action", action.Name, action.TypeName!, action.TypePackage, value, withType: false, action.Package.Name);
        }
        return new XeEventRecord(memory.Append("</event>").ToString(), file.Append("</event>").ToString());
    }

    /// <summary>
    /// Whether the event collects <paramref name="column"/>: a field a
    /// <c>collect_*</c> flag governs is collected only while the flag is on,
    /// and written empty otherwise (probed 2026-10-16 against SQL Server
    /// 2025, where <c>sql_statement_completed</c> writes an empty
    /// <c>parameterized_plan_handle</c>).
    /// </summary>
    private static bool Collects(SessionEvent sessionEvent, XeObjectColumn column)
    {
        foreach (var flag in sessionEvent.Event.Columns)
        {
            if (flag.ColumnType == "customizable" && flag.Name == "collect_" + column.Name)
                return sessionEvent.FieldText(flag) is "true" or "1";
        }
        return true;
    }

    /// <summary>One value's element; the in-memory targets name its type, an event file doesn't (probed 2026-10-16 against SQL Server 2025).</summary>
    private static void AppendValue(StringBuilder builder, string element, string name, string typeName, XePackage? typePackage, XeValue value, bool withType, string? package = null)
    {
        _ = builder.Append('<').Append(element).Append(" name=\"").Append(name).Append('"');
        if (package is not null)
            _ = builder.Append(" package=\"").Append(package).Append('"');
        _ = builder.Append('>');
        if (withType)
            _ = builder.Append("<type name=\"").Append(typeName).Append("\" package=\"").Append(typePackage?.Name ?? "package0").Append("\"></type>");
        switch (value.Family)
        {
            case XeTypeFamily.UnicodeString:
                _ = builder.Append("<value><![CDATA[").Append(value.Text).Append("]]></value>");
                break;
            case XeTypeFamily.Map:
                var type = XeType.Of(typeName, typePackage);
                var bitmask = type.Map?.CapabilitiesDesc == "bitmask";
                _ = builder.Append("<value>").Append(bitmask ? "0x" + value.Unsigned.ToString("X8", CultureInfo.InvariantCulture) : value.XmlText).Append("</value>")
                    .Append("<text><![CDATA[").Append(value.Text ?? MapText(type, value.Unsigned, bitmask)).Append("]]></text>");
                break;
            default:
                _ = builder.Append("<value>").Append(value.XmlText).Append("</value>");
                break;
        }
        _ = builder.Append("</").Append(element).Append('>');
    }

    private static string MapText(XeType type, ulong key, bool bitmask)
    {
        if (type.Map is not { } map)
            return "";
        if (!bitmask)
        {
            foreach (var mapValue in map.MapValues)
            {
                if ((ulong)mapValue.Key == key)
                    return mapValue.Value;
            }
            return "";
        }
        var names = new List<string>();
        foreach (var mapValue in map.MapValues)
        {
            if (mapValue.Key != 0 && (key & (ulong)mapValue.Key) == (ulong)mapValue.Key)
                names.Add(mapValue.Value);
        }
        return string.Join(' ', names);
    }
}
