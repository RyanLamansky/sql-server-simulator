using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The small report procedures: sp_helplanguage, sp_helpsort, sp_helpserver,
// sp_monitor, sp_lock and sp_MSforeach_worker.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] HelpLanguageParameters =
    [
        new("language", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] HelpServerParameters =
    [
        new("server", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("optname", SqlType.NVarchar, 35, SqlValue.Null(SqlType.NVarchar)),
        new("show_topology", SqlType.NVarchar, 1, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] LockParameters =
    [
        new("spid1", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
        new("spid2", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
    ];

    private static readonly SystemProcedureParameter[] ForEachWorkerParameters =
    [
        new("command1", SqlType.NVarchar, 2000),
        new("replacechar", SqlType.NVarchar, 128, SqlValue.FromNVarchar("?")),
        new("command2", SqlType.NVarchar, 2000, SqlValue.Null(SqlType.NVarchar)),
        new("command3", SqlType.NVarchar, 2000, SqlValue.Null(SqlType.NVarchar)),
        new("worker_type", SqlType.Int32, 0, SqlValue.FromInt32(1)),
    ];

    /// <summary>
    /// <c>sp_helplanguage [@language]</c> is <c>sys.syslanguages</c>' rows, or the one language
    /// named by its official name or alias — Msg 15033 from line 41 for one that isn't there.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpLanguage(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_helplanguage", calledAs, arguments, HelpLanguageParameters);
        Language? only = null;
        if (!values[0].IsNull)
        {
            only = Language.Find(values[0].AsString.TrimEnd(' ')) ?? throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAnOfficialLanguageName(values[0].AsString), 41);
        }

        var rows = new List<SqlValue[]>();
        foreach (var language in Language.All)
        {
            if (only is not null && language != only)
                continue;
            rows.Add([
                SqlValue.FromInt16(language.LangId),
                SqlValue.FromString(SqlType.GetNChar(3), language.DateFormat),
                SqlValue.FromByte(language.DateFirst),
                SqlValue.FromInt32(0),
                SqlValue.FromNVarchar(language.Name),
                SqlValue.FromNVarchar(language.Alias),
                SqlValue.FromNVarchar(language.Months),
                SqlValue.FromNVarchar(language.ShortMonths),
                SqlValue.FromNVarchar(language.Days),
                SqlValue.FromInt32(language.Lcid),
                SqlValue.FromInt16(language.MsgLangId),
            ]);
        }
        yield return new SimulatedSqlResultSet(HelpLanguageSchema, HelpLanguageColumnNames, rows);
    }

    private static readonly SqlType[] HelpLanguageSchema =
    [
        SqlType.SmallInt, SqlType.GetNChar(3), SqlType.TinyInt, SqlType.Int32, SqlType.NVarchar, SqlType.NVarchar,
        SqlType.NVarchar, SqlType.NVarchar, SqlType.NVarchar, SqlType.Int32, SqlType.SmallInt,
    ];

    private static readonly string[] HelpLanguageColumnNames =
        ["langid", "dateformat", "datefirst", "upgrade", "name", "alias", "months", "shortmonths", "days", "lcid", "msglangid"];

    /// <summary>
    /// <c>sp_helpsort</c> describes the server collation in one row. The default,
    /// <c>SQL_Latin1_General_CP1_CI_AS</c>, is real's exact text; another collation's is
    /// composed from its name in the same words, with the SQL sort order number only where it is known.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpSort(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        _ = BindSystemProcedureArguments("sp_helpsort", calledAs, arguments, []);
        yield return new SimulatedSqlResultSet([SqlType.NVarchar], ["Server default collation"],
            [[SqlValue.FromNVarchar(DescribeCollationForHelpSort(batch.Connection.Simulation.ServerCollationName))]]);
    }

    private static string DescribeCollationForHelpSort(string name)
    {
        var isSql = name.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase);
        var upper = name.ToUpperInvariant();
        var locale = isSql ? name[4..] : name;
        var underscore = locale.IndexOf("_CP", StringComparison.OrdinalIgnoreCase);
        var localeName = underscore > 0 ? locale[..underscore] : locale;
        var description = $"{(localeName.StartsWith("Latin1_General", StringComparison.OrdinalIgnoreCase) ? "Latin1-General" : localeName.Replace('_', '-'))}, "
            + $"{(upper.Contains("_CS", StringComparison.Ordinal) ? "case-sensitive" : "case-insensitive")}, "
            + $"{(upper.Contains("_AS", StringComparison.Ordinal) ? "accent-sensitive" : "accent-insensitive")}, "
            + $"{(upper.Contains("_KS", StringComparison.Ordinal) ? "kanatype-sensitive" : "kanatype-insensitive")}, "
            + $"{(upper.Contains("_WS", StringComparison.Ordinal) ? "width-sensitive" : "width-insensitive")}";
        if (!isSql)
            return description;
        return upper == "SQL_LATIN1_GENERAL_CP1_CI_AS"
            ? description + " for Unicode Data, SQL Server Sort Order 52 on Code Page 1252 for non-Unicode Data"
            : description + " for Unicode Data";
    }

    /// <summary>
    /// <c>sp_helpserver [@server]</c> lists the instance's own row and its linked servers as
    /// <c>sys.servers</c> reports them, with <c>status</c> the comma-joined names of the options
    /// that are on and <c>id</c> padded to four characters; a name that isn't a server is
    /// Msg 15015 from line 19.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpServer(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_helpserver", calledAs, arguments, HelpServerParameters);
        var only = values[0].IsNull ? null : values[0].AsString.TrimEnd(' ');
        var rows = new List<SqlValue[]>();
        var nullNetwork = SqlValue.Null(SqlType.GetChar(30));
        foreach (var row in BuiltInResources.EnumerateSysServers(batch, batch.CurrentDatabase))
        {
            var name = row[1].AsString;
            if (only is not null && !BuiltInToken.Comparer.Equals(name, only))
                continue;
            var status = new List<string>(6);
            if (row[11].AsBoolean)
                status.Add("rpc");
            if (row[19].AsBoolean)
                status.Add("pub");
            if (row[20].AsBoolean)
                status.Add("sub");
            if (row[21].AsBoolean)
                status.Add("dist");
            if (row[12].AsBoolean)
                status.Add("rpc out");
            if (row[13].AsBoolean)
                status.Add("data access");
            if (row[14].AsBoolean)
                status.Add("collation compatible");
            if (row[15].AsBoolean)
                status.Add("use remote collation");
            if (row[17].AsBoolean)
                status.Add("lazy schema validation");
            rows.Add([
                SqlValue.FromNVarchar(name),
                string.Equals(row[2].AsString, "SQL Server", StringComparison.OrdinalIgnoreCase) && !row[4].IsNull ? SqlValue.FromString(SqlType.GetChar(30), row[4].AsString) : nullNetwork,
                SqlValue.FromVarchar(string.Join(',', status)),
                SqlValue.FromString(SqlType.GetChar(4), row[0].AsInt32.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                row[16].IsNull ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(row[16].AsString),
                row[8],
                row[9],
            ]);
        }
        if (only is not null && rows.Count == 0)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.ServerDoesNotExistForHelp(values[0].AsString.TrimEnd(' ')), 19);
        yield return new SimulatedSqlResultSet(
            [SqlType.NVarchar, SqlType.GetChar(30), SqlType.Varchar, SqlType.GetChar(4), SqlType.NVarchar, SqlType.Int32, SqlType.Int32],
            ["name", "network_name", "status", "id", "collation_name", "connect_timeout", "query_timeout"], rows);
    }

    private static long monitorLastRunTicks;

    /// <summary>
    /// <c>sp_monitor</c> reports the instance's activity since it last ran: four result sets of
    /// <c>total(since last run)</c> figures. The figures are the host process's own, not an
    /// engine's; each call moves the last-run mark to now.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMonitor(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        _ = BindSystemProcedureArguments("sp_monitor", calledAs, arguments, []);
        var simulation = batch.Connection.Simulation;
        var now = DateTime.UtcNow;
        var lastRun = new DateTime(Interlocked.Exchange(ref monitorLastRunTicks, now.Ticks) is var previous and > 0 ? previous : simulation.SeedDate.Ticks, DateTimeKind.Utc);
        var seconds = (int)Math.Max(0, (now - lastRun).TotalSeconds);
        var cpu = (long)System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        var elapsed = (long)(now - simulation.SeedDate).TotalMilliseconds;
        var connections = simulation.SnapshotConnections().Length;
        static SqlValue Figure(long total, long since, string suffix = "") => SqlValue.FromVarchar($"{total}({since}){suffix}");
        yield return new SimulatedSqlResultSet([SqlType.DateTime, SqlType.DateTime, SqlType.Int32], ["last_run", "current_run", "seconds"],
            [[SqlValue.FromDateTime(lastRun), SqlValue.FromDateTime(now), SqlValue.FromInt32(seconds)]]);
        yield return new SimulatedSqlResultSet([SqlType.Varchar, SqlType.Varchar, SqlType.Varchar], ["cpu_busy", "io_busy", "idle"],
            [[Figure(cpu, 0, "-0%"), Figure(0, 0, "-0%"), Figure(Math.Max(0, elapsed - cpu), 0, "-0%")]]);
        yield return new SimulatedSqlResultSet([SqlType.Varchar, SqlType.Varchar, SqlType.Varchar], ["packets_received", "packets_sent", "packet_errors"],
            [[Figure(0, 0), Figure(0, 0), Figure(0, 0)]]);
        yield return new SimulatedSqlResultSet([SqlType.Varchar, SqlType.Varchar, SqlType.Varchar, SqlType.Varchar], ["total_read", "total_write", "total_errors", "connections"],
            [[Figure(0, 0), Figure(0, 0), Figure(0, 0), Figure(connections, 0)]]);
    }

    /// <summary>
    /// <c>sp_lock [@spid1 [, @spid2]]</c> lists the locks held or awaited by every session, or by
    /// the one or two named, as <c>sys.dm_tran_locks</c> reports them plus the shared lock each
    /// session holds on its current database. Real also lists the locks the procedure's own
    /// metadata reads take, which nothing here does.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpLock(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_lock", calledAs, arguments, LockParameters);
        var simulation = batch.Connection.Simulation;
        var rows = new List<(int Spid, int DbId, int ObjId, SqlValue[] Row)>();
        var blankResource = SqlValue.FromNVarchar(new string(' ', 32));
        foreach (var connection in simulation.SnapshotConnections())
        {
            if (connection.CurrentDatabase is not { } current)
                continue;
            rows.Add((connection.Spid, SmallDatabaseId(simulation, current), 0, [
                SqlValue.FromInt16((short)connection.Spid), SqlValue.FromInt16(SmallDatabaseId(simulation, current)), SqlValue.FromInt32(0), SqlValue.FromInt16(0),
                SqlValue.FromNVarchar("DB"), blankResource, SqlValue.FromNVarchar("S"), SqlValue.FromNVarchar("GRANT"),
            ]));
        }
        foreach (var row in LockDmvs.EnumerateDmTranLocks(batch, batch.CurrentDatabase))
        {
            var type = row[0].AsString switch
            {
                "OBJECT" => "TAB",
                "APPLICATION" => "APP",
                var other => other,
            };
            var spid = row[6].AsInt32;
            var entity = row[3].IsNull ? 0 : (int)row[3].AsInt64;
            rows.Add((spid, row[1].AsInt32, entity, [
                SqlValue.FromInt16((short)spid), SqlValue.FromInt16((short)row[1].AsInt32), SqlValue.FromInt32(entity), SqlValue.FromInt16(0),
                SqlValue.FromNVarchar(type), type is "TAB" ? blankResource : SqlValue.FromNVarchar(row[2].AsString), SqlValue.FromNVarchar(row[4].AsString), SqlValue.FromNVarchar(row[5].AsString),
            ]));
        }
        var first = values[0].IsNull ? (int?)null : values[0].AsInt32;
        var second = values[1].IsNull ? (int?)null : values[1].AsInt32;
        var chosen = rows.FindAll(row => (first is null && second is null) || row.Spid == first || row.Spid == second);
        chosen.Sort(static (a, b) => a.Spid != b.Spid ? a.Spid.CompareTo(b.Spid) : a.DbId != b.DbId ? a.DbId.CompareTo(b.DbId) : a.ObjId.CompareTo(b.ObjId));
        yield return new SimulatedSqlResultSet(
            [SqlType.SmallInt, SqlType.SmallInt, SqlType.Int32, SqlType.SmallInt, SqlType.NVarchar, SqlType.NVarchar, SqlType.NVarchar, SqlType.NVarchar],
            ["spid", "dbid", "ObjId", "IndId", "Type", "Resource", "Mode", "Status"], chosen.ConvertAll(static row => row.Row));
    }

    /// <summary>
    /// <c>sp_MSforeach_worker @command1 [, @replacechar [, @command2 [, @command3 [, @worker_type]]]]</c>
    /// runs its commands for each row of a global cursor the procedures that call it
    /// (<c>sp_MSforeachtable</c>, <c>sp_MSforeachdb</c>) declare first. Those procedures here
    /// materialize their lists instead, so no such cursor exists to drive: a direct call meets
    /// what real's does without one — four errors, each statement of the worker that reads the
    /// cursor failing in turn.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpMsForEachWorker(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        _ = BindSystemProcedureArguments("sp_MSforeach_worker", calledAs, arguments, ForEachWorkerParameters);
        List<SimulatedSqlException> errors =
        [
            AtSystemProcedureLine(calledAs, SimulatedSqlException.CursorVariableNotAllocated("local_cursor"), 27),
            AtSystemProcedureLine(calledAs, SimulatedSqlException.CursorVariableNotAllocated("local_cursor"), 32),
            AtSystemProcedureLine(calledAs, SimulatedSqlException.CursorVariableNotAllocated("local_cursor"), 156),
            AtSystemProcedureLine(calledAs, SimulatedSqlException.CursorDoesNotExist("hCForEachDatabase"), 158),
        ];
        throw SimulatedSqlException.Aggregate(errors);
    }
}
