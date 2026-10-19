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
}
