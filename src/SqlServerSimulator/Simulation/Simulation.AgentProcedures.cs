using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// msdb's SQL Server Agent procedures: the dispatch, the argument binding,
// permissions and lookups they share, and the statements they write msdb's
// Agent tables through. The procedures themselves live in Simulation.AgentJobs.cs,
// Simulation.AgentSchedules.cs, Simulation.AgentHelp.cs and Simulation.AgentAlerts.cs.
partial class Simulation
{
    /// <summary>
    /// The msdb procedures the simulator dispatches. Unlike the system
    /// procedures they live in msdb alone, so a call resolves only through a
    /// name naming msdb (<c>msdb.dbo.sp_add_job</c>, <c>msdb..sp_add_job</c>)
    /// or an unqualified or <c>dbo</c> name with msdb current, and elsewhere
    /// is the ordinary Msg 2812.
    /// </summary>
    private static readonly string[] AgentProcedureNames =
    [
        "sp_add_alert", "sp_add_category", "sp_add_job", "sp_add_jobschedule", "sp_add_jobserver", "sp_add_jobstep",
        "sp_add_notification", "sp_add_operator", "sp_add_schedule", "sp_attach_schedule", "sp_delete_alert",
        "sp_delete_category", "sp_delete_job", "sp_delete_jobschedule", "sp_delete_jobserver", "sp_delete_jobstep",
        "sp_delete_notification", "sp_delete_operator", "sp_delete_schedule", "sp_detach_schedule", "sp_enum_sqlagent_subsystems", "sp_help_alert",
        "sp_help_category", "sp_help_job", "sp_help_jobactivity", "sp_help_jobhistory", "sp_help_jobschedule",
        "sp_help_jobserver", "sp_help_jobstep", "sp_help_operator", "sp_help_schedule", "sp_purge_jobhistory",
        "sp_start_job", "sp_stop_job", "sp_update_category", "sp_update_job", "sp_update_jobschedule", "sp_update_jobstep", "sp_update_operator", "sp_update_schedule",
    ];

