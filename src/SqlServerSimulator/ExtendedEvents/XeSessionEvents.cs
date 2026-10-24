using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// The session's own events: <c>login</c> — after the <c>error_reported</c>
/// real fires for the login's Msg 5701 and 5703 — <c>logout</c>,
/// <c>existing_connection</c> for each open session as an event session
/// starts, <c>attention</c>, and <c>wait_info</c> / <c>wait_completed</c> as a
/// lock wait or a <c>WAITFOR</c> ends, with SQL Server 2025's fields (probed
/// 2026-10-16).
/// </summary>
internal static class XeSessionEvents
{
    /// <summary>
    /// <c>login</c> as <paramref name="connection"/> opens, the two
    /// environment-change messages first; a pooled connection's reset is
    /// <c>is_cached</c>.
    /// </summary>
    public static void Login(SimulatedDbConnection connection, bool pooled)
    {
        var simulation = connection.Simulation;
        // system_health's predicate takes neither message, so a server whose
        // Extended Events state no statement made fires them nowhere.
        if ((simulation.ExtendedEventsIfMade is not null || simulation.TraceTaken is not null) && XeEvents.Takes(simulation, XeEventKind.ErrorReported))
        {
            XeEvents.ErrorReported(connection, 5701, 10, 2, $"Changed database context to '{connection.CurrentDatabase.Name}'.", intercepted: false);
            XeEvents.ErrorReported(connection, 5703, 10, 1, connection.Language.ChangedMessage, intercepted: false);
        }
        if (XeEvents.Takes(simulation, XeEventKind.Login))
            FireLogin(connection, pooled);
    }

    // Apart from the check above, since the event's data closure is allocated
    // as this is entered.
    private static void FireLogin(SimulatedDbConnection connection, bool pooled) =>
        XeEvents.Fire(connection, XeEventKind.Login, @event => XeEvents.Data(@event,
            ("is_cached", XeValue.FromBoolean(pooled)),
            ("is_recovered", XeValue.FromBoolean(false)),
            ("is_dac", XeValue.FromBoolean(false)),
            ("database_id", XeValue.FromUnsigned((ulong)connection.CurrentDatabase.Id)),
            ("packet_size", XeValue.FromUnsigned(4096)),
            ("options", XeValue.FromBinary(new byte[12])),
            ("options_text", XeValue.FromUnicode(OptionsText(connection))),
            ("database_name", XeValue.FromUnicode(""))));

    /// <summary><c>logout</c> as a session that logged in ends: its duration since the login, its counters read zero.</summary>
    public static void Logout(SimulatedDbConnection connection)
    {
        if (XeEvents.Takes(connection.Simulation, XeEventKind.Logout))
            FireLogout(connection);
    }

    // Apart from the check above, as FireLogin is.
    private static void FireLogout(SimulatedDbConnection connection) =>
        XeEvents.Fire(connection, XeEventKind.Logout, @event => XeEvents.Data(@event,
            ("is_cached", XeValue.FromBoolean(false)),
            ("is_recovered", XeValue.FromBoolean(false)),
            ("is_dac", XeValue.FromBoolean(false)),
            ("duration", XeValue.FromUnsigned((ulong)Math.Max(0, (DateTime.UtcNow - connection.LoginTimeUtc).TotalMicroseconds)))));

    /// <summary>
    /// <c>existing_connection</c> into <paramref name="started"/> alone, for
    /// each open session but the one starting it, which real reports by its
    /// own fields — a predicate on the starter's actions sees the starter
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    public static void ExistingConnections(SimulatedDbConnection starter, RunningEventSession started)
    {
        foreach (var sessionEvent in started.Definition.Events)
        {
            if (XeEventKinds.Of(sessionEvent.Event) != XeEventKind.ExistingConnection)
                continue;
            foreach (var connection in starter.Simulation.SnapshotConnections())
            {
                if (ReferenceEquals(connection, starter) || connection.State != System.Data.ConnectionState.Open)
                    continue;
                var security = connection.Security;
                var data = XeEvents.Data(sessionEvent.Event,
                    ("session_id", XeValue.FromSigned(connection.Spid)),
                    ("is_dac", XeValue.FromBoolean(false)),
                    ("database_id", XeValue.FromUnsigned((ulong)connection.CurrentDatabase.Id)),
                    ("packet_size", XeValue.FromUnsigned(4096)),
                    ("transaction_count", XeValue.FromUnsigned((ulong)connection.OpenTransactionCount)),
                    ("group_id", XeValue.FromUnsigned(2)),
                    ("duration", XeValue.FromUnsigned((ulong)Math.Max(0, (DateTime.UtcNow - connection.LoginTimeUtc).TotalMicroseconds))),
                    ("client_pid", XeValue.FromUnsigned((ulong)connection.ClientProcessId)),
                    ("options", XeValue.FromBinary(new byte[12])),
                    ("options_text", XeValue.FromUnicode(OptionsText(connection))),
                    ("started_event_session_name", XeValue.FromUnicode(started.Definition.Name)),
                    ("database_name", XeValue.FromUnicode("")),
                    ("client_app_name", XeValue.FromUnicode(connection.ClientApplicationName)),
                    ("client_hostname", XeValue.FromUnicode(connection.ClientHostName)),
                    ("server_principal_name", XeValue.FromUnicode(security.OriginalLoginName)),
                    ("session_server_principal_name", XeValue.FromUnicode(security.OriginalLoginName)));
                XeEvents.FireInto(started, sessionEvent, new XeFiring(sessionEvent.Event, data, starter, DateTime.UtcNow));
            }
        }
    }

