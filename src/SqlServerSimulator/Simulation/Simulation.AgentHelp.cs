using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Agent procedures that report jobs, steps, schedules, target servers,
// history, activity, categories, operators and alerts, each in SQL Server
// 2025's columns, types, nullability and order (probed 2026-10-16). Each job
// reads as Agent's stopped service leaves it: idle (current_execution_status
// 4), never run (last_run_outcome 5) and unscheduled (next run 0).
partial class Simulation
{
    /// <summary>
    /// A result set of the columns <paramref name="specs"/> describes, in
    /// <see cref="MsdbTables"/>' <c>name type [null]</c> form, each row's
    /// values converted to its column's type.
    /// </summary>
    private SimulatedSqlResultSet AgentResultSet(string[] specs, List<SqlValue[]> rows)
    {
        var msdb = this.Databases[MsdbDatabaseName];
        var columns = Array.ConvertAll(specs, spec => MsdbColumn(msdb, spec));
        var types = Array.ConvertAll(columns, column => column.Type);
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
                row[i] = row[i].CoerceTo(types[i]);
        }
        return new SimulatedSqlResultSet(types, Array.ConvertAll(columns, column => column.Name), rows)
        {
            ColumnNullability = Array.ConvertAll(columns, column => column.Nullable),
            CountSuppressed = true,
        };
    }

    private static SimulatedInfoOutcome AgentPrint(BatchContext batch, string procedure, int line, int number, string text) =>
        new(SimulatedSqlException.SystemProcedureMessage(batch, procedure, line, number, text));

    private static SqlValue AgentNText(string? text) => text is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(text);

    private static readonly SystemProcedureParameter[] SpHelpJobParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("job_aspect", AgentVarchar, 9),
        AgentParameter("job_type", AgentVarchar, 12),
        AgentParameter("owner_login_name", AgentName, 128),
        AgentParameter("subsystem", AgentName, 40),
        AgentParameter("category_name", AgentName, 128),
        AgentParameter("enabled", SqlType.TinyInt),
        AgentParameter("execution_status", SqlType.Int32),
        AgentParameter("date_comparator", AgentVarchar, 1),
        AgentParameter("date_created", SqlType.DateTime),
        AgentParameter("date_last_modified", SqlType.DateTime),
        AgentParameter("description", AgentText, 512),
    ];

    private static readonly string[] AgentJobColumns =
    [
        "job_id uniqueidentifier null", "originating_server nvarchar(128) null", "name nvarchar(128) null", "enabled tinyint null",
        "description nvarchar(512) null", "start_step_id int null", "category nvarchar(128) null", "owner nvarchar(128) null",
        "notify_level_eventlog int null", "notify_level_email int null", "notify_level_netsend int null", "notify_level_page int null",
        "notify_email_operator nvarchar(128) null", "notify_netsend_operator nvarchar(128) null", "notify_page_operator nvarchar(128) null",
        "delete_level int null", "date_created datetime null", "date_modified datetime null", "version_number int null",
        "last_run_date int", "last_run_time int", "last_run_outcome int", "next_run_date int", "next_run_time int",
        "next_run_schedule_id int", "current_execution_status int", "current_execution_step nvarchar(max) null", "current_retry_attempt int",
        "has_step int null", "has_schedule int null", "has_target int null", "type int",
    ];

    /// <summary>
    /// <c>sp_help_job</c>: with a job named, its details by <c>@job_aspect</c>
    /// — <c>ALL</c> (the default) prints a heading before each of the four
    /// sets — and with none, every job the caller sees that the filters keep,
    /// in job-id order. A job together with a filter is Msg 14280.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpJob(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_job", SpHelpJobParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_job", AgentUserRoleId);
        var filtered = false;
        for (var i = 3; i < 13; i++)
            filtered |= !call.IsNull(i);
        var named = !call.IsNull(0) || !call.IsNull(1);
        if (named && filtered)
            throw SimulatedSqlException.AgentHelpJobMixedArguments().AtAgentSite(74);
        if (!named)
        {
            yield return this.AgentResultSet(AgentJobColumns, this.AgentJobRows(batch, this.FilteredAgentJobs(batch, call)));
            AgentReturn(batch, returnCodeVar, 0);
            yield break;
        }
        var aspect = (call.Text(2) ?? "ALL").Trim().ToUpperInvariant();
        if (aspect is not ("ALL" or "JOB" or "SCHEDULES" or "STEPS" or "TARGETS"))
            throw SimulatedSqlException.AgentInvalidValue("@job_aspect", "JOB, STEPS, SCHEDULES, TARGETS, ALL").AtAgentSite(86);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        var jobId = AgentJobId(job);
        var all = aspect == "ALL";
        if (all)
        {
            yield return AgentPrint(batch, "sp_help_job", 96, 14213, "Core Job Details:");
            yield return AgentPrint(batch, "sp_help_job", 97, 0, "=================");
        }
        if (all || aspect == "JOB")
            yield return this.AgentResultSet(AgentJobColumns, this.AgentJobRows(batch, [job]));
        if (all)
        {
            yield return AgentPrint(batch, "sp_help_job", 116, 0, " ");
            yield return AgentPrint(batch, "sp_help_job", 117, 14214, "Job Steps:");
            yield return AgentPrint(batch, "sp_help_job", 118, 0, "==========");
        }
        if (all || aspect == "STEPS")
            yield return this.AgentStepsResultSet(batch, jobId, null, described: true);
        if (all)
        {
            yield return AgentPrint(batch, "sp_help_job", 127, 0, " ");
            yield return AgentPrint(batch, "sp_help_job", 128, 14215, "Job Schedules:");
            yield return AgentPrint(batch, "sp_help_job", 129, 0, "==============");
        }
        if (all || aspect == "SCHEDULES")
            yield return this.AgentJobSchedulesResultSet(batch, jobId, null, describe: false);
        if (all)
        {
            yield return AgentPrint(batch, "sp_help_job", 138, 0, " ");
            yield return AgentPrint(batch, "sp_help_job", 139, 14216, "Job Target Servers:");
            yield return AgentPrint(batch, "sp_help_job", 140, 0, "===================");
        }
        if (all || aspect == "TARGETS")
            yield return this.AgentTargetsResultSet(batch, jobId, details: true);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>The jobs the caller sees that <c>sp_help_job</c>'s filters keep, in job-id order.</summary>
    private List<AgentRow> FilteredAgentJobs(BatchContext batch, AgentCall call)
    {
        var conditions = new List<string> { "1 = 1" };
        var parameters = new List<(string Name, SqlValue Value)>();
        if (call.Text(3) is { } jobType)
            conditions.Add(BuiltInToken.Equals(jobType.Trim(), "MULTI-SERVER") ? "1 = 0" : "1 = 1");
        if (call.Text(4) is { } owner)
        {
            conditions.Add("owner_sid = @owner");
            parameters.Add(("owner", SqlValue.FromVarbinary(VarbinarySqlType.Get(85), LoginSid(batch.Connection.Simulation, owner))));
        }
        if (call.Text(5) is { } subsystem)
        {
            conditions.Add("job_id in (select job_id from dbo.sysjobsteps where subsystem = @subsystem)");
            parameters.Add(("subsystem", SqlValue.FromNVarchar(subsystem)));
        }
        if (call.Text(6) is { } category)
        {
            conditions.Add("category_id in (select category_id from dbo.syscategories where name = @category)");
            parameters.Add(("category", SqlValue.FromNVarchar(category)));
        }
        if (!call.IsNull(7))
        {
            conditions.Add("enabled = @enabled");
            parameters.Add(("enabled", call.Values[7]));
        }
        // Real's filter asks the stopped service which jobs are in that
        // state, which answers none, idle included (probed 2026-10-19
        // against SQL Server 2025).
        if (!call.IsNull(8))
            conditions.Add("1 = 0");
        var comparator = call.Text(9)?.Trim() is { Length: 1 } written && written is "<" or ">" ? written : "=";
        if (!call.IsNull(10))
        {
            conditions.Add($"date_created {comparator} @created");
            parameters.Add(("created", call.Values[10]));
        }
        if (!call.IsNull(11))
        {
            conditions.Add($"date_modified {comparator} @modified");
            parameters.Add(("modified", call.Values[11]));
        }
        if (call.Text(12) is { } description)
        {
            conditions.Add("description like @description");
            parameters.Add(("description", SqlValue.FromNVarchar(description)));
        }
        var jobs = this.AgentRows(batch, "sysjobs", string.Join(" and ", conditions) + " order by job_id", [.. parameters]);
        if (!this.AgentSeesAllJobs(batch))
            _ = jobs.RemoveAll(job => !AgentOwns(batch, job));
        return jobs;
    }

    /// <summary><c>sp_help_job</c>'s job details for <paramref name="jobs"/>.</summary>
    private List<SqlValue[]> AgentJobRows(BatchContext batch, List<AgentRow> jobs)
    {
        var rows = new List<SqlValue[]>(jobs.Count);
        foreach (var job in jobs)
        {
            var jobId = AgentJobId(job);
            var counts = this.AgentSql(batch, """
                select (select category.name from dbo.syscategories as category where category.category_id = @category),
                    (select count(*) from dbo.sysjobsteps where job_id = @id),
                    (select count(*) from dbo.sysjobschedules where job_id = @id),
                    (select count(*) from dbo.sysjobservers where job_id = @id),
                    (select name from dbo.sysoperators where id = @email),
                    (select name from dbo.sysoperators where id = @netsend),
                    (select name from dbo.sysoperators where id = @page),
                    isnull((select top (1) last_run_date from dbo.sysjobservers where job_id = @id), 0),
                    isnull((select top (1) last_run_time from dbo.sysjobservers where job_id = @id), 0),
                    isnull((select top (1) convert(int, last_run_outcome) from dbo.sysjobservers where job_id = @id), 5)
                """,
                ("id", jobId), ("category", job["category_id"]), ("email", job["notify_email_operator_id"]),
                ("netsend", job["notify_netsend_operator_id"]), ("page", job["notify_page_operator_id"]))[0];
            var targets = counts[3].AsInt32;
            rows.Add(
            [
                jobId, SqlValue.FromNVarchar(AgentLocalServer.ToUpperInvariant()), job["name"], job["enabled"], job["description"],
                job["start_step_id"], counts[0], AgentNText(this.LoginNameForSid(job["owner_sid"])), job["notify_level_eventlog"],
                job["notify_level_email"], job["notify_level_netsend"], job["notify_level_page"],
                counts[4].IsNull ? SqlValue.FromNVarchar("(unknown)") : counts[4], counts[5].IsNull ? SqlValue.FromNVarchar("(unknown)") : counts[5],
                counts[6].IsNull ? SqlValue.FromNVarchar("(unknown)") : counts[6], job["delete_level"], job["date_created"], job["date_modified"],
                job["version_number"], counts[7], counts[8], counts[9], SqlValue.FromInt32(0), SqlValue.FromInt32(0), SqlValue.FromInt32(0),
                SqlValue.FromInt32(4), SqlValue.FromNVarchar("0 (unknown)"), SqlValue.FromInt32(0), counts[1], counts[2], counts[3],
                SqlValue.FromInt32(targets > 0 ? 1 : 0),
            ]);
        }
        return rows;
    }

    private static readonly string[] AgentStepColumns =
    [
        "step_id int", "step_name nvarchar(128)", "subsystem nvarchar(40)", "command nvarchar(max) null", "flags int",
        "cmdexec_success_code int", "on_success_action tinyint", "on_success_step_id int", "on_fail_action tinyint", "on_fail_step_id int",
        "server nvarchar(128) null", "database_name nvarchar(128) null", "database_user_name nvarchar(128) null", "retry_attempts int",
        "retry_interval int", "os_run_priority int", "output_file_name nvarchar(200) null", "last_run_outcome int", "last_run_duration int",
        "last_run_retries int", "last_run_date int", "last_run_time int", "proxy_id int null",
    ];

    /// <summary><see cref="AgentStepColumns"/> as <c>sp_help_job</c> reports them, the flags, actions and run priority spelled out.</summary>
    private static readonly string[] AgentDescribedStepColumns =
        Array.ConvertAll(AgentStepColumns, spec => spec.Split(' ')[0] is "flags" or "on_success_action" or "on_fail_action" or "os_run_priority"
            ? $"{spec.Split(' ')[0]} nvarchar(4000) null"
            : spec);

    /// <summary>
    /// A job's steps, in step order — every one, or the one
    /// <paramref name="stepId"/> names — with <paramref name="described"/> the
    /// flags, actions and run priority spelled out as <c>sp_help_job</c>
    /// spells them.
    /// </summary>
    private SimulatedSqlResultSet AgentStepsResultSet(BatchContext batch, SqlValue jobId, int? stepId, bool described)
    {
        var steps = this.AgentRows(batch, "sysjobsteps", stepId is null ? "job_id = @id order by step_id" : "job_id = @id and step_id = @step order by step_id",
            ("id", jobId), ("step", SqlValue.FromInt32(stepId ?? 0)));
        var rows = steps.ConvertAll(step =>
        {
            var flags = step.Int("flags");
            var onSuccess = step.Int("on_success_action");
            var onFail = step.Int("on_fail_action");
            var priority = step.Int("os_run_priority");
            return new[]
            {
                step["step_id"], step["step_name"], step["subsystem"], step["command"],
                described ? SqlValue.FromNVarchar(DescribeAgentStepFlags(flags)) : step["flags"], step["cmdexec_success_code"],
                described ? SqlValue.FromNVarchar(DescribeAgentStepAction(onSuccess)) : step["on_success_action"], step["on_success_step_id"],
                described ? SqlValue.FromNVarchar(DescribeAgentStepAction(onFail)) : step["on_fail_action"], step["on_fail_step_id"],
                step["server"], step["database_name"], step["database_user_name"], step["retry_attempts"], step["retry_interval"],
                described ? SqlValue.FromNVarchar(DescribeAgentRunPriority(priority)) : step["os_run_priority"], step["output_file_name"],
                step["last_run_outcome"], step["last_run_duration"], step["last_run_retries"], step["last_run_date"], step["last_run_time"],
                step["proxy_id"],
            };
        });
        return this.AgentResultSet(described ? AgentDescribedStepColumns : AgentStepColumns, rows);
    }

    private static string DescribeAgentStepFlags(int flags)
    {
        var parts = new List<string>();
        if (flags == 0)
            parts.Add("(normal)");
        if ((flags & 1) != 0)
            parts.Add("(encrypted command)");
        if ((flags & 2) != 0)
            parts.Add("(append output file)");
        if ((flags & 4) != 0)
            parts.Add("(include results in history)");
        return string.Create(CultureInfo.InvariantCulture, $"{flags} ({string.Join(", ", parts)})");
    }

    private static string DescribeAgentStepAction(int action) => action switch
    {
        1 => "1 (quit with success)",
        2 => "2 (quit with failure)",
        3 => "3 (goto next step)",
        4 => "4 (goto step)",
        _ => action.ToString(CultureInfo.InvariantCulture),
    };

    private static string DescribeAgentRunPriority(int priority) => priority switch
    {
        -15 => "-15 (idle)",
        -1 => "-1 (below normal)",
        0 => "0 (normal)",
        1 => "1 (above normal)",
        15 => "15 (time critical)",
        _ => priority.ToString(CultureInfo.InvariantCulture),
    };

    private static readonly SystemProcedureParameter[] SpHelpJobStepParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("step_id", SqlType.Int32),
        AgentParameter("step_name", AgentName, 128),
        AgentParameter("suffix", SqlType.Bit, SqlValue.FromBoolean(false)),
    ];

    /// <summary>
    /// <c>sp_help_jobstep</c>: the job's steps, or the one <c>@step_id</c>
    /// (Msg 14266 outside them) or <c>@step_name</c> (Msg 14262 for none)
    /// names.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpJobStep(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_jobstep", SpHelpJobStepParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_jobstep", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        var jobId = AgentJobId(job);
        var stepId = call.Int(2);
        if (stepId is { } id)
        {
            var count = this.AgentStepCount(batch, jobId);
            if (id < 1 || id > count)
                throw SimulatedSqlException.AgentInvalidValue("@step_id", $"1..{count}").AtAgentSite(42);
        }
        else if (call.Text(3) is { } stepName)
        {
            stepId = this.AgentScalar(batch, "select step_id from dbo.sysjobsteps where job_id = @id and step_name = @name", ("id", jobId), ("name", SqlValue.FromNVarchar(stepName)))?.AsInt32
                ?? throw SimulatedSqlException.AgentDoesNotExist("@step_name", stepName).AtAgentSite(58);
        }
        yield return this.AgentStepsResultSet(batch, jobId, stepId, described: false);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly string[] AgentJobScheduleColumns =
    [
        "schedule_id int", "schedule_name nvarchar(128)", "enabled int", "freq_type int", "freq_interval int", "freq_subday_type int",
        "freq_subday_interval int", "freq_relative_interval int", "freq_recurrence_factor int", "active_start_date int", "active_end_date int",
        "active_start_time int", "active_end_time int", "date_created datetime", "schedule_description nvarchar(4000) null",
        "next_run_date int", "next_run_time int", "schedule_uid uniqueidentifier", "job_count int null",
    ];

    /// <summary>A job's schedules, in schedule-id order — every one, or those <paramref name="filter"/> keeps — as <c>sp_help_jobschedule</c> reports them.</summary>
    private SimulatedSqlResultSet AgentJobSchedulesResultSet(BatchContext batch, SqlValue jobId, string? filter, bool describe, params ReadOnlySpan<(string Name, SqlValue Value)> parameters)
    {
        var rows = this.AgentSql(batch, $"""
            select s.*, (select count(*) from dbo.sysjobschedules as uses where uses.schedule_id = s.schedule_id), js.next_run_date, js.next_run_time
            from dbo.sysjobschedules as js join dbo.sysschedules as s on s.schedule_id = js.schedule_id
            where js.job_id = @job {filter}
            order by s.schedule_id
            """, [("job", jobId), .. parameters]);
        var table = this.MsdbTable("sysschedules");
        var width = table.StoredColumns.Length;
        return this.AgentResultSet(AgentJobScheduleColumns, rows.ConvertAll(values =>
        {
            var schedule = new AgentRow(table, values[..width]);
            return new[]
            {
                schedule["schedule_id"], schedule["name"], schedule["enabled"], schedule["freq_type"], schedule["freq_interval"],
                schedule["freq_subday_type"], schedule["freq_subday_interval"], schedule["freq_relative_interval"], schedule["freq_recurrence_factor"],
                schedule["active_start_date"], schedule["active_end_date"], schedule["active_start_time"], schedule["active_end_time"],
                schedule["date_created"], SqlValue.FromNVarchar(describe ? DescribeAgentSchedule(AgentSchedule.From(schedule)) : "(Description not requested.)"),
                values[width + 1], values[width + 2], schedule["schedule_uid"], values[width],
            };
        }));
    }

    private static readonly SystemProcedureParameter[] SpHelpJobScheduleParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("schedule_name", AgentName, 128),
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("include_description", SqlType.Bit, SqlValue.FromBoolean(false)),
    ];

    /// <summary><c>sp_help_jobschedule</c>: the job's schedules, or the one named, with their descriptions on request.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpJobSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_jobschedule", SpHelpJobScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_jobschedule", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        var filter = call.Text(2) is not null ? "and s.name = @name" : call.Int(3) is not null ? "and s.schedule_id = @id" : null;
        yield return this.AgentJobSchedulesResultSet(batch, AgentJobId(job), filter, call.Values[4].AsBoolean,
            ("name", AgentNText(call.Text(2))), ("id", SqlValue.FromInt32(call.Int(3) ?? 0)));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpHelpScheduleParameters =
    [
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("schedule_name", AgentName, 128),
        AgentParameter("attached_schedules_only", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("include_description", SqlType.Bit, SqlValue.FromBoolean(false)),
    ];

    private static readonly string[] AgentScheduleColumns =
    [
        "schedule_id int", "schedule_uid uniqueidentifier", "schedule_name nvarchar(128)", "enabled int", "freq_type int", "freq_interval int",
        "freq_subday_type int", "freq_subday_interval int", "freq_relative_interval int", "freq_recurrence_factor int", "active_start_date int",
        "active_end_date int", "active_start_time int", "active_end_time int", "date_created datetime",
        "schedule_description nvarchar(4000) null", "job_count int null",
    ];

    /// <summary>
    /// <c>sp_help_schedule</c>: every schedule the caller sees, in id order —
    /// or the one named, or only those attached to a job — with descriptions
    /// on request.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_schedule", SpHelpScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_schedule", AgentUserRoleId);
        var conditions = "1 = 1";
        var scheduleId = SqlValue.FromInt32(0);
        if (call.Int(0) is not null || call.Text(1) is not null)
        {
            scheduleId = this.ResolveAgentSchedule(batch, call.Int(0), call.Text(1))["schedule_id"];
            conditions = "s.schedule_id = @id";
        }
        if (call.Values[2].AsBoolean)
            conditions += " and exists (select 1 from dbo.sysjobschedules as js where js.schedule_id = s.schedule_id)";
        var describe = call.Values[3].AsBoolean;
        var rows = this.AgentSql(batch, $"""
            select s.*, (select count(*) from dbo.sysjobschedules as uses where uses.schedule_id = s.schedule_id)
            from dbo.sysschedules as s where {conditions} order by s.schedule_id
            """, ("id", scheduleId));
        var table = this.MsdbTable("sysschedules");
        var width = table.StoredColumns.Length;
        if (!this.AgentSeesAllJobs(batch))
        {
            var caller = CallerSid(batch).AsBytes;
            _ = rows.RemoveAll(values => !values[MsdbOrdinal(table, "owner_sid")].AsBytes.AsSpan().SequenceEqual(caller));
        }
        yield return this.AgentResultSet(AgentScheduleColumns, rows.ConvertAll(values =>
        {
            var schedule = new AgentRow(table, values[..width]);
            return new[]
            {
                schedule["schedule_id"], schedule["schedule_uid"], schedule["name"], schedule["enabled"], schedule["freq_type"],
                schedule["freq_interval"], schedule["freq_subday_type"], schedule["freq_subday_interval"], schedule["freq_relative_interval"],
                schedule["freq_recurrence_factor"], schedule["active_start_date"], schedule["active_end_date"], schedule["active_start_time"],
                schedule["active_end_time"], schedule["date_created"],
                SqlValue.FromNVarchar(describe ? DescribeAgentSchedule(AgentSchedule.From(schedule)) : "(Description not requested.)"),
                values[width],
            };
        }));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// The description <c>sp_get_schedule_description</c> writes for a
    /// schedule, in its wording (probed 2026-10-16 against SQL Server 2025:
    /// <c>Every 1 week(s) on Monday, Tuesday … every 2 hour(s) between 13000
    /// and 200000</c>), its times the stored integers as they are.
    /// </summary>
    private static string DescribeAgentSchedule(AgentSchedule schedule)
    {
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        string[] dayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
        var frequency = schedule.FreqType switch
        {
            1 => $"Once on {Number(schedule.StartDate ?? 0)}",
            4 => "Every day",
            8 => $"Every {Number(schedule.RecurrenceFactor)} week(s) on {string.Join(", ", Enumerable.Range(0, 7).Where(day => (schedule.FreqInterval & (1 << day)) != 0).Select(day => dayNames[day]))}",
            16 => $"Every {Number(schedule.RecurrenceFactor)} months(s) on day {Number(schedule.FreqInterval)} of that month",
            32 => $"Every {Number(schedule.RecurrenceFactor)} months(s) on the {schedule.RelativeInterval switch { 1 => "first", 2 => "second", 4 => "third", 8 => "fourth", _ => "last" }} "
                + $"{schedule.FreqInterval switch { >= 1 and <= 7 => dayNames[schedule.FreqInterval - 1], 8 => "day", 9 => "week day", _ => "weekend day" }} of that month",
            64 => "Automatically starts when SQL Server Agent starts.",
            128 => "Starts whenever the CPU usage has remained below 10 percent for 600 seconds.",
            _ => "",
        };
        if (schedule.FreqType is 1 or 64 or 128)
            return schedule.FreqType == 1 ? $"{frequency} at {Number(schedule.StartTime)}" : frequency;
        return schedule.SubdayType switch
        {
            2 => $"{frequency} every {Number(schedule.SubdayInterval)} second(s) between {Number(schedule.StartTime)} and {Number(schedule.EndTime)}",
            4 => $"{frequency} every {Number(schedule.SubdayInterval)} minute(s) between {Number(schedule.StartTime)} and {Number(schedule.EndTime)}",
            8 => $"{frequency} every {Number(schedule.SubdayInterval)} hour(s) between {Number(schedule.StartTime)} and {Number(schedule.EndTime)}",
            _ => $"{frequency} at {Number(schedule.StartTime)}",
        };
    }

    private static readonly string[] AgentTargetColumns =
    [
        "server_id int null", "server_name nvarchar(128) null", "enlist_date datetime null", "last_poll_date datetime null",
        "last_run_date int", "last_run_time int", "last_run_duration int", "last_run_outcome tinyint", "last_outcome_message nvarchar(4000) null",
    ];

    /// <summary>
    /// The job's target servers — on a server outside a multi-server setup
    /// the local one alone, reading real's 1998-11-13 enlistment — with
    /// <paramref name="details"/> its last run's outcome too.
    /// </summary>
    private SimulatedSqlResultSet AgentTargetsResultSet(BatchContext batch, SqlValue jobId, bool details)
    {
        var enlisted = SqlValue.FromDateTime(new DateTime(1998, 11, 13));
        var rows = this.AgentRows(batch, "sysjobservers", "job_id = @id", ("id", jobId)).ConvertAll(server =>
        {
            SqlValue[] values = [server["server_id"], SqlValue.FromNVarchar(AgentLocalServer), enlisted, enlisted];
            return details
                ? [.. values, server["last_run_date"], server["last_run_time"], server["last_run_duration"], server["last_run_outcome"], server["last_outcome_message"]]
                : values;
        });
        return this.AgentResultSet(details ? AgentTargetColumns : AgentTargetColumns[..4], rows);
    }

    private static readonly SystemProcedureParameter[] SpHelpJobServerParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("show_last_run_details", SqlType.TinyInt, SqlValue.FromByte(0)),
    ];

    /// <summary><c>sp_help_jobserver</c>: the job's target servers, with their last run's outcome on request.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpJobServer(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_jobserver", SpHelpJobServerParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_jobserver", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        yield return this.AgentTargetsResultSet(batch, AgentJobId(job), call.Int(2) is > 0);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpHelpJobHistoryParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("step_id", SqlType.Int32),
        AgentParameter("sql_message_id", SqlType.Int32),
        AgentParameter("sql_severity", SqlType.Int32),
        AgentParameter("start_run_date", SqlType.Int32),
        AgentParameter("end_run_date", SqlType.Int32),
        AgentParameter("start_run_time", SqlType.Int32),
        AgentParameter("end_run_time", SqlType.Int32),
        AgentParameter("minimum_run_duration", SqlType.Int32),
        AgentParameter("run_status", SqlType.Int32),
        AgentParameter("minimum_retries", SqlType.Int32),
        AgentParameter("oldest_first", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("server", AgentName, 128),
        AgentParameter("mode", AgentVarchar, SqlValue.FromVarchar("SUMMARY")),
    ];

    /// <summary>
    /// <c>sp_help_jobhistory</c>: <c>sysjobhistory</c>'s rows for the jobs the
    /// caller sees — in <c>SUMMARY</c> mode each run's job outcome, in
    /// <c>FULL</c> mode every step's — newest first unless
    /// <c>@oldest_first</c>. A stopped Agent runs nothing, so only rows
    /// written to the table directly are there to list.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpJobHistory(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_jobhistory", SpHelpJobHistoryParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_jobhistory", AgentUserRoleId);
        var mode = (call.Text(14) ?? "SUMMARY").Trim().ToUpperInvariant();
        if (mode is not ("FULL" or "SEM" or "SUMMARY"))
            throw SimulatedSqlException.AgentInvalidValue("@mode", "SUMMARY, FULL, SEM").AtAgentSite(79);
        var full = mode == "FULL";
        var conditions = new List<string> { full ? "1 = 1" : "h.step_id = 0" };
        var jobId = SqlValue.Null(SqlType.UniqueIdentifier);
        if (!call.IsNull(0) || !call.IsNull(1))
        {
            jobId = AgentJobId(this.ResolveAgentJob(batch, call.Guid(0), call.Text(1)));
            conditions.Add("h.job_id = @job");
        }
        string[] filters = ["h.step_id = @p2", "h.sql_message_id = @p3", "h.sql_severity >= @p4", "h.run_date >= @p5", "h.run_date <= @p6",
            "h.run_time >= @p7", "h.run_time <= @p8", "h.run_duration >= @p9", "h.run_status = @p10", "h.retries_attempted >= @p11"];
        for (var i = 0; i < filters.Length; i++)
        {
            if (!call.IsNull(i + 2))
                conditions.Add(filters[i]);
        }
        var parameters = new List<(string Name, SqlValue Value)> { ("job", jobId) };
        for (var i = 2; i < 12; i++)
            parameters.Add(($"p{i}", call.IsNull(i) ? SqlValue.FromInt32(0) : call.Values[i]));
        var rows = this.AgentSql(batch, $"""
            select h.instance_id, h.job_id, j.name, h.step_id, h.step_name, h.sql_message_id, h.sql_severity, h.message, h.run_status,
                h.run_date, h.run_time, h.run_duration,
                isnull((select name from dbo.sysoperators where id = h.operator_id_emailed), N''),
                isnull((select name from dbo.sysoperators where id = h.operator_id_netsent), N''),
                isnull((select name from dbo.sysoperators where id = h.operator_id_paged), N''),
                h.retries_attempted, h.server, j.owner_sid
            from dbo.sysjobhistory as h join dbo.sysjobs as j on j.job_id = h.job_id
            where {string.Join(" and ", conditions)}
            order by h.instance_id {(call.Int(12) is > 0 ? "asc" : "desc")}
            """, [.. parameters]);
        if (!this.AgentSeesAllJobs(batch))
        {
            var caller = CallerSid(batch).AsBytes;
            _ = rows.RemoveAll(values => !values[17].AsBytes.AsSpan().SequenceEqual(caller));
        }
        yield return full
            ? this.AgentResultSet(
                [
                    "instance_id int", "job_id uniqueidentifier", "job_name nvarchar(128)", "step_id int", "step_name nvarchar(128)",
                    "sql_message_id int", "sql_severity int", "message nvarchar(4000) null", "run_status int", "run_date int", "run_time int",
                    "run_duration int", "operator_emailed nvarchar(128) null", "operator_netsent nvarchar(128) null",
                    "operator_paged nvarchar(128) null", "retries_attempted int", "server nvarchar(128)",
                ],
                rows.ConvertAll(values => values[..17]))
            : this.AgentResultSet(
                [
                    "job_id uniqueidentifier", "job_name nvarchar(128)", "run_status int", "run_date int", "run_time int", "run_duration int",
                    "operator_emailed nvarchar(20) null", "operator_netsent nvarchar(20) null", "operator_paged nvarchar(20) null",
                    "retries_attempted int", "server nvarchar(128)",
                ],
                rows.ConvertAll(values => new[] { values[1], values[2], values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15], values[16] }));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpHelpJobActivityParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("session_id", SqlType.Int32),
    ];

    /// <summary>
    /// <c>sp_help_jobactivity</c>: the jobs' activity in the Agent session
    /// asked for, by default the latest; an Agent that never started has
    /// none, so its <c>syssessions</c> and the answer are empty.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpJobActivity(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_jobactivity", SpHelpJobActivityParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_jobactivity", AgentUserRoleId);
        var jobId = SqlValue.Null(SqlType.UniqueIdentifier);
        if (!call.IsNull(0) || !call.IsNull(1))
            jobId = AgentJobId(this.ResolveAgentJob(batch, call.Guid(0), call.Text(1)));
        var rows = this.AgentSql(batch, """
            select a.session_id, a.job_id, j.name, a.run_requested_date, a.run_requested_source, a.queued_date, a.start_execution_date,
                a.last_executed_step_id, a.last_executed_step_date, a.stop_execution_date, a.next_scheduled_run_date, a.job_history_id,
                h.message, h.run_status, h.operator_id_emailed, h.operator_id_netsent, h.operator_id_paged, j.owner_sid
            from dbo.sysjobactivity as a join dbo.sysjobs as j on j.job_id = a.job_id
                left join dbo.sysjobhistory as h on h.instance_id = a.job_history_id
            where a.session_id = isnull(@session, (select max(session_id) from dbo.syssessions)) and (@job is null or a.job_id = @job)
            order by j.name
            """, ("session", call.Values[2]), ("job", jobId));
        if (!this.AgentSeesAllJobs(batch))
        {
            var caller = CallerSid(batch).AsBytes;
            _ = rows.RemoveAll(values => !values[17].AsBytes.AsSpan().SequenceEqual(caller));
        }
        yield return this.AgentResultSet(
            [
                "session_id int", "job_id uniqueidentifier", "job_name nvarchar(128)", "run_requested_date datetime null",
                "run_requested_source nvarchar(128) null", "queued_date datetime null", "start_execution_date datetime null",
                "last_executed_step_id int null", "last_executed_step_date datetime null", "stop_execution_date datetime null",
                "next_scheduled_run_date datetime null", "job_history_id int null", "message nvarchar(4000) null", "run_status int null",
                "operator_id_emailed int null", "operator_id_netsent int null", "operator_id_paged int null",
            ],
            rows.ConvertAll(values => values[..17]));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// The subsystems a Linux server's Agent lists, as <c>(name, description,
    /// dll, executable, id, worker threads)</c> (probed 2026-10-16 against SQL
    /// Server 2025); CmdExec and PowerShell steps are taken though neither is
    /// listed.
    /// </summary>
    private static readonly (string Name, string Description, string Dll, string Executable, int Id, int Threads)[] AgentListedSubsystems =
    [
        ("TSQL", "Transact-SQL Subsystem", "[Internal]", "[Internal]", 1, 320),
        ("Snapshot", "Replication Snapshot Subsystem", "SQLREPSS.DLL", @"C:\COM/SNAPSHOT.EXE", 4, 1600),
        ("LogReader", "Replication Transaction-Log Reader Subsystem", "SQLREPSS.DLL", @"C:\COM/logread.exe", 5, 400),
        ("Distribution", "Replication Distribution Subsystem", "SQLREPSS.DLL", @"C:\COM/DISTRIB.EXE", 6, 1600),
        ("Merge", "Replication Merge Subsystem", "SQLREPSS.DLL", @"C:\COM/REPLMERG.EXE", 7, 1600),
        ("QueueReader", "Replication Transaction Queue Reader Subsystem", "SQLREPSS.dll", @"C:\COM/qrdrsvc.exe", 8, 1600),
    ];

    /// <summary><c>sp_enum_sqlagent_subsystems</c>: the subsystems the Agent lists, with their entry points.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpEnumAgentSubsystems(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_enum_sqlagent_subsystems", []) is null)
            yield break;
        this.RequireAgentExecute(batch, "sp_enum_sqlagent_subsystems", AgentUserRoleId);
        var rows = new List<SqlValue[]>(AgentListedSubsystems.Length);
        foreach (var (name, description, dll, executable, id, threads) in AgentListedSubsystems)
        {
            var internalEntry = id == 1;
            rows.Add(
            [
                SqlValue.FromNVarchar(name), SqlValue.FromNVarchar(description), SqlValue.FromNVarchar(dll), SqlValue.FromNVarchar(executable),
                SqlValue.FromNVarchar(internalEntry ? "[Internal]" : "ReplStart"), SqlValue.FromNVarchar(internalEntry ? "[Internal]" : "ReplEvent"),
                SqlValue.FromNVarchar(internalEntry ? "[Internal]" : "ReplStop"), SqlValue.FromInt32(threads), SqlValue.FromInt32(id),
            ]);
        }
        yield return this.AgentResultSet(
            [
                "subsystem nvarchar(40)", "description nvarchar(4000) null", "subsystem_dll nvarchar(255) null", "agent_exe nvarchar(255) null",
                "start_entry_point nvarchar(30) null", "event_entry_point nvarchar(30) null", "stop_entry_point nvarchar(30) null",
                "max_worker_threads int null", "subsystem_id int",
            ], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }
}
