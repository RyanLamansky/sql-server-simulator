using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// msdb's SQL Server Agent procedures as SQL Server 2025 runs them with the
/// Agent service stopped (probed 2026-10-16): jobs, steps, schedules,
/// target servers, operators, alerts and categories are kept in msdb's
/// tables, each change a stopped Agent would be told of answers Msg 22022 at
/// severity 0, and a start or stop is refused with it.
/// </summary>
[TestClass]
public sealed class AgentProcedureTests
{
    private static List<string> Messages(Simulation simulation, string commandText)
    {
        var messages = new List<string>();
        using var connection = simulation.CreateOpenConnection();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => $"{error.Number}:{error.Message}"));
        _ = connection.CreateCommand(commandText).ExecuteNonQuery();
        return messages;
    }

    [TestMethod]
    public void AddJob_FillsRealDefaults()
        => AreEqual("j|1|No description available.|1|0|0x01|2|0|0|1|0", new Simulation().ExecuteScalar("""
            declare @id uniqueidentifier;
            exec msdb.dbo.sp_add_job @job_name = N'  j  ', @job_id = @id output;
            select name + '|' + cast(enabled as varchar) + '|' + description + '|' + cast(start_step_id as varchar) + '|' + cast(category_id as varchar)
                + '|' + convert(varchar, owner_sid, 1) + '|' + cast(notify_level_eventlog as varchar) + '|' + cast(notify_level_email as varchar)
                + '|' + cast(originating_server_id as varchar) + '|' + cast(version_number as varchar) + '|' + cast(delete_level as varchar)
            from msdb.dbo.sysjobs where job_id = @id
            """));

    [TestMethod]
    public void AddJob_DuplicateName_IsVerifyJobsMsg14261()
    {
        var ex = new Simulation().AssertSqlError("""
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_add_job @job_name = N'J'
            """, 14261);
        AreEqual("The specified @name ('J') already exists.", ex.Errors[0].Message);
        AreEqual("sp_verify_job", ex.Errors[0].Procedure);
        AreEqual(60, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    [DataRow("@enabled = 2", 14266, "The specified '@enabled' is invalid (valid values are: 0, 1).")]
    [DataRow("@start_step_id = 2", 14266, "The specified '@start_step_id' is invalid (valid values are: 1).")]
    [DataRow("@category_name = N'nocat'", 14262, "The specified @category_name ('nocat') does not exist.")]
    [DataRow("@category_name = N'Replication'", 14234, "The specified '@category_name' is invalid (valid values are returned by sp_help_category).")]
    [DataRow("@owner_login_name = N'nologin'", 14234, "The specified '@owner_login_name' is invalid (valid values are returned by sp_helplogins [excluding Windows NT groups]).")]
    [DataRow("@notify_level_email = 9", 14266, "The specified '@notify_level_email' is invalid (valid values are: 0, 1, 2, 3).")]
    [DataRow("@notify_level_email = 2, @notify_email_operator_name = N'noop'", 14234, "The specified '@notify_email_operator_name' is invalid (valid values are returned by sp_help_operator).")]
    [DataRow("@delete_level = 4", 14266, "The specified '@delete_level' is invalid (valid values are: 0, 1, 2, 3).")]
    [DataRow("@originating_server = N'elsewhere'", 14275, "The originating server must be either local server or MSX server.")]
    [DataRow("@job_id = '00000000-0000-0000-0000-000000000001'", 14274, "Cannot add, update, or delete a job (or its steps or schedules) that originated from an MSX server.")]
    public void AddJob_RefusesAsReal(string arguments, int number, string message)
        => new Simulation().AssertSqlError($"declare @rc int; exec @rc = msdb.dbo.sp_add_job @job_name = N'j', {arguments}", number, message);

    /// <summary>
    /// Refusals each raised from the procedure and line real names (probed
    /// 2026-10-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec msdb.dbo.sp_delete_job", 14279, "msdb.dbo.sp_delete_job", 47, "Supply either @job_name, @job_id or @originating_server.")]
    [DataRow("exec msdb.dbo.sp_add_job @job_name = N'j', @category_name = N'[Uncategorized (Local)]', @category_id = 0", 14524, "sp_verify_category_identifiers", 23, "Supply either @category_id or @category_name.")]
    [DataRow("exec msdb.dbo.sp_add_job @job_name = N'j'; exec msdb.dbo.sp_add_jobstep @job_name = N'j', @step_name = NULL, @command = N'select 1'", 50000, "dbo.sp_add_jobstep_internal", 255,
        "Cannot insert the value NULL into column 'step_name', table 'msdb.dbo.sysjobsteps'; column does not allow nulls. INSERT fails.")]
    [DataRow("""
        exec msdb.dbo.sp_add_operator @name = N'op';
        exec msdb.dbo.sp_add_alert @name = N'al', @severity = 25;
        exec msdb.dbo.sp_add_notification @alert_name = N'al', @operator_name = N'op', @notification_method = 1;
        exec msdb.dbo.sp_add_notification @alert_name = N'al', @operator_name = N'op', @notification_method = 1
        """, 14261, "msdb.dbo.sp_add_notification", 55, "The specified notification ('al / op') already exists.")]
    public void Procedure_RefusesFromRealsSite(string batch, int number, string procedure, int line, string message)
    {
        var error = new Simulation().AssertSqlError(batch, number).Errors[0];
        AreEqual(message, error.Message);
        AreEqual(procedure, error.Procedure);
        AreEqual(line, error.LineNumber);
    }

    [TestMethod]
    public void AddJob_RefusalAnswersOne_BindingLeavesNull_AndANullNameIsTheInserts515()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table codes (n int, rc int)");
        _ = simulation.AssertSqlError("""
            use msdb;
            declare @a int, @b int, @c int;
            exec @a = sp_add_job @job_name = N'j', @enabled = 2;
            insert simulated.dbo.codes values (1, @a);
            exec @b = sp_add_job @job_name = N'j', @enabled = 300;
            insert simulated.dbo.codes values (2, @b);
            exec @c = sp_add_job @job_name = NULL;
            insert simulated.dbo.codes values (3, @c)
            """, 14266);
        AreEqual("1:1;2:;3:515", simulation.ExecuteScalar("select string_agg(concat(n, ':', rc), ';') within group (order by n) from codes"));
    }

    /// <summary>
    /// <c>sp_add_operator</c>'s <c>@enabled</c> is 0 or 1, checked by
    /// <c>sp_verify_operator</c> at its line 38 (probed 2026-10-21 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void AddOperator_EnabledOutOfRange_IsMsg14266()
    {
        var simulation = new Simulation();
        var ex = simulation.AssertSqlError("exec msdb.dbo.sp_add_operator @name = N'op', @enabled = 7", 14266);
        AreEqual("The specified '@enabled' is invalid (valid values are: 0, 1).", ex.Errors[0].Message);
        AreEqual("sp_verify_operator", ex.Errors[0].Procedure);
        AreEqual(38, ex.Errors[0].LineNumber);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from msdb.dbo.sysoperators where name = N'op'"));
    }

    [TestMethod]
    public void AgentRefusal_CaughtByATry_LeavesTheReturnCode()
        => AreEqual("14262|sp_verify_job_identifiers|67|42", new Simulation().ExecuteScalar("""
            declare @rc int = 42, @caught nvarchar(200);
            begin try exec @rc = msdb.dbo.sp_update_job @job_name = N'nope'; end try
            begin catch set @caught = concat(error_number(), '|', error_procedure(), '|', error_line()); end catch
            select concat(@caught, '|', @rc)
            """));

    [TestMethod]
    public void AddJob_UnqualifiedOutsideMsdb_IsMsg2812()
        => new Simulation().AssertSqlError("exec sp_add_job @job_name = N'j'", 2812);

    [TestMethod]
    public void AddJob_ResolvesThroughEveryMsdbName()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            exec msdb.dbo.sp_add_job @job_name = N'a';
            exec msdb..sp_add_job @job_name = N'b';
            use msdb;
            exec dbo.sp_add_job @job_name = N'c';
            select count(*) from msdb.dbo.sysjobs
            """));

    [TestMethod]
    public void AddJob_RollsBackWithItsTransaction()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            begin tran;
            exec msdb.dbo.sp_add_job @job_name = N'j';
            rollback;
            select count(*) from msdb.dbo.sysjobs
            """));

    [TestMethod]
    public void AddJobStep_InsertsAtItsPosition_AndKeepsTheSubsystemAsWritten()
        => AreEqual("1:first:TSQL:master;2:second:CmdExec:;3:third:tsql:model|4", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobstep @job_name = N'j', @step_name = N'second', @subsystem = N'CmdExec', @command = N'ls', @database_name = N'model';
            exec sp_add_jobstep @job_name = N'j', @step_name = N'third', @subsystem = N'tsql', @command = N'select 3', @database_name = N'model';
            exec sp_add_jobstep @job_name = N'j', @step_name = N'first', @step_id = 1, @command = N'select 1';
            select string_agg(concat(s.step_id, ':', s.step_name, ':', s.subsystem, ':', s.database_name), ';') within group (order by s.step_id)
                + '|' + cast(max(j.version_number) as varchar)
            from sysjobsteps s join sysjobs j on j.job_id = s.job_id
            """));

    [TestMethod]
    [DataRow("@step_id = 3", 14266, "The specified '@step_id' is invalid (valid values are: 1..1).")]
    [DataRow("@subsystem = N'SSIS'", 14234, "The specified '@subsystem' is invalid (valid values are returned by sp_enum_sqlagent_subsystems).")]
    [DataRow("@on_success_action = 7", 14266, "The specified '@on_success_action' is invalid (valid values are: 1, 2, 3, 4).")]
    [DataRow("@on_fail_action = 0", 14266, "The specified '@on_failure_action' is invalid (valid values are: 1, 2, 3, 4).")]
    [DataRow("@os_run_priority = 99", 14266, "The specified '@os_run_priority' is invalid (valid values are: -15, -1, 0, 1, 15).")]
    [DataRow("@flags = 64", 14545, "The @flags parameter is not valid for a job step of type 'TSQL'.")]
    [DataRow("@database_name = N'nodb'", 14262, "The specified @database_name ('nodb') does not exist.")]
    [DataRow("@subsystem = N'ANALYSISQUERY'", 14262, "The specified @server ('(null)') does not exist.")]
    [DataRow("@on_success_action = 4, @on_success_step_id = 1", 14235, "The specified '@on_success_step' is invalid (valid values are greater than 0 but excluding 1).")]
    [DataRow("@on_fail_action = 4", 14235, "The specified '@on_failure_step' is invalid (valid values are greater than 0 but excluding 1).")]
    public void AddJobStep_RefusesAsReal(string arguments, int number, string message)
        => new Simulation().AssertSqlError($"""
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_add_jobstep @job_name = N'j', @step_name = N's', @command = N'x', {arguments}
            """, number, message);

    [TestMethod]
    public void AddJobStep_MissingGotoStep_IsOnlyAWarning()
        => CollectionAssert.AreEqual(new[] { "14236:Warning: Non-existent step referenced by @on_success_step_id." }, Messages(new Simulation(), """
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_add_jobstep @job_name = N'j', @step_name = N's', @on_success_action = 4, @on_success_step_id = 9
            """));

    [TestMethod]
    public void JobIdentifiers_NeitherOrBoth_IsMsg14294()
    {
        var ex = new Simulation().AssertSqlError("exec msdb.dbo.sp_add_jobstep @step_name = N's'", 14294);
        AreEqual("Supply either @job_id or @job_name to identify the job.", ex.Errors[0].Message);
        AreEqual("sp_verify_job_identifiers", ex.Errors[0].Procedure);
        AreEqual(25, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    public void JobServer_NotifiesTheStoppedAgent()
        => CollectionAssert.AreEqual(new[] { "22022:SQLServerAgent is not currently running so it cannot be notified of this action." }, Messages(new Simulation(), """
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_add_jobserver @job_name = N'j'
            """));

    /// <summary>
    /// A schedule a targeted job uses tells the stopped Agent of its update
    /// and of its detachment, once each (probed 2026-10-25 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    public void ScheduleOfATargetedJob_NotifiesAsItChanges()
        => CollectionAssert.AreEqual(new[] { "22022", "22022", "22022" }, Messages(new Simulation(), """
            exec msdb.dbo.sp_add_schedule @schedule_name = N's', @freq_type = 4, @freq_interval = 1;
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_attach_schedule @job_name = N'j', @schedule_name = N's';
            exec msdb.dbo.sp_add_jobserver @job_name = N'j';
            exec msdb.dbo.sp_update_schedule @name = N's', @freq_interval = 2;
            exec msdb.dbo.sp_detach_schedule @job_name = N'j', @schedule_name = N's'
            """).ConvertAll(static message => message.Split(':')[0]));

    [TestMethod]
    public void JobServer_OnlyTheLocalServer_Once()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sp_add_job @job_name = N'j'; exec msdb.dbo.sp_add_jobserver @job_name = N'j', @server_name = N'(local)'");
        simulation.AssertSqlError("exec msdb.dbo.sp_add_jobserver @job_name = N'j', @server_name = N'other'", 14262, "The specified @server_name ('OTHER') does not exist.");
        simulation.AssertSqlError("exec msdb.dbo.sp_add_jobserver @job_name = N'j'", 14269, "Job 'j' is already targeted at server 'SIMULATED'.");
        AreEqual("0|5", simulation.ExecuteScalar("select concat(server_id, '|', last_run_outcome) from msdb.dbo.sysjobservers"));
    }

    [TestMethod]
    public void StartJob_WithoutServer_IsMsg14256()
        => new Simulation().AssertSqlError("""
            declare @id uniqueidentifier = '2B5C1F6E-0A35-4E61-9C4B-1D2E3F405162';
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_start_job @job_name = N'j'
            """, 14256);

    [TestMethod]
    public void StartJob_StoppedAgent_RefusesPastTry_AnsweringOne()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table caught (n int);
            create table code (rc int);
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_add_jobserver @job_name = N'j'
            """);
        var ex = simulation.AssertSqlError("""
            declare @rc int;
            begin try
                exec @rc = msdb.dbo.sp_start_job @job_name = N'j';
                insert code values (@rc);
            end try
            begin catch
                insert caught values (1);
            end catch
            """, 22022);
        AreEqual("SQLServerAgent is not currently running so it cannot be notified of this action.", ex.Errors[0].Message);
        AreEqual("0|1", simulation.ExecuteScalar("select concat((select count(*) from caught), '|', (select rc from code))"));
    }

    [TestMethod]
    public void UpdateJob_ReplacesWhatItIsGiven_AndAnEmptyDescriptionClears()
        => AreEqual("k|0|NULL|3|4", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_job @job_name = N'j', @description = N'd';
            exec sp_update_job @job_name = N'j', @new_name = N'k', @enabled = 0, @category_name = N'Database Maintenance';
            exec sp_update_job @job_name = N'k', @new_name = N'';
            exec sp_update_job @job_name = N'k', @description = N'';
            select concat(name, '|', enabled, '|', isnull(description, 'NULL'), '|', category_id, '|', version_number) from sysjobs
            """));

    [TestMethod]
    public void JobServer_OnlyTheFirstStepNotifies()
        => CollectionAssert.AreEqual(new[] { "22022", "22022" }, Messages(new Simulation(), """
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_add_jobserver @job_name = N'j';
            exec msdb.dbo.sp_add_jobstep @job_name = N'j', @step_name = N'a';
            exec msdb.dbo.sp_add_jobstep @job_name = N'j', @step_name = N'b';
            exec msdb.dbo.sp_add_jobstep @job_name = N'j', @step_name = N'c', @step_id = 1
            """).ConvertAll(static message => message.Split(':')[0]));

    [TestMethod]
    public void DeleteJobStep_ResetsGotosAtOrPastIt()
        => AreEqual("1:b:1:0:2:0;2:c:1:0:2:0", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobstep @job_name = N'j', @step_name = N'a';
            exec sp_add_jobstep @job_name = N'j', @step_name = N'b', @on_fail_action = 4, @on_fail_step_id = 1;
            exec sp_add_jobstep @job_name = N'j', @step_name = N'c', @on_success_action = 4, @on_success_step_id = 2, @on_fail_action = 4, @on_fail_step_id = 1;
            exec sp_delete_jobstep @job_name = N'j', @step_id = 1;
            select string_agg(concat(step_id, ':', step_name, ':', on_success_action, ':', on_success_step_id, ':', on_fail_action, ':', on_fail_step_id), ';')
                within group (order by step_id)
            from sysjobsteps
            """));

    [TestMethod]
    public void UpdateJobStep_And_DeleteJobStep_Renumber()
        => AreEqual("1:b:select 22|3", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobstep @job_name = N'j', @step_name = N'a', @command = N'select 1';
            exec sp_add_jobstep @job_name = N'j', @step_name = N'b', @command = N'select 2';
            exec sp_delete_jobstep @job_name = N'j', @step_id = 1;
            exec sp_update_jobstep @job_name = N'j', @step_id = 1, @command = N'select 22';
            select concat(s.step_id, ':', s.step_name, ':', s.command, '|', j.version_number - 2) from sysjobsteps s join sysjobs j on j.job_id = s.job_id
            """));

    [TestMethod]
    public void DeleteJob_TakesItsStepsServersAndUnusedSchedules()
        => AreEqual("0|0|0|0|7", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobstep @job_name = N'j', @step_name = N's', @command = N'select 1';
            exec sp_add_jobschedule @job_name = N'j', @name = N'daily', @freq_type = 4, @freq_interval = 1;
            exec sp_add_jobserver @job_name = N'j';
            exec sp_delete_job @job_name = N'j';
            select concat((select count(*) from sysjobs), '|', (select count(*) from sysjobsteps), '|', (select count(*) from sysjobservers), '|',
                (select count(*) from sysjobschedules), '|', (select count(*) from sysschedules))
            """));

    [TestMethod]
    public void Schedules_StartAfterTheDataCollectorsSeven_AndNormalize()
        => AreEqual("8:4:1:1:0:0;9:1:0:0:0:0;10:8:62:8:4:1", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_schedule @schedule_name = N's', @freq_type = 4, @freq_interval = 1, @freq_recurrence_factor = 5;
            exec sp_add_schedule @schedule_name = N's', @freq_type = 1, @freq_interval = 5, @freq_subday_type = 4, @freq_subday_interval = 5;
            exec sp_add_schedule @schedule_name = N'w', @freq_type = 8, @freq_interval = 62, @freq_recurrence_factor = 1, @freq_subday_type = 8,
                @freq_subday_interval = 2, @freq_relative_interval = 4;
            select string_agg(concat(schedule_id, ':', freq_type, ':', freq_interval, ':', freq_subday_type, ':', freq_relative_interval, ':', freq_recurrence_factor), ';')
                within group (order by schedule_id)
            from sysschedules where schedule_id > 7
            """));

    [TestMethod]
    [DataRow("@freq_type = 3", 14266, "The specified '@freq_type' is invalid (valid values are: 1, 4, 8, 16, 32, 64, 128).")]
    [DataRow("@freq_type = 4", 14278, "The schedule for this job is invalid (reason: @freq_interval must be at least 1 for a daily job.).")]
    [DataRow("@freq_type = 8, @freq_interval = 2", 14278, "The schedule for this job is invalid (reason: @freq_recurrence_factor must be at least 1.).")]
    [DataRow("@freq_type = 32, @freq_interval = 1", 14278, "The schedule for this job is invalid (reason: @freq_relative_interval must be one of 1st (0x1), 2nd (0x2), 3rd [0x4], 4th (0x8) or Last (0x10).).")]
    [DataRow("@freq_type = 4, @freq_interval = 1, @active_start_time = 250000", 14266, "The specified '@active_start_time' is invalid (valid values are: 000000..235959).")]
    [DataRow("@freq_type = 4, @freq_interval = 1, @active_start_time = 120060", 14287, "The '@active_start_time' supplied has an invalid second.")]
    [DataRow("@freq_type = 4, @freq_interval = 1, @active_start_date = 20260101, @active_end_date = 20251231", 14288, "@active_end_date cannot be before @active_start_date.")]
    public void AddSchedule_RefusesAsReal(string arguments, int number, string message)
        => new Simulation().AssertSqlError($"exec msdb.dbo.sp_add_schedule @schedule_name = N's', {arguments}", number, message);

    [TestMethod]
    public void Schedules_AttachOnce_RefuseDeletingInUse_AndNameDuplicatesAmbiguously()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_schedule @schedule_name = N's', @freq_type = 64;
            exec sp_attach_schedule @job_name = N'j', @schedule_name = N's';
            exec sp_attach_schedule @job_name = N'j', @schedule_name = N's';
            exec sp_add_schedule @schedule_name = N'twice', @freq_type = 64;
            exec sp_add_schedule @schedule_name = N'twice', @freq_type = 128
            """);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from msdb.dbo.sysjobschedules"));
        simulation.AssertSqlError("exec msdb.dbo.sp_delete_schedule @schedule_name = N's'", 14372, "The schedule was not deleted because it is being used by one or more jobs.");
        simulation.AssertSqlError("exec msdb.dbo.sp_delete_schedule @schedule_name = N'twice'", 14371,
            "There are two or more schedules named \"twice\". Specify @schedule_id instead of @schedule_name to uniquely identify the schedule.");
    }

    [TestMethod]
    public void HelpJob_AllAspects_PrintTheirHeadings()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sp_add_job @job_name = N'j'");
        CollectionAssert.AreEqual(
            new[] { "14213:Core Job Details:", "0:=================", "0: ", "14214:Job Steps:", "0:==========", "0: ", "14215:Job Schedules:", "0:==============", "0: ", "14216:Job Target Servers:", "0:===================" },
            Messages(simulation, "exec msdb.dbo.sp_help_job @job_name = N'j'"));
    }

    [TestMethod]
    public void HelpJob_JobRow_ReadsAsTheStoppedAgentLeavesIt()
        => AreEqual("SIMULATED|[Uncategorized (Local)]|sa|(unknown)|5|4|0 (unknown)|1|0|1|1", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobstep @job_name = N'j', @step_name = N's', @command = N'select 1';
            exec sp_add_jobserver @job_name = N'j';
            create table #h (job_id uniqueidentifier, originating_server nvarchar(128), name nvarchar(128), enabled tinyint, description nvarchar(512),
                start_step_id int, category nvarchar(128), owner nvarchar(128), notify_level_eventlog int, notify_level_email int, notify_level_netsend int,
                notify_level_page int, notify_email_operator nvarchar(128), notify_netsend_operator nvarchar(128), notify_page_operator nvarchar(128),
                delete_level int, date_created datetime, date_modified datetime, version_number int, last_run_date int, last_run_time int,
                last_run_outcome int, next_run_date int, next_run_time int, next_run_schedule_id int, current_execution_status int,
                current_execution_step nvarchar(max), current_retry_attempt int, has_step int, has_schedule int, has_target int, type int);
            insert #h exec sp_help_job @job_name = N'j', @job_aspect = 'JOB';
            select concat(originating_server, '|', category, '|', owner, '|', notify_email_operator, '|', last_run_outcome, '|', current_execution_status,
                '|', current_execution_step, '|', has_step, '|', has_schedule, '|', has_target, '|', type) from #h
            """));

    [TestMethod]
    public void HelpJob_Steps_SpellTheirFlagsActionsAndPriority()
        => AreEqual("6 ((append output file), (include results in history))|3 (goto next step)|4 (goto step)|-15 (idle)", new Simulation().ExecuteScalar("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobstep @job_name = N'j', @step_name = N's', @command = N'select 1', @flags = 6, @on_success_action = 3,
                @on_fail_action = 4, @on_fail_step_id = 2, @os_run_priority = -15;
            create table #s (step_id int, step_name nvarchar(128), subsystem nvarchar(40), command nvarchar(max), flags nvarchar(4000),
                cmdexec_success_code int, on_success_action nvarchar(4000), on_success_step_id int, on_fail_action nvarchar(4000),
                on_fail_step_id int, server nvarchar(128), database_name nvarchar(128), database_user_name nvarchar(128), retry_attempts int,
                retry_interval int, os_run_priority nvarchar(4000), output_file_name nvarchar(200), last_run_outcome int, last_run_duration int,
                last_run_retries int, last_run_date int, last_run_time int, proxy_id int);
            insert #s exec sp_help_job @job_name = N'j', @job_aspect = 'STEPS';
            select concat(flags, '|', on_success_action, '|', on_fail_action, '|', os_run_priority) from #s
            """));

    [TestMethod]
    public void HelpSchedule_DescribesItsSchedule()
        => AreEqual("Every 1 week(s) on Monday, Tuesday, Wednesday, Thursday, Friday every 2 hour(s) between 13000 and 200000", new Simulation().ExecuteScalar("""
            use msdb;
            declare @id int;
            exec sp_add_schedule @schedule_name = N's', @freq_type = 8, @freq_interval = 62, @freq_recurrence_factor = 1, @freq_subday_type = 8,
                @freq_subday_interval = 2, @active_start_time = 13000, @active_end_time = 200000, @schedule_id = @id output;
            create table #s (schedule_id int, schedule_uid uniqueidentifier, schedule_name nvarchar(128), enabled int, freq_type int, freq_interval int,
                freq_subday_type int, freq_subday_interval int, freq_relative_interval int, freq_recurrence_factor int, active_start_date int,
                active_end_date int, active_start_time int, active_end_time int, date_created datetime, schedule_description nvarchar(4000), job_count int);
            insert #s exec sp_help_schedule @schedule_id = @id, @include_description = 1;
            select schedule_description from #s
            """));

    [TestMethod]
    public void HelpCategory_ListsTheShippedJobCategories_ByTypeThenName()
        => AreEqual("[Uncategorized (Local)];Data Collector;Database Engine Tuning Advisor;Database Maintenance;Full-Text;Jobs from MSX;Log Shipping;"
            + "REPL-Alert Response;REPL-Checkup;REPL-Distribution;REPL-Distribution Cleanup;REPL-History Cleanup;REPL-LogReader;REPL-Merge;"
            + "REPL-QueueReader;REPL-Snapshot;REPL-Subscription Cleanup;[Uncategorized (Multi-Server)]", new Simulation().ExecuteScalar("""
            create table #c (category_id int, category_type tinyint, name nvarchar(128));
            insert #c exec msdb.dbo.sp_help_category;
            select string_agg(name, ';') from #c
            """));

    [TestMethod]
    public void AddCategory_NumbersFrom100_AndAlertCategoriesTakeTypeNone()
    {
        var simulation = new Simulation();
        AreEqual(100, simulation.ExecuteScalar("exec msdb.dbo.sp_add_category @name = N'mine'; select category_id from msdb.dbo.syscategories where name = N'mine'"));
        simulation.AssertSqlError("exec msdb.dbo.sp_add_category @class = 'ALERT', @name = N'mine'", 14266, "The specified '@type' is invalid (valid values are: NONE).");
    }

    [TestMethod]
    public void Alerts_RefuseAnsweringZero_AndNotificationsMarkTheAlert()
    {
        var simulation = new Simulation();
        AreEqual(1, simulation.ExecuteScalar("""
            use msdb;
            exec sp_add_operator @name = N'op', @email_address = N'a@b.c';
            exec sp_add_alert @name = N'sev17', @severity = 17;
            exec sp_add_notification @alert_name = N'sev17', @operator_name = N'op', @notification_method = 1;
            select has_notification from sysalerts where name = N'sev17'
            """));
        _ = simulation.ExecuteNonQuery("create table codes (rc int)");
        simulation.AssertSqlError("""
            declare @rc int;
            exec @rc = msdb.dbo.sp_add_alert @name = N'again', @severity = 17;
            insert codes values (@rc)
            """, 14501, "An alert ('sev17') has already been defined on this condition.");
        AreEqual(0, simulation.ExecuteScalar("select rc from codes"));
        AreEqual(0, simulation.ExecuteScalar("exec msdb.dbo.sp_delete_operator @name = N'op'; select has_notification from msdb.dbo.sysalerts"));
    }

    [TestMethod]
    public void Permissions_OutsideTheAgentRoles_IsMsg229()
    {
        var ex = new Simulation().AssertSqlError("""
            create login l with password = 'Passw0rd!x', check_policy = off;
            use master; execute as login = 'l';
            exec msdb.dbo.sp_add_job @job_name = N'j'
            """, 229);
        AreEqual("The EXECUTE permission was denied on the object 'sp_add_job', database 'msdb', schema 'dbo'.", ex.Errors[0].Message);
    }

    [TestMethod]
    public void Permissions_AgentUserRole_ManagesItsOwnJobsAlone()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create login l with password = 'Passw0rd!x', check_policy = off;
            use msdb;
            create user l for login l;
            alter role SQLAgentUserRole add member l;
            exec sp_add_job @job_name = N'admins'
            """);
        AreEqual("mine|l|1", simulation.ExecuteScalar("""
            use master; execute as login = 'l';
            exec msdb.dbo.sp_add_job @job_name = N'mine';
            select concat(min(name), '|', min(suser_sname(owner_sid)), '|', count(*)) from msdb.dbo.sysjobs_view;
            revert
            """));
        simulation.AssertSqlError("use master; execute as login = 'l'; exec msdb.dbo.sp_update_job @job_name = N'admins', @enabled = 0", 14262,
            "The specified @job_name ('admins') does not exist.");
        simulation.AssertSqlError("use master; execute as login = 'l'; exec msdb.dbo.sp_add_job @job_name = N'theirs', @owner_login_name = N'sa'", 14515,
            "Only a member of the sysadmin server role can add a job for a different owner with @owner_login_name.");
        _ = simulation.AssertSqlError("use master; execute as login = 'l'; select count(*) from msdb.dbo.sysjobs", 229);
    }

    /// <summary>
    /// What a principal outside sysadmin may not do with what it doesn't own,
    /// each refused from the procedure and line real names (probed 2026-10-26
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void Permissions_OwnershipRefusals_RaiseFromRealsSites()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create login l with password = 'Passw0rd!x', check_policy = off; create login r with password = 'Passw0rd!x', check_policy = off",
            """
            use msdb;
            create user l for login l; alter role SQLAgentUserRole add member l;
            create user r for login r; alter role SQLAgentReaderRole add member r;
            exec sp_add_job @job_name = N'admins'; exec sp_add_jobserver @job_name = N'admins';
            exec sp_add_schedule @schedule_name = N'attached', @freq_type = 1; exec sp_attach_schedule @job_name = N'admins', @schedule_name = N'attached';
            exec sp_add_schedule @schedule_name = N'loose', @freq_type = 1
            """);
        void Refused(string login, string call, int number, string procedure, int line)
        {
            var error = simulation.AssertSqlError($"use master; execute as login = '{login}'; {call}", number).Errors[0];
            AreEqual(procedure, error.Procedure, call);
            AreEqual(line, error.LineNumber, call);
        }
        Refused("l", "exec msdb.dbo.sp_add_job @job_name = N'mine'; exec msdb.dbo.sp_add_jobstep @job_name = N'mine', @step_name = N's', @command = N'select 1', @database_user_name = N'dbo'",
            14583, "msdb.dbo.sp_add_jobstep", 59);
        Refused("l", "exec msdb.dbo.sp_add_schedule @schedule_name = N's', @freq_type = 1, @owner_login_name = N'sa'", 14366, "sp_verify_schedule", 59);
        Refused("r", "exec msdb.dbo.sp_attach_schedule @job_name = N'admins', @schedule_name = N'loose'", 14377, "msdb.dbo.sp_attach_schedule", 48);
        Refused("r", "exec msdb.dbo.sp_detach_schedule @job_name = N'admins', @schedule_name = N'attached'", 14391, "msdb.dbo.sp_detach_schedule", 74);
        Refused("r", "exec msdb.dbo.sp_update_schedule @name = N'loose', @enabled = 0", 14394, "msdb.dbo.sp_update_schedule", 108);
        Refused("r", "exec msdb.dbo.sp_start_job @job_name = N'admins'", 14393, "msdb.dbo.sp_start_job", 55);
        Refused("r", "exec msdb.dbo.sp_stop_job @job_name = N'admins'", 14393, "msdb.dbo.sp_stop_job", 114);
    }

    [TestMethod]
    public void Permissions_ReaderRole_SeesEveryJob_ButChangesOnlyItsOwn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create login r with password = 'Passw0rd!x', check_policy = off;
            use msdb;
            create user r for login r;
            alter role SQLAgentReaderRole add member r;
            exec sp_add_job @job_name = N'admins'
            """);
        AreEqual(1, simulation.ExecuteScalar("use master; execute as login = 'r'; select count(*) from msdb.dbo.sysjobs_view"));
        var ex = simulation.AssertSqlError("use master; execute as login = 'r'; exec msdb.dbo.sp_update_job @job_name = N'admins', @enabled = 0", 14525);
        AreEqual("msdb.dbo.sp_update_job", ex.Errors[0].Procedure);
        AreEqual(156, ex.Errors[0].LineNumber);
    }

    [TestMethod]
    public void AgentDateTime_ReadsAgentsIntegers()
        => AreEqual("2026-10-16 10:25:30|2026-10-16 00:00:05", new Simulation().ExecuteScalar("""
            select convert(varchar(19), msdb.dbo.agent_datetime(20261016, 102530), 120) + '|' + convert(varchar(19), msdb.dbo.agent_datetime(20261016, 5), 120)
            """));

    [TestMethod]
    [DataRow(20261016, 6000, 242)]
    [DataRow(20261016, 1000000, 8115)]
    [DataRow(2026101, 0, 241)]
    public void AgentDateTime_OutOfRange_IsTheConversionsError(int date, int time, int number)
        => new Simulation().AssertSqlError($"select msdb.dbo.agent_datetime({date}, {time})", number);

    [TestMethod]
    public void OlaHallengrensJobShape_InstallsAndUpdatesItsStep()
        => AreEqual("DatabaseBackup - USER_DATABASES - FULL|Database Maintenance|EXECUTE [dbo].[DatabaseBackup] @Checksum = 'Y'|3", new Simulation().ExecuteScalar("""
            declare @name nvarchar(max) = N'DatabaseBackup - USER_DATABASES - FULL', @job_id uniqueidentifier, @step_id int, @command nvarchar(max);
            if not exists (select * from msdb.dbo.sysjobs where [name] = @name)
            begin
                execute msdb.dbo.sp_add_job @job_name = @name, @description = N'Source: https://ola.hallengren.com', @category_name = N'Database Maintenance', @owner_login_name = N'sa';
                execute msdb.dbo.sp_add_jobstep @job_name = @name, @step_name = @name, @subsystem = N'TSQL',
                    @command = N'EXECUTE [dbo].[DatabaseBackup] @CheckSum = ''Y''', @output_file_name = N'/var/opt/mssql/log/$(ESCAPE_SQUOTE(JOBNAME))_$(ESCAPE_SQUOTE(STEPID)).txt', @database_name = N'master';
                execute msdb.dbo.sp_add_jobserver @job_name = @name;
            end
            select @job_id = job_id, @step_id = step_id, @command = replace(command, '@CheckSum', '@Checksum') from msdb.dbo.sysjobsteps where command like '%DatabaseBackup%@CheckSum%' collate SQL_Latin1_General_CP1_CS_AS;
            execute msdb.dbo.sp_update_jobstep @job_id = @job_id, @step_id = @step_id, @command = @command;
            select concat(j.name, '|', c.name, '|', s.command, '|', j.version_number)
            from msdb.dbo.sysjobs j join msdb.dbo.syscategories c on c.category_id = j.category_id join msdb.dbo.sysjobsteps s on s.job_id = j.job_id
            """));

    /// <summary>
    /// The procedure called is named as the call spelled it, the verification
    /// procedures it calls as it spells them (probed 2026-10-18 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec msdb.dbo.sp_delete_alert @name = N'none'", "msdb.dbo.sp_delete_alert")]
    [DataRow("exec msdb..sp_delete_alert @name = N'none'", "msdb..sp_delete_alert")]
    [DataRow("use msdb; exec dbo.sp_delete_alert @name = N'none'", "dbo.sp_delete_alert")]
    [DataRow("use msdb; exec sp_delete_alert @name = N'none'", "sp_delete_alert")]
    public void Refusal_NamesTheCalledProcedureAsWritten(string call, string expected)
        => AreEqual(expected, new Simulation().AssertSqlError(call, 14262).Errors[0].Procedure);

    [TestMethod]
    public void HelpAlert_RefusesAMissingNameOrId_NotACategory()
    {
        var simulation = new Simulation();
        var byName = simulation.AssertSqlError("exec msdb.dbo.sp_help_alert @alert_name = N'none'", 14262);
        AreEqual("30:The specified @alert_name ('none') does not exist.", $"{byName.LineNumber}:{byName.Errors[0].Message}");
        var byId = simulation.AssertSqlError("exec msdb.dbo.sp_help_alert @alert_id = 99", 14262);
        AreEqual("43:The specified @alert_id ('99') does not exist.", $"{byId.LineNumber}:{byId.Errors[0].Message}");
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sp_help_alert @category_name = N'none'");
    }

    /// <summary>
    /// <c>sp_delete_notification</c> raises its own Msg 14262 for the
    /// notification it can't find, after its verifier's for a missing alert or
    /// operator (probed 2026-10-18 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void DeleteNotification_RefusesWhatItCantFind_AfterItsVerifier()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            exec msdb.dbo.sp_add_operator @name = N'o';
            exec msdb.dbo.sp_add_alert @name = N'a', @severity = 17
            """);
        var ex = simulation.AssertSqlError("exec msdb.dbo.sp_delete_notification @alert_name = N'none', @operator_name = N'o'", 14262);
        AreEqual("sp_verify_notification:33:The specified @alert_name ('none') does not exist.|msdb.dbo.sp_delete_notification:44:The specified notification ('none / o') does not exist.",
            string.Join("|", ex.Errors.Select(static e => $"{e.Procedure}:{e.LineNumber}:{e.Message}")));
        AreEqual("sp_verify_notification:44", simulation.ExecuteScalar("""
            begin try exec msdb.dbo.sp_delete_notification @alert_name = N'a', @operator_name = N'none' end try
            begin catch select concat(error_procedure(), ':', error_line()) end catch
            """));
        simulation.AssertSqlError("exec msdb.dbo.sp_delete_notification @alert_name = N'a', @operator_name = N'o'", 14262, "The specified notification ('a / o') does not exist.");
    }

    /// <summary>
    /// The two job-schedule procedures refuse a schedule that exists but isn't
    /// the job's with Msg 14374 and one that doesn't exist with Msg 14262, each
    /// at a line of its own (probed 2026-10-18 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("sp_update_jobschedule", "theirs", 14374, 122, "The specified schedule name \"theirs\" is not associated with the job \"j\".")]
    [DataRow("sp_update_jobschedule", "none", 14262, 144, "The specified Schedule Name ('none') does not exist.")]
    [DataRow("sp_delete_jobschedule", "theirs", 14374, 110, "The specified schedule name \"theirs\" is not associated with the job \"j\".")]
    [DataRow("sp_delete_jobschedule", "none", 14262, 132, "The specified @name ('none') does not exist.")]
    public void JobSchedule_RefusesAnUnattachedOrMissingSchedule(string procedure, string schedule, int number, int line, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            exec msdb.dbo.sp_add_job @job_name = N'j';
            exec msdb.dbo.sp_add_schedule @schedule_name = N'theirs', @freq_type = 1
            """);
        var ex = simulation.AssertSqlError($"exec msdb.dbo.{procedure} @job_name = N'j', @name = N'{schedule}'", number);
        AreEqual($"msdb.dbo.{procedure}:{line}:{message}", $"{ex.Errors[0].Procedure}:{ex.LineNumber}:{ex.Errors[0].Message}");
    }

    [TestMethod]
    public void UpdateSchedule_MissingName_IsTheVerifiersScheduleName()
        => new Simulation().AssertSqlError("exec msdb.dbo.sp_update_schedule @name = N'none', @enabled = 0", 14262, "The specified @schedule_name ('none') does not exist.");

    /// <summary>
    /// The reports' columns and the rows their filters keep, as SQL Server
    /// 2025's read for one job with two steps, a schedule and its server and
    /// no history (probed 2026-10-18).
    /// </summary>
    [TestMethod]
    [DataRow("sp_help_jobhistory @job_name = N'j'", 0,
        "job_id|job_name|run_status|run_date|run_time|run_duration|operator_emailed|operator_netsent|operator_paged|retries_attempted|server")]
    [DataRow("sp_help_jobhistory @job_name = N'j', @mode = N'FULL'", 0,
        "instance_id|job_id|job_name|step_id|step_name|sql_message_id|sql_severity|message|run_status|run_date|run_time|run_duration|operator_emailed|operator_netsent|operator_paged|retries_attempted|server")]
    [DataRow("sp_help_jobactivity @job_name = N'j'", 0,
        "session_id|job_id|job_name|run_requested_date|run_requested_source|queued_date|start_execution_date|last_executed_step_id|last_executed_step_date|stop_execution_date|next_scheduled_run_date|job_history_id|message|run_status|operator_id_emailed|operator_id_netsent|operator_id_paged")]
    [DataRow("sp_help_jobstep @job_name = N'j', @step_id = 2", 1,
        "step_id|step_name|subsystem|command|flags|cmdexec_success_code|on_success_action|on_success_step_id|on_fail_action|on_fail_step_id|server|database_name|database_user_name|retry_attempts|retry_interval|os_run_priority|output_file_name|last_run_outcome|last_run_duration|last_run_retries|last_run_date|last_run_time|proxy_id")]
    [DataRow("sp_help_jobschedule @job_name = N'j'", 1,
        "schedule_id|schedule_name|enabled|freq_type|freq_interval|freq_subday_type|freq_subday_interval|freq_relative_interval|freq_recurrence_factor|active_start_date|active_end_date|active_start_time|active_end_time|date_created|schedule_description|next_run_date|next_run_time|schedule_uid|job_count")]
    [DataRow("sp_help_jobserver @job_name = N'j'", 1, "server_id|server_name|enlist_date|last_poll_date")]
    [DataRow("sp_help_job @enabled = 1, @subsystem = N'CmdExec', @category_name = N'[Uncategorized (Local)]'", 1, null)]
    [DataRow("sp_help_job @execution_status = 4", 0, null)]
    [DataRow("sp_help_job @owner_login_name = N'sa'", 1, null)]
    public void HelpReports_ReadTheJobAsReal(string call, int rows, string? columns)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use msdb;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobstep @job_name = N'j', @step_name = N's1', @command = N'select 1';
            exec sp_add_jobstep @job_name = N'j', @step_name = N's2', @subsystem = N'CmdExec', @command = N'echo';
            exec sp_add_schedule @schedule_name = N's', @freq_type = 4, @freq_interval = 1;
            exec sp_attach_schedule @job_name = N'j', @schedule_name = N's';
            exec sp_add_jobserver @job_name = N'j'
            """);
        using var reader = simulation.ExecuteReader($"exec msdb.dbo.{call}");
        if (columns is not null)
            AreEqual(columns, string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        HasCount(rows, reader.EnumerateRecords().ToList());
    }

    [TestMethod]
    public void HelpOperator_ReadsTheOperatorAsReal()
        => AreEqual("op|1|a@b.c|0|0||0|0|90000|180000|90000|180000|90000|180000|0||0|0|[Uncategorized]", new Simulation().ExecuteScalar("""
            exec msdb.dbo.sp_add_operator @name = N'op', @email_address = N'a@b.c';
            create table #o (id int, name nvarchar(128), enabled tinyint, email_address nvarchar(100), last_email_date int, last_email_time int,
                pager_address nvarchar(100), last_pager_date int, last_pager_time int, weekday_pager_start_time int, weekday_pager_end_time int,
                saturday_pager_start_time int, saturday_pager_end_time int, sunday_pager_start_time int, sunday_pager_end_time int, pager_days tinyint,
                netsend_address nvarchar(100), last_netsend_date int, last_netsend_time int, category_name nvarchar(128));
            insert #o exec msdb.dbo.sp_help_operator @operator_name = N'op';
            select concat_ws('|', name, enabled, email_address, last_email_date, last_email_time, isnull(pager_address, ''), last_pager_date, last_pager_time,
                weekday_pager_start_time, weekday_pager_end_time, saturday_pager_start_time, saturday_pager_end_time, sunday_pager_start_time, sunday_pager_end_time,
                pager_days, isnull(netsend_address, ''), last_netsend_date, last_netsend_time, category_name) from #o
            """));

    /// <summary>
    /// sp_update_category renames within the class, trimming its names, to a
    /// name the class doesn't hold, itself included; a missing category meets
    /// the Msg 2786 real's own procedure raises formatting its Msg 14262, and a
    /// NULL new name the UPDATE's Msg 515 (probed 2026-10-19).
    /// </summary>
    [TestMethod]
    public void UpdateCategory_RenamesWithinTheClass()
    {
        var simulation = new Simulation();
        AreEqual("0|mine2", simulation.ExecuteScalar("""
            use msdb;
            exec sp_add_category @name = N'mine';
            declare @rc int;
            exec @rc = sp_update_category @class = ' job ', @name = N' MINE ', @new_name = N' mine2 ';
            select cast(@rc as varchar) + '|' + name from syscategories where category_id = 100
            """));
        var missing = simulation.AssertSqlError("exec msdb.dbo.sp_update_category 'ALERT', N'mine2', N'x'", 2786);
        AreEqual(("msdb.dbo.sp_update_category", 35), (missing.Errors[0].Procedure, missing.Errors[0].LineNumber));
        var taken = simulation.AssertSqlError("exec msdb.dbo.sp_update_category 'JOB', N'mine2', N'MINE2'", 14261);
        AreEqual(("The specified @new_name ('MINE2') already exists.", 46), (taken.Errors[0].Message, taken.Errors[0].LineNumber));
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_update_category 'BOGUS', N'mine2', N'x'", 14266);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_update_category 'JOB', N'mine2', null", 515);
        AreEqual(0, simulation.ExecuteScalar("declare @rc int; exec @rc = msdb.dbo.sp_update_category 'JOB', null, N'x'; select @rc"));
    }

    /// <summary>
    /// sp_help_job verifies a named job before anything else, refuses a filter
    /// beside one (Msg 14280), and lets @execution_status and @description
    /// beside one filter its details row instead (probed 2026-10-19).
    /// </summary>
    [TestMethod]
    public void HelpJob_VerifiesTheJobBeforeItsArguments()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sp_add_job @job_name = N'j'");
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_job @job_name = N'none', @enabled = 1", 14262);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_job @job_name = N'none', @job_aspect = 'BOGUS'", 14262);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_job @job_name = N'j', @enabled = 1", 14280);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_job @job_name = N'j', @date_comparator = '='", 14280);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_job @job_name = N'j', @job_aspect = 'BOGUS', @execution_status = 1", 14266);
        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("exec msdb.dbo.sp_help_job @job_name = N'j', @job_aspect = 'JOB', @description = N'x'").ExecuteReader();
        IsFalse(reader.Read());
    }

    /// <summary>
    /// The deletion procedures' refusals and answers as real gives them
    /// (probed 2026-10-19): a category, a target server, a schedule detached,
    /// and job history purged.
    /// </summary>
    [TestMethod]
    public void DeletionProcedures_RemoveWhatTheyName()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use msdb;
            exec sp_add_category @name = N'cat';
            exec sp_add_job @job_name = N'j', @category_name = N'cat';
            exec sp_add_jobserver @job_name = N'j';
            exec sp_add_schedule @schedule_name = N's', @freq_type = 4, @freq_interval = 1;
            exec sp_attach_schedule @job_name = N'j', @schedule_name = N's';
            """);
        AreEqual("0|0", simulation.ExecuteScalar("""
            use msdb;
            exec sp_delete_category @class = 'JOB', @name = N'cat';
            select cast(category_id as varchar) + '|' + cast((select count(*) from syscategories where name = N'cat') as varchar) from sysjobs where name = N'j'
            """));
        simulation.AssertSqlError("exec msdb.dbo.sp_delete_category @class = 'JOB', @name = N'cat'", 14262, "The specified @name ('cat') does not exist.");
        AreEqual(0, simulation.ExecuteScalar("use msdb; exec sp_delete_jobserver @job_name = N'j', @server_name = N'(local)'; select count(*) from sysjobservers"));
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_delete_jobserver @job_name = N'j', @server_name = N'(local)'", 14270);
        AreEqual(0, simulation.ExecuteScalar("use msdb; exec sp_detach_schedule @job_name = N'j', @schedule_name = N's'; select count(*) from sysjobschedules"));
        simulation.AssertSqlError("exec msdb.dbo.sp_detach_schedule @job_name = N'j', @schedule_name = N's'", 14262, "The specified @schedule_name ('s') does not exist.");
        AreEqual(0, simulation.ExecuteScalar("declare @rc int; exec @rc = msdb.dbo.sp_purge_jobhistory @job_name = N'j'; select @rc"));
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_purge_jobhistory @job_name = N'none'", 14262);
        AreEqual(6, simulation.ExecuteScalar("create table #s (subsystem nvarchar(40), description nvarchar(512), subsystem_dll nvarchar(255), agent_exe nvarchar(255), start_entry_point nvarchar(30), event_entry_point nvarchar(30), stop_entry_point nvarchar(30), max_worker_threads int, subsystem_id int); insert #s exec msdb.dbo.sp_enum_sqlagent_subsystems; select count(*) from #s"));
    }

    /// <summary>
    /// sp_update_operator belongs to the 'Agent XPs' component, which the
    /// stopped Agent's configuration turns off: once its arguments bind it is
    /// Msg 15281 at line 0, the caller's return code unset (probed 2026-10-19).
    /// </summary>
    [TestMethod]
    public void UpdateOperator_IsBlockedWithAgentXpsOff()
    {
        var simulation = new Simulation();
        var blocked = simulation.AssertSqlError("declare @rc int = 7; exec @rc = msdb.dbo.sp_update_operator @name = N'none'", 15281);
        AreEqual(("SQL Server blocked access to procedure 'dbo.sp_update_operator' of component 'Agent XPs' because this component is turned off as part of the security configuration for this server. A system administrator can enable the use of 'Agent XPs' by using sp_configure. For more information about enabling 'Agent XPs', search for 'Agent XPs' in SQL Server Books Online.", 0),
            (blocked.Errors[0].Message, blocked.Errors[0].LineNumber));
        AreEqual(7, simulation.ExecuteScalar("declare @rc int = 7; begin try exec @rc = msdb.dbo.sp_update_operator @name = N'none' end try begin catch end catch; select @rc"));
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_update_operator", 201);
    }

    /// <summary>
    /// sp_add_alert and sp_update_alert share sp_verify_alert's checks of the
    /// response — @enabled, the delay, the event-description mask — and of a
    /// job, which needs a target server (probed 2026-10-19).
    /// </summary>
    [TestMethod]
    [DataRow("@enabled = 3", 14266, 133)]
    [DataRow("@delay_between_responses = -1", 14266, 141)]
    [DataRow("@include_event_description_in = 8", 14266, 151)]
    [DataRow("@job_name = N'j'", 14527, 197)]
    public void AddAlert_VerifiesTheResponse(string arguments, int number, int line)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sp_add_job @job_name = N'j'");
        var refused = simulation.AssertSqlError($"exec msdb.dbo.sp_add_alert @name = N'a', @severity = 17, {arguments}", number);
        AreEqual(("sp_verify_alert", line), (refused.Errors[0].Procedure, refused.Errors[0].LineNumber));
    }

    /// <summary>
    /// sp_update_alert lays the arguments given over the alert, verifies the
    /// result as sp_add_alert verifies a new one, and notifies the stopped
    /// Agent only of a change beyond the counters (probed 2026-10-19).
    /// </summary>
    [TestMethod]
    public void UpdateAlert_LaysTheArgumentsOverTheAlert()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use msdb;
            exec sp_add_alert @name = N'a', @severity = 17;
            exec sp_add_alert @name = N'b', @severity = 18;
            exec sp_add_job @job_name = N'j';
            exec sp_add_jobserver @job_name = N'j';
            """);
        CollectionAssert.AreEqual(new[] { "22022:SQLServerAgent is not currently running so it cannot be notified of this action." },
            Messages(simulation, "exec msdb.dbo.sp_update_alert @name = N'a', @new_name = N' c ', @enabled = 0, @notification_message = N'm', @database_name = N'', @job_name = N'j'"));
        AreEqual("c|0|17|m|NULL|1", simulation.ExecuteScalar("""
            select name + '|' + cast(enabled as varchar) + '|' + cast(severity as varchar) + '|' + notification_message + '|' + isnull(database_name, 'NULL')
                + '|' + cast((select count(*) from msdb.dbo.sysjobs as j where j.job_id = a.job_id) as varchar)
            from msdb.dbo.sysalerts as a where name = N'c'
            """));
        IsEmpty(Messages(simulation, "exec msdb.dbo.sp_update_alert @name = N'c', @occurrence_count = 0, @count_reset_date = 20200101"));
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_update_alert @name = N' c '", 14262);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_update_alert @name = N'c', @occurrence_count = 5", 14266);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_update_alert @name = N'c', @new_name = N'C'", 14261);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_update_alert @name = N'c', @message_id = 2627", 14500);
        var category = simulation.AssertSqlError("exec msdb.dbo.sp_update_alert @name = N'c', @category_name = N'none'", 14262);
        AreEqual(263, category.Errors[0].LineNumber);
        AreEqual(1, simulation.ExecuteScalar("use msdb; exec sp_update_alert @name = N'c', @job_id = '00000000-0000-0000-0000-000000000000'; select count(*) from sysalerts where name = N'c' and job_id = '00000000-0000-0000-0000-000000000000'"));
    }

    /// <summary>
    /// sp_help_jobcount and sp_help_jobs_in_schedule report the jobs a schedule
    /// named by exactly one identifier is attached to (probed 2026-10-19).
    /// </summary>
    [TestMethod]
    public void ScheduleJobReports_CountAndListTheJobs()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use msdb;
            exec sp_add_job @job_name = N'j1';
            exec sp_add_job @job_name = N'j2';
            exec sp_add_schedule @schedule_name = N's', @freq_type = 4, @freq_interval = 1;
            exec sp_attach_schedule @job_name = N'j1', @schedule_name = N's';
            exec sp_attach_schedule @job_name = N'j2', @schedule_name = N's';
            """);
        AreEqual(2, simulation.ExecuteScalar("exec msdb.dbo.sp_help_jobcount @schedule_name = N's'"));
        using (var connection = simulation.CreateOpenConnection())
        using (var reader = connection.CreateCommand("exec msdb.dbo.sp_help_jobs_in_schedule @schedule_id = 8").ExecuteReader())
        {
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(reader.GetOrdinal("name")));
            names.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(new[] { "j1", "j2" }, names);
        }
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_jobcount", 14373);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_jobs_in_schedule @schedule_name = N's', @schedule_id = 8", 14373);
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_help_jobcount @schedule_id = 99", 14262);
    }

    /// <summary>
    /// An Agent procedure binds its arguments as a T-SQL procedure's are
    /// bound: more than it has parameters is Msg 8144 whatever their names,
    /// and any to sp_enum_sqlagent_subsystems, which has none, Msg 8146 at
    /// state 1 (probed 2026-10-20).
    /// </summary>
    [TestMethod]
    [DataRow("exec msdb.dbo.sp_delete_category @class = N'JOB', @name = N'c', @dummy = 1", 8144, 2, "Procedure or function sp_delete_category has too many arguments specified.")]
    [DataRow("exec msdb.dbo.sp_help_category @dummy = 1", 8145, 1, "@dummy is not a parameter for procedure sp_help_category.")]
    [DataRow("exec msdb.dbo.sp_enum_sqlagent_subsystems @x = 1", 8146, 1, "Procedure sp_enum_sqlagent_subsystems has no parameters and arguments were supplied.")]
    public void Arguments_CountBeforeTheirNames(string call, int number, int state, string message)
    {
        var refused = new Simulation().AssertSqlError(call, number);
        AreEqual((message, (byte)state), (refused.Errors[0].Message, refused.Errors[0].State));
    }

    /// <summary>
    /// sp_stop_job takes exactly one of a job's name, its id or an originating
    /// server (Msg 14232), and a server no job came from is Msg 14268 named in
    /// upper case; sp_attach_schedule and sp_detach_schedule call the job
    /// verifier by its qualified name, which its errors carry (probed
    /// 2026-10-20).
    /// </summary>
    [TestMethod]
    public void JobIdentifiers_StopJobAndScheduleAttachment()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec msdb.dbo.sp_add_job @job_name = N'j'");
        var both = simulation.AssertSqlError("exec msdb.dbo.sp_stop_job @job_name = N'j', @job_id = '00000000-0000-0000-0000-000000000000'", 14232);
        AreEqual(("Specify either a job_name, job_id, or an originating_server.", "msdb.dbo.sp_stop_job", 35), (both.Errors[0].Message, both.Errors[0].Procedure, both.Errors[0].LineNumber));
        _ = simulation.AssertSqlError("exec msdb.dbo.sp_stop_job @job_name = N'j', @originating_server = N'x'", 14232);
        var origin = simulation.AssertSqlError("exec msdb.dbo.sp_stop_job @originating_server = N'far'", 14268);
        AreEqual(("There are no jobs at this server that originated from server 'FAR'.", 46), (origin.Errors[0].Message, origin.Errors[0].LineNumber));
        AreEqual("msdb.dbo.sp_verify_job_identifiers", simulation.AssertSqlError("exec msdb.dbo.sp_detach_schedule @schedule_name = N's'", 14294).Errors[0].Procedure);
        AreEqual("msdb.dbo.sp_verify_job_identifiers", simulation.AssertSqlError("exec msdb.dbo.sp_attach_schedule @job_name = N'none', @schedule_name = N's'", 14262).Errors[0].Procedure);
        AreEqual("sp_verify_job_identifiers", simulation.AssertSqlError("exec msdb.dbo.sp_add_jobschedule @job_name = N'none', @name = N's'", 14262).Errors[0].Procedure);
    }

    /// <summary>
    /// sp_help_jobschedule names a job, optionally with one of its schedules,
    /// or a schedule id alone, which answers for the one job using it
    /// (probed 2026-10-20).
    /// </summary>
    [TestMethod]
    public void HelpJobSchedule_ByJobOrBySchedule()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            use msdb;
            exec sp_add_job @job_name = N'j1';
            exec sp_add_job @job_name = N'j2';
            exec sp_add_schedule @schedule_name = N'one', @freq_type = 1;
            exec sp_add_schedule @schedule_name = N'two', @freq_type = 1;
            exec sp_add_schedule @schedule_name = N'none', @freq_type = 1;
            exec sp_attach_schedule @job_name = N'j1', @schedule_name = N'one';
            exec sp_attach_schedule @job_name = N'j1', @schedule_name = N'two';
            exec sp_attach_schedule @job_name = N'j2', @schedule_name = N'two';
            """);
        var (one, two) = (simulation.ExecuteScalar("select schedule_id from msdb.dbo.sysschedules where name = N'one'"), simulation.ExecuteScalar("select schedule_id from msdb.dbo.sysschedules where name = N'two'"));
        AreEqual(one, simulation.ExecuteScalar($"exec msdb.dbo.sp_help_jobschedule @schedule_id = {one}"));
        AreEqual(two, simulation.ExecuteScalar("exec msdb.dbo.sp_help_jobschedule @job_name = N'j2', @schedule_name = N'two'"));
        var many = simulation.AssertSqlError($"exec msdb.dbo.sp_help_jobschedule @schedule_id = {two}", 14369);
        AreEqual(70, many.Errors[0].LineNumber);
        AreEqual(80, simulation.AssertSqlError("declare @id int = (select schedule_id from msdb.dbo.sysschedules where name = N'none'); exec msdb.dbo.sp_help_jobschedule @schedule_id = @id", 14262).Errors[0].LineNumber);
        var unattached = simulation.AssertSqlError("exec msdb.dbo.sp_help_jobschedule @job_name = N'j2', @schedule_name = N'one'", 14262);
        AreEqual(("The specified @schedule_name ('one') does not exist.", "msdb.dbo.sp_verify_schedule_identifiers", 138),
            (unattached.Errors[0].Message, unattached.Errors[0].Procedure, unattached.Errors[0].LineNumber));
        var neither = simulation.AssertSqlError("exec msdb.dbo.sp_help_jobschedule @schedule_name = N'one'", 14273);
        AreEqual(("You must provide either @job_id or @job_name (and, optionally, @schedule_name), or @schedule_id.", 44), (neither.Errors[0].Message, neither.Errors[0].LineNumber));
        AreEqual(52, simulation.AssertSqlError("exec msdb.dbo.sp_help_jobschedule @job_name = N'j1', @schedule_id = 1", 14273).Errors[0].LineNumber);
    }

    /// <summary>
    /// An alert may name RAISERROR's own message 50000, which sys.messages
    /// lists no row for, but no other user number not added (probed
    /// 2026-10-20).
    /// </summary>
    [TestMethod]
    public void AddAlert_TakesMessage50000()
    {
        var simulation = new Simulation();
        AreEqual(50000, simulation.ExecuteScalar("use msdb; exec sp_add_alert @name = N'a', @message_id = 50000; select message_id from sysalerts where name = N'a'"));
        simulation.AssertSqlError("exec msdb.dbo.sp_add_alert @name = N'b', @message_id = 50001", 14262, "The specified @message_id ('50001') does not exist.");
    }
}
