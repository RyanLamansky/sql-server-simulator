using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Trace;

/// <summary>
/// The trace event classes the simulator produces, each read off the
/// Extended Events firing at the same point — the seam both share, as real's
/// <c>sys.trace_xe_event_map</c> pairs them — with the columns SQL Server 2025
/// writes for it (probed 2026-10-16): the event's own fields mapped onto the
/// trace columns, the session's columns (<c>sys.trace_xe_action_map</c>'s)
/// read off the session it fired on.
/// </summary>
internal static class TraceEvents
{
    /// <summary>The <c>SPID</c> real's system tasks report, its lock monitor's (see <see cref="XeContextValues.LockMonitorSpid"/>).</summary>
    private const int SystemSpid = XeContextValues.LockMonitorSpid;

    /// <summary>Per <see cref="XeEventKind"/>, the trace event classes a firing of it produces, in the order real writes them.</summary>
    private static readonly short[][] ClassesByKind = BuildClasses();

    private static short[][] BuildClasses()
    {
        var classes = new short[XeEventKinds.Count][];
        for (var i = 0; i < classes.Length; i++)
            classes[i] = [];
        classes[(int)XeEventKind.ErrorReported] = [33, 162];
        classes[(int)XeEventKind.SqlBatchStarting] = [13];
        classes[(int)XeEventKind.SqlBatchCompleted] = [12];
        classes[(int)XeEventKind.RpcStarting] = [11];
        classes[(int)XeEventKind.RpcCompleted] = [10];
        classes[(int)XeEventKind.XmlDeadlockReport] = [148];
        classes[(int)XeEventKind.LockDeadlock] = [25];
        classes[(int)XeEventKind.LockDeadlockChain] = [59];
        classes[(int)XeEventKind.BlockedProcessReport] = [137];
        classes[(int)XeEventKind.SqlStatementStarting] = [40];
        classes[(int)XeEventKind.SqlStatementCompleted] = [41];
        classes[(int)XeEventKind.SpStatementStarting] = [44];
        classes[(int)XeEventKind.SpStatementCompleted] = [45];
        classes[(int)XeEventKind.ModuleStart] = [42];
        classes[(int)XeEventKind.ModuleEnd] = [43];
        classes[(int)XeEventKind.Login] = [14];
        classes[(int)XeEventKind.Logout] = [15];
        classes[(int)XeEventKind.Attention] = [16];
        classes[(int)XeEventKind.LockTimeout] = [27];
        classes[(int)XeEventKind.LockTimeoutGreaterThan0] = [189];
        classes[(int)XeEventKind.ObjectCreated] = [46];
        classes[(int)XeEventKind.ObjectAltered] = [164];
        classes[(int)XeEventKind.ObjectDeleted] = [47];
        classes[(int)XeEventKind.UserEvent] = [82, 83, 84, 85, 86, 87, 88, 89, 90, 91];
        return classes;
    }

    /// <summary>Per <see cref="XeEventKind"/>, whether a trace taking the event classes <paramref name="events"/> marks takes it.</summary>
    public static bool[] KindsTaken(bool[] events)
    {
        var taken = new bool[XeEventKinds.Count];
        for (var i = 0; i < taken.Length; i++)
        {
            foreach (var eventClass in ClassesByKind[i])
            {
                if (events[eventClass])
                    taken[i] = true;
            }
        }
        return taken;
    }

    /// <summary>
    /// Writes <paramref name="firing"/> into every running trace taking the
    /// class it produces; <paramref name="text"/> is the <c>TextData</c> a
    /// class whose event carries no text of its own reports — a module's
    /// calling statement.
    /// </summary>
    public static void Deliver(Simulation simulation, XeEventKind kind, XeFiring firing, string? text)
    {
        if (simulation.TracesIfMade is not { } server)
            return;
        var running = server.Running;
        foreach (var eventClass in ClassesByKind[(int)kind])
        {
            if (!Produces(eventClass, firing))
                continue;
            SqlValue?[]? values = null;
            long sequence = 0;
            foreach (var trace in running)
            {
                if (!trace.TakesEvent(eventClass))
                    continue;
                if (values is null)
                {
                    values = Values(eventClass, firing, text);
                    sequence = Interlocked.Increment(ref server.EventSequence);
                    values[51] = SqlValue.FromInt64(sequence);
                }
                trace.Write(simulation, server, firing.Connection, eventClass, values, firing.Timestamp);
            }
        }
    }

