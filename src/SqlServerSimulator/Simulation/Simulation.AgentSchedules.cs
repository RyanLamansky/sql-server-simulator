using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Agent procedures over schedules, which since SQL Server 2005 stand on
// their own and attach to any number of jobs, as SQL Server 2025 runs them
// (probed 2026-10-16). Schedule names need not be unique; a name more than
// one schedule carries is refused where a call names one by it.
partial class Simulation
{
    /// <summary>
    /// One schedule's frequency fields, as a call gives them or
    /// <c>sysschedules</c> holds them, in the parameter order
    /// <c>sp_add_schedule</c>, <c>sp_add_jobschedule</c>,
    /// <c>sp_update_schedule</c> and <c>sp_update_jobschedule</c> share from
    /// <c>@enabled</c> on.
    /// </summary>
    private sealed class AgentSchedule
    {
        public int Enabled = 1;
        public int FreqType;
        public int FreqInterval;
        public int SubdayType;
        public int SubdayInterval;
        public int RelativeInterval;
        public int RecurrenceFactor;
        public int? StartDate;
        public int EndDate = 99991231;
        public int StartTime;
        public int EndTime = 235959;

        public static AgentSchedule From(AgentRow row) => new()
        {
            Enabled = row.Int("enabled"),
            FreqType = row.Int("freq_type"),
            FreqInterval = row.Int("freq_interval"),
            SubdayType = row.Int("freq_subday_type"),
            SubdayInterval = row.Int("freq_subday_interval"),
            RelativeInterval = row.Int("freq_relative_interval"),
            RecurrenceFactor = row.Int("freq_recurrence_factor"),
            StartDate = row.Int("active_start_date"),
            EndDate = row.Int("active_end_date"),
            StartTime = row.Int("active_start_time"),
            EndTime = row.Int("active_end_time"),
        };

        /// <summary>Overwrites each field <paramref name="call"/> gives a value for, <paramref name="first"/> the index of its <c>@enabled</c>.</summary>
        public void Merge(AgentCall call, int first)
        {
            this.Enabled = call.Int(first) ?? this.Enabled;
            this.FreqType = call.Int(first + 1) ?? this.FreqType;
            this.FreqInterval = call.Int(first + 2) ?? this.FreqInterval;
            this.SubdayType = call.Int(first + 3) ?? this.SubdayType;
            this.SubdayInterval = call.Int(first + 4) ?? this.SubdayInterval;
            this.RelativeInterval = call.Int(first + 5) ?? this.RelativeInterval;
            this.RecurrenceFactor = call.Int(first + 6) ?? this.RecurrenceFactor;
            this.StartDate = call.Int(first + 7) ?? this.StartDate;
            this.EndDate = call.Int(first + 8) ?? this.EndDate;
            this.StartTime = call.Int(first + 9) ?? this.StartTime;
            this.EndTime = call.Int(first + 10) ?? this.EndTime;
        }

        /// <summary>The fields as the variables the Agent statements write them through.</summary>
        public (string, SqlValue)[] Parameters() =>
        [
            ("enabled", SqlValue.FromInt32(this.Enabled)),
            ("freq_type", SqlValue.FromInt32(this.FreqType)),
            ("freq_interval", SqlValue.FromInt32(this.FreqInterval)),
            ("subday_type", SqlValue.FromInt32(this.SubdayType)),
            ("subday_interval", SqlValue.FromInt32(this.SubdayInterval)),
            ("relative_interval", SqlValue.FromInt32(this.RelativeInterval)),
            ("recurrence_factor", SqlValue.FromInt32(this.RecurrenceFactor)),
            ("start_date", SqlValue.FromInt32(this.StartDate!.Value)),
            ("end_date", SqlValue.FromInt32(this.EndDate)),
            ("start_time", SqlValue.FromInt32(this.StartTime)),
            ("end_time", SqlValue.FromInt32(this.EndTime)),
        ];
    }

