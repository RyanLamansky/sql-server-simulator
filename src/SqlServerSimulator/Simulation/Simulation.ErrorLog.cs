using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>The instance's SQL Server error log, which the error-log procedures read and cycle and the logged messages write.</summary>
    internal readonly ErrorLog ErrorLog;

    private static readonly NVarcharSqlType ErrorLogProcessInfoType = NVarcharSqlType.Get(12, Collation.Baseline, Coercibility.Implicit);
    private static readonly NVarcharSqlType ErrorLogTextType = NVarcharSqlType.Get(3999, Collation.Baseline, Coercibility.Implicit);
    private static readonly NVarcharSqlType ErrorLogDateType = NVarcharSqlType.Get(259, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] ReadErrorLogSchema = [SqlType.DateTime, ErrorLogProcessInfoType, ErrorLogTextType];
    private static readonly string[] ReadErrorLogColumnNames = ["LogDate", "ProcessInfo", "Text"];
    private static readonly SqlType[] EnumErrorLogsSchema = [SqlType.Int32, ErrorLogDateType, SqlType.Int32];
    private static readonly string[] EnumErrorLogsColumnNames = ["Archive #", "Date", "Log File Size (Byte)"];

    private static readonly SystemProcedureParameter[] SpReadErrorLogParameters =
    [
        new("p1", SqlType.Int32, 0, SqlValue.FromInt32(0)),
        new("p2", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
        new("p3", SqlType.NVarchar, 4000, SqlValue.Null(SqlType.NVarchar)),
        new("p4", SqlType.NVarchar, 4000, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] SpEnumErrorLogsParameters =
    [
        new("p1", SqlType.Int32, 0, SqlValue.FromInt32(1)),
    ];

    /// <summary>
    /// Writes a logged error — a <c>RAISERROR … WITH LOG</c>, a registered
    /// message <c>sp_addmessage</c> marked <c>@with_log</c> — as real logs it
    /// under the raising session: through severity 10 the message alone,
    /// above it the <c>Error: n, Severity: s, State: t.</c> line first
    /// (probed 2026-10-16 against SQL Server 2025, a caught error included).
    /// </summary>
    internal static void LogRaisedError(BatchContext batch, int number, int severity, byte state, string message)
    {
        var log = batch.Connection.Simulation.ErrorLog;
        var process = ErrorLog.SessionProcess(batch.Connection.Spid);
        if (severity > 10)
            log.WriteError(batch.CurrentStatement.UtcNow, process, number, severity, state, message);
        else
            log.Write(batch.CurrentStatement.UtcNow, process, message);
    }

    /// <summary>Writes one line under the running session's process column.</summary>
    internal static void LogSessionLine(BatchContext batch, string text) =>
        batch.Connection.Simulation.ErrorLog.Write(batch.CurrentStatement.UtcNow, ErrorLog.SessionProcess(batch.Connection.Spid), text);

    /// <summary>
    /// Writes real's line for one <c>ALTER DATABASE … SET</c> option that took
    /// effect, whether or not it changed anything: <c>Setting database option
    /// RECOVERY to SIMPLE for database 'd'.</c> under the altering session
    /// (probed 2026-10-16 against SQL Server 2025). Each option's spelling of
    /// <paramref name="option"/> and <paramref name="value"/> is real's own —
    /// upper case for most, lower case for the newer ones
    /// (<c>target_recovery_time</c>, <c>query_store</c>, <c>delayed_durability</c>…).
    /// </summary>
    internal static void LogDatabaseOption(BatchContext batch, Database database, string option, string value) =>
        LogSessionLine(batch, $"Setting database option {option} to {value} for database '{database.Name}'.");

    /// <summary>
    /// Writes the lines real writes as a database starts up — on creation, on
    /// coming online, on a read-only or read-committed-snapshot change and in a
    /// restore: <c>Starting up database 'd'.</c>, then, once a <c>DBCC
    /// CHECKDB</c> has passed over it, the line recalling when (probed
    /// 2026-10-16 against SQL Server 2025). Real's diagnostic lines between
    /// them — the database id it locked, its feature status, parallel redo
    /// starting and stopping — are left out.
    /// </summary>
    internal static void LogDatabaseStartup(BatchContext batch, Database database)
    {
        LogSessionLine(batch, $"Starting up database '{database.Name}'.");
        if (database.LastGoodCheckDbUtc is { } lastGood)
        {
            LogSessionLine(batch, string.Create(CultureInfo.InvariantCulture,
                $"CHECKDB for database '{database.Name}' finished without errors on {DateTimeSqlType.RoundToClientMilliseconds(lastGood):yyyy-MM-dd HH:mm:ss.fff} (local time). This is an informational message only; no user action is required."));
        }
    }

    /// <summary>
    /// Writes real's line for a database that started up in a state recovery
    /// doesn't run in — <c>EMERGENCY_MODE</c>, <c>RESTORING</c> — after its
    /// startup line.
    /// </summary>
    internal static void LogDatabaseNotRecovered(BatchContext batch, Database database, string state) =>
        LogSessionLine(batch, $"The database '{database.Name}' is marked {state} and is in a state that does not allow recovery to be run.");

    /// <summary>
    /// The reason real's log gives a refused login, after the two lines
    /// <c>Error: 18456, Severity: 14, State: n.</c> (or Msg 18470's for a
    /// disabled login) and <c>Login failed for user '…'. Reason: … [CLIENT:
    /// …]</c> under the process column <c>Logon</c> (probed 2026-10-16
    /// against SQL Server 2025). The client is the peer address over TDS and
    /// real's <c>&lt;local machine&gt;</c> in-process.
    /// </summary>
    internal void LogLoginFailure(string userName, int number, byte state, string reason, string client)
    {
        var message = number == 18470
            ? $"Login failed for user '{userName}'. Reason: The account is disabled. [CLIENT: {client}]"
            : $"Login failed for user '{userName}'. Reason: {reason} [CLIENT: {client}]";
        this.ErrorLog.WriteError(DateTime.UtcNow, "Logon", number, 14, state, message);
    }

    /// <summary>
    /// Refuses with Msg 229 a session that may not run one of the error-log
    /// extended procedures, which <c>public</c> holds no <c>EXECUTE</c> on: a
    /// sysadmin or <c>CONTROL SERVER</c> login runs them, as does one whose
    /// <c>master</c> user is granted <c>EXECUTE</c> on the procedure, while
    /// even a database owner elsewhere is refused (probed 2026-10-16 against
    /// SQL Server 2025). The refusal names the procedure in
    /// <c>mssqlsystemresource</c> and is reported at line 1 under the name it
    /// was called by.
    /// </summary>
    private static void RequireErrorLogProcedureExecute(BatchContext batch, string procedure, string calledAs)
    {
        var connection = batch.Connection;
        var simulation = connection.Simulation;
        if (simulation.SessionHoldsServerPermission(connection, Permission.ControlServer))
            return;
        var effective = connection.Security.Effective;
        var master = simulation.Databases[MasterDatabaseName];
        if (!effective.IsDatabaseScoped
            && TryMapLoginToDatabaseUser(simulation, master, effective.LoginName, out var principal)
            && Schemas.CatalogViewObjectIds.ByProcedureName.TryGetValue(procedure, out var objectId)
            && PermissionChecker.IsGranted(master, principal.PrincipalId, Permission.Execute, PermissionChecker.ClassObject, objectId, Database.SysSchemaId))
        {
            return;
        }
        throw AtProcedureLine(SimulatedSqlException.PermissionDenied("EXECUTE", procedure, "mssqlsystemresource", "sys", calledAs), calledAs, 1);
    }

    /// <summary>
    /// Whether the session may read the log through <c>sp_readerrorlog</c> /
    /// <c>sp_enumerrorlogs</c>, which check for themselves: a
    /// <c>securityadmin</c> member, or a holder of <c>VIEW SERVER STATE</c> or
    /// <c>VIEW ANY ERROR LOG</c> (probed 2026-10-16 against SQL Server 2025:
    /// <c>VIEW SERVER PERFORMANCE STATE</c> or <c>ALTER TRACE</c> is refused).
    /// </summary>
    private static bool MayReadErrorLog(BatchContext batch)
    {
        var connection = batch.Connection;
        var simulation = connection.Simulation;
        var effective = connection.Security.Effective;
        return simulation.SessionHoldsServerPermission(connection, Permission.ViewServerState)
            || simulation.SessionHoldsServerPermission(connection, Permission.ViewAnyErrorLog)
            || (!effective.IsDatabaseScoped && simulation.IsLoginInServerRole(effective.LoginName, SecurityAdminRoleId));
    }

    /// <summary>
    /// <c>xp_readerrorlog [log [, type [, search1 [, search2 [, start [, end [, order]]]]]]]</c>:
    /// the entries of one log as <c>(LogDate, ProcessInfo, Text)</c>. The
    /// arguments are positional whatever name they are passed under. Log 0
    /// (or -1) is the current one; type 1 is SQL Server's log and 2 the
    /// Agent's, which a Linux server doesn't have. Each search string keeps
    /// entries whose text holds it, case aside; the start and end times keep
    /// those written within them, both ends included; and an order beginning
    /// <c>asc</c> or <c>desc</c> lists the log forward or backward (all probed
    /// 2026-10-16 against SQL Server 2025). A bad argument is Msg 22004 —
    /// sent without stopping the batch, entering a <c>CATCH</c> or setting
    /// <c>@@ERROR</c>, and answering return code 1 — whose severity and text
    /// tell which check failed (<see cref="ReadErrorLog"/>).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpReadErrorLog(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireErrorLogProcedureExecute(batch, "xp_readerrorlog", calledAs);
        var values = new SqlValue?[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
            values[i] = arguments[i].IsDefault ? SqlValue.Null(SqlType.Int32) : arguments[i].Value;
        yield return ReadErrorLog(batch, values);
    }

    /// <summary>
    /// <c>sp_readerrorlog [@p1 [, @p2 [, @p3 [, @p4]]]]</c>: real's T-SQL
    /// wrapper, which checks <see cref="MayReadErrorLog"/> (Msg 27219 at its
    /// line 11 otherwise, return code 1) and passes <c>@p1</c> alone to
    /// <c>xp_readerrorlog</c> when <c>@p2</c> is NULL, all four otherwise —
    /// answering 0 whatever the extended procedure answered.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpReadErrorLog(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_readerrorlog", calledAs, arguments, SpReadErrorLogParameters);
        if (!MayReadErrorLog(batch))
            throw SimulatedSqlException.ErrorLogProcedurePermissionDenied();
        yield return ReadErrorLog(batch, values[1].IsNull ? [values[0]] : [values[0], values[1], values[2], values[3]]);
        SetReturnCode(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// The result set, or the Msg 22004 refusal, of <c>xp_readerrorlog</c>
    /// over <paramref name="arguments"/> (null where none was passed). The
    /// checks run in the order real's answers to mixed bad arguments imply:
    /// the log number's type, the log type's type and value, the order, the
    /// two times and then the search strings, and last whether the log exists
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// The text reads back as real's reader reads its UTF-8 file, a byte to a
    /// character, so <c>ü</c> reads <c>Ã¼</c>; and the last entry of a log
    /// carries the file's closing CRLF.
    /// </remarks>
    private static SimulatedStatementOutcome ReadErrorLog(BatchContext batch, SqlValue?[] arguments)
    {
        SqlValue? Argument(int index) => index < arguments.Length ? arguments[index] : null;

        var number = 0;
        if (Argument(0) is { } first)
        {
            if (!IsErrorLogInteger(first))
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterType());
            number = first.CoerceTo(SqlType.Int32).AsInt32;
        }
        var type = 1;
        if (Argument(1) is { } second)
        {
            if (!IsErrorLogInteger(second))
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterType());
            type = second.CoerceTo(SqlType.Int32).AsInt32;
            if (type == 3)
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameter());
            if (type is not (1 or 2))
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterValue());
        }
        var descending = false;
        if (Argument(6) is { IsNull: false } order)
        {
            if (order.Type.Category != SqlTypeCategory.String)
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterType());
            var text = order.AsString;
            descending = text.StartsWith("desc", StringComparison.OrdinalIgnoreCase);
            if (!descending && !text.StartsWith("asc", StringComparison.OrdinalIgnoreCase))
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterValue());
        }
        DateTime? start = null, end = null;
        for (var i = 4; i <= 5; i++)
        {
            if (Argument(i) is not { IsNull: false } bound)
                continue;
            if (!TryReadErrorLogTime(bound, out var time, out var refusal))
                return ExtendedProcedureRefusal(batch, refusal!);
            if (i == 4)
                start = time;
            else
                end = time;
        }
        string? search1 = null, search2 = null;
        for (var i = 2; i <= 3; i++)
        {
            if (Argument(i) is not { IsNull: false } bound)
                continue;
            if (bound.Type is not (NVarcharSqlType or NCharSqlType))
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterType());
            if (i == 2)
                search1 = bound.AsString;
            else
                search2 = bound.AsString;
        }
        if (number < -1)
            return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterValue());
        if (type == 2 || batch.Connection.Simulation.ErrorLog.Read(Math.Max(number, 0)) is not { } entries)
            return ExtendedProcedureRefusal(batch, SimulatedSqlException.ErrorLogFileNotFound());

        var rows = new List<SqlValue[]>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var text = entry.Text;
            if ((search1 is { Length: > 0 } && (text is null || !text.Contains(search1, StringComparison.OrdinalIgnoreCase)))
                || (search2 is { Length: > 0 } && (text is null || !text.Contains(search2, StringComparison.OrdinalIgnoreCase)))
                || entry.LogDate < start || entry.LogDate > end)
            {
                continue;
            }
            var read = AsErrorLogReaderReadsIt(text, last: i == entries.Length - 1);
            rows.Add([SqlValue.FromDateTime(entry.LogDate), SqlValue.FromNVarchar(ErrorLogProcessInfoType, entry.ProcessInfo),
                read is null ? SqlValue.Null(ErrorLogTextType) : SqlValue.FromNVarchar(ErrorLogTextType, read)]);
        }
        if (descending)
            rows.Reverse();
        return new SimulatedSqlResultSet(ReadErrorLogSchema, ReadErrorLogColumnNames, rows);
    }

    /// <summary>Whether an <c>xp_readerrorlog</c> number argument is one it reads: a non-NULL <c>int</c>, <c>smallint</c> or <c>tinyint</c>.</summary>
    private static bool IsErrorLogInteger(SqlValue value) =>
        !value.IsNull && (value.Type == SqlType.Int32 || value.Type == SqlType.SmallInt || value.Type == SqlType.TinyInt);

    /// <summary>
    /// Reads a start or end time: a <c>datetime</c>, <c>smalldatetime</c> or
    /// <c>date</c>, or a string in the formats of a us_english session's
    /// <c>mdy</c> whatever the session's own <c>SET DATEFORMAT</c>, an empty
    /// one setting no bound. Another date-time type or an unreadable string
    /// is the date-filter refusal, any other type the parameter-type one
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static bool TryReadErrorLogTime(SqlValue value, out DateTime? time, out SimulatedSqlException? refusal)
    {
        time = null;
        refusal = null;
        switch (value.Type)
        {
            case DateTimeSqlType or SmallDateTimeSqlType or DateSqlType:
                time = value.CoerceTo(SqlType.DateTime).AsDateTime;
                return true;
            case { Category: SqlTypeCategory.String }:
                if (string.IsNullOrWhiteSpace(value.AsString))
                    return true;
                var order = DateOrder.Current;
                try
                {
                    DateOrder.Current = DateOrder.Mdy;
                    time = value.CoerceTo(SqlType.DateTime).AsDateTime;
                    return true;
                }
                catch (SimulatedSqlException)
                {
                    refusal = SimulatedSqlException.ErrorLogDateFilterIncorrect();
                    return false;
                }
                finally
                {
                    DateOrder.Current = order;
                }
            case { Category: SqlTypeCategory.DateTime }:
                refusal = SimulatedSqlException.ErrorLogDateFilterIncorrect();
                return false;
            default:
                refusal = SimulatedSqlException.ExtendedProcedureInvalidParameterType();
                return false;
        }
    }

    /// <summary>
    /// An entry's text as real's reader returns it: the UTF-8 file read a
    /// byte to a character, and the closing CRLF of the file kept on its last
    /// entry.
    /// </summary>
    private static string? AsErrorLogReaderReadsIt(string? text, bool last)
    {
        if (text is not null && !Ascii.IsValid(text))
            text = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));
        return last ? text + "\r\n" : text;
    }

    /// <summary>
    /// <c>xp_enumerrorlogs [type]</c>: one row per log of type 1 — its number,
    /// its last write as <c>MM/dd/yyyy  HH:mm</c> and its size in bytes — and
    /// no result set at all for type 2, the Agent's. Another type is Msg 22004
    /// as <c>xp_readerrorlog</c> sends it, type 3 (and a second argument)
    /// reporting a parameter type (probed 2026-10-16 against SQL Server 2025).
    /// Real lists the logs in its directory's order; the simulator lists them
    /// by number.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpEnumErrorLogs(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireErrorLogProcedureExecute(batch, "xp_enumerrorlogs", calledAs);
        if (EnumErrorLogs(batch, arguments.Count switch
        {
            0 => SqlValue.FromInt32(1),
            1 => arguments[0].IsDefault ? SqlValue.Null(SqlType.Int32) : arguments[0].Value,
            _ => null,
        }) is { } outcome)
        {
            yield return outcome;
        }
    }

    /// <summary>
    /// <c>sp_enumerrorlogs [@p1]</c>: real's wrapper, checking
    /// <see cref="MayReadErrorLog"/> (Msg 27219 at its line 8 otherwise) and
    /// answering 0 whatever <c>xp_enumerrorlogs</c> answered.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpEnumErrorLogs(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_enumerrorlogs", calledAs, arguments, SpEnumErrorLogsParameters);
        if (!MayReadErrorLog(batch))
            throw SimulatedSqlException.ErrorLogProcedurePermissionDenied();
        if (EnumErrorLogs(batch, values[0]) is { } outcome)
            yield return outcome;
        SetReturnCode(batch, returnCodeVar, 0);
    }

    private static SimulatedStatementOutcome? EnumErrorLogs(BatchContext batch, SqlValue? type)
    {
        if (type is not { } bound || !IsErrorLogInteger(bound))
            return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterType());
        switch (bound.CoerceTo(SqlType.Int32).AsInt32)
        {
            case 1:
                break;
            case 2:
                return null;
            case 3:
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterType());
            default:
                return ExtendedProcedureRefusal(batch, SimulatedSqlException.ExtendedProcedureInvalidParameterValue());
        }
        var logs = batch.Connection.Simulation.ErrorLog.Enumerate();
        var rows = new List<SqlValue[]>(logs.Length);
        foreach (var (number, lastWrite, bytes) in logs)
        {
            rows.Add([SqlValue.FromInt32(number), SqlValue.FromNVarchar(ErrorLogDateType, lastWrite.ToString("MM/dd/yyyy  HH:mm", CultureInfo.InvariantCulture)),
                SqlValue.FromInt32((int)Math.Min(bytes, int.MaxValue))]);
        }
        return new SimulatedSqlResultSet(EnumErrorLogsSchema, EnumErrorLogsColumnNames, rows);
    }

    /// <summary>
    /// <c>xp_logevent error_number, 'message' [, 'informational' | 'warning' | 'error']</c>:
    /// writes the message to the log under the calling session — alone when
    /// informational, after an <c>Error: n, Severity: 15</c> (warning) or
    /// <c>16</c> (error) <c>, State: 1.</c> line otherwise, an empty message
    /// as a NULL text — and answers 0. A bad call writes nothing, answers 1
    /// and sends one class-0 message numbered 0, checked in this order: two
    /// or three arguments with an <c>int</c> first (<c>Invalid
    /// Parameter.</c>), a number past 50000, a non-NULL <c>char</c> /
    /// <c>varchar</c> / <c>nchar</c> / <c>nvarchar</c> message short of
    /// <c>max</c> and severity (<c>Invalid Parameter Type</c>), and one of the
    /// three severities, case aside (all probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpLogEvent(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireErrorLogProcedureExecute(batch, "xp_logevent", calledAs);
        var failure = LogEvent(batch, arguments);
        if (failure is not null)
            batch.Connection.PendingMessages.Enqueue(new SimulatedError(0, 0, failure, 0, "", batch.Connection.DataSource, "SqlServerSimulator", 0));
        SetReturnCode(batch, returnCodeVar, failure is null ? 0 : 1);
    }

    /// <summary>The message <c>xp_logevent</c> refuses <paramref name="arguments"/> with, or null once it has written the entry.</summary>
    private static string? LogEvent(BatchContext batch, List<ProcArgument> arguments)
    {
        const string invalidParameter = "Error executing extended stored procedure: Invalid Parameter.";
        const string invalidParameterType = "Error executing extended stored procedure: Invalid Parameter Type";
        if (arguments.Count is < 2 or > 3 || arguments[0].IsDefault || !IsErrorLogInteger(arguments[0].Value))
            return invalidParameter;
        var number = arguments[0].Value.CoerceTo(SqlType.Int32).AsInt32;
        if (number <= 50000)
            return "Invalid error number. Error number must be greater than 50000.";
        if (!IsLogEventText(arguments[1]))
            return invalidParameterType;
        var severity = 0;
        if (arguments.Count == 3)
        {
            if (!IsLogEventText(arguments[2]))
                return invalidParameterType;
            var named = arguments[2].Value.AsString;
            severity = named.Equals("informational", StringComparison.OrdinalIgnoreCase) ? 0
                : named.Equals("warning", StringComparison.OrdinalIgnoreCase) ? 15
                : named.Equals("error", StringComparison.OrdinalIgnoreCase) ? 16
                : -1;
            if (severity < 0)
                return "Invalid severity value. Severity must be 'Informational' (default), 'Warning', or 'Error'.";
        }
        var message = arguments[1].Value.AsString;
        var log = batch.Connection.Simulation.ErrorLog;
        var process = ErrorLog.SessionProcess(batch.Connection.Spid);
        if (severity == 0)
            log.Write(batch.CurrentStatement.UtcNow, process, message.Length == 0 ? null : message);
        else
            log.WriteError(batch.CurrentStatement.UtcNow, process, number, severity, 1, message);
        return null;
    }

    private static bool IsLogEventText(ProcArgument argument) =>
        !argument.IsDefault && !argument.Value.IsNull && argument.Value.Type.Category == SqlTypeCategory.String && !SqlType.IsMaxLength(argument.Value.Type);

    /// <summary>
    /// <c>sp_cycle_errorlog</c>: closes the current log and opens a new one
    /// (<see cref="ErrorLog.Cycle"/>), reporting Msg 2528 at line 9 of real's
    /// source as the <c>DBCC ERRORLOG</c> it runs does. Beyond the
    /// <c>EXECUTE</c> grant, the caller must be a <c>sysadmin</c> member —
    /// Msg 15247 at line 5 otherwise (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCycleErrorLog(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        RequireSystemProcedureShape("sp_cycle_errorlog", arguments, []);
        if (batch.IsSkipping)
            yield break;
        RequireErrorLogProcedureExecute(batch, "sp_cycle_errorlog", calledAs);
        if (!IsSysadminSession(batch))
            throw SimulatedSqlException.UserDoesNotHavePermission(1);
        CycleErrorLog(batch);
        batch.Connection.PendingMessages.Enqueue(new SimulatedError(0, 9, batch.Connection.Language.DbccCompletedMessage, 2528, calledAs, batch.Connection.DataSource, "SqlServerSimulator", 1));
    }

    /// <summary>Cycles the log under the calling session, the work of <c>sp_cycle_errorlog</c> and <c>DBCC ERRORLOG</c>.</summary>
    private static void CycleErrorLog(BatchContext batch) =>
        batch.Connection.Simulation.ErrorLog.Cycle(batch.CurrentStatement.UtcNow, ErrorLog.SessionProcess(batch.Connection.Spid));

    /// <summary>
    /// An extended procedure's Msg 22004 as real sends it: an error with no
    /// procedure, at line 0, that the batch runs on past without entering a
    /// <c>CATCH</c> or setting <c>@@ERROR</c>.
    /// </summary>
    private static SimulatedErrorOutcome ExtendedProcedureRefusal(BatchContext batch, SimulatedSqlException refusal)
    {
        refusal.PreserveDiagnostics(0, null);
        return new SimulatedErrorOutcome(refusal) { DoneKind = batch.CurrentStatement.DoneKind };
    }

    private static void SetReturnCode(BatchContext batch, string? returnCodeVar, int code)
    {
        if (returnCodeVar is null)
            return;
        var slot = batch.GetVariableSlot(returnCodeVar);
        slot.Value = SqlValue.FromInt32(code).CoerceTo(slot.DeclaredType);
    }
}