    /// <summary>
    /// Whether a firing produces <paramref name="eventClass"/>: an
    /// <c>Exception</c> only for an error the engine raised — not a
    /// <c>RAISERROR</c>, <c>THROW</c> or informational message — and a
    /// user-configurable class only for its own event id (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    private static bool Produces(int eventClass, XeFiring firing) => eventClass switch
    {
        33 => Field(firing, "user_defined") is { } userDefined && !userDefined.Boolean && Field(firing, "severity") is { Signed: > 10 },
        >= 82 and <= 91 => Field(firing, "event_id") is { } eventId && eventId.Signed == eventClass,
        _ => true,
    };

    /// <summary>The value of the event's field named <paramref name="name"/>, null for a field it lacks.</summary>
    private static XeValue? Field(XeFiring firing, string name)
    {
        var columns = firing.Event.DataColumns;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].Name == name)
                return firing.Data[i];
        }
        return null;
    }

    private static Database? DatabaseById(Simulation simulation, int id)
    {
        foreach (var (database, databaseId) in Parser.Expressions.DbId.DatabasesWithIds(simulation))
        {
            if (databaseId == id)
                return database;
        }
        return null;
    }

    private static long Integer(XeValue value) => value.Family == XeTypeFamily.Signed ? value.Signed : (long)value.Unsigned;

    /// <summary>
    /// Every column <paramref name="eventClass"/> can carry for the firing,
    /// by column id; a trace keeps those it selected and the class binds.
    /// </summary>
    private static SqlValue?[] Values(int eventClass, XeFiring firing, string? text)
    {
        var values = new SqlValue?[TraceCatalog.MaxColumnId + 1];
        var connection = firing.Connection;
        var timestamp = firing.Timestamp;
        if (firing.SystemTask)
        {
            values[12] = SqlValue.FromInt32(SystemSpid);
            values[60] = SqlValue.FromInt32(1);
        }
        else
        {
            SessionColumns(values, connection);
        }
        values[14] = SqlValue.FromDateTime(timestamp);
        values[26] = SqlValue.FromNVarchar(Simulation.ServerNameValue);
        if (connection.CurrentTransaction is { } transaction)
            values[4] = SqlValue.FromInt64(transaction.TransactionId);

        void Int(int column, string field)
        {
            if (Field(firing, field) is { } value)
                values[column] = SqlValue.FromInt32((int)Integer(value));
        }
        void BigInt(int column, string field)
        {
            if (Field(firing, field) is { } value)
                values[column] = SqlValue.FromInt64(Integer(value));
        }
        void Text(int column, string field)
        {
            if (Field(firing, field) is { Text: { Length: > 0 } value })
                values[column] = column == 1 ? SqlValue.FromNText(value) : SqlValue.FromNVarchar(value);
        }
        void Binary(int column, string field)
        {
            if (Field(firing, field) is { Bytes: { Length: > 0 } bytes })
                values[column] = SqlValue.FromImage(bytes);
        }
        void Completed()
        {
            if (Field(firing, "duration") is not { } duration)
                return;
            var micros = Integer(duration);
            values[13] = SqlValue.FromInt64(micros);
            values[14] = SqlValue.FromDateTime(timestamp.AddTicks(-micros * 10));
            values[15] = SqlValue.FromDateTime(timestamp);
        }
        void Counters()
        {
            values[16] = SqlValue.FromInt64(0);
            values[17] = SqlValue.FromInt64(0);
            values[18] = SqlValue.FromInt32(0);
        }
        void Database()
        {
            if (Field(firing, "database_id") is { } id && Integer(id) != 0)
            {
                values[3] = SqlValue.FromInt32((int)Integer(id));
                if (DatabaseById(connection.Simulation, (int)Integer(id)) is { } found)
                    values[35] = SqlValue.FromNVarchar(found.Name);
            }
        }
        void Statement()
        {
            Text(1, "statement");
            Int(5, "line_number");
            Int(61, "offset");
            Int(55, "offset_end");
        }
        void Module()
        {
            if (Field(firing, "source_database_id") is not { } sourceDatabase)
                return;
            var databaseId = (int)Integer(sourceDatabase);
            values[62] = SqlValue.FromInt32(databaseId);
            Int(22, "object_id");
            if (Field(firing, "object_type") is { } type)
                values[28] = SqlValue.FromInt32(type.Family == XeTypeFamily.AnsiString ? ObjectTypeCode(type.Text ?? "") : (int)type.Unsigned);
            if (Field(firing, "object_name") is { Text: { Length: > 0 } name })
            {
                values[34] = SqlValue.FromNVarchar(name);
            }
            else if (Field(firing, "object_id") is { } objectId
                && DatabaseById(connection.Simulation, databaseId) is { } database
                && Parser.Expressions.ObjectProperty.FindObject(database, (int)Integer(objectId)) is { } found)
            {
                values[34] = SqlValue.FromNVarchar(found.Name);
            }
        }
        void Lock()
        {
            Database();
            BigInt(4, "transaction_id");
            Int(32, "mode");
            Int(22, "object_id");
            BigInt(56, "associated_object_id");
            Int(57, "resource_type");
            Int(58, "owner_type");
            Text(1, "resource_description");
            values[52] = SqlValue.FromInt64(0);
            values[55] = SqlValue.FromInt32(0);
        }

        switch (eventClass)
        {
            case 10 or 11:
                Text(1, "statement");
                Binary(2, "data_stream");
                Text(34, "object_name");
                if (eventClass == 10)
                {
                    Completed();
                    Counters();
                    Int(31, "result");
                    BigInt(48, "row_count");
                }
                break;
            case 12 or 13:
                Text(1, "batch_text");
                if (eventClass == 12)
                {
                    Completed();
                    Counters();
                    Int(31, "result");
                    BigInt(48, "row_count");
                }
                break;
            case 14 or 15:
                LoginColumns(values, eventClass, connection, timestamp, Field(firing, "is_cached") is { } cached && cached.Boolean,
                    Field(firing, "packet_size") is { } packet ? (int)Integer(packet) : 0);
                break;
            case 16:
                Completed();
                Int(49, "request_id");
                break;
            case 25 or 27 or 189:
                Lock();
                if (eventClass == 25)
                    Int(25, "deadlock_id");
                Completed();
                if (eventClass != 25 && values[13] is null)
                    values[13] = SqlValue.FromInt64(0);
                break;
            case 59:
                Lock();
                Int(25, "deadlock_id");
                values[21] = SqlValue.FromInt32(101);
                break;
            case 33 or 162:
                Text(1, "message");
                Int(20, "severity");
                Int(30, "state");
                Int(31, "error_number");
                break;
            case 40 or 41:
                Statement();
                values[29] = SqlValue.FromInt32(0);
                if (eventClass == 41)
                {
                    Completed();
                    Counters();
                    BigInt(48, "row_count");
                    Int(25, "last_row_count");
                }
                break;
            case 42 or 43:
                if (text is { Length: > 0 })
                    values[1] = SqlValue.FromNText(text);
                if (Field(firing, "line_number") is { } line && Integer(line) != 0)
                    values[5] = SqlValue.FromInt32((int)Integer(line));
                Module();
                values[29] = SqlValue.FromInt32(Math.Max(1, connection.NestingLevel));
                if (eventClass == 43)
                {
                    // Real reports the module's duration as 0 (probed 2026-10-16 against SQL Server 2025).
                    Completed();
                    values[13] = SqlValue.FromInt64(0);
                    BigInt(48, "row_count");
                }
                break;
            case 44 or 45:
                Statement();
                Module();
                Int(29, "nest_level");
                if (eventClass == 45)
                {
                    Completed();
                    Counters();
                    BigInt(48, "row_count");
                    Int(25, "last_row_count");
                }
                break;
            case 46 or 47 or 164:
                Database();
                BigInt(4, "transaction_id");
                Int(21, "ddl_phase");
                Int(22, "object_id");
                Int(24, "index_id");
                Int(28, "object_type");
                Text(34, "object_name");
                BigInt(56, "related_object_id");
                break;
            case 137:
                Database();
                Completed();
                BigInt(4, "transaction_id");
                Int(22, "object_id");
                Int(24, "index_id");
                Int(32, "lock_mode");
                Text(1, "blocked_process");
                break;
            case 148:
                if (Field(firing, "xml_report") is { Text: { } report })
                    values[1] = SqlValue.FromNText(DeadlockList(report));
                // Real's report comes from session 2 under sa (probed 2026-10-16 against SQL Server 2025).
                values[11] = SqlValue.FromNVarchar("sa");
                values[12] = SqlValue.FromInt32(2);
                break;
            case >= 82 and <= 91:
                Text(1, "user_info");
                Binary(2, "user_data");
                break;
        }
        return values;
    }

    /// <summary>
    /// The login events' columns besides the session's own: the options the
    /// session runs under, as <c>Audit Login</c> and <c>ExistingConnection</c>
    /// report them (one setting a line, each ending CRLF), its packet size,
    /// and for a logout its duration since the login.
    /// </summary>
    public static void LoginColumns(SqlValue?[] values, int eventClass, SimulatedDbConnection connection, DateTime now, bool pooled, int packetSize)
    {
        values[3] = SqlValue.FromInt32(connection.CurrentDatabase.Id);
        values[35] = SqlValue.FromNVarchar(connection.CurrentDatabase.Name);
        values[57] = SqlValue.FromInt32(1);
        var duration = (long)Math.Max(0, (now - connection.LoginTimeUtc).TotalMicroseconds);
        switch (eventClass)
        {
            case 14 or 17:
                values[1] = SqlValue.FromNText(XeSessionEvents.OptionsText(connection).Replace("\n", "\r\n", StringComparison.Ordinal));
                values[2] = SqlValue.FromImage(new byte[12]);
                values[25] = SqlValue.FromInt32(packetSize);
                if (eventClass == 14)
                {
                    values[21] = SqlValue.FromInt32(pooled ? 2 : 1);
                    values[23] = SqlValue.FromInt32(1);
                }
                else
                {
                    values[13] = SqlValue.FromInt64(duration);
                    values[14] = SqlValue.FromDateTime(connection.LoginTimeUtc);
                    values[15] = SqlValue.FromDateTime(now);
                    values[55] = SqlValue.FromInt32(0);
                }
                break;
            case 15:
                values[13] = SqlValue.FromInt64(duration);
                values[14] = SqlValue.FromDateTime(connection.LoginTimeUtc);
                values[15] = SqlValue.FromDateTime(now);
                values[16] = SqlValue.FromInt64(0);
                values[17] = SqlValue.FromInt64(0);
                values[18] = SqlValue.FromInt32(0);
                values[21] = SqlValue.FromInt32(1);
                values[23] = SqlValue.FromInt32(1);
                break;
        }
    }

    /// <summary>The columns every event of a session carries: who and where it is.</summary>
    public static void SessionColumns(SqlValue?[] values, SimulatedDbConnection connection)
    {
        var security = connection.Security;
        values[3] = SqlValue.FromInt32(connection.CurrentDatabase.Id);
        values[8] = SqlValue.FromNVarchar(connection.ClientHostName);
        values[9] = SqlValue.FromInt32(connection.ClientProcessId);
        values[10] = SqlValue.FromNVarchar(connection.ClientApplicationName);
        values[11] = SqlValue.FromNVarchar(security.Effective.LoginName);
        values[12] = SqlValue.FromInt32(connection.Spid);
        values[35] = SqlValue.FromNVarchar(connection.CurrentDatabase.Name);
        values[41] = SqlValue.FromImage(Simulation.LoginSid(security.Effective.LoginName));
        values[49] = SqlValue.FromInt32(0);
        values[50] = SqlValue.FromInt64(0);
        values[64] = SqlValue.FromNVarchar(security.OriginalLoginName);
        values[66] = SqlValue.FromInt32(2);
    }

    /// <summary>
    /// A module type's two-character code as the trace's <c>ObjectType</c>
    /// reports it: the characters' codes, the first in the low byte —
    /// <c>P </c> is 8272 (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static int ObjectTypeCode(string code) =>
        code.Length == 0 ? 0 : code[0] | ((code.Length > 1 ? code[1] : ' ') << 8);

    /// <summary>
    /// The <c>Deadlock graph</c> class's document: the
    /// <c>xml_deadlock_report</c> graph in the older shape real's trace writes
    /// it in — inside a <c>deadlock-list</c>, its victim an attribute of
    /// <c>deadlock</c> rather than a <c>victim-list</c>, one space deeper and
    /// each line ending CRLF (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static string DeadlockList(string report)
    {
        var lines = report.Split('\n');
        var victim = "";
        var builder = new System.Text.StringBuilder(report.Length + 64);
        _ = builder.Append("<deadlock-list>\r\n");
        var inVictims = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("<victim-list>", StringComparison.Ordinal))
            {
                inVictims = true;
                continue;
            }
            if (inVictims)
            {
                if (trimmed.StartsWith("</victim-list>", StringComparison.Ordinal))
                {
                    inVictims = false;
                    _ = builder.Append(" <deadlock victim=\"").Append(victim).Append("\">\r\n");
                }
                else if (victim.Length == 0 && trimmed.StartsWith("<victimProcess id=\"", StringComparison.Ordinal))
                {
                    victim = trimmed[19..trimmed.IndexOf('"', 19)];
                }
                continue;
            }
            if (trimmed == "<deadlock>")
                continue;
            _ = builder.Append(' ').Append(line).Append("\r\n");
        }
        return builder.Append("</deadlock-list>\r\n").ToString();
    }
}
