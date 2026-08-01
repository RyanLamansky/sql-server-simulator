using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_who / sp_who2 — the session lists. Both project the live connection
// registry (one row per open SimulatedDbConnection) the way real projects
// sys.sysprocesses; the lock manager supplies the blocking spid. Column names,
// types, ordering, the status / cmd vocabulary and sp_who2's dynamic column
// widths are probe-confirmed against SQL Server 2025 (2026-07-31).
partial class Simulation
{
    private static readonly NCharSqlType WhoStatusType =
        NCharSqlType.Get(30, Collation.Baseline, Coercibility.Implicit);

    private static readonly NCharSqlType WhoHostNameType =
        NCharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);

    private static readonly NCharSqlType WhoCommandType =
        NCharSqlType.Get(26, Collation.Baseline, Coercibility.Implicit);

    private static readonly CharSqlType WhoBlockedType =
        CharSqlType.Get(5, Collation.Baseline, Coercibility.Implicit);

    private static readonly NVarcharSqlType WhoNameType =
        NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] SpWhoSchema =
    [
        SqlType.SmallInt,  // spid
        SqlType.SmallInt,  // ecid
        WhoStatusType,     // status
        WhoNameType,       // loginame
        WhoHostNameType,   // hostname
        WhoBlockedType,    // blk
        WhoNameType,       // dbname
        WhoCommandType,    // cmd
        SqlType.Int32,     // request_id
    ];

    private static readonly string[] SpWhoColumnNames =
        ["spid", "ecid", "status", "loginame", "hostname", "blk", "dbname", "cmd", "request_id"];

    private static readonly string[] SpWho2ColumnNames =
    [
        "SPID", "Status", "Login", "HostName", "BlkBy", "DBName", "Command",
        "CPUTime", "DiskIO", "LastBatch", "ProgramName", "SPID", "REQUESTID",
    ];

    // The idle-session command real reports for a connection with no statement
    // in flight, and the one it reports for the session running sp_who itself
    // (real's sp_who body is a SELECT over sysprocesses, so the observing
    // session always sees itself running a SELECT).
    private const string WhoIdleCommand = "AWAITING COMMAND";
    private const string WhoSelfCommand = "SELECT";

    /// <summary>
    /// Handles <c>EXEC sp_who [@loginame]</c> — one nine-column row per live
    /// session: <c>spid smallint</c>, <c>ecid smallint</c>, <c>status
    /// nchar(30)</c>, <c>loginame nvarchar(128)</c>, <c>hostname
    /// nchar(128)</c>, <c>blk char(5)</c>, <c>dbname nvarchar(128)</c>,
    /// <c>cmd nchar(26)</c>, <c>request_id int</c>. Rows sort by spid.
    /// <c>@loginame</c> selects: a numeric argument is a spid, the literal
    /// <c>'active'</c> drops the sessions whose <c>cmd</c> is
    /// <c>AWAITING COMMAND</c>, anything else is a login name (unknown → Msg
    /// 15007).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpWho(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var filter = ParseWhoFilter(arguments, "sp_who");
        var rows = new List<SqlValue[]>();
        foreach (var session in SessionSnapshot(batch, filter))
        {
            rows.Add([
                SqlValue.FromInt16((short)session.Spid),
                SqlValue.FromInt16(0),
                SqlValue.FromString(WhoStatusType, session.Status),
                SqlValue.FromString(WhoNameType, session.Login),
                SqlValue.FromString(WhoHostNameType, session.HostName),
                SqlValue.FromString(WhoBlockedType, session.BlockedBy.ToString(CultureInfo.InvariantCulture)),
                SqlValue.FromString(WhoNameType, session.DatabaseName),
                SqlValue.FromString(WhoCommandType, session.Command),
                SqlValue.FromInt32(0),
            ]);
        }

        yield return new SimulatedSqlResultSet(SpWhoSchema, SpWhoColumnNames, rows);
    }

    /// <summary>
    /// Handles <c>EXEC sp_who2 [@loginame]</c> — the wider session list, with
    /// the same selection rules <see cref="InvokeSpWho"/> applies. Thirteen
    /// columns, two of them named <c>SPID</c> (real repeats it as a
    /// right-scrolling convenience). Real's proc builds this set through a
    /// generated <c>EXEC()</c> whose <c>substring(col, 1, N)</c> widths are
    /// the measured maximum data lengths of the rows being reported, so the
    /// column types vary with the data; that measurement is reproduced here,
    /// including its per-column floor and its use of <em>byte</em> length for
    /// <c>Login</c> (real's <c>datalength</c> over an nvarchar) against
    /// character length for the rest.
    /// </summary>
    /// <remarks>
    /// <c>CPUTime</c> and <c>DiskIO</c> report <c>0</c>: the simulator meters
    /// neither CPU nor physical I/O per session, and <c>0</c> is what real
    /// reports for a session that has done neither. <c>ProgramName</c> is the
    /// empty string and <c>HostName</c> renders as real's <c>"  ."</c>
    /// placeholder, since no client program / host name reaches the session.
    /// <c>LastBatch</c> is the session's login instant in real's
    /// <c>MM/DD hh:mm:ss</c> rendering — the simulator's nearest analogue to
    /// the last-batch timestamp.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpWho2(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var filter = ParseWhoFilter(arguments, "sp_who2");
        var sessions = SessionSnapshot(batch, filter);

        // Real's width measurement: isnull(max(<length>), <floor>) per column,
        // so a zero-row report falls back to the floors and a populated one
        // takes the widest value even when that is narrower than the floor.
        var loginWidth = WhoColumnWidth(sessions, 5, static s => s.Login.Length * 2);
        var hostWidth = WhoColumnWidth(sessions, 8, static s => s.HostName.Length);
        var databaseWidth = WhoColumnWidth(sessions, 6, static s => s.DatabaseName.Length);
        var commandWidth = WhoColumnWidth(sessions, 7, static s => s.Command.Length);
        var cpuWidth = WhoColumnWidth(sessions, 7, static _ => 1);
        var diskWidth = WhoColumnWidth(sessions, 6, static _ => 1);
        var lastBatchWidth = WhoColumnWidth(sessions, 9, static _ => WhoLastBatchWidth);
        var programWidth = WhoColumnWidth(sessions, 11, static _ => 0);

        var spidType = CharSqlType.Get(5, Collation.Baseline, Coercibility.Implicit);
        var loginType = NVarcharSqlType.Get(loginWidth, Collation.Baseline, Coercibility.Implicit);
        // The HostName arm is a CASE between real's varchar(3) placeholder and
        // a substring of the nchar host name, so the result widens to whichever
        // of the two is longer.
        var hostType = NVarcharSqlType.Get(Math.Max(hostWidth, 3), Collation.Baseline, Coercibility.Implicit);
        var databaseType = NVarcharSqlType.Get(databaseWidth, Collation.Baseline, Coercibility.Implicit);
        var commandType = NVarcharSqlType.Get(commandWidth, Collation.Baseline, Coercibility.Implicit);
        var cpuType = VarcharSqlType.Get(cpuWidth, Collation.Baseline, Coercibility.Implicit);
        var diskType = VarcharSqlType.Get(diskWidth, Collation.Baseline, Coercibility.Implicit);
        var lastBatchType = VarcharSqlType.Get(lastBatchWidth, Collation.Baseline, Coercibility.Implicit);
        var programType = NVarcharSqlType.Get(programWidth, Collation.Baseline, Coercibility.Implicit);

        SqlType[] schema =
        [
            spidType, WhoStatusType, loginType, hostType, spidType, databaseType, commandType,
            cpuType, diskType, lastBatchType, programType, spidType, spidType,
        ];

        var zero = "0";
        var rows = new List<SqlValue[]>(sessions.Count);
        foreach (var session in sessions)
        {
            var spid = SqlValue.FromString(spidType, session.Spid.ToString(CultureInfo.InvariantCulture));
            rows.Add([
                spid,
                // Real lower-cases 'sleeping' and upper-cases everything else.
                SqlValue.FromString(WhoStatusType,
                    session.Status == "sleeping" ? session.Status : session.Status.ToUpperInvariant()),
                SqlValue.FromString(loginType, Truncate(session.Login, loginWidth)),
                SqlValue.FromString(hostType, session.HostName.Length == 0 ? "  ." : Truncate(session.HostName, hostWidth)),
                SqlValue.FromString(spidType,
                    session.BlockedBy == 0 ? "  ." : session.BlockedBy.ToString(CultureInfo.InvariantCulture)),
                SqlValue.FromString(databaseType, Truncate(session.DatabaseName, databaseWidth)),
                SqlValue.FromString(commandType, Truncate(session.Command, commandWidth)),
                SqlValue.FromString(cpuType, Truncate(zero, cpuWidth)),
                SqlValue.FromString(diskType, Truncate(zero, diskWidth)),
                SqlValue.FromString(lastBatchType, Truncate(WhoLastBatch(session.LoginTimeUtc), lastBatchWidth)),
                SqlValue.FromString(programType, ""),
                spid,
                SqlValue.FromString(spidType, "0"),
            ]);
        }

        yield return new SimulatedSqlResultSet(schema, SpWho2ColumnNames, rows);
    }

    // Real's `MM/DD hh:mm:ss` LastBatch rendering — style 111's date part with
    // the year dropped, then style 113's time part.
    private const int WhoLastBatchWidth = 14;

    private static string WhoLastBatch(DateTime loginTimeUtc) =>
        loginTimeUtc.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..width];

    private static int WhoColumnWidth(List<WhoSession> sessions, int floor, Func<WhoSession, int> length)
    {
        if (sessions.Count == 0)
            return floor;
        var max = 0;
        foreach (var session in sessions)
            max = Math.Max(max, length(session));
        // A zero-length measurement is real's NULL / empty case, which
        // isnull(...) replaces with the floor; the substring width also has to
        // stay at least 1 for the type to be legal.
        return max == 0 ? floor : max;
    }

    /// <summary>
    /// One row's worth of session facts, snapshotted from the connection
    /// registry so <c>sp_who</c> and <c>sp_who2</c> report the same instant
    /// and the same selection.
    /// </summary>
    private readonly struct WhoSession(
        int spid, string status, string login, string hostName, int blockedBy,
        string databaseName, string command, DateTime loginTimeUtc)
    {
        public readonly int Spid = spid;
        public readonly string Status = status;
        public readonly string Login = login;
        public readonly string HostName = hostName;
        public readonly int BlockedBy = blockedBy;
        public readonly string DatabaseName = databaseName;
        public readonly string Command = command;
        public readonly DateTime LoginTimeUtc = loginTimeUtc;
    }

    // The three shapes @loginame takes: every session, one spid, one login, or
    // real's 'active' filter.
    private readonly struct WhoFilter(int? spid, string? login, bool activeOnly)
    {
        public readonly int? Spid = spid;
        public readonly string? Login = login;
        public readonly bool ActiveOnly = activeOnly;
    }

    private static List<WhoSession> SessionSnapshot(BatchContext batch, WhoFilter filter)
    {
        var simulation = batch.Connection.Simulation;
        var connections = simulation.SnapshotConnections();

        // Real resolves @loginame through suser_sid() before selecting, so an
        // unrecognized name is Msg 15007 while a known login with no live
        // session simply reports nothing. The login registry plus sa (the fixed
        // login sys.server_principals carries) and the logins the live sessions
        // report are the simulator's whole login evidence.
        if (filter.Login is { } loginName && !WhoLoginExists(simulation, connections, loginName))
            throw SimulatedSqlException.HelpLoginIsNotValid(loginName);

        var sessions = new List<WhoSession>(connections.Length);
        foreach (var connection in connections)
        {
            var login = connection.Security.OriginalLoginName;
            if (filter.Login is { } wanted && !BuiltInToken.Equals(login, wanted))
                continue;
            if (filter.Spid is { } spid && connection.Spid != spid)
                continue;

            var isSelf = ReferenceEquals(connection, batch.Connection);
            var blockedBy = WhoBlockingSpid(connection);
            var status = blockedBy != 0 || connection.WaitingOnResource is not null
                ? "suspended"
                : isSelf || connection.CurrentExecutingThreadId is not null ? "runnable" : "sleeping";
            var command = isSelf || connection.CurrentExecutingThreadId is not null
                ? WhoSelfCommand
                : WhoIdleCommand;
            if (filter.ActiveOnly && command == WhoIdleCommand)
                continue;

            sessions.Add(new WhoSession(
                connection.Spid, status, login, hostName: "", blockedBy,
                connection.CurrentDatabase.Name, command, connection.LoginTimeUtc));
        }

        sessions.Sort(static (a, b) => a.Spid.CompareTo(b.Spid));
        return sessions;
    }

    private static bool WhoLoginExists(Simulation simulation, SimulatedDbConnection[] connections, string loginName)
    {
        if (BuiltInToken.Equals(loginName, "sa") || simulation.Logins.ContainsKey(loginName))
            return true;
        foreach (var connection in connections)
        {
            if (BuiltInToken.Equals(connection.Security.OriginalLoginName, loginName))
                return true;
        }

        return false;
    }

    // The spid of one connection holding a lock incompatible with what this
    // connection is waiting for — the same attribution sys.dm_os_waiting_tasks
    // makes. Zero when the session isn't blocked, which is real's `blocked = 0`.
    private static int WhoBlockingSpid(SimulatedDbConnection connection)
    {
        if (connection.WaitingOnResource is not { } resource || connection.WaitingForMode is not { } mode)
            return 0;
        foreach (var hold in resource.Holders)
        {
            if (!ReferenceEquals(hold.Owner, connection) && !LockManager.IsCompatible(hold.Mode, mode))
                return hold.Owner.Spid;
        }

        return 0;
    }

    private static WhoFilter ParseWhoFilter(List<ProcArgument> arguments, string procedureName)
    {
        string? loginName = null;
        var positional = 0;
        foreach (var arg in arguments)
        {
            if (arg.Name is null)
            {
                if (positional++ != 0)
                    throw SimulatedSqlException.InvalidProcedureParameters(procedureName);
                loginName = CatalogStringArg(arg);
                continue;
            }

            if (!BuiltInToken.Equals(arg.Name, "loginame"))
                throw SimulatedSqlException.InvalidProcedureParameters(procedureName);
            loginName = CatalogStringArg(arg);
        }

        return loginName is null ? default
            : BuiltInToken.Equals(loginName, "active") ? new WhoFilter(null, null, activeOnly: true)
            : int.TryParse(loginName, NumberStyles.None, CultureInfo.InvariantCulture, out var spid) ? new WhoFilter(spid, null, activeOnly: false)
            : new WhoFilter(null, loginName, activeOnly: false);
    }
}