    /// <summary>The schedule parameters <c>sp_add_schedule</c> and <c>sp_add_jobschedule</c> share, from <c>@enabled</c> to <c>@active_end_time</c>.</summary>
    private static SystemProcedureParameter[] AgentScheduleParameters(bool defaults) =>
    [
        defaults ? AgentParameter("enabled", SqlType.TinyInt, SqlValue.FromByte(1)) : AgentParameter("enabled", SqlType.TinyInt),
        defaults ? AgentParameter("freq_type", SqlType.Int32, SqlValue.FromInt32(defaults ? 0 : 1)) : AgentParameter("freq_type", SqlType.Int32),
        defaults ? AgentParameter("freq_interval", SqlType.Int32, SqlValue.FromInt32(0)) : AgentParameter("freq_interval", SqlType.Int32),
        defaults ? AgentParameter("freq_subday_type", SqlType.Int32, SqlValue.FromInt32(0)) : AgentParameter("freq_subday_type", SqlType.Int32),
        defaults ? AgentParameter("freq_subday_interval", SqlType.Int32, SqlValue.FromInt32(0)) : AgentParameter("freq_subday_interval", SqlType.Int32),
        defaults ? AgentParameter("freq_relative_interval", SqlType.Int32, SqlValue.FromInt32(0)) : AgentParameter("freq_relative_interval", SqlType.Int32),
        defaults ? AgentParameter("freq_recurrence_factor", SqlType.Int32, SqlValue.FromInt32(0)) : AgentParameter("freq_recurrence_factor", SqlType.Int32),
        AgentParameter("active_start_date", SqlType.Int32),
        defaults ? AgentParameter("active_end_date", SqlType.Int32, SqlValue.FromInt32(99991231)) : AgentParameter("active_end_date", SqlType.Int32),
        defaults ? AgentParameter("active_start_time", SqlType.Int32, SqlValue.FromInt32(0)) : AgentParameter("active_start_time", SqlType.Int32),
        defaults ? AgentParameter("active_end_time", SqlType.Int32, SqlValue.FromInt32(235959)) : AgentParameter("active_end_time", SqlType.Int32),
    ];