    /// <summary>
    /// The session's settings as the login events' <c>options_text</c> lists
    /// them, in real's order and words (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    public static string OptionsText(SimulatedDbConnection connection)
    {
        static string OnOff(bool on) => on ? "on" : "off";
        var dateFirst = connection.DateFirst;
        var builder = new StringBuilder(512);
        _ = builder.Append("-- network protocol: TCP/IP\n")
            .Append("set quoted_identifier ").Append(OnOff(connection.QuotedIdentifiers)).Append('\n')
            .Append("set arithabort ").Append(OnOff(connection.Arithabort)).Append('\n')
            .Append("set numeric_roundabort ").Append(OnOff(connection.NumericRoundabort)).Append('\n')
            .Append("set ansi_warnings ").Append(OnOff(connection.AnsiWarnings)).Append('\n')
            .Append("set ansi_padding ").Append(OnOff(connection.AnsiPadding)).Append('\n')
            .Append("set ansi_nulls ").Append(OnOff(connection.AnsiNulls)).Append('\n')
            .Append("set concat_null_yields_null ").Append(OnOff(connection.ConcatNullYieldsNull)).Append('\n')
            .Append("set cursor_close_on_commit ").Append(OnOff(connection.CursorCloseOnCommit)).Append('\n')
            .Append("set implicit_transactions ").Append(OnOff(connection.ImplicitTransactions)).Append('\n')
            .Append("set language ").Append(connection.Language.Name).Append('\n')
            .Append("set dateformat ").Append(connection.DateFormat.Name).Append('\n')
            .Append("set datefirst ").Append(dateFirst.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("set transaction isolation level ").Append(connection.SessionIsolationLevel switch
            {
                System.Data.IsolationLevel.ReadUncommitted => "read uncommitted",
                System.Data.IsolationLevel.RepeatableRead => "repeatable read",
                System.Data.IsolationLevel.Serializable => "serializable",
                System.Data.IsolationLevel.Snapshot => "snapshot",
                _ => "read committed",
            }).Append('\n');
        return builder.ToString();
    }

    /// <summary><c>attention</c> as a cancel or a command timeout ends a request, the request's elapsed time its duration.</summary>
    public static void Attention(SimulatedDbConnection connection)
    {
        if (!XeEvents.Takes(connection.Simulation, XeEventKind.Attention))
            return;
        var started = connection.Session.RequestStartUtc;
        XeEvents.Fire(connection, XeEventKind.Attention, @event => XeEvents.Data(@event,
            ("duration", XeValue.FromUnsigned(started == default ? 0 : (ulong)Math.Max(0, (DateTime.UtcNow - started).TotalMicroseconds))),
            ("request_id", XeValue.FromUnsigned(0))));
    }

    /// <summary>
    /// <c>wait_info</c> (its <c>End</c>) and <c>wait_completed</c> as a wait
    /// ends: a lock wait as its lock's <c>LCK_M_*</c> type, granted
    /// (<c>wait_result</c> 0); a <c>WAITFOR</c> as <c>WAITFOR</c>, timed
    /// out (258) — the duration in milliseconds, as real reports both
    /// (probed 2026-10-16 against SQL Server 2025). <c>system_health</c> takes
    /// the lock waits past thirty seconds.
    /// </summary>
    public static void WaitEnded(SimulatedDbConnection connection, LockMode? lockMode, long milliseconds)
    {
        var simulation = connection.Simulation;
        // system_health's predicate takes no wait of fifteen seconds or less.
        if (simulation.ExtendedEventsIfMade is null && milliseconds <= 15000)
            return;
        var waitType = lockMode is { } mode ? DeadlockReport.ModeKey(mode) : 308;
        var result = lockMode is null ? 258UL : 0UL;
        XeEvents.Fire(connection, XeEventKind.WaitInfo, @event => XeEvents.Data(@event,
            ("wait_type", XeValue.FromMap(waitType, null)),
            ("opcode", XeValue.FromMap(1, null)),
            ("duration", XeValue.FromUnsigned((ulong)Math.Max(0, milliseconds))),
            ("signal_duration", XeValue.FromUnsigned(0)),
            ("wait_resource", XeValue.FromPointer(0)),
            ("wait_resource_desc", XeValue.FromUnicode(""))));
        if (!XeEvents.Takes(simulation, XeEventKind.WaitCompleted))
            return;
        XeEvents.Fire(connection, XeEventKind.WaitCompleted, @event => XeEvents.Data(@event,
            ("wait_type", XeValue.FromMap(waitType, null)),
            ("wait_result", XeValue.FromUnsigned(result)),
            ("duration", XeValue.FromUnsigned((ulong)Math.Max(0, milliseconds))),
            ("signal_duration", XeValue.FromUnsigned(0)),
            ("wait_resource", XeValue.FromUnicode(""))));
    }
}
