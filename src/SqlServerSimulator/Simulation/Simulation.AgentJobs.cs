using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Agent procedures that create, change, target, start and remove jobs and
// their steps, as SQL Server 2025 runs them with its Agent service stopped
// (probed 2026-10-16; sys.dm_server_services reports the Agent stopped, as a
// Linux server's is until it is enabled): every change is made, a job
// targeted at the local server answers with the Msg 22022 notice the stopped
// service couldn't be told, and a start or stop is refused with it.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] SpAddJobParameters =
    [
        AgentRequired("job_name", AgentName, 128),
        AgentParameter("enabled", SqlType.TinyInt, SqlValue.FromByte(1)),
        AgentParameter("description", AgentText, 512),
        AgentParameter("start_step_id", SqlType.Int32, SqlValue.FromInt32(1)),
        AgentParameter("category_name", AgentName, 128),
        AgentParameter("category_id", SqlType.Int32),
        AgentParameter("owner_login_name", AgentName, 128),
        AgentParameter("notify_level_eventlog", SqlType.Int32, SqlValue.FromInt32(2)),
        AgentParameter("notify_level_email", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("notify_level_netsend", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("notify_level_page", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("notify_email_operator_name", AgentName, 128),
        AgentParameter("notify_netsend_operator_name", AgentName, 128),
        AgentParameter("notify_page_operator_name", AgentName, 128),
        AgentParameter("delete_level", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("originating_server", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_add_job</c>: a new job, owned by the caller unless a
    /// <c>sysadmin</c> names another login (Msg 14515 otherwise), its name
    /// trimmed, an empty or missing description reading <c>No description
    /// available.</c>, each notification level reset to 0 without its
    /// operator. Real's own checks, in its order, refuse the rest; a NULL name
    /// is the INSERT's own Msg 515, answering 515, and a non-NULL
    /// <c>@job_id</c> Msg 14274, the job one only an MSX server sends.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddJob(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_job", SpAddJobParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_job", AgentUserRoleId);
        var sysadmin = AgentIsSysadmin(batch);
        var owner = call.Text(6) is { Length: > 0 } ownerName ? ownerName : null;
        if (owner is not null && !sysadmin && !Collation.Baseline.Equals(owner, batch.Connection.Security.Effective.LoginName))
            throw SimulatedSqlException.AgentOwnerNeedsSysadmin().AtAgentSite(65);
        var categoryId = this.ResolveAgentCategoryIdentifiers(batch, call.Text(4), call.Int(5), 1);
        var name = call.Text(0)?.Trim();
        var levels = new int[4];
        for (var i = 0; i < 4; i++)
            levels[i] = call.Int(7 + i)!.Value;
        var operators = this.VerifyAgentJob(batch, call.Text(16), name, existingJob: null, call.Int(1)!.Value, call.Int(3)!.Value, stepCount: 0,
            categoryId, owner, levels, [call.Text(11), call.Text(12), call.Text(13)], call.Int(14)!.Value);
        if (!call.IsNull(15))
            throw SimulatedSqlException.AgentJobFromMsx().AtAgentSite(143);
        if (name is null)
        {
            var refusal = SimulatedSqlException.CannotInsertNull("name", "msdb.dbo.sysjobs").AtAgentSite(148);
            refusal.SystemProcedureReturnCode = 515;
            // It ends the procedure's INSERT, which real follows with Msg 3621.
            batch.CurrentStatement.WritesRows = true;
            throw refusal;
        }
        var description = call.Text(2) is { Length: > 0 } text ? text : "No description available.";
        var jobId = Guid.NewGuid();
        _ = this.AgentSql(batch, """
            insert dbo.sysjobs (job_id, originating_server_id, name, enabled, description, start_step_id, category_id, owner_sid,
                notify_level_eventlog, notify_level_email, notify_level_netsend, notify_level_page, notify_email_operator_id,
                notify_netsend_operator_id, notify_page_operator_id, delete_level, date_created, date_modified, version_number)
            values (@id, 0, @name, @enabled, @description, @start, @category, @owner, @eventlog, @email, @netsend, @page,
                @emailop, @netsendop, @pageop, @delete, getdate(), getdate(), 1)
            """,
            ("id", SqlValue.FromGuid(jobId)), ("name", SqlValue.FromNVarchar(name)), ("enabled", call.Values[1]),
            ("description", SqlValue.FromNVarchar(description)), ("start", call.Values[3]), ("category", SqlValue.FromInt32(categoryId ?? 0)),
            ("owner", owner is null ? CallerSid(batch) : SqlValue.FromVarbinary(VarbinarySqlType.Get(85), LoginSid(owner))),
            ("eventlog", SqlValue.FromInt32(levels[0])), ("email", SqlValue.FromInt32(operators[0] == 0 ? 0 : levels[1])),
            ("netsend", SqlValue.FromInt32(operators[1] == 0 ? 0 : levels[2])), ("page", SqlValue.FromInt32(operators[2] == 0 ? 0 : levels[3])),
            ("emailop", SqlValue.FromInt32(operators[0])), ("netsendop", SqlValue.FromInt32(operators[1])), ("pageop", SqlValue.FromInt32(operators[2])),
            ("delete", call.Values[14]));
        call.SetOutput(15, SqlValue.FromGuid(jobId));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// The category <c>@category_name</c> or <c>@category_id</c> names, as
    /// <c>sp_verify_category_identifiers</c> resolves it: never both (Msg
    /// 14524), and one that exists (Msg 14262). Null when neither is given.
    /// </summary>
    private int? ResolveAgentCategoryIdentifiers(BatchContext batch, string? categoryName, int? categoryId, int categoryClass)
    {
        if (categoryName is not null && categoryId is not null)
            throw SimulatedSqlException.AgentSupplyEitherCategory().AtAgentSite(23, "sp_verify_category_identifiers");
        if (categoryName is { } name)
        {
            return this.AgentScalar(batch, "select top (1) category_id from dbo.syscategories where name = @name order by case when category_class = @class then 0 else 1 end",
                ("name", SqlValue.FromNVarchar(name)), ("class", SqlValue.FromInt32(categoryClass)))?.AsInt32
                ?? throw SimulatedSqlException.AgentDoesNotExist("@category_name", name).AtAgentSite(50, "sp_verify_category_identifiers");
        }
        if (categoryId is { } id)
        {
            return this.AgentScalar(batch, "select category_id from dbo.syscategories where category_id = @id", ("id", SqlValue.FromInt32(id)))?.AsInt32
                ?? throw SimulatedSqlException.AgentDoesNotExist("@category_id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)).AtAgentSite(36, "sp_verify_category_identifiers");
        }
        return null;
    }

    /// <summary>
    /// <c>sp_verify_job</c>'s checks, in its order and at its lines: a local
    /// originating server (Msg 14275), a name no other job holds (14261),
    /// <c>@enabled</c> 0 or 1, a start step among the job's steps and the one
    /// past them (14266), a job category, an existing owner login (14234),
    /// notification levels 0 to 3, operators that exist, and a delete level 0
    /// to 3. Returns the e-mail, net-send and pager operators' ids, 0 for each
    /// not named.
    /// </summary>
    private int[] VerifyAgentJob(BatchContext batch, string? originatingServer, string? name, AgentRow? existingJob, int enabled, int startStepId, int stepCount,
        int? categoryId, string? owner, int[] levels, string?[] operatorNames, int deleteLevel)
    {
        const string verifier = "sp_verify_job";
        if (originatingServer is { Length: > 0 } server && !BuiltInToken.Equals(server, AgentLocalServer) && !BuiltInToken.Equals(server, "(local)"))
            throw SimulatedSqlException.AgentOriginatingServerInvalid().AtAgentSite(45, verifier);
        if (name is not null
            && this.AgentScalar(batch, "select job_id from dbo.sysjobs where name = @name", ("name", SqlValue.FromNVarchar(name))) is { } holder
            && (existingJob is null || holder.AsGuid != AgentJobId(existingJob).AsGuid))
        {
            throw SimulatedSqlException.AgentAlreadyExists("@name", name).AtAgentSite(60, verifier);
        }
        if (enabled is not (0 or 1))
            throw SimulatedSqlException.AgentInvalidValue("@enabled", "0, 1").AtAgentSite(67, verifier);
        if (existingJob is null ? startStepId != 1 : startStepId < 1 || startStepId > stepCount + 1)
        {
            throw SimulatedSqlException.AgentInvalidValue("@start_step_id", existingJob is null ? "1" : $"1..{stepCount + 1}")
                .AtAgentSite(existingJob is null ? 79 : 97, verifier);
        }
        if (categoryId is { } category
            && this.AgentScalar(batch, "select category_class from dbo.syscategories where category_id = @id", ("id", SqlValue.FromInt32(category))) is { } categoryClass
            && categoryClass.AsInt32 != 1)
        {
            throw SimulatedSqlException.AgentInvalidReturnedBy("@category_name", "sp_help_category").AtAgentSite(163, verifier);
        }
        if (owner is not null && !this.IsAgentLogin(owner))
            throw SimulatedSqlException.AgentInvalidReturnedBy("@owner_login_name", "sp_helplogins [excluding Windows NT groups]").AtAgentSite(200, verifier);
        string[] levelNames = ["@notify_level_eventlog", "@notify_level_email", "@notify_level_netsend", "@notify_level_page"];
        for (var i = 0; i < 4; i++)
        {
            if (levels[i] is < 0 or > 3)
                throw SimulatedSqlException.AgentInvalidValue(levelNames[i], "0, 1, 2, 3").AtAgentSite(208 + (i * 5), verifier);
        }
        string[] operatorParameters = ["@notify_email_operator_name", "@notify_netsend_operator_name", "@notify_page_operator_name"];
        var operatorIds = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (operatorNames[i] is not { Length: > 0 } operatorName)
                continue;
            operatorIds[i] = this.AgentScalar(batch, "select id from dbo.sysoperators where name = @name", ("name", SqlValue.FromNVarchar(operatorName)))?.AsInt32
                ?? throw SimulatedSqlException.AgentInvalidReturnedBy(operatorParameters[i], "sp_help_operator").AtAgentSite(248 + (i * 10), verifier);
        }
        if (deleteLevel is < 0 or > 3)
            throw SimulatedSqlException.AgentInvalidValue("@delete_level", "0, 1, 2, 3").AtAgentSite(317, verifier);
        return operatorIds;
    }

    /// <summary>The number of steps <paramref name="jobId"/> has.</summary>
    private int AgentStepCount(BatchContext batch, SqlValue jobId) =>
        this.AgentScalar(batch, "select count(*) from dbo.sysjobsteps where job_id = @id", ("id", jobId))!.Value.AsInt32;

    /// <summary>Bumps a job's version and modification date, as each change to it or its steps does.</summary>
    private void TouchAgentJob(BatchContext batch, SqlValue jobId) =>
        _ = this.AgentSql(batch, "update dbo.sysjobs set version_number = version_number + 1, date_modified = getdate() where job_id = @id", ("id", jobId));

    private static readonly SystemProcedureParameter[] SpUpdateJobParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("new_name", AgentName, 128),
        AgentParameter("enabled", SqlType.TinyInt),
        AgentParameter("description", AgentText, 512),
        AgentParameter("start_step_id", SqlType.Int32),
        AgentParameter("category_name", AgentName, 128),
        AgentParameter("owner_login_name", AgentName, 128),
        AgentParameter("notify_level_eventlog", SqlType.Int32),
        AgentParameter("notify_level_email", SqlType.Int32),
        AgentParameter("notify_level_netsend", SqlType.Int32),
        AgentParameter("notify_level_page", SqlType.Int32),
        AgentParameter("notify_email_operator_name", AgentName, 128),
        AgentParameter("notify_netsend_operator_name", AgentName, 128),
        AgentParameter("notify_page_operator_name", AgentName, 128),
        AgentParameter("delete_level", SqlType.Int32),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// <c>sp_update_job</c>: each attribute given replaces the job's own, an
    /// empty new name keeping the old one and an empty description clearing
    /// it to NULL; the job's version rises with every call. A caller outside
    /// <c>sysadmin</c> may change only its own jobs (Msg 14525), but a member
    /// of <c>SQLAgentOperatorRole</c> may enable or disable any. A job
    /// targeted at the local server gets the stopped Agent's notice unless the
    /// call changed only its description.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateJob(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_update_job", SpUpdateJobParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_update_job", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        var onlyEnabled = !call.IsNull(3);
        for (var i = 2; i < 16 && onlyEnabled; i++)
            onlyEnabled = i == 3 || call.IsNull(i);
        if (!(onlyEnabled && this.AgentInRole(batch, AgentOperatorRoleId)))
            RequireAgentJobOwner(batch, job, 156, null);
        var jobId = AgentJobId(job);
        var newName = call.Text(2) is { Length: > 0 } renamed ? renamed.Trim() : null;
        // sp_update_job looks its category up within sp_verify_job, where a
        // name no job category carries is Msg 14234.
        int? categoryId = null;
        if (call.Text(6) is { } categoryName)
        {
            categoryId = this.AgentScalar(batch, "select category_id from dbo.syscategories where name = @name and category_class = 1", ("name", SqlValue.FromNVarchar(categoryName)))?.AsInt32
                ?? throw SimulatedSqlException.AgentInvalidReturnedBy("@category_name", "sp_help_category").AtAgentSite(163, "sp_verify_job");
        }
        var owner = call.Text(7) is { Length: > 0 } ownerName ? ownerName : null;
        var levels = new int[4];
        string[] levelColumns = ["notify_level_eventlog", "notify_level_email", "notify_level_netsend", "notify_level_page"];
        for (var i = 0; i < 4; i++)
            levels[i] = call.Int(8 + i) ?? job.Int(levelColumns[i]);
        var operators = this.VerifyAgentJob(batch, null, newName, job, call.Int(3) ?? job.Int("enabled"), call.Int(5) ?? job.Int("start_step_id"),
            this.AgentStepCount(batch, jobId), categoryId, owner, levels, [call.Text(12), call.Text(13), call.Text(14)], call.Int(15) ?? job.Int("delete_level"));
        string[] operatorColumns = ["notify_email_operator_id", "notify_netsend_operator_id", "notify_page_operator_id"];
        for (var i = 0; i < 3; i++)
        {
            if (operators[i] == 0)
                operators[i] = job.Int(operatorColumns[i]);
        }
        var description = call.Text(4) is { } text ? text.Length == 0 ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(text) : job["description"];
        _ = this.AgentSql(batch, """
            update dbo.sysjobs set name = @name, enabled = @enabled, description = @description, start_step_id = @start, category_id = @category,
                owner_sid = @owner, notify_level_eventlog = @eventlog, notify_level_email = @email, notify_level_netsend = @netsend,
                notify_level_page = @page, notify_email_operator_id = @emailop, notify_netsend_operator_id = @netsendop,
                notify_page_operator_id = @pageop, delete_level = @delete, date_modified = getdate(), version_number = version_number + 1
            where job_id = @id
            """,
            ("id", jobId), ("name", newName is null ? job["name"] : SqlValue.FromNVarchar(newName)), ("enabled", call.IsNull(3) ? job["enabled"] : call.Values[3]),
            ("description", description), ("start", SqlValue.FromInt32(call.Int(5) ?? job.Int("start_step_id"))),
            ("category", SqlValue.FromInt32(categoryId ?? job.Int("category_id"))),
            ("owner", owner is null ? job["owner_sid"] : SqlValue.FromVarbinary(VarbinarySqlType.Get(85), LoginSid(owner))),
            ("eventlog", SqlValue.FromInt32(levels[0])), ("email", SqlValue.FromInt32(levels[1])), ("netsend", SqlValue.FromInt32(levels[2])),
            ("page", SqlValue.FromInt32(levels[3])), ("emailop", SqlValue.FromInt32(operators[0])), ("netsendop", SqlValue.FromInt32(operators[1])),
            ("pageop", SqlValue.FromInt32(operators[2])), ("delete", SqlValue.FromInt32(call.Int(15) ?? job.Int("delete_level"))));
        var changedMoreThanDescription = false;
        for (var i = 2; i < 16; i++)
            changedMoreThanDescription |= i != 4 && !call.IsNull(i);
        if (changedMoreThanDescription && this.AgentJobTargetsLocalServer(batch, jobId))
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteJobParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("originating_server", AgentName, 128),
        AgentParameter("delete_history", SqlType.Bit, SqlValue.FromBoolean(true)),
        AgentParameter("delete_unused_schedule", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// <c>sp_delete_job</c>: the job with its steps, target servers, activity
    /// and schedule attachments — and, by default, its history and every
    /// schedule it leaves unused. A caller outside <c>sysadmin</c> may delete
    /// only its own jobs (Msg 14525); a job targeted at the local server gets
    /// the stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteJob(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_job", SpDeleteJobParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_job", AgentUserRoleId);
        if (call.IsNull(0) && call.IsNull(1) && call.IsNull(2))
            throw SimulatedSqlException.AgentDeleteJobNeedsIdentifier().AtAgentSite(47);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        RequireAgentJobOwner(batch, job, 105, null);
        var jobId = AgentJobId(job);
        var targeted = this.AgentJobTargetsLocalServer(batch, jobId);
        var deleteHistory = call.Values[3].AsBoolean;
        var deleteSchedules = call.Values[4].AsBoolean;
        _ = this.AgentSql(batch, $"""
            declare @schedules table (schedule_id int);
            insert @schedules select schedule_id from dbo.sysjobschedules where job_id = @id;
            delete dbo.sysjobschedules where job_id = @id;
            {(deleteSchedules ? "delete dbo.sysschedules where schedule_id in (select schedule_id from @schedules) and schedule_id not in (select schedule_id from dbo.sysjobschedules);" : "")}
            delete dbo.sysjobsteps where job_id = @id;
            delete dbo.sysjobservers where job_id = @id;
            delete dbo.sysjobactivity where job_id = @id;
            {(deleteHistory ? "delete dbo.sysjobhistory where job_id = @id;" : "")}
            delete dbo.sysjobs where job_id = @id;
            """, ("id", jobId));
        if (targeted)
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpAddJobStepParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("step_id", SqlType.Int32),
        AgentRequired("step_name", AgentName, 128),
        AgentParameter("subsystem", AgentName, SqlValue.FromNVarchar("TSQL")),
        AgentParameter("command", AgentText),
        AgentParameter("additional_parameters", AgentText),
        AgentParameter("cmdexec_success_code", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("on_success_action", SqlType.TinyInt, SqlValue.FromByte(1)),
        AgentParameter("on_success_step_id", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("on_fail_action", SqlType.TinyInt, SqlValue.FromByte(2)),
        AgentParameter("on_fail_step_id", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("server", AgentName, 128),
        AgentParameter("database_name", AgentName, 128),
        AgentParameter("database_user_name", AgentName, 128),
        AgentParameter("retry_attempts", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("retry_interval", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("os_run_priority", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("output_file_name", AgentText, 200),
        AgentParameter("flags", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("proxy_id", SqlType.Int32),
        AgentParameter("proxy_name", AgentName, 128),
        AgentParameter("step_uid", SqlType.UniqueIdentifier),
    ];

    /// <summary>
    /// <c>sp_add_jobstep</c>: a step inserted at <c>@step_id</c> — the end of
    /// the job by default — the steps at and past it moving up one, its
    /// subsystem kept as written, and a T-SQL step's database defaulting to
    /// <c>master</c> while every other subsystem's is NULL. The checks of
    /// <c>sp_verify_jobstep</c> refuse what real refuses, a step a
    /// <c>goto</c> action names that doesn't exist is only a warning
    /// (Msg 14236), and the job's version rises.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddJobStep(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_jobstep", SpAddJobStepParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_jobstep", AgentUserRoleId);
        if (!call.IsNull(14) && !AgentIsSysadmin(batch))
            throw SimulatedSqlException.AgentDatabaseUserNeedsSysadmin().AtAgentSite(59);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        RequireAgentJobOwner(batch, job, 75, "dbo.sp_add_jobstep_internal");
        var jobId = AgentJobId(job);
        var stepCount = this.AgentStepCount(batch, jobId);
        var stepId = call.Int(2) ?? (stepCount + 1);
        if (stepId < 1 || stepId > stepCount + 1)
            throw SimulatedSqlException.AgentInvalidValue("@step_id", $"1..{stepCount + 1}").AtAgentSite(46, "sp_verify_jobstep");
        var step = new AgentStep(call, 3);
        this.VerifyAgentStepProxy(batch, step);
        var warnings = new List<SimulatedStatementOutcome>();
        this.VerifyAgentJobStep(batch, jobId, step, stepId, stepCount + 1, excludeStepId: null, warnings);
        if (call.Text(3) is null)
            throw SimulatedSqlException.AgentStepNameNull().AtAgentSite(255, "dbo.sp_add_jobstep_internal");
        foreach (var warning in warnings)
            yield return warning;
        var stepUid = Guid.NewGuid();
        _ = this.AgentSql(batch, """
            update dbo.sysjobsteps set step_id = step_id + 1 where job_id = @id and step_id >= @step;
            insert dbo.sysjobsteps (job_id, step_id, step_name, subsystem, command, flags, additional_parameters, cmdexec_success_code,
                on_success_action, on_success_step_id, on_fail_action, on_fail_step_id, server, database_name, database_user_name,
                retry_attempts, retry_interval, os_run_priority, output_file_name, last_run_outcome, last_run_duration, last_run_retries,
                last_run_date, last_run_time, proxy_id, step_uid)
            values (@id, @step, @name, @subsystem, @command, @flags, @additional, @success_code, @success_action, @success_step,
                @fail_action, @fail_step, @server, @database, @user, @retries, @interval, @priority, @output, 0, 0, 0, 0, 0, @proxy, @uid);
            update dbo.sysjobs set version_number = version_number + 1, date_modified = getdate() where job_id = @id;
            """,
            [("id", jobId), ("step", SqlValue.FromInt32(stepId)), .. step.Parameters(), ("uid", SqlValue.FromGuid(stepUid))]);
        call.SetOutput(22, SqlValue.FromGuid(stepUid));
        // Only a job's first step reaches the Agent (probed 2026-10-16: a
        // targeted job's later steps send no notice, at any position).
        if (stepCount == 0 && this.AgentJobTargetsLocalServer(batch, jobId))
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// One step's attributes as a call to <c>sp_add_jobstep</c> or
    /// <c>sp_update_jobstep</c> gives them, the update's merged over the step's
    /// own, in the parameter order the two procedures share from
    /// <c>@step_name</c> on.
    /// </summary>
    private sealed class AgentStep
    {
        public string? Name;
        public string Subsystem = "TSQL";
        public SqlValue Command = SqlValue.Null(SqlType.NVarchar);
        public SqlValue AdditionalParameters = SqlValue.Null(SqlType.NVarchar);
        public int CmdExecSuccessCode;
        public int OnSuccessAction = 1;
        public int OnSuccessStepId;
        public int OnFailAction = 2;
        public int OnFailStepId;
        public string? Server;
        public string? DatabaseName;
        public string? DatabaseUserName;
        public int RetryAttempts;
        public int RetryInterval;
        public int OsRunPriority;
        public SqlValue OutputFileName = SqlValue.Null(SqlType.NVarchar);
        public int Flags;
        public int? ProxyId;
        public string? ProxyName;

        public AgentStep()
        {
        }

        /// <summary>The attributes <paramref name="call"/> gives, <paramref name="first"/> the index of its <c>@step_name</c>.</summary>
        public AgentStep(AgentCall call, int first)
        {
            this.Merge(call, first);
        }

        /// <summary>The step <paramref name="row"/> of <c>sysjobsteps</c> holds.</summary>
        public static AgentStep From(AgentRow row) => new()
        {
            Name = row.Text("step_name"),
            Subsystem = row.Text("subsystem")!,
            Command = row["command"],
            AdditionalParameters = row["additional_parameters"],
            CmdExecSuccessCode = row.Int("cmdexec_success_code"),
            OnSuccessAction = row.Int("on_success_action"),
            OnSuccessStepId = row.Int("on_success_step_id"),
            OnFailAction = row.Int("on_fail_action"),
            OnFailStepId = row.Int("on_fail_step_id"),
            Server = row.Text("server"),
            DatabaseName = row.Text("database_name"),
            DatabaseUserName = row.Text("database_user_name"),
            RetryAttempts = row.Int("retry_attempts"),
            RetryInterval = row.Int("retry_interval"),
            OsRunPriority = row.Int("os_run_priority"),
            OutputFileName = row["output_file_name"],
            Flags = row.Int("flags"),
            ProxyId = row["proxy_id"].IsNull ? null : row.Int("proxy_id"),
        };

        /// <summary>Overwrites each attribute <paramref name="call"/> gives a value for.</summary>
        public void Merge(AgentCall call, int first)
        {
            this.Name = call.Text(first) is { } name ? name.Trim() : this.Name;
            if (call.Text(first + 1) is { } subsystem)
                this.Subsystem = subsystem;
            this.Command = call.IsNull(first + 2) ? this.Command : call.Values[first + 2];
            this.AdditionalParameters = call.IsNull(first + 3) ? this.AdditionalParameters : call.Values[first + 3];
            this.CmdExecSuccessCode = call.Int(first + 4) ?? this.CmdExecSuccessCode;
            this.OnSuccessAction = call.Int(first + 5) ?? this.OnSuccessAction;
            this.OnSuccessStepId = call.Int(first + 6) ?? this.OnSuccessStepId;
            this.OnFailAction = call.Int(first + 7) ?? this.OnFailAction;
            this.OnFailStepId = call.Int(first + 8) ?? this.OnFailStepId;
            this.Server = call.Text(first + 9) ?? this.Server;
            this.DatabaseName = call.Text(first + 10) ?? this.DatabaseName;
            this.DatabaseUserName = call.Text(first + 11) ?? this.DatabaseUserName;
            this.RetryAttempts = call.Int(first + 12) ?? this.RetryAttempts;
            this.RetryInterval = call.Int(first + 13) ?? this.RetryInterval;
            this.OsRunPriority = call.Int(first + 14) ?? this.OsRunPriority;
            this.OutputFileName = call.IsNull(first + 15) ? this.OutputFileName : call.Values[first + 15];
            this.Flags = call.Int(first + 16) ?? this.Flags;
            this.ProxyId = call.Int(first + 17) ?? this.ProxyId;
            this.ProxyName = call.Text(first + 18) ?? this.ProxyName;
        }

        public bool IsTransactSql => BuiltInToken.Equals(this.Subsystem, "TSQL");

        /// <summary>The step's attributes as the variables the Agent statements write them through.</summary>
        public (string, SqlValue)[] Parameters() =>
        [
            ("name", this.Name is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(this.Name)),
            ("subsystem", SqlValue.FromNVarchar(this.Subsystem)),
            ("command", this.Command),
            ("flags", SqlValue.FromInt32(this.Flags)),
            ("additional", this.AdditionalParameters),
            ("success_code", SqlValue.FromInt32(this.CmdExecSuccessCode)),
            ("success_action", SqlValue.FromInt32(this.OnSuccessAction)),
            ("success_step", SqlValue.FromInt32(this.OnSuccessStepId)),
            ("fail_action", SqlValue.FromInt32(this.OnFailAction)),
            ("fail_step", SqlValue.FromInt32(this.OnFailStepId)),
            ("server", Optional(this.Server)),
            ("database", Optional(this.IsTransactSql ? this.DatabaseName ?? MasterDatabaseName : null)),
            ("user", Optional(this.IsTransactSql ? this.DatabaseUserName : null)),
            ("retries", SqlValue.FromInt32(this.RetryAttempts)),
            ("interval", SqlValue.FromInt32(this.RetryInterval)),
            ("priority", SqlValue.FromInt32(this.OsRunPriority)),
            ("output", this.OutputFileName),
            ("proxy", this.ProxyId is { } proxy ? SqlValue.FromInt32(proxy) : SqlValue.Null(SqlType.Int32)),
        ];

        private static SqlValue Optional(string? text) => text is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(text);
    }

    /// <summary>
    /// The subsystems a Linux server's Agent takes a step for, as
    /// <c>sp_verify_subsystem</c> reads them (probed 2026-10-16): the six
    /// <c>sp_enum_sqlagent_subsystems</c> lists, CmdExec and PowerShell
    /// beside them, and the two Analysis Services ones, which then need a
    /// server. SSIS and ActiveScripting are refused.
    /// </summary>
    private static readonly string[] AgentSubsystems =
        ["ANALYSISCOMMAND", "ANALYSISQUERY", "CmdExec", "Distribution", "LogReader", "Merge", "PowerShell", "QueueReader", "Snapshot", "TSQL"];

    /// <summary>
    /// <c>sp_verify_subsystem</c> and <c>sp_verify_jobstep</c>, in real's
    /// order and at its lines: a known subsystem (Msg 14234), a step name no
    /// other step of the job holds (14261), success and failure actions 1 to
    /// 4 (14266), a <c>goto</c> step that exists (the warning Msg 14236,
    /// added to <paramref name="warnings"/>), an Analysis Services step's
    /// server, a run priority and flags from real's lists, flags the
    /// subsystem takes (14545), and an existing database and user (14262).
    /// </summary>
    private void VerifyAgentJobStep(BatchContext batch, SqlValue jobId, AgentStep step, int stepId, int stepCountAfter, int? excludeStepId, List<SimulatedStatementOutcome> warnings)
    {
        const string verifier = "sp_verify_jobstep";
        if (Array.FindIndex(AgentSubsystems, known => BuiltInToken.Equals(known, step.Subsystem)) < 0)
            throw SimulatedSqlException.AgentInvalidReturnedBy("@subsystem", "sp_enum_sqlagent_subsystems").AtAgentSite(28, "sp_verify_subsystem");
        if (step.Name is { } name
            && this.AgentScalar(batch, "select step_id from dbo.sysjobsteps where job_id = @id and step_name = @name", ("id", jobId), ("name", SqlValue.FromNVarchar(name))) is { } holder
            && holder.AsInt32 != excludeStepId)
        {
            throw SimulatedSqlException.AgentAlreadyExists("@step_name", name).AtAgentSite(107, verifier);
        }
        if (step.OnSuccessAction is < 1 or > 4)
            throw SimulatedSqlException.AgentInvalidValue("@on_success_action", "1, 2, 3, 4").AtAgentSite(117, verifier);
        if (step.OnFailAction is < 1 or > 4)
            throw SimulatedSqlException.AgentInvalidValue("@on_failure_action", "1, 2, 3, 4").AtAgentSite(135, verifier);
        if (step.OnSuccessAction == 4 && (step.OnSuccessStepId < 1 || step.OnSuccessStepId == stepId))
            throw SimulatedSqlException.AgentGotoStepInvalid("@on_success_step", stepId).AtAgentSite(125, verifier);
        if (step.OnFailAction == 4 && (step.OnFailStepId < 1 || step.OnFailStepId == stepId))
            throw SimulatedSqlException.AgentGotoStepInvalid("@on_failure_step", stepId).AtAgentSite(143, verifier);
        if (step.OnSuccessAction == 4 && step.OnSuccessStepId > stepCountAfter)
            warnings.Add(new SimulatedInfoOutcome(SimulatedSqlException.SystemProcedureMessage(batch, verifier, 149, 14236, "Warning: Non-existent step referenced by @on_success_step_id.")));
        if (step.OnFailAction == 4 && step.OnFailStepId > stepCountAfter)
            warnings.Add(new SimulatedInfoOutcome(SimulatedSqlException.SystemProcedureMessage(batch, verifier, 151, 14236, "Warning: Non-existent step referenced by @on_fail_step_id.")));
        if (BuiltInToken.Equals(step.Subsystem, "ANALYSISQUERY") || BuiltInToken.Equals(step.Subsystem, "ANALYSISCOMMAND"))
        {
            if (step.Server is null)
                throw SimulatedSqlException.AgentDoesNotExist("@server", "(null)").AtAgentSite(161, verifier);
        }
        else if (step.Server is { } server && !BuiltInToken.Equals(server, AgentLocalServer) && !BuiltInToken.Equals(server, "(local)"))
        {
            throw SimulatedSqlException.AgentInvalidReturnedBy("@srv", "sp_helpserver").AtAgentSite(182, verifier);
        }
        if (step.OsRunPriority is not (-15 or -1 or 0 or 1 or 15))
            throw SimulatedSqlException.AgentInvalidValue("@os_run_priority", "-15, -1, 0, 1, 15").AtAgentSite(191, verifier);
        if (step.Flags is < 0 or > 114)
            throw SimulatedSqlException.AgentInvalidValue("@flags", "0..114").AtAgentSite(198, verifier);
        if (step.IsTransactSql && (step.Flags & 64) != 0)
            throw SimulatedSqlException.AgentFlagsInvalidForSubsystem("TSQL").AtAgentSite(218, verifier);
        if (step.IsTransactSql)
        {
            var database = step.DatabaseName ?? MasterDatabaseName;
            if (!this.Databases.TryGetValue(database, out var target))
                throw SimulatedSqlException.AgentDoesNotExist("@database_name", database).AtAgentSite(262, verifier);
            if (step.DatabaseUserName is { } user && !target.Principals.ContainsKey(user))
                throw SimulatedSqlException.AgentDoesNotExist("@database_user_name", user).AtAgentSite(302, verifier);
        }
    }

    /// <summary>The proxy a step names, which must exist (Msg 14262) before the step itself is verified.</summary>
    private void VerifyAgentStepProxy(BatchContext batch, AgentStep step)
    {
        if (step.ProxyName is { } proxyName
            && this.AgentScalar(batch, "select proxy_id from dbo.sysproxies where name = @name", ("name", SqlValue.FromNVarchar(proxyName))) is null)
        {
            throw SimulatedSqlException.AgentDoesNotExist("@proxy_name", proxyName).AtAgentSite(51, "sp_verify_proxy_identifiers");
        }
    }

    private static readonly SystemProcedureParameter[] SpUpdateJobStepParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentRequired("step_id", SqlType.Int32),
        AgentParameter("step_name", AgentName, 128),
        AgentParameter("subsystem", AgentName),
        AgentParameter("command", AgentText),
        AgentParameter("additional_parameters", AgentText),
        AgentParameter("cmdexec_success_code", SqlType.Int32),
        AgentParameter("on_success_action", SqlType.TinyInt),
        AgentParameter("on_success_step_id", SqlType.Int32),
        AgentParameter("on_fail_action", SqlType.TinyInt),
        AgentParameter("on_fail_step_id", SqlType.Int32),
        AgentParameter("server", AgentName, 128),
        AgentParameter("database_name", AgentName, 128),
        AgentParameter("database_user_name", AgentName, 128),
        AgentParameter("retry_attempts", SqlType.Int32),
        AgentParameter("retry_interval", SqlType.Int32),
        AgentParameter("os_run_priority", SqlType.Int32),
        AgentParameter("output_file_name", AgentText, 200),
        AgentParameter("flags", SqlType.Int32),
        AgentParameter("proxy_id", SqlType.Int32),
        AgentParameter("proxy_name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_update_jobstep</c>: each attribute given replaces the step's
    /// own, verified as <c>sp_add_jobstep</c> verifies a new one; a step the
    /// job doesn't have is Msg 14262, and the job's version rises.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateJobStep(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_update_jobstep", SpUpdateJobStepParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_update_jobstep", AgentUserRoleId);
        if (!call.IsNull(14) && !AgentIsSysadmin(batch))
            throw SimulatedSqlException.AgentDatabaseUserNeedsSysadmin().AtAgentSite(59);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        RequireAgentJobOwner(batch, job, 113, null);
        var jobId = AgentJobId(job);
        var stepId = call.Int(2)!.Value;
        if (this.AgentRows(batch, "sysjobsteps", "job_id = @id and step_id = @step", ("id", jobId), ("step", SqlValue.FromInt32(stepId))) is not [var row, ..])
            throw SimulatedSqlException.AgentDoesNotExist("@step_id", stepId.ToString(System.Globalization.CultureInfo.InvariantCulture)).AtAgentSite(124);
        var step = AgentStep.From(row);
        step.Merge(call, 3);
        this.VerifyAgentStepProxy(batch, step);
        var warnings = new List<SimulatedStatementOutcome>();
        this.VerifyAgentJobStep(batch, jobId, step, stepId, this.AgentStepCount(batch, jobId), excludeStepId: stepId, warnings);
        foreach (var warning in warnings)
            yield return warning;
        _ = this.AgentSql(batch, """
            update dbo.sysjobsteps set step_name = @name, subsystem = @subsystem, command = @command, flags = @flags,
                additional_parameters = @additional, cmdexec_success_code = @success_code, on_success_action = @success_action,
                on_success_step_id = @success_step, on_fail_action = @fail_action, on_fail_step_id = @fail_step, server = @server,
                database_name = @database, database_user_name = @user, retry_attempts = @retries, retry_interval = @interval,
                os_run_priority = @priority, output_file_name = @output, proxy_id = @proxy
            where job_id = @id and step_id = @step;
            update dbo.sysjobs set version_number = version_number + 1, date_modified = getdate() where job_id = @id;
            """,
            [("id", jobId), ("step", SqlValue.FromInt32(stepId)), .. step.Parameters()]);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteJobStepParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentRequired("step_id", SqlType.Int32),
    ];

    /// <summary>
    /// <c>sp_delete_jobstep</c>: step <c>@step_id</c>, the steps past it moving
    /// down one and every <c>goto</c> naming it or a step past it returning to
    /// the step's default action (probed 2026-10-16 against SQL Server 2025),
    /// or with 0 every step; any other number outside the job's
    /// steps is Msg 14266. Deleting every step reaches the job's own update,
    /// so a job targeted at the local server gets the stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteJobStep(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_jobstep", SpDeleteJobStepParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_jobstep", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        RequireAgentJobOwner(batch, job, 32, null);
        var jobId = AgentJobId(job);
        var stepId = call.Int(2)!.Value;
        var stepCount = this.AgentStepCount(batch, jobId);
        if (stepId < 0 || stepId > stepCount)
            throw SimulatedSqlException.AgentInvalidValue("@step_id", $"0 (all steps) ..{stepCount}").AtAgentSite(45);
        if (stepId == 0)
        {
            _ = this.AgentSql(batch, """
                delete dbo.sysjobsteps where job_id = @id;
                update dbo.sysjobs set start_step_id = 1, version_number = version_number + 1, date_modified = getdate() where job_id = @id;
                """, ("id", jobId));
            if (this.AgentJobTargetsLocalServer(batch, jobId))
                yield return AgentNotRunningNotice(batch);
        }
        else
        {
            _ = this.AgentSql(batch, """
                delete dbo.sysjobsteps where job_id = @id and step_id = @step;
                update dbo.sysjobsteps set step_id = step_id - 1 where job_id = @id and step_id > @step;
                update dbo.sysjobsteps set on_success_action = 1, on_success_step_id = 0 where job_id = @id and on_success_action = 4 and on_success_step_id >= @step;
                update dbo.sysjobsteps set on_fail_action = 2, on_fail_step_id = 0 where job_id = @id and on_fail_action = 4 and on_fail_step_id >= @step;
                update dbo.sysjobs set version_number = version_number + 1, date_modified = getdate() where job_id = @id;
                """, ("id", jobId), ("step", SqlValue.FromInt32(stepId)));
        }
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpAddJobServerParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("server_name", AgentName, 128),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>Whether a server name a call gives names the local server: NULL, <c>(local)</c> or its own name, in any case.</summary>
    private static bool IsAgentLocalServer(string? server) =>
        server is null || BuiltInToken.Equals(server, "(local)") || BuiltInToken.Equals(server, AgentLocalServer);

    /// <summary>
    /// <c>sp_add_jobserver</c>: targets the job at the local server, the only
    /// one a server outside a multi-server setup has (Msg 14262 for any other,
    /// named in upper case), once (Msg 14269), and sends the stopped Agent's
    /// notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddJobServer(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_jobserver", SpAddJobServerParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_jobserver", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        if (!IsAgentLocalServer(call.Text(2)))
            throw SimulatedSqlException.AgentDoesNotExist("@server_name", call.Text(2)!.ToUpperInvariant()).AtAgentSite(88);
        var jobId = AgentJobId(job);
        if (this.AgentJobTargetsLocalServer(batch, jobId))
            throw SimulatedSqlException.AgentJobAlreadyTargeted(job.Text("name")!, AgentLocalServer.ToUpperInvariant()).AtAgentSite(101);
        _ = this.AgentSql(batch, """
            insert dbo.sysjobservers (job_id, server_id, last_run_outcome, last_outcome_message, last_run_date, last_run_time, last_run_duration)
            values (@id, 0, 5, null, 0, 0, 0)
            """, ("id", jobId));
        yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteJobServerParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentRequired("server_name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_delete_jobserver</c>: removes the job's local target — any other
    /// server is Msg 14262, a job not targeted Msg 14270 — and sends the
    /// stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteJobServer(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_jobserver", SpDeleteJobServerParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_jobserver", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        if (!IsAgentLocalServer(call.Text(2)))
            throw SimulatedSqlException.AgentDoesNotExist("@server_name", call.Text(2)!.ToUpperInvariant()).AtAgentSite(47);
        var jobId = AgentJobId(job);
        if (!this.AgentJobTargetsLocalServer(batch, jobId))
            throw SimulatedSqlException.AgentJobNotTargeted(job.Text("name")!, AgentLocalServer.ToUpperInvariant()).AtAgentSite(62);
        _ = this.AgentSql(batch, "delete dbo.sysjobservers where job_id = @id and server_id = 0", ("id", jobId));
        yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpStartJobParameters =
    [
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("error_flag", SqlType.Int32, SqlValue.FromInt32(1)),
        AgentParameter("server_name", AgentName, 128),
        AgentParameter("step_name", AgentName, 128),
        AgentParameter("output_flag", SqlType.Int32, SqlValue.FromInt32(1)),
    ];

    private static readonly SystemProcedureParameter[] SpStopJobParameters =
    [
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("originating_server", AgentName, 128),
        AgentParameter("server_name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_start_job</c> / <c>sp_stop_job</c>: after real's checks — a job
    /// named (Msg 14294, or 14232 for a stop), its owner or a member of
    /// <c>sysadmin</c> or <c>SQLAgentOperatorRole</c> (14393), a job that
    /// targets a server (14256 / 14257) and a start step that exists (14262)
    /// — the request reaches the stopped Agent service, whose Msg 22022 goes
    /// to the client past any <c>TRY</c>, and the procedure answers 1.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpStartStopJob(BatchContext batch, string? returnCodeVar, bool start)
    {
        var procedure = start ? "sp_start_job" : "sp_stop_job";
        if (BindAgentCall(batch, procedure, start ? SpStartJobParameters : SpStopJobParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, procedure, AgentUserRoleId);
        if (!start && call.IsNull(0) && call.IsNull(1) && call.IsNull(2))
            throw SimulatedSqlException.AgentStopJobNeedsIdentifier().AtAgentSite(35);
        var job = this.ResolveAgentJob(batch, call.Guid(1), call.Text(0));
        var mayRun = AgentIsSysadmin(batch) || AgentOwns(batch, job) || this.AgentInRole(batch, AgentOperatorRoleId);
        // A start checks the caller first, a stop the job's server first
        // (probed 2026-10-16 against SQL Server 2025).
        if (start && !mayRun)
            throw SimulatedSqlException.AgentStartStopNotOwner().AtAgentSite(55);
        var jobId = AgentJobId(job);
        if (!this.AgentJobTargetsLocalServer(batch, jobId))
            throw SimulatedSqlException.AgentJobHasNoServer(start, job.Text("name")!, jobId.AsGuid).AtAgentSite(start ? 64 : 103);
        if (!mayRun)
            throw SimulatedSqlException.AgentStartStopNotOwner().AtAgentSite(114);
        if (start && call.Text(4) is { } stepName
            && this.AgentScalar(batch, "select step_id from dbo.sysjobsteps where job_id = @id and step_name = @name", ("id", jobId), ("name", SqlValue.FromNVarchar(stepName))) is null)
        {
            throw SimulatedSqlException.AgentDoesNotExist("@step_name", stepName).AtAgentSite(85);
        }
        AgentReturn(batch, returnCodeVar, 1);
        yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.AgentNotRunning());
    }
}