    private static readonly SystemProcedureParameter[] SpAddScheduleParameters =
    [
        AgentRequired("schedule_name", AgentName, 128),
        .. AgentScheduleParameters(defaults: true),
        AgentParameter("owner_login_name", AgentName, 128),
        AgentParameter("schedule_uid", SqlType.UniqueIdentifier),
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("originating_server", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_verify_schedule</c>, its date and time helpers included, in
    /// real's order and at its lines, normalizing what it keeps as real does:
    /// a one-time, Agent-start or idle schedule drops every frequency field, a
    /// recurring one without a sub-day unit runs once a day (unit 1, interval
    /// 0), and a daily one drops its relative interval and recurrence factor,
    /// where the other frequencies keep whatever they are given. A missing
    /// start date is today.
    /// </summary>
    private static void VerifyAgentSchedule(BatchContext batch, AgentSchedule schedule)
    {
        const string verifier = "sp_verify_schedule";
        schedule.StartDate ??= AgentDate(batch.CurrentStatement.UtcNow);
        if (schedule.Enabled is not (0 or 1))
            throw SimulatedSqlException.AgentInvalidValue("@enabled", "0, 1").AtAgentSite(92, verifier);
        if (schedule.FreqType is not (1 or 4 or 8 or 16 or 32 or 64 or 128))
            throw SimulatedSqlException.AgentInvalidValue("@freq_type", "1, 4, 8, 16, 32, 64, 128").AtAgentSite(104, verifier);
        var recurring = schedule.FreqType is 4 or 8 or 16 or 32;
        if (recurring && schedule.SubdayType is not (0 or 1 or 2 or 4 or 8))
            throw SimulatedSqlException.AgentInvalidValue("@freq_subday_type", "0x1, 0x2, 0x4, 0x8").AtAgentSite(111, verifier);
        VerifyAgentDate(schedule.StartDate.Value, "@active_start_date");
        VerifyAgentDate(schedule.EndDate, "@active_end_date");
        VerifyAgentTime(schedule.StartTime, "@active_start_time");
        VerifyAgentTime(schedule.EndTime, "@active_end_time");
        if (schedule.EndDate < schedule.StartDate)
            throw SimulatedSqlException.AgentEndBeforeStart("@active_end_date", "@active_start_date").AtAgentSite(141, verifier);
        if (!recurring)
        {
            schedule.FreqInterval = schedule.SubdayType = schedule.SubdayInterval = schedule.RelativeInterval = schedule.RecurrenceFactor = 0;
            return;
        }
        if (schedule.SubdayType == 0)
            schedule.SubdayType = 1;
        if (schedule.SubdayType == 1)
            schedule.SubdayInterval = 0;
        else if (schedule.SubdayInterval < 1)
            throw SimulatedSqlException.AgentScheduleInvalid("The specified '@freq_subday_interval' is invalid.").AtAgentSite(222, verifier);
        switch (schedule.FreqType)
        {
            case 4:
                if (schedule.FreqInterval < 1)
                    throw SimulatedSqlException.AgentScheduleInvalid("@freq_interval must be at least 1 for a daily job.").AtAgentSite(232, verifier);
                schedule.RelativeInterval = schedule.RecurrenceFactor = 0;
                return;
            case 8:
                if (schedule.FreqInterval is < 1 or > 127)
                    throw SimulatedSqlException.AgentScheduleInvalid("@freq_interval must be a valid day of the week bitmask [Sunday = 1 .. Saturday = 64] for a weekly job.").AtAgentSite(243, verifier);
                break;
            case 16:
                if (schedule.FreqInterval is < 1 or > 31)
                    throw SimulatedSqlException.AgentScheduleInvalid("@freq_interval must be between 1 and 31 for a monthly job.").AtAgentSite(255, verifier);
                break;
            default:
                if (schedule.RelativeInterval is not (1 or 2 or 4 or 8 or 16))
                {
                    throw SimulatedSqlException.AgentScheduleInvalid("@freq_relative_interval must be one of 1st (0x1), 2nd (0x2), 3rd [0x4], 4th (0x8) or Last (0x10).")
                        .AtAgentSite(270, verifier);
                }
                if (schedule.FreqInterval is < 1 or > 10)
                {
                    throw SimulatedSqlException.AgentScheduleInvalid("@freq_interval must be between 1 and 10 (1 = Sunday .. 7 = Saturday, 8 = Day, 9 = Weekday, 10 = Weekend-day) for a monthly-relative job.")
                        .AtAgentSite(289, verifier);
                }
                break;
        }
        if (schedule.RecurrenceFactor < 1)
            throw SimulatedSqlException.AgentScheduleInvalid("@freq_recurrence_factor must be at least 1.").AtAgentSite(300, verifier);
    }

    /// <summary><c>sp_verify_job_date</c>: a <c>yyyymmdd</c> date from 1990 to 9999 that exists (Msg 14266).</summary>
    private static void VerifyAgentDate(int date, string parameter)
    {
        if (date is < 19900101 or > 99991231
            || !DateTime.TryParseExact(date.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw SimulatedSqlException.AgentInvalidValue(parameter, "19900101..99991231").AtAgentSite(14, "sp_verify_job_date");
        }
    }

    /// <summary><c>sp_verify_job_time</c>: an <c>hhmmss</c> time of day (Msg 14266) whose minutes and seconds are below 60 (Msg 14287).</summary>
    private static void VerifyAgentTime(int time, string parameter)
    {
        if (time is < 0 or > 235959)
            throw SimulatedSqlException.AgentInvalidValue(parameter, "000000..235959").AtAgentSite(20, "sp_verify_job_time");
        if (time / 100 % 100 > 59)
            throw SimulatedSqlException.AgentTimeInvalidPart(parameter, "minute").AtAgentSite(40, "sp_verify_job_time");
        if (time % 100 > 59)
            throw SimulatedSqlException.AgentTimeInvalidPart(parameter, "second").AtAgentSite(48, "sp_verify_job_time");
    }

    /// <summary>
    /// <c>sp_add_schedule</c>: a new schedule, owned by the caller or the
    /// login <c>@owner_login_name</c> names (Msg 14262 for none), whose id
    /// and uid return through the two <c>OUTPUT</c> parameters.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_schedule", SpAddScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_schedule", AgentUserRoleId);
        // The id reads 0 until the schedule is made, so a refused call
        // returns 0 through it.
        call.SetOutput(14, SqlValue.FromInt32(0));
        var owner = call.Text(12) is { Length: > 0 } ownerName ? ownerName : null;
        if (owner is not null && !AgentIsSysadmin(batch))
            throw SimulatedSqlException.AgentScheduleOwnerNeedsSysadmin().AtAgentSite(59, "sp_verify_schedule");
        if (owner is not null && !this.IsAgentLogin(owner))
            throw SimulatedSqlException.AgentDoesNotExist("@owner_login_name", owner).AtAgentSite(57);
        var schedule = new AgentSchedule();
        schedule.Merge(call, 1);
        var (id, uid) = this.InsertAgentSchedule(batch, call.Text(0)!.Trim(), schedule,
            owner is null ? CallerSid(batch) : SqlValue.FromVarbinary(VarbinarySqlType.Get(85), LoginSid(batch.Connection.Simulation, owner)));
        call.SetOutput(13, SqlValue.FromGuid(uid));
        call.SetOutput(14, SqlValue.FromInt32(id));
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>Verifies <paramref name="schedule"/> and inserts it under <paramref name="name"/>, returning its id and uid.</summary>
    private (int Id, Guid Uid) InsertAgentSchedule(BatchContext batch, string name, AgentSchedule schedule, SqlValue owner)
    {
        VerifyAgentSchedule(batch, schedule);
        var uid = Guid.NewGuid();
        var id = this.AgentScalar(batch, """
            insert dbo.sysschedules (schedule_uid, originating_server_id, name, owner_sid, enabled, freq_type, freq_interval, freq_subday_type,
                freq_subday_interval, freq_relative_interval, freq_recurrence_factor, active_start_date, active_end_date, active_start_time,
                active_end_time, date_created, date_modified, version_number)
            values (@uid, 0, @name, @owner, @enabled, @freq_type, @freq_interval, @subday_type, @subday_interval, @relative_interval,
                @recurrence_factor, @start_date, @end_date, @start_time, @end_time, getdate(), getdate(), 1);
            select convert(int, scope_identity());
            """,
            [("uid", SqlValue.FromGuid(uid)), ("name", SqlValue.FromNVarchar(name)), ("owner", owner), .. schedule.Parameters()])!.Value.AsInt32;
        return (id, uid);
    }

    /// <summary>
    /// The schedule a call names by <c>@schedule_id</c> or
    /// <c>@schedule_name</c>, as <c>sp_verify_schedule_identifiers</c> finds
    /// it: exactly one of the two (Msg 14373), one that exists (14262), and a
    /// name only one schedule carries (14371). With <paramref name="attachedTo"/>
    /// a name is looked up among that job's schedules alone (probed
    /// 2026-10-16: <c>sp_detach_schedule</c> of another job's schedule by name
    /// is Msg 14262, by id Msg 14374).
    /// </summary>
    private AgentRow ResolveAgentSchedule(BatchContext batch, int? scheduleId, string? scheduleName, SqlValue? attachedTo = null)
    {
        const string verifier = "msdb.dbo.sp_verify_schedule_identifiers";
        if ((scheduleId is null) == (scheduleName is null))
            throw SimulatedSqlException.AgentSupplyScheduleIdentifier().AtAgentSite(29, verifier);
        if (scheduleId is { } id)
        {
            return this.AgentRows(batch, "sysschedules", "schedule_id = @id", ("id", SqlValue.FromInt32(id))) is [var byId, ..]
                ? byId
                : throw SimulatedSqlException.AgentDoesNotExist("@schedule_id", id.ToString(CultureInfo.InvariantCulture)).AtAgentSite(73, verifier);
        }
        var named = this.AgentRows(batch, "sysschedules",
            attachedTo is null ? "name = @name" : "name = @name and schedule_id in (select schedule_id from dbo.sysjobschedules where job_id = @job)",
            ("name", SqlValue.FromNVarchar(scheduleName!.Trim())), ("job", attachedTo ?? SqlValue.Null(SqlType.UniqueIdentifier)));
        return named switch
        {
            [] => throw SimulatedSqlException.AgentDoesNotExist("@schedule_name", scheduleName).AtAgentSite(138, verifier),
            [var only] => only,
            _ => throw SimulatedSqlException.AgentScheduleNameAmbiguous(scheduleName).AtAgentSite(113, verifier),
        };
    }

    private static readonly SystemProcedureParameter[] SpAttachScheduleParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("schedule_name", AgentName, 128),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// <c>sp_attach_schedule</c>: attaches the schedule to the job, a second
    /// attachment of the same pair doing nothing; a job targeted at the local
    /// server gets the stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAttachSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_attach_schedule", SpAttachScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_attach_schedule", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1), QualifiedJobVerifier);
        var schedule = this.ResolveAgentSchedule(batch, call.Int(2), call.Text(3));
        if (!AgentIsSysadmin(batch) && !AgentOwns(batch, job))
            throw SimulatedSqlException.AgentAttachNotOwner().AtAgentSite(48);
        var jobId = AgentJobId(job);
        if (this.AttachAgentSchedule(batch, jobId, schedule["schedule_id"]) && this.AgentJobTargetsLocalServer(batch, jobId))
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>Refuses (Msg 14394) a change to a schedule the caller doesn't own unless it is a <c>sysadmin</c>.</summary>
    private static void RequireAgentScheduleOwner(BatchContext batch, AgentRow schedule, int line)
    {
        if (!AgentIsSysadmin(batch) && !schedule["owner_sid"].AsBytes.AsSpan().SequenceEqual(CallerSid(batch).AsBytes))
            throw SimulatedSqlException.AgentScheduleNotOwner().AtAgentSite(line);
    }

    /// <summary>Attaches a schedule to a job unless it already is, reporting whether it attached.</summary>
    private bool AttachAgentSchedule(BatchContext batch, SqlValue jobId, SqlValue scheduleId) =>
        this.AgentScalar(batch, """
            if exists (select 1 from dbo.sysjobschedules where job_id = @job and schedule_id = @schedule)
                select 0;
            else
            begin
                insert dbo.sysjobschedules (schedule_id, job_id, next_run_date, next_run_time) values (@schedule, @job, 0, 0);
                select 1;
            end
            """, ("job", jobId), ("schedule", scheduleId))!.Value.AsInt32 == 1;

    /// <summary>
    /// The name <c>sp_attach_schedule</c> and <c>sp_detach_schedule</c> call
    /// <c>sp_verify_job_identifiers</c> by, which its errors carry (probed
    /// 2026-10-20 against SQL Server 2025; the other job procedures call it
    /// unqualified).
    /// </summary>
    private const string QualifiedJobVerifier = "msdb.dbo.sp_verify_job_identifiers";

    private static readonly SystemProcedureParameter[] SpDetachScheduleParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("schedule_name", AgentName, 128),
        AgentParameter("delete_unused_schedule", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// <c>sp_detach_schedule</c>: detaches the schedule from the job — and with
    /// <c>@delete_unused_schedule</c> deletes it once no job uses it. A
    /// schedule not attached to the job is Msg 14374, which real sends at
    /// severity 0 while answering 1.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDetachSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_detach_schedule", SpDetachScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_detach_schedule", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1), QualifiedJobVerifier);
        var schedule = this.ResolveAgentSchedule(batch, call.Int(2), call.Text(3), attachedTo: AgentJobId(job));
        if (!AgentIsSysadmin(batch) && !AgentOwns(batch, job))
            throw SimulatedSqlException.AgentDetachNotOwner().AtAgentSite(74);
        var jobId = AgentJobId(job);
        var scheduleId = schedule["schedule_id"];
        if (this.AgentScalar(batch, "select count(*) from dbo.sysjobschedules where job_id = @job and schedule_id = @schedule", ("job", jobId), ("schedule", scheduleId))!.Value.AsInt32 == 0)
        {
            yield return new SimulatedInfoOutcome(SimulatedSqlException.SystemProcedureMessage(batch, "sp_detach_schedule", 51, 14374,
                $"The specified schedule name \"{schedule.Text("name")}\" is not associated with the job \"{job.Text("name")}\"."));
            AgentReturn(batch, returnCodeVar, 1);
            yield break;
        }
        this.DetachAgentSchedule(batch, jobId, scheduleId, call.Values[4].AsBoolean);
        if (this.AgentJobTargetsLocalServer(batch, jobId))
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>Detaches a schedule from a job, deleting it when <paramref name="deleteUnused"/> and no job uses it any longer.</summary>
    private void DetachAgentSchedule(BatchContext batch, SqlValue jobId, SqlValue scheduleId, bool deleteUnused) =>
        _ = this.AgentSql(batch, $"""
            delete dbo.sysjobschedules where job_id = @job and schedule_id = @schedule;
            {(deleteUnused ? "if not exists (select 1 from dbo.sysjobschedules where schedule_id = @schedule) delete dbo.sysschedules where schedule_id = @schedule;" : "")}
            """, ("job", jobId), ("schedule", scheduleId));

    private static readonly SystemProcedureParameter[] SpAddJobScheduleParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentRequired("name", AgentName, 128),
        .. AgentScheduleParameters(defaults: true),
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
        AgentParameter("schedule_uid", SqlType.UniqueIdentifier),
    ];

    /// <summary>
    /// <c>sp_add_jobschedule</c>: a new schedule owned by the job's owner,
    /// attached to the job; a job targeted at the local server gets the
    /// stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddJobSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_jobschedule", SpAddJobScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_jobschedule", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        RequireAgentJobOwner(batch, job, 57, null);
        var schedule = new AgentSchedule();
        schedule.Merge(call, 3);
        var jobId = AgentJobId(job);
        var (id, uid) = this.InsertAgentSchedule(batch, call.Text(2)!.Trim(), schedule, job["owner_sid"]);
        _ = this.AttachAgentSchedule(batch, jobId, SqlValue.FromInt32(id));
        call.SetOutput(14, SqlValue.FromInt32(id));
        call.SetOutput(16, SqlValue.FromGuid(uid));
        if (this.AgentJobTargetsLocalServer(batch, jobId))
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpUpdateScheduleParameters =
    [
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("name", AgentName, 128),
        AgentParameter("new_name", AgentName, 128),
        .. AgentScheduleParameters(defaults: false),
        AgentParameter("owner_login_name", AgentName, 128),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// <c>sp_update_schedule</c>: each field given replaces the schedule's
    /// own, verified as a new schedule's are, and its version rises; each job
    /// using it that targets the local server gets the stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_update_schedule", SpUpdateScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_update_schedule", AgentUserRoleId);
        var row = this.ResolveAgentSchedule(batch, call.Int(0), call.Text(1));
        RequireAgentScheduleOwner(batch, row, 108);
        var owner = call.Text(14) is { Length: > 0 } ownerName ? ownerName : null;
        if (owner is not null && !AgentIsSysadmin(batch))
            throw SimulatedSqlException.AgentScheduleOwnerNeedsSysadmin().AtAgentSite(59, "sp_verify_schedule");
        if (owner is not null && !this.IsAgentLogin(owner))
            throw SimulatedSqlException.AgentDoesNotExist("@owner_login_name", owner).AtAgentSite(57);
        var schedule = AgentSchedule.From(row);
        schedule.Merge(call, 3);
        VerifyAgentSchedule(batch, schedule);
        var scheduleId = row["schedule_id"];
        _ = this.AgentSql(batch, """
            update dbo.sysschedules set name = @name, owner_sid = @owner, enabled = @enabled, freq_type = @freq_type, freq_interval = @freq_interval,
                freq_subday_type = @subday_type, freq_subday_interval = @subday_interval, freq_relative_interval = @relative_interval,
                freq_recurrence_factor = @recurrence_factor, active_start_date = @start_date, active_end_date = @end_date,
                active_start_time = @start_time, active_end_time = @end_time, date_modified = getdate(), version_number = version_number + 1
            where schedule_id = @id
            """,
            [("id", scheduleId), ("name", call.Text(2) is { Length: > 0 } renamed ? SqlValue.FromNVarchar(renamed.Trim()) : row["name"]),
                ("owner", owner is null ? row["owner_sid"] : SqlValue.FromVarbinary(VarbinarySqlType.Get(85), LoginSid(batch.Connection.Simulation, owner))), .. schedule.Parameters()]);
        foreach (var notice in this.AgentScheduleNotices(batch, scheduleId))
            yield return notice;
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>The stopped Agent's notice for each job using <paramref name="scheduleId"/> that targets the local server.</summary>
    private List<SimulatedStatementOutcome> AgentScheduleNotices(BatchContext batch, SqlValue scheduleId)
    {
        var count = this.AgentScalar(batch, """
            select count(*) from dbo.sysjobschedules as js join dbo.sysjobservers as s on s.job_id = js.job_id and s.server_id = 0
            where js.schedule_id = @id
            """, ("id", scheduleId))!.Value.AsInt32;
        var notices = new List<SimulatedStatementOutcome>(count);
        for (var i = 0; i < count; i++)
            notices.Add(AgentNotRunningNotice(batch));
        return notices;
    }

    private static readonly SystemProcedureParameter[] SpDeleteScheduleParameters =
    [
        AgentParameter("schedule_id", SqlType.Int32),
        AgentParameter("schedule_name", AgentName, 128),
        AgentParameter("force_delete", SqlType.Bit, SqlValue.FromBoolean(false)),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// <c>sp_delete_schedule</c>: a schedule no job uses, or with
    /// <c>@force_delete</c> any, detached from its jobs first; one in use
    /// otherwise is Msg 14372.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_schedule", SpDeleteScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_schedule", AgentUserRoleId);
        var row = this.ResolveAgentSchedule(batch, call.Int(0), call.Text(1));
        RequireAgentScheduleOwner(batch, row, 36);
        var scheduleId = row["schedule_id"];
        var used = this.AgentScalar(batch, "select count(*) from dbo.sysjobschedules where schedule_id = @id", ("id", scheduleId))!.Value.AsInt32 > 0;
        if (used && !call.Values[2].AsBoolean)
            throw SimulatedSqlException.AgentScheduleInUse().AtAgentSite(48);
        var notices = used ? this.AgentScheduleNotices(batch, scheduleId) : [];
        _ = this.AgentSql(batch, """
            delete dbo.sysjobschedules where schedule_id = @id;
            delete dbo.sysschedules where schedule_id = @id;
            """, ("id", scheduleId));
        foreach (var notice in notices)
            yield return notice;
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpUpdateJobScheduleParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentRequired("name", AgentName, 128),
        AgentParameter("new_name", AgentName, 128),
        .. AgentScheduleParameters(defaults: false),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// The schedule named <paramref name="name"/> among those attached to
    /// <paramref name="job"/>, as the two job-schedule procedures look it up:
    /// one that exists unattached is Msg 14374 at
    /// <paramref name="unattachedLine"/>, and one that doesn't Msg 14262 at
    /// <paramref name="missingLine"/> naming <paramref name="missingParameter"/>
    /// (probed 2026-10-18 against SQL Server 2025).
    /// </summary>
    private AgentRow ResolveAgentJobSchedule(BatchContext batch, AgentRow job, string name, int unattachedLine, int missingLine, string missingParameter)
    {
        var trimmed = SqlValue.FromNVarchar(name.Trim());
        var rows = this.AgentRows(batch, "sysschedules",
            "name = @name and schedule_id in (select schedule_id from dbo.sysjobschedules where job_id = @job)",
            ("name", trimmed), ("job", AgentJobId(job)));
        if (rows is [var row, ..])
            return row;
        throw this.AgentScalar(batch, "select 1 from dbo.sysschedules where name = @name", ("name", trimmed)) is null
            ? SimulatedSqlException.AgentDoesNotExist(missingParameter, name).AtAgentSite(missingLine)
            : SimulatedSqlException.AgentScheduleNotAttached(name, job.Text("name")!).AtAgentSite(unattachedLine);
    }

    /// <summary>
    /// <c>sp_update_jobschedule</c>: <c>sp_update_schedule</c> over the job's
    /// schedule of that name; a job targeted at the local server gets the
    /// stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateJobSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_update_jobschedule", SpUpdateJobScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_update_jobschedule", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        RequireAgentJobOwner(batch, job, 93, null);
        var jobId = AgentJobId(job);
        var row = this.ResolveAgentJobSchedule(batch, job, call.Text(2)!, 122, 144, "Schedule Name");
        var schedule = AgentSchedule.From(row);
        schedule.Merge(call, 4);
        VerifyAgentSchedule(batch, schedule);
        _ = this.AgentSql(batch, """
            update dbo.sysschedules set name = @name, enabled = @enabled, freq_type = @freq_type, freq_interval = @freq_interval,
                freq_subday_type = @subday_type, freq_subday_interval = @subday_interval, freq_relative_interval = @relative_interval,
                freq_recurrence_factor = @recurrence_factor, active_start_date = @start_date, active_end_date = @end_date,
                active_start_time = @start_time, active_end_time = @end_time, date_modified = getdate(), version_number = version_number + 1
            where schedule_id = @id
            """,
            [("id", row["schedule_id"]), ("name", call.Text(3) is { Length: > 0 } renamed ? SqlValue.FromNVarchar(renamed.Trim()) : row["name"]), .. schedule.Parameters()]);
        if (this.AgentJobTargetsLocalServer(batch, jobId))
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteJobScheduleParameters =
    [
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentRequired("name", AgentName, 128),
        AgentParameter("keep_schedule", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("automatic_post", SqlType.Bit, SqlValue.FromBoolean(true)),
    ];

    /// <summary>
    /// <c>sp_delete_jobschedule</c>: detaches the job's schedule of that name
    /// and, unless <c>@keep_schedule</c>, deletes it once no job uses it; a job
    /// targeted at the local server gets the stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteJobSchedule(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_jobschedule", SpDeleteJobScheduleParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_jobschedule", AgentUserRoleId);
        var job = this.ResolveAgentJob(batch, call.Guid(0), call.Text(1));
        RequireAgentJobOwner(batch, job, 36, null);
        var jobId = AgentJobId(job);
        var row = this.ResolveAgentJobSchedule(batch, job, call.Text(2)!, 110, 132, "@name");
        this.DetachAgentSchedule(batch, jobId, row["schedule_id"], deleteUnused: call.Int(3) == 0);
        if (this.AgentJobTargetsLocalServer(batch, jobId))
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }
}