    /// <summary>
    /// The Agent procedure <paramref name="procName"/> calls, by its canonical
    /// name, or null when it names none — a name outside msdb, a schema other
    /// than <c>dbo</c>, or a leaf no Agent procedure has. The leaf matches
    /// under msdb's collation, as a procedure of its own would.
    /// </summary>
    private static string? ResolveAgentProcedureName(BatchContext batch, MultiPartName procName)
    {
        if (procName.Count > 3)
            return null;
        var simulation = batch.Connection.Simulation;
        var msdb = simulation.Databases[MsdbDatabaseName];
        if (procName.Count == 3)
        {
            if (!simulation.Databases.TryGetValue(procName[0], out var named) || !ReferenceEquals(named, msdb))
                return null;
        }
        else if (!ReferenceEquals(batch.CurrentDatabase, msdb))
        {
            return null;
        }
        if (procName.Count >= 2 && procName[procName.Count - 2] is { Length: > 0 } schema && !msdb.Collation.Equals(schema, Database.DefaultSchemaName))
            return null;
        foreach (var names in (ReadOnlySpan<string[]>)[AgentProcedureNames, DatabaseMailProcedureNames])
        {
            foreach (var name in names)
            {
                if (msdb.Collation.Equals(name, procName.Leaf))
                    return name;
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="name"/> is the canonical name of an msdb procedure the simulator dispatches: an Agent or a Database Mail one.</summary>
    private static bool IsAgentProcedure(string name) =>
        Array.IndexOf(AgentProcedureNames, name) >= 0 || Array.IndexOf(DatabaseMailProcedureNames, name) >= 0;

    private IEnumerable<SimulatedStatementOutcome> InvokeAgentProcedure(BatchContext batch, string name, string? returnCodeVar)
    {
        using var outcomes = this.AgentProcedure(batch, name, returnCodeVar).GetEnumerator();
        while (true)
        {
            try
            {
                if (!outcomes.MoveNext())
                    yield break;
            }
            catch (SimulatedSqlException refusal) when (batch.Connection.OpenTryFrames > 0)
            {
                refusal.CaughtBeforeReturn = true;
                throw;
            }
            yield return outcomes.Current;
        }
    }

    private IEnumerable<SimulatedStatementOutcome> AgentProcedure(BatchContext batch, string name, string? returnCodeVar) => name switch
    {
        "sp_add_alert" => this.InvokeSpAddAlert(batch, returnCodeVar),
        "sp_add_category" => this.InvokeSpAddCategory(batch, returnCodeVar),
        "sp_add_job" => this.InvokeSpAddJob(batch, returnCodeVar),
        "sp_add_jobschedule" => this.InvokeSpAddJobSchedule(batch, returnCodeVar),
        "sp_add_jobserver" => this.InvokeSpAddJobServer(batch, returnCodeVar),
        "sp_add_jobstep" => this.InvokeSpAddJobStep(batch, returnCodeVar),
        "sp_add_notification" => this.InvokeSpAddNotification(batch, returnCodeVar),
        "sp_add_operator" => this.InvokeSpAddOperator(batch, returnCodeVar),
        "sp_add_schedule" => this.InvokeSpAddSchedule(batch, returnCodeVar),
        "sp_attach_schedule" => this.InvokeSpAttachSchedule(batch, returnCodeVar),
        "sp_delete_alert" => this.InvokeSpDeleteAlert(batch, returnCodeVar),
        "sp_delete_category" => this.InvokeSpDeleteCategory(batch, returnCodeVar),
        "sp_delete_job" => this.InvokeSpDeleteJob(batch, returnCodeVar),
        "sp_delete_jobschedule" => this.InvokeSpDeleteJobSchedule(batch, returnCodeVar),
        "sp_delete_jobserver" => this.InvokeSpDeleteJobServer(batch, returnCodeVar),
        "sp_delete_jobstep" => this.InvokeSpDeleteJobStep(batch, returnCodeVar),
        "sp_delete_notification" => this.InvokeSpDeleteNotification(batch, returnCodeVar),
        "sp_delete_operator" => this.InvokeSpDeleteOperator(batch, returnCodeVar),
        "sp_delete_schedule" => this.InvokeSpDeleteSchedule(batch, returnCodeVar),
        "sp_detach_schedule" => this.InvokeSpDetachSchedule(batch, returnCodeVar),
        "sp_enum_sqlagent_subsystems" => this.InvokeSpEnumAgentSubsystems(batch, returnCodeVar),
        "sp_help_alert" => this.InvokeSpHelpAlert(batch, returnCodeVar),
        "sp_help_category" => this.InvokeSpHelpCategory(batch, returnCodeVar),
        "sp_help_job" => this.InvokeSpHelpJob(batch, returnCodeVar),
        "sp_help_jobactivity" => this.InvokeSpHelpJobActivity(batch, returnCodeVar),
        "sp_help_jobhistory" => this.InvokeSpHelpJobHistory(batch, returnCodeVar),
        "sp_help_jobschedule" => this.InvokeSpHelpJobSchedule(batch, returnCodeVar),
        "sp_help_jobserver" => this.InvokeSpHelpJobServer(batch, returnCodeVar),
        "sp_help_jobstep" => this.InvokeSpHelpJobStep(batch, returnCodeVar),
        "sp_help_operator" => this.InvokeSpHelpOperator(batch, returnCodeVar),
        "sp_help_schedule" => this.InvokeSpHelpSchedule(batch, returnCodeVar),
        "sp_purge_jobhistory" => this.InvokeSpPurgeJobHistory(batch, returnCodeVar),
        "sp_start_job" => this.InvokeSpStartStopJob(batch, returnCodeVar, start: true),
        "sp_stop_job" => this.InvokeSpStartStopJob(batch, returnCodeVar, start: false),
        "sp_update_category" => this.InvokeSpUpdateCategory(batch, returnCodeVar),
        "sp_update_job" => this.InvokeSpUpdateJob(batch, returnCodeVar),
        "sp_update_jobschedule" => this.InvokeSpUpdateJobSchedule(batch, returnCodeVar),
        "sp_update_jobstep" => this.InvokeSpUpdateJobStep(batch, returnCodeVar),
        "sp_update_operator" => this.InvokeSpUpdateOperator(batch),
        "sp_update_schedule" => this.InvokeSpUpdateSchedule(batch, returnCodeVar),
        _ => this.DatabaseMailProcedure(batch, name, returnCodeVar),
    };

    /// <summary>A parameter of an Agent procedure that may be left out, taking NULL.</summary>
    private static SystemProcedureParameter AgentParameter(string name, SqlType type, int maxLength = 0) =>
        new(name, type, maxLength, SqlValue.Null(type));

    /// <summary>A parameter of an Agent procedure that may be left out, taking <paramref name="defaultValue"/>.</summary>
    private static SystemProcedureParameter AgentParameter(string name, SqlType type, SqlValue defaultValue) =>
        new(name, type, 0, defaultValue);

    /// <summary>A parameter an Agent procedure requires.</summary>
    private static SystemProcedureParameter AgentRequired(string name, SqlType type, int maxLength = 0) =>
        new(name, type, maxLength);

    // The parameter types, read through properties because the parameter
    // tables of the other partial files initialize from them, and static
    // fields across partial files initialize in no defined order.
    private static SqlType AgentName => NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.CoercibleDefault);

    private static SqlType AgentText => NVarcharSqlType.Get(SqlType.MaxLengthSentinel, Collation.Baseline, Coercibility.CoercibleDefault);

    private static SqlType AgentVarchar => VarcharSqlType.Get(SqlType.MaxLengthSentinel, Collation.Baseline, Coercibility.CoercibleDefault);

    /// <summary>
    /// One call's bound arguments: each parameter's value, converted to its
    /// type, and the caller's <c>OUTPUT</c> variable where one was passed.
    /// </summary>
    private sealed class AgentCall(SqlValue[] values, VariableSlot?[] outputs)
    {
        public readonly SqlValue[] Values = values;

        private readonly VariableSlot?[] outputs = outputs;

        public bool IsNull(int index) => this.Values[index].IsNull;

        public string? Text(int index) => this.Values[index].IsNull ? null : this.Values[index].AsString;

        public int? Int(int index) => this.Values[index].IsNull ? null : this.Values[index].CoerceTo(SqlType.Int32).AsInt32;

        public Guid? Guid(int index) => this.Values[index].IsNull ? null : this.Values[index].AsGuid;

        /// <summary>Writes <paramref name="value"/> to the <c>OUTPUT</c> variable the caller passed for parameter <paramref name="index"/>, if any.</summary>
        public void SetOutput(int index, SqlValue value)
        {
            if (this.outputs[index] is { } slot)
                slot.Value = value.CoerceTo(slot.DeclaredType);
        }
    }

    /// <summary>
    /// Parses and binds an Agent procedure's arguments as a T-SQL procedure's
    /// are bound: Msg 8144 for a positional argument too many, Msg 201 (state
    /// 4) for a required one left out, Msg 8145 for a name that is no
    /// parameter, and Msg 8114 (state 1) for a value that won't convert; each
    /// at line 0, leaving the caller's return code unset. Null while the
    /// batch walks in skip mode.
    /// </summary>
    private static AgentCall? BindAgentCall(BatchContext batch, string procedure, SystemProcedureParameter[] parameters)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            return null;
        try
        {
            var supplied = new ProcArgument?[parameters.Length];
            string? unknownName = null;
            for (var i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];
                var slot = i;
                if (argument.Name is { } name)
                {
                    slot = Array.FindIndex(parameters, parameter => BuiltInToken.Equals(name, parameter.Name));
                    if (slot < 0)
                    {
                        unknownName ??= name;
                        continue;
                    }
                }
                else if (i >= parameters.Length)
                {
                    throw SimulatedSqlException.TooManyArgumentsToFunction(procedure);
                }
                supplied[slot] = argument;
            }
            for (var i = 0; i < parameters.Length; i++)
            {
                if (supplied[i] is not { IsDefault: false } && parameters[i].Default is null)
                    throw SimulatedSqlException.ProcedureExpectsParameter(procedure, parameters[i].Name);
            }
            if (unknownName is not null)
                throw SimulatedSqlException.NotAParameterForProcedure(unknownName, procedure);
            var values = new SqlValue[parameters.Length];
            var outputs = new VariableSlot?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (supplied[i] is not { IsDefault: false } argument)
                {
                    values[i] = parameter.Default!.Value;
                    continue;
                }
                outputs[i] = argument.OutputSlot;
                values[i] = ConvertAgentArgument(argument, parameter);
            }
            return new AgentCall(values, outputs);
        }
        catch (SimulatedSqlException refusal)
        {
            refusal.PreserveDiagnostics(0, procedure);
            refusal.SystemProcedureBindingError = true;
            throw;
        }
    }

    private static SqlValue ConvertAgentArgument(ProcArgument argument, SystemProcedureParameter parameter)
    {
        var value = argument.Value;
        if (value.IsNull)
            return SqlValue.Null(parameter.Type);
        if (parameter.Type.Category == SqlTypeCategory.String)
        {
            var text = value.CoerceTo(SqlType.NVarchar).AsString;
            return SqlValue.FromNVarchar(parameter.MaxLength > 0 && text.Length > parameter.MaxLength ? text[..parameter.MaxLength] : text);
        }
        try
        {
            return value.CoerceTo(parameter.Type);
        }
        catch (Exception refused) when (refused is SimulatedSqlException or OverflowException)
        {
            throw argument.IsNumericLiteral
                ? SimulatedSqlException.ConvertingDataTypeError("numeric", parameter.Type.SqlServerName, state: 1)
                : SimulatedSqlException.ConvertingDataTypeError(value.Type, parameter.Type.SqlServerName, state: 1);
        }
    }

    /// <summary>
    /// Runs <paramref name="sql"/> in msdb as its <c>dbo</c> — the owner whose
    /// chain real's procedures reach the Agent tables through — with
    /// <paramref name="parameters"/> declared as its variables, and returns
    /// the rows of every result set it produced. Its writes join the
    /// session's transaction, so a rollback undoes them as it undoes real's.
    /// </summary>
    private List<SqlValue[]> AgentSql(BatchContext batch, string sql, params ReadOnlySpan<(string Name, SqlValue Value)> parameters)
    {
        var variables = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        foreach (var (name, value) in parameters)
        {
            var type = value.Type.Category == SqlTypeCategory.String ? AgentText : value.Type;
            variables[name] = new VariableSlot(type, declaredMaxLength: null, value.CoerceTo(type), parameter: null);
        }
        var security = batch.Connection.Security;
        var depth = security.ImpersonationDepth;
        security.Push(new SecurityPrincipalFrame(Database.DboPrincipalId, Database.DefaultSchemaName, security.Effective.LoginName, isDatabaseScoped: true));
        var rows = new List<SqlValue[]>();
        try
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, sql, variables, runsIn: this.Databases[MsdbDatabaseName]))
            {
                switch (outcome)
                {
                    case SimulatedErrorOutcome error:
                        throw error.Exception;
                    case SimulatedSqlResultSet result:
                        var cursor = result.CreateCursor();
                        while (cursor.MoveNext())
                        {
                            var row = new SqlValue[cursor.FieldCount];
                            for (var i = 0; i < row.Length; i++)
                                row[i] = cursor[i];
                            rows.Add(row);
                        }
                        break;
                }
            }
        }
        finally
        {
            security.RevertTo(depth);
        }
        return rows;
    }

    /// <summary>The single value of the first row <see cref="AgentSql"/> returns, or null for none.</summary>
    private SqlValue? AgentScalar(BatchContext batch, string sql, params ReadOnlySpan<(string Name, SqlValue Value)> parameters) =>
        this.AgentSql(batch, sql, parameters) is [var first, ..] ? first[0] : null;

    /// <summary>One row of an msdb Agent table, its cells read by column name.</summary>
    private sealed class AgentRow(HeapTable table, SqlValue[] values)
    {
        public readonly SqlValue[] Values = values;

        public SqlValue this[string column] => this.Values[MsdbOrdinal(table, column)];

        public int Int(string column) => this[column].CoerceTo(SqlType.Int32).AsInt32;

        public string? Text(string column) => this[column] is { IsNull: false } value ? value.AsString : null;
    }

    /// <summary>The rows of msdb's <paramref name="table"/> matching <paramref name="where"/>, every column in table order.</summary>
    private List<AgentRow> AgentRows(BatchContext batch, string table, string where, params ReadOnlySpan<(string Name, SqlValue Value)> parameters)
    {
        var heap = this.MsdbTable(table);
        return this.AgentSql(batch, $"select * from dbo.{table} where {where}", parameters).ConvertAll(values => new AgentRow(heap, values));
    }

    /// <summary>Whether <paramref name="loginName"/> names a login: <c>sa</c> or one the simulation holds.</summary>
    private bool IsAgentLogin(string loginName) =>
        Collation.Baseline.Equals(loginName, "sa") || this.Logins.ContainsKey(loginName);

    /// <summary>The login whose sid is <paramref name="sid"/>, as <c>SUSER_SNAME</c> reads it, or null for none.</summary>
    private string? LoginNameForSid(SqlValue sid)
    {
        if (sid.IsNull)
            return null;
        var bytes = sid.AsBytes;
        if (bytes is [0x01])
            return "sa";
        foreach (var (name, login) in this.Logins)
        {
            if (SidsMatch(login.Sid, bytes))
                return name;
        }
        return null;
    }

    /// <summary>The calling login's sid, which a job or schedule it creates is owned by.</summary>
    private static SqlValue CallerSid(BatchContext batch) =>
        SqlValue.FromVarbinary(VarbinarySqlType.Get(85), LoginSid(batch.Connection.Simulation, batch.Connection.Security.Effective.LoginName));

    /// <summary>Whether the caller is a <c>sysadmin</c>, the bar most of Agent's ownership rules are set at.</summary>
    private static bool AgentIsSysadmin(BatchContext batch) => IsSysadminSession(batch);

    /// <summary>Whether the caller's msdb principal is a member, directly or through a role, of the msdb role <paramref name="roleId"/>.</summary>
    private bool AgentInRole(BatchContext batch, int roleId)
    {
        var msdb = this.Databases[MsdbDatabaseName];
        var principalId = PermissionEnforcement.ResolveCrossDatabasePrincipal(batch.Connection, msdb).PrincipalId;
        foreach (var (_, principal) in msdb.Principals)
        {
            if (principal.PrincipalId == roleId)
                return PermissionChecker.IsRoleMember(msdb, principalId, principal);
        }
        return false;
    }

    /// <summary>Whether the caller sees every job, not only its own: a <c>sysadmin</c> or a member of <c>SQLAgentReaderRole</c> or <c>TargetServersRole</c>.</summary>
    private bool AgentSeesAllJobs(BatchContext batch) =>
        AgentIsSysadmin(batch) || this.AgentInRole(batch, AgentReaderRoleId) || this.AgentInRole(batch, TargetServersRoleId);

    /// <summary>msdb's <c>TargetServersRole</c>.</summary>
    private const int TargetServersRoleId = 5;

    /// <summary>
    /// Refuses the call (Msg 229 on the procedure at line 1) unless the caller
    /// holds <c>EXECUTE</c> on it: msdb's <c>dbo</c> or a <c>sysadmin</c>, or
    /// a member of <paramref name="grantedRole"/>, the Agent role real grants
    /// it to — none for a procedure only a <c>sysadmin</c> runs.
    /// </summary>
    private void RequireAgentExecute(BatchContext batch, string procedure, int? grantedRole)
    {
        var msdb = this.Databases[MsdbDatabaseName];
        if (PermissionEnforcement.Bypasses(batch.Connection, msdb) || AgentIsSysadmin(batch))
            return;
        if (grantedRole is { } role && this.AgentInRole(batch, role))
            return;
        // The procedure never runs, so the caller's return code stays unset.
        var refusal = SimulatedSqlException.PermissionDenied("EXECUTE", procedure, MsdbDatabaseName, Database.DefaultSchemaName).AtAgentSite(1);
        refusal.SystemProcedureBindingError = true;
        throw refusal;
    }

    /// <summary>
    /// The job a call names by <c>@job_id</c> or <c>@job_name</c>, as
    /// <c>sp_verify_job_identifiers</c> finds it: exactly one of the two
    /// (Msg 14294), the name trimmed and matched under msdb's collation, among
    /// the jobs the caller may see (Msg 14262 for one it can't).
    /// </summary>
    private AgentRow ResolveAgentJob(BatchContext batch, Guid? jobId, string? jobName)
    {
        // An empty name names nothing (probed 2026-10-16: sp_delete_job of the
        // job sp_add_job made under N'' is Msg 14294).
        if (jobName is not null && jobName.Trim().Length == 0)
            jobName = null;
        if ((jobId is null) == (jobName is null))
            throw SimulatedSqlException.AgentSupplyEither("@job_id", "@job_name", "job").AtAgentSite(25, "sp_verify_job_identifiers");
        var rows = jobName is { } name
            ? this.AgentRows(batch, "sysjobs", "name = @name", ("name", SqlValue.FromNVarchar(name.Trim())))
            : this.AgentRows(batch, "sysjobs", "job_id = @id", ("id", SqlValue.FromGuid(jobId!.Value)));
        if (!this.AgentSeesAllJobs(batch))
        {
            var caller = CallerSid(batch).AsBytes;
            _ = rows.RemoveAll(row => !row["owner_sid"].AsBytes.AsSpan().SequenceEqual(caller));
        }
        if (rows is [var job, ..])
            return job;
        throw (jobName is { } missing
            ? SimulatedSqlException.AgentDoesNotExist("@job_name", missing)
            : SimulatedSqlException.AgentDoesNotExist("@job_id", jobId!.Value.ToString("D").ToUpperInvariant()))
            .AtAgentSite(jobName is null ? 41 : 67, "sp_verify_job_identifiers");
    }

    /// <summary>Whether the caller owns <paramref name="job"/>.</summary>
    private static bool AgentOwns(BatchContext batch, AgentRow job) =>
        job["owner_sid"].AsBytes.AsSpan().SequenceEqual(CallerSid(batch).AsBytes);

    /// <summary>Refuses (Msg 14525) a change to a job the caller doesn't own unless it is a <c>sysadmin</c>.</summary>
    private static void RequireAgentJobOwner(BatchContext batch, AgentRow job, int line, string? procedure = null)
    {
        if (!AgentIsSysadmin(batch) && !AgentOwns(batch, job))
            throw SimulatedSqlException.AgentJobOwnedByOther().AtAgentSite(line, procedure);
    }

    /// <summary>Whether <paramref name="jobId"/> is targeted at the local server, which is what a stopped Agent would be told of a change to.</summary>
    private bool AgentJobTargetsLocalServer(BatchContext batch, SqlValue jobId) =>
        this.AgentScalar(batch, "select count(*) from dbo.sysjobservers where job_id = @id and server_id = 0", ("id", jobId)) is { } count && count.AsInt32 > 0;

    /// <summary>
    /// The Msg 22022 a stopped Agent's notification sends at severity 0: the
    /// change is made, and <c>xp_sqlagent_notify</c> reports it couldn't tell
    /// the service.
    /// </summary>
    private static SimulatedInfoOutcome AgentNotRunningNotice(BatchContext batch) =>
        new(SimulatedSqlException.SystemProcedureMessage(batch, "", 0, 22022, SimulatedSqlException.AgentNotRunningText));

    /// <summary>A job's id as a value.</summary>
    private static SqlValue AgentJobId(AgentRow job) => job["job_id"];

    /// <summary>The local server's name as <c>@@SERVERNAME</c> reads it.</summary>
    private const string AgentLocalServer = Network.TdsSession.ServerName;

    /// <summary>Writes the return code an Agent procedure answers, which every one of them sets itself.</summary>
    private static void AgentReturn(BatchContext batch, string? returnCodeVar, int code) => SetReturnCode(batch, returnCodeVar, code);
}
