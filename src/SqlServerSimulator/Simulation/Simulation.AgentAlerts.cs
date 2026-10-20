using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The Agent procedures over operators, alerts, their notifications and the
// categories jobs, alerts and operators file under, plus the job-history
// purge, as SQL Server 2025 runs them (probed 2026-10-16). Only a sysadmin
// manages operators, alerts and categories; SQLAgentUserRole reads operators
// and categories, and SQLAgentOperatorRole alerts and the history purge.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] SpAddOperatorParameters =
    [
        AgentRequired("name", AgentName, 128),
        AgentParameter("enabled", SqlType.TinyInt, SqlValue.FromByte(1)),
        AgentParameter("email_address", AgentText, 100),
        AgentParameter("pager_address", AgentText, 100),
        AgentParameter("weekday_pager_start_time", SqlType.Int32, SqlValue.FromInt32(90000)),
        AgentParameter("weekday_pager_end_time", SqlType.Int32, SqlValue.FromInt32(180000)),
        AgentParameter("saturday_pager_start_time", SqlType.Int32, SqlValue.FromInt32(90000)),
        AgentParameter("saturday_pager_end_time", SqlType.Int32, SqlValue.FromInt32(180000)),
        AgentParameter("sunday_pager_start_time", SqlType.Int32, SqlValue.FromInt32(90000)),
        AgentParameter("sunday_pager_end_time", SqlType.Int32, SqlValue.FromInt32(180000)),
        AgentParameter("pager_days", SqlType.TinyInt, SqlValue.FromByte(0)),
        AgentParameter("netsend_address", AgentText, 100),
        AgentParameter("category_name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_add_operator</c>: a new operator, after <c>sp_verify_operator</c>'s
    /// checks — a name no operator holds (Msg 14261), pager days 0 to 127 and
    /// pager hours of day (14266), and an operator category that exists
    /// (14262) — filed under <c>[Uncategorized]</c> by default.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddOperator(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_operator", SpAddOperatorParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_operator", grantedRole: null);
        var name = call.Text(0)!.Trim();
        if (this.AgentScalar(batch, "select id from dbo.sysoperators where name = @name", ("name", SqlValue.FromNVarchar(name))) is not null)
            throw SimulatedSqlException.AgentAlreadyExists("@name", name).AtAgentSite(31, "sp_verify_operator");
        if (call.Int(10) is < 0 or > 127)
            throw SimulatedSqlException.AgentInvalidValue("@pager_days", "0..127 [1 = Sunday .. 64 = Saturday]").AtAgentSite(45, "sp_verify_operator");
        string[] times = ["@weekday_pager_start_time", "@weekday_pager_end_time", "@saturday_pager_start_time", "@saturday_pager_end_time", "@sunday_pager_start_time", "@sunday_pager_end_time"];
        for (var i = 0; i < times.Length; i++)
            VerifyAgentTime(call.Int(4 + i)!.Value, times[i]);
        var categoryId = 99;
        if (call.Text(12) is { } category)
        {
            categoryId = this.AgentScalar(batch, "select category_id from dbo.syscategories where name = @name and category_class = 3", ("name", SqlValue.FromNVarchar(category)))?.AsInt32
                ?? throw SimulatedSqlException.AgentDoesNotExist("@category_name", category).AtAgentSite(87, "sp_verify_operator");
        }
        _ = this.AgentSql(batch, """
            insert dbo.sysoperators (name, enabled, email_address, last_email_date, last_email_time, pager_address, last_pager_date, last_pager_time,
                weekday_pager_start_time, weekday_pager_end_time, saturday_pager_start_time, saturday_pager_end_time, sunday_pager_start_time,
                sunday_pager_end_time, pager_days, netsend_address, last_netsend_date, last_netsend_time, category_id)
            values (@name, @enabled, @email, 0, 0, @pager, 0, 0, @t1, @t2, @t3, @t4, @t5, @t6, @days, @netsend, 0, 0, @category)
            """,
            ("name", SqlValue.FromNVarchar(name)), ("enabled", call.Values[1]), ("email", call.Values[2]), ("pager", call.Values[3]),
            ("t1", call.Values[4]), ("t2", call.Values[5]), ("t3", call.Values[6]), ("t4", call.Values[7]), ("t5", call.Values[8]), ("t6", call.Values[9]),
            ("days", call.Values[10]), ("netsend", call.Values[11]), ("category", SqlValue.FromInt32(categoryId)));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteOperatorParameters =
    [
        AgentRequired("name", AgentName, 128),
        AgentParameter("reassign_to_operator", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_delete_operator</c>: the operator and its notifications, the jobs
    /// that notified it notifying <c>@reassign_to_operator</c> or no one; one
    /// that doesn't exist is Msg 14262.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteOperator(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_operator", SpDeleteOperatorParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_operator", grantedRole: null);
        var name = call.Text(0)!;
        var id = this.AgentScalar(batch, "select id from dbo.sysoperators where name = @name", ("name", SqlValue.FromNVarchar(name)))
            ?? throw SimulatedSqlException.AgentDoesNotExist("@name", name).AtAgentSite(39);
        var reassign = call.Text(1) is { } other
            ? this.AgentScalar(batch, "select id from dbo.sysoperators where name = @name", ("name", SqlValue.FromNVarchar(other)))
                ?? throw SimulatedSqlException.AgentDoesNotExist("@reassign_to_operator", other).AtAgentSite(48)
            : SqlValue.FromInt32(0);
        _ = this.AgentSql(batch, """
            update dbo.sysjobs set notify_email_operator_id = @to where notify_email_operator_id = @id;
            update dbo.sysjobs set notify_netsend_operator_id = @to where notify_netsend_operator_id = @id;
            update dbo.sysjobs set notify_page_operator_id = @to where notify_page_operator_id = @id;
            delete dbo.sysnotifications where operator_id = @id;
            update dbo.sysalerts set has_notification = isnull((select max(n.notification_method) from dbo.sysnotifications as n where n.alert_id = sysalerts.id), 0);
            delete dbo.sysoperators where id = @id;
            """, ("id", id), ("to", reassign));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpHelpOperatorParameters =
    [
        AgentParameter("operator_name", AgentName, 128),
        AgentParameter("operator_id", SqlType.Int32),
    ];

    /// <summary><c>sp_help_operator</c>: every operator, or the one named, by name, with its category.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpOperator(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_operator", SpHelpOperatorParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_operator", AgentUserRoleId);
        if (call.Text(0) is { } operatorName
            && this.AgentScalar(batch, "select id from dbo.sysoperators where name = @name", ("name", SqlValue.FromNVarchar(operatorName))) is null)
        {
            throw SimulatedSqlException.AgentDoesNotExist("@operator_name", operatorName).AtAgentSite(21);
        }
        var rows = this.AgentSql(batch, """
            select o.id, o.name, o.enabled, o.email_address, o.last_email_date, o.last_email_time, o.pager_address, o.last_pager_date,
                o.last_pager_time, o.weekday_pager_start_time, o.weekday_pager_end_time, o.saturday_pager_start_time, o.saturday_pager_end_time,
                o.sunday_pager_start_time, o.sunday_pager_end_time, o.pager_days, o.netsend_address, o.last_netsend_date, o.last_netsend_time,
                (select c.name from dbo.syscategories as c where c.category_id = o.category_id)
            from dbo.sysoperators as o
            where (@name is null or o.name = @name) and (@id is null or o.id = @id)
            order by o.name
            """, ("name", call.Values[0]), ("id", call.Values[1]));
        yield return this.AgentResultSet(
            [
                "id int", "name nvarchar(128)", "enabled tinyint", "email_address nvarchar(100) null", "last_email_date int", "last_email_time int",
                "pager_address nvarchar(100) null", "last_pager_date int", "last_pager_time int", "weekday_pager_start_time int",
                "weekday_pager_end_time int", "saturday_pager_start_time int", "saturday_pager_end_time int", "sunday_pager_start_time int",
                "sunday_pager_end_time int", "pager_days tinyint", "netsend_address nvarchar(100) null", "last_netsend_date int",
                "last_netsend_time int", "category_name nvarchar(128) null",
            ], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpAddAlertParameters =
    [
        AgentRequired("name", AgentName, 128),
        AgentParameter("message_id", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("severity", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("enabled", SqlType.TinyInt, SqlValue.FromByte(1)),
        AgentParameter("delay_between_responses", SqlType.Int32, SqlValue.FromInt32(0)),
        AgentParameter("notification_message", AgentText, 512),
        AgentParameter("include_event_description_in", SqlType.TinyInt, SqlValue.FromByte(5)),
        AgentParameter("database_name", AgentName, 128),
        AgentParameter("event_description_keyword", AgentText, 100),
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("raise_snmp_trap", SqlType.TinyInt, SqlValue.FromByte(0)),
        AgentParameter("performance_condition", AgentText, 512),
        AgentParameter("category_name", AgentName, 128),
        AgentParameter("wmi_namespace", AgentName, 128),
        AgentParameter("wmi_query", AgentText, 512),
    ];

    /// <summary>
    /// <c>sp_add_alert</c>: a new alert on one condition — a message number, a
    /// severity, a performance condition or a WMI query (Msg 14500 for none or
    /// two), a message <c>sys.messages</c> holds and a severity 0 to 25, a
    /// database that exists (15010) and a job (14262) — that no alert already
    /// watches (14501), filed under <c>[Uncategorized]</c> by default, with the
    /// stopped Agent's notice. Real's procedure answers 0 even when it
    /// refuses.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddAlert(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_alert", SpAddAlertParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_alert", grantedRole: null);
        try
        {
            this.AddAgentAlert(batch, call);
        }
        catch (SimulatedSqlException refusal) when (refusal.AgentSite is not null)
        {
            refusal.SystemProcedureReturnCode = 0;
            throw;
        }
        yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private void AddAgentAlert(BatchContext batch, AgentCall call)
    {
        const string verifier = "sp_verify_alert";
        var name = call.Text(0)!.Trim();
        if (this.AgentScalar(batch, "select id from dbo.sysalerts where name = @name", ("name", SqlValue.FromNVarchar(name))) is not null)
            throw SimulatedSqlException.AgentAlreadyExists("@name", name).AtAgentSite(71, verifier);
        var messageId = call.Int(1)!.Value;
        var severity = call.Int(2)!.Value;
        var jobId = this.VerifyAgentAlert(batch, messageId, severity, call.Int(3)!.Value, call.Int(4)!.Value, call.Int(6)!.Value, EmptyAsNull(call.Text(7)),
            !call.IsNull(12), !call.IsNull(14), call.Guid(9), call.Text(10));
        var categoryId = 98;
        if (call.Text(13) is { } category)
        {
            categoryId = this.AgentScalar(batch, "select category_id from dbo.syscategories where name = @name and category_class = 2", ("name", SqlValue.FromNVarchar(category)))?.AsInt32
                ?? throw SimulatedSqlException.AgentDoesNotExist("@category_name", category).AtAgentSite(50, "sp_verify_category_identifiers");
        }
        if (this.AgentScalar(batch, """
            select top (1) name from dbo.sysalerts
            where message_id = @message and severity = @severity and isnull(database_name, N'') = isnull(@database, N'')
                and isnull(event_description_keyword, N'') = isnull(@keyword, N'') and performance_condition is null and @performance is null
            """,
            ("message", SqlValue.FromInt32(messageId)), ("severity", SqlValue.FromInt32(severity)), ("database", AgentNText(EmptyAsNull(call.Text(7)))),
            ("keyword", AgentNText(EmptyAsNull(call.Text(8)))), ("performance", call.Values[12])) is { } existing)
        {
            throw SimulatedSqlException.AgentAlertConditionTaken(existing.AsString).AtAgentSite(177, "msdb.dbo.sp_add_alert_internal");
        }
        _ = this.AgentSql(batch, """
            insert dbo.sysalerts (name, event_source, event_category_id, event_id, message_id, severity, enabled, delay_between_responses,
                last_occurrence_date, last_occurrence_time, last_response_date, last_response_time, notification_message, include_event_description,
                database_name, event_description_keyword, occurrence_count, count_reset_date, count_reset_time, job_id, has_notification, flags,
                performance_condition, category_id)
            values (@name, N'MSSQLSERVER', null, null, @message, @severity, @enabled, @delay, 0, 0, 0, 0, @text, @include, @database, @keyword,
                0, 0, 0, @job, 0, 0, @performance, @category)
            """,
            ("name", SqlValue.FromNVarchar(name)), ("message", SqlValue.FromInt32(messageId)), ("severity", SqlValue.FromInt32(severity)),
            ("enabled", call.Values[3]), ("delay", call.Values[4]), ("text", AgentNText(EmptyAsNull(call.Text(5)))), ("include", call.Values[6]),
            ("database", AgentNText(EmptyAsNull(call.Text(7)))), ("keyword", AgentNText(EmptyAsNull(call.Text(8)))), ("job", jobId), ("performance", call.Values[12]),
            ("category", SqlValue.FromInt32(categoryId)));
    }

    /// <summary>
    /// <c>sp_verify_alert</c>'s checks of an alert's condition and response, in
    /// its order: exactly one condition (Msg 14500), a severity 0 to 25
    /// (14266), a message that exists (14262), <c>@enabled</c> 0 or 1, a
    /// delay of no fewer than 0 seconds and an event-description mask 0 to 7
    /// (14266), a database that exists (15010) and a job that exists and has a
    /// target server (14527) — answering the job's id, or the empty one for
    /// none, which the empty id names too (probed 2026-10-19 against SQL
    /// Server 2025).
    /// </summary>
    private SqlValue VerifyAgentAlert(BatchContext batch, int messageId, int severity, int enabled, int delay, int include, string? database, bool hasPerformance, bool hasWmi, Guid? jobGuid, string? jobName)
    {
        const string verifier = "sp_verify_alert";
        var conditions = (messageId != 0 ? 1 : 0) + (severity != 0 ? 1 : 0) + (hasPerformance ? 1 : 0) + (hasWmi ? 1 : 0);
        if (conditions != 1)
            throw SimulatedSqlException.AgentAlertNeedsCondition().AtAgentSite(81, verifier);
        if (severity is < 0 or > 25)
            throw SimulatedSqlException.AgentInvalidValue("@severity", "0..25").AtAgentSite(88, verifier);
        // The engine's own messages aren't catalogued here, so any number
        // below the user range stands for one; a user message must have been
        // added with sp_addmessage, except RAISERROR's own 50000, which real
        // takes though sys.messages lists no row for it (probed 2026-10-20
        // against SQL Server 2025).
        if (messageId != 0 && (messageId < 1 || (messageId > 50000
            && this.AgentScalar(batch, "select 1 from sys.messages where message_id = @id", ("id", SqlValue.FromInt32(messageId))) is null)))
        {
            throw SimulatedSqlException.AgentDoesNotExist("@message_id", messageId.ToString(CultureInfo.InvariantCulture)).AtAgentSite(102, verifier);
        }
        if (enabled is not (0 or 1))
            throw SimulatedSqlException.AgentInvalidValue("@enabled", "0, 1").AtAgentSite(133, verifier);
        if (delay < 0)
            throw SimulatedSqlException.AgentInvalidValue("@delay_between_responses", "0..n seconds").AtAgentSite(141, verifier);
        if (include is < 0 or > 7)
            throw SimulatedSqlException.AgentInvalidValue("@include_event_description_in", "1..7 [1 = E-mail, 2 = Pager, 4 = NetSend]").AtAgentSite(151, verifier);
        if (database is not null && !this.Databases.ContainsKey(database))
            throw SimulatedSqlException.HelpDatabaseDoesNotExist(database).AtAgentSite(158, verifier);
        // The empty id names no job, which clears an alert's.
        if (jobName is null && (jobGuid ?? Guid.Empty) == Guid.Empty)
            return SqlValue.FromGuid(Guid.Empty);
        var job = this.ResolveAgentJob(batch, jobGuid, jobName);
        var jobId = AgentJobId(job);
        if (jobId.AsGuid != Guid.Empty && this.AgentScalar(batch, "select 1 from dbo.sysjobservers where job_id = @id", ("id", jobId)) is null)
            throw SimulatedSqlException.AgentAlertJobNotTargeted(job["name"].AsString).AtAgentSite(197, verifier);
        return jobId;
    }

    /// <summary>An alert's text argument, an empty one meaning none, as <c>sp_verify_alert</c> reads it.</summary>
    private static string? EmptyAsNull(string? text) => text is { Length: 0 } ? null : text;

    private static readonly SystemProcedureParameter[] SpUpdateAlertParameters =
    [
        AgentRequired("name", AgentName, 128),
        AgentParameter("new_name", AgentName, 128),
        AgentParameter("enabled", SqlType.TinyInt),
        AgentParameter("message_id", SqlType.Int32),
        AgentParameter("severity", SqlType.Int32),
        AgentParameter("delay_between_responses", SqlType.Int32),
        AgentParameter("notification_message", AgentText, 512),
        AgentParameter("include_event_description_in", SqlType.TinyInt),
        AgentParameter("database_name", AgentName, 128),
        AgentParameter("event_description_keyword", AgentText, 100),
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("occurrence_count", SqlType.Int32),
        AgentParameter("count_reset_date", SqlType.Int32),
        AgentParameter("count_reset_time", SqlType.Int32),
        AgentParameter("last_occurrence_date", SqlType.Int32),
        AgentParameter("last_occurrence_time", SqlType.Int32),
        AgentParameter("last_response_date", SqlType.Int32),
        AgentParameter("last_response_time", SqlType.Int32),
        AgentParameter("raise_snmp_trap", SqlType.TinyInt),
        AgentParameter("performance_condition", AgentText, 512),
        AgentParameter("category_name", AgentName, 128),
        AgentParameter("wmi_namespace", AgentName, 128),
        AgentParameter("wmi_query", AgentText, 512),
    ];

    /// <summary>
    /// <c>sp_update_alert</c>: the alert named exactly as written (Msg 14262),
    /// an occurrence count only reset to 0 (14266), then the arguments given
    /// laid over the alert and checked as <see cref="VerifyAgentAlert"/> checks
    /// a new one — a new name, even the alert's own, that an alert holds is
    /// Msg 14261, and the category last (14262). An empty new name keeps the
    /// name and an empty text clears its column; only a change beyond the
    /// counters and dates sends the stopped Agent's notice (probed 2026-10-19
    /// against SQL Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateAlert(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_update_alert", SpUpdateAlertParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_update_alert", grantedRole: null);
        var name = call.Text(0)!;
        var alert = this.AgentRows(batch, "sysalerts", "name = @name", [("name", SqlValue.FromNVarchar(name))]) is [var found] ? found
            : throw SimulatedSqlException.AgentDoesNotExist("@name", name).AtAgentSite(120);
        if (call.Int(12) is { } count && count != 0)
            throw SimulatedSqlException.AgentInvalidValue("@occurrence_count", "0").AtAgentSite(127);
        if (EmptyAsNull(call.Text(1)?.Trim()) is { } newName
            && this.AgentScalar(batch, "select 1 from dbo.sysalerts where name = @name", ("name", SqlValue.FromNVarchar(newName))) is not null)
        {
            throw SimulatedSqlException.AgentAlreadyExists("@name", newName).AtAgentSite(71, "sp_verify_alert");
        }
        string? Kept(int index, string column) => call.IsNull(index) ? alert[column] is { IsNull: false } kept ? kept.AsString : null : EmptyAsNull(call.Text(index));
        var messageId = call.Int(3) ?? alert["message_id"].AsInt32;
        var severity = call.Int(4) ?? alert["severity"].AsInt32;
        var enabled = call.Int(2) ?? alert["enabled"].CoerceTo(SqlType.Int32).AsInt32;
        var delay = call.Int(5) ?? alert["delay_between_responses"].AsInt32;
        var include = call.Int(7) ?? alert["include_event_description"].CoerceTo(SqlType.Int32).AsInt32;
        var database = Kept(8, "database_name");
        var performance = Kept(20, "performance_condition");
        var verifiedJob = this.VerifyAgentAlert(batch, messageId, severity, enabled, delay, include, database, performance is not null, !call.IsNull(22), call.Guid(10), call.Text(11));
        var jobId = call.IsNull(10) && call.IsNull(11) ? alert["job_id"] : verifiedJob;
        var categoryId = alert["category_id"];
        if (call.Text(21) is { } category)
        {
            categoryId = this.AgentScalar(batch, "select category_id from dbo.syscategories where name = @name and category_class = 2", ("name", SqlValue.FromNVarchar(category)))
                ?? throw SimulatedSqlException.AgentDoesNotExist("@category_name", category).AtAgentSite(263, "sp_verify_alert");
        }
        _ = this.AgentSql(batch, """
            update dbo.sysalerts set name = @name, message_id = @message, severity = @severity, enabled = @enabled, delay_between_responses = @delay,
                notification_message = @text, include_event_description = @include, database_name = @database, event_description_keyword = @keyword,
                job_id = @job, occurrence_count = isnull(@count, occurrence_count), count_reset_date = isnull(@resetDate, count_reset_date),
                count_reset_time = isnull(@resetTime, count_reset_time), last_occurrence_date = isnull(@occurredDate, last_occurrence_date),
                last_occurrence_time = isnull(@occurredTime, last_occurrence_time), last_response_date = isnull(@respondedDate, last_response_date),
                last_response_time = isnull(@respondedTime, last_response_time), performance_condition = @performance, category_id = @category
            where id = @id
            """,
            ("name", SqlValue.FromNVarchar(EmptyAsNull(call.Text(1)?.Trim()) ?? name)), ("message", SqlValue.FromInt32(messageId)),
            ("severity", SqlValue.FromInt32(severity)), ("enabled", SqlValue.FromByte((byte)enabled)), ("delay", SqlValue.FromInt32(delay)),
            ("text", AgentNText(Kept(6, "notification_message"))), ("include", SqlValue.FromByte((byte)include)), ("database", AgentNText(database)),
            ("keyword", AgentNText(Kept(9, "event_description_keyword"))), ("job", jobId), ("count", call.Values[12]), ("resetDate", call.Values[13]),
            ("resetTime", call.Values[14]), ("occurredDate", call.Values[15]), ("occurredTime", call.Values[16]), ("respondedDate", call.Values[17]),
            ("respondedTime", call.Values[18]), ("performance", AgentNText(performance)), ("category", categoryId), ("id", alert["id"]));
        var changesDefinition = false;
        for (var i = 1; i < call.Values.Length; i++)
            changesDefinition |= i is not (>= 12 and <= 18) && !call.IsNull(i);
        if (changesDefinition)
            yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteAlertParameters = [AgentRequired("name", AgentName, 128)];

    /// <summary><c>sp_delete_alert</c>: the alert and its notifications, with the stopped Agent's notice; one that doesn't exist is Msg 14262.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteAlert(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_alert", SpDeleteAlertParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_alert", grantedRole: null);
        var name = call.Text(0)!;
        var id = this.AgentScalar(batch, "select id from dbo.sysalerts where name = @name", ("name", SqlValue.FromNVarchar(name)))
            ?? throw SimulatedSqlException.AgentDoesNotExist("@name", name).AtAgentSite(30);
        _ = this.AgentSql(batch, "delete dbo.sysnotifications where alert_id = @id; delete dbo.sysalerts where id = @id;", ("id", id));
        yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpHelpAlertParameters =
    [
        AgentParameter("alert_name", AgentName, 128),
        AgentParameter("order_by", AgentName, SqlValue.FromNVarchar("name")),
        AgentParameter("alert_id", SqlType.Int32),
        AgentParameter("category_name", AgentName, 128),
        AgentParameter("legacy_format", SqlType.Bit, SqlValue.FromBoolean(false)),
    ];

    /// <summary><c>sp_help_alert</c>: every alert, or the one named, by name, with its job's and category's names.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpAlert(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_alert", SpHelpAlertParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_alert", AgentOperatorRoleId);
        // A name or id that matches no alert is refused; a category's is not
        // (probed 2026-10-18 against SQL Server 2025).
        if (call.Text(0) is { } alertName && this.AgentScalar(batch, "select 1 from dbo.sysalerts where name = @name", ("name", SqlValue.FromNVarchar(alertName))) is null)
            throw SimulatedSqlException.AgentDoesNotExist("@alert_name", alertName).AtAgentSite(30);
        if (call.Int(2) is { } alertId && this.AgentScalar(batch, "select 1 from dbo.sysalerts where id = @id", ("id", SqlValue.FromInt32(alertId))) is null)
            throw SimulatedSqlException.AgentDoesNotExist("@alert_id", alertId.ToString(CultureInfo.InvariantCulture)).AtAgentSite(43);
        var rows = this.AgentSql(batch, """
            select a.id, a.name, a.event_source, a.event_category_id, a.event_id, a.message_id, a.severity, a.enabled, a.delay_between_responses,
                a.last_occurrence_date, a.last_occurrence_time, a.last_response_date, a.last_response_time, a.notification_message,
                a.include_event_description, a.database_name, a.event_description_keyword, a.occurrence_count, a.count_reset_date,
                a.count_reset_time, a.job_id, (select j.name from dbo.sysjobs as j where j.job_id = a.job_id), a.has_notification, a.flags,
                a.performance_condition, (select c.name from dbo.syscategories as c where c.category_id = a.category_id), null, null,
                case when a.performance_condition is not null then 2 else 1 end
            from dbo.sysalerts as a
            where (@name is null or a.name = @name) and (@id is null or a.id = @id)
                and (@category is null or a.category_id in (select c.category_id from dbo.syscategories as c where c.name = @category))
            order by a.name
            """, ("name", call.Values[0]), ("id", call.Values[2]), ("category", call.Values[3]));
        yield return this.AgentResultSet(
            [
                "id int", "name nvarchar(128)", "event_source nvarchar(100)", "event_category_id int null", "event_id int null", "message_id int",
                "severity int", "enabled tinyint", "delay_between_responses int", "last_occurrence_date int", "last_occurrence_time int",
                "last_response_date int", "last_response_time int", "notification_message nvarchar(512) null", "include_event_description tinyint",
                "database_name nvarchar(512) null", "event_description_keyword nvarchar(100) null", "occurrence_count int", "count_reset_date int",
                "count_reset_time int", "job_id uniqueidentifier null", "job_name nvarchar(128) null", "has_notification int", "flags int",
                "performance_condition nvarchar(512) null", "category_name nvarchar(128) null", "wmi_namespace nvarchar(512) null",
                "wmi_query nvarchar(512) null", "type int",
            ], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpNotificationParameters =
    [
        AgentRequired("alert_name", AgentName, 128),
        AgentRequired("operator_name", AgentName, 128),
        AgentRequired("notification_method", SqlType.TinyInt),
    ];

    /// <summary>
    /// <c>sp_add_notification</c>: the alert notifies the operator by the
    /// method given (1 e-mail, 2 pager, 4 net send, in any sum), once
    /// (Msg 14261), with the stopped Agent's notice.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddNotification(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_notification", SpNotificationParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_notification", grantedRole: null);
        var (alertId, operatorId) = this.ResolveAgentNotification(batch, call.Text(0)!, call.Text(1)!);
        var method = call.Int(2)!.Value;
        if (method is < 1 or > 7)
            throw SimulatedSqlException.AgentInvalidValue("@notification_method", "1..7 [1 = E-mail, 2 = Pager, 4 = NetSend]").AtAgentSite(60, "msdb.dbo.sp_verify_notification");
        if (this.AgentScalar(batch, "select 1 from dbo.sysnotifications where alert_id = @alert and operator_id = @op", ("alert", alertId), ("op", operatorId)) is not null)
            throw SimulatedSqlException.AgentNotificationExists(call.Text(0)!, call.Text(1)!).AtAgentSite(55);
        _ = this.AgentSql(batch, """
            insert dbo.sysnotifications (alert_id, operator_id, notification_method) values (@alert, @op, @method);
            update dbo.sysalerts set has_notification = has_notification | @method where id = @alert;
            """, ("alert", alertId), ("op", operatorId), ("method", SqlValue.FromInt32(method)));
        yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteNotificationParameters =
    [
        AgentRequired("alert_name", AgentName, 128),
        AgentRequired("operator_name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_delete_notification</c>: the alert stops notifying the operator,
    /// with the stopped Agent's notice. A missing alert or operator is
    /// <c>sp_verify_notification</c>'s Msg 14262, after which the procedure
    /// goes on to raise its own for the notification it then can't find, as
    /// it does for a pair with none (probed 2026-10-18 against SQL Server
    /// 2025); a <c>TRY</c> catches the first.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteNotification(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_notification", SpDeleteNotificationParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_notification", grantedRole: null);
        var (alert, op) = (call.Text(0)!, call.Text(1)!);
        var alertId = this.AgentScalar(batch, "select id from dbo.sysalerts where name = @name", ("name", SqlValue.FromNVarchar(alert)));
        var operatorId = this.AgentScalar(batch, "select id from dbo.sysoperators where name = @name", ("name", SqlValue.FromNVarchar(op)));
        if (alertId is null || operatorId is null)
        {
            var unverified = alertId is null
                ? SimulatedSqlException.AgentDoesNotExist("@alert_name", alert)
                : SimulatedSqlException.AgentDoesNotExist("@operator_name", op);
            AttributeToSystemProcedure(unverified, (alertId is null ? 33 : 44, "sp_verify_notification"), "sp_delete_notification", "sp_delete_notification");
            foreach (var outcome in GoneOnPast(batch, unverified, ""))
                yield return outcome;
        }
        if (alertId is not { } alertKey || operatorId is not { } operatorKey
            || this.AgentScalar(batch, "select 1 from dbo.sysnotifications where alert_id = @alert and operator_id = @op", ("alert", alertKey), ("op", operatorKey)) is null)
        {
            throw SimulatedSqlException.AgentDoesNotExist("notification", $"{alert} / {op}").AtAgentSite(44);
        }
        _ = this.AgentSql(batch, """
            delete dbo.sysnotifications where alert_id = @alert and operator_id = @op;
            update dbo.sysalerts set has_notification = isnull((select max(n.notification_method) from dbo.sysnotifications as n where n.alert_id = @alert), 0) where id = @alert;
            """, ("alert", alertKey), ("op", operatorKey));
        yield return AgentNotRunningNotice(batch);
        AgentReturn(batch, returnCodeVar, 0);
    }

    /// <summary>The ids of the alert and operator a notification procedure names (Msg 14262 for either missing).</summary>
    private (SqlValue AlertId, SqlValue OperatorId) ResolveAgentNotification(BatchContext batch, string alert, string op)
    {
        var alertId = this.AgentScalar(batch, "select id from dbo.sysalerts where name = @name", ("name", SqlValue.FromNVarchar(alert)))
            ?? throw SimulatedSqlException.AgentDoesNotExist("@alert_name", alert).AtAgentSite(33, "msdb.dbo.sp_verify_notification");
        var operatorId = this.AgentScalar(batch, "select id from dbo.sysoperators where name = @name", ("name", SqlValue.FromNVarchar(op)))
            ?? throw SimulatedSqlException.AgentDoesNotExist("@operator_name", op).AtAgentSite(44, "msdb.dbo.sp_verify_notification");
        return (alertId, operatorId);
    }

    /// <summary>The <c>category_class</c> a category procedure's <c>@class</c> names (Msg 14266 for none of the three).</summary>
    private static int AgentCategoryClass(string? written, int line, string? procedure) => written?.Trim().ToUpperInvariant() switch
    {
        "ALERT" => 2,
        "JOB" => 1,
        "OPERATOR" => 3,
        _ => throw SimulatedSqlException.AgentInvalidValue("@class", "JOB, ALERT, OPERATOR").AtAgentSite(line, procedure),
    };

    private static readonly SystemProcedureParameter[] SpAddCategoryParameters =
    [
        AgentParameter("class", AgentVarchar, SqlValue.FromVarchar("JOB")),
        AgentParameter("type", AgentVarchar, SqlValue.FromVarchar("LOCAL")),
        AgentRequired("name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_add_category</c>: a job category of type <c>LOCAL</c> or
    /// <c>MULTI-SERVER</c>, or an alert or operator category of type
    /// <c>NONE</c> (Msg 14266 for any other pairing, the default <c>LOCAL</c>
    /// with an alert included), named as no category of its class is
    /// (14261).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddCategory(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_add_category", SpAddCategoryParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_add_category", grantedRole: null);
        var categoryClass = AgentCategoryClass(call.Text(0), 30, "sp_verify_category");
        var name = call.Text(2)!.Trim();
        if (this.AgentScalar(batch, "select 1 from dbo.syscategories where name = @name and category_class = @class", ("name", SqlValue.FromNVarchar(name)), ("class", SqlValue.FromInt32(categoryClass))) is not null)
            throw SimulatedSqlException.AgentAlreadyExists("@name", name).AtAgentSite(32);
        var type = call.Text(1)?.Trim().ToUpperInvariant();
        var categoryType = (categoryClass, type) switch
        {
            (1, "LOCAL") => 1,
            (1, "MULTI-SERVER") => 2,
            (1, _) => throw SimulatedSqlException.AgentInvalidValue("@type", "LOCAL, MULTI-SERVER").AtAgentSite(54, "sp_verify_category"),
            (_, "NONE") => 3,
            _ => throw SimulatedSqlException.AgentInvalidValue("@type", "NONE").AtAgentSite(62, "sp_verify_category"),
        };
        _ = this.AgentSql(batch, "insert dbo.syscategories (category_class, category_type, name) values (@class, @type, @name)",
            ("class", SqlValue.FromInt32(categoryClass)), ("type", SqlValue.FromByte((byte)categoryType)), ("name", SqlValue.FromNVarchar(name)));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpDeleteCategoryParameters =
    [
        AgentRequired("class", AgentVarchar, 8),
        AgentRequired("name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_delete_category</c>: the category, its jobs, alerts or operators
    /// returning to the class's uncategorized one; one that doesn't exist is
    /// Msg 14262.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDeleteCategory(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_delete_category", SpDeleteCategoryParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_delete_category", grantedRole: null);
        var categoryClass = AgentCategoryClass(call.Text(0), 30, "sp_verify_category");
        var name = call.Text(1)!;
        var id = this.AgentScalar(batch, "select category_id from dbo.syscategories where name = @name and category_class = @class",
            ("name", SqlValue.FromNVarchar(name)), ("class", SqlValue.FromInt32(categoryClass)))
            ?? throw SimulatedSqlException.AgentDoesNotExist("@name", name).AtAgentSite(33);
        _ = this.AgentSql(batch, categoryClass switch
        {
            1 => "update dbo.sysjobs set category_id = 0 where category_id = @id; delete dbo.syscategories where category_id = @id;",
            2 => "update dbo.sysalerts set category_id = 98 where category_id = @id; delete dbo.syscategories where category_id = @id;",
            _ => "update dbo.sysoperators set category_id = 99 where category_id = @id; delete dbo.syscategories where category_id = @id;",
        }, ("id", id));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpUpdateOperatorParameters =
    [
        AgentRequired("name", AgentName, 128),
        AgentParameter("new_name", AgentName, 128),
        AgentParameter("enabled", SqlType.TinyInt),
        AgentParameter("email_address", AgentText, 100),
        AgentParameter("pager_address", AgentText, 100),
        AgentParameter("weekday_pager_start_time", SqlType.Int32),
        AgentParameter("weekday_pager_end_time", SqlType.Int32),
        AgentParameter("saturday_pager_start_time", SqlType.Int32),
        AgentParameter("saturday_pager_end_time", SqlType.Int32),
        AgentParameter("sunday_pager_start_time", SqlType.Int32),
        AgentParameter("sunday_pager_end_time", SqlType.Int32),
        AgentParameter("pager_days", SqlType.TinyInt),
        AgentParameter("netsend_address", AgentText, 100),
        AgentParameter("category_name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_update_operator</c>, which with <c>Agent XPs</c> off — the
    /// stopped Agent's configuration — is Msg 15281 once its arguments bind,
    /// at line 0 with the caller's return code unset, whatever they name
    /// (probed 2026-10-19 against SQL Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateOperator(BatchContext batch)
    {
        if (BindAgentCall(batch, "sp_update_operator", SpUpdateOperatorParameters) is null)
            yield break;
        this.RequireAgentExecute(batch, "sp_update_operator", grantedRole: null);
        var refusal = SimulatedSqlException.ComponentBlocked("sp_update_operator", "Agent XPs", Database.DefaultSchemaName).AtAgentSite(0);
        refusal.SystemProcedureBindingError = true;
        throw refusal;
    }

    private static readonly SystemProcedureParameter[] SpUpdateCategoryParameters =
    [
        AgentRequired("class", AgentVarchar, 8),
        AgentRequired("name", AgentName, 128),
        AgentRequired("new_name", AgentName, 128),
    ];

    /// <summary>
    /// <c>sp_update_category</c>: renames a category of the class, a shipped
    /// one included, its names trimmed and matched under msdb's collation, to a
    /// name no category of the class has — itself included (Msg 14261). One
    /// that doesn't exist raises the Msg 2786 real's procedure meets formatting
    /// its own Msg 14262; a NULL name renames nothing, and a NULL new name is
    /// the UPDATE's Msg 515, answering 515 (probed 2026-10-19 against SQL
    /// Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpUpdateCategory(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_update_category", SpUpdateCategoryParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_update_category", grantedRole: null);
        var categoryClass = AgentCategoryClass(call.Text(0), 30, "sp_verify_category");
        if (call.Text(1)?.Trim() is not { } name)
        {
            AgentReturn(batch, returnCodeVar, 0);
            yield break;
        }
        var classValue = SqlValue.FromInt32(categoryClass);
        var id = this.AgentScalar(batch, "select category_id from dbo.syscategories where name = @name and category_class = @class", ("name", SqlValue.FromNVarchar(name)), ("class", classValue))
            ?? throw SimulatedSqlException.RaiserrorTypeMismatch(2).AtAgentSite(35);
        var newName = call.Text(2)?.Trim();
        if (newName is not null
            && this.AgentScalar(batch, "select 1 from dbo.syscategories where name = @name and category_class = @class", ("name", SqlValue.FromNVarchar(newName)), ("class", classValue)) is not null)
        {
            throw SimulatedSqlException.AgentAlreadyExists("@new_name", newName).AtAgentSite(46);
        }
        if (newName is null)
        {
            var refusal = SimulatedSqlException.CannotInsertNull("name", "msdb.dbo.syscategories", "UPDATE").AtAgentSite(58);
            refusal.SystemProcedureReturnCode = 515;
            // It ends the procedure's UPDATE, which real follows with Msg 3621.
            batch.CurrentStatement.WritesRows = true;
            throw refusal;
        }
        _ = this.AgentSql(batch, "update dbo.syscategories set name = @name where category_id = @id", ("name", SqlValue.FromNVarchar(newName)), ("id", id));
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpHelpCategoryParameters =
    [
        AgentParameter("class", AgentVarchar, SqlValue.FromVarchar("JOB")),
        AgentParameter("type", AgentVarchar, 12),
        AgentParameter("name", AgentName, 128),
        AgentParameter("suffix", SqlType.Bit, SqlValue.FromBoolean(false)),
    ];

    /// <summary><c>sp_help_category</c>: the categories of one class, by type and then name, optionally one type or name.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpHelpCategory(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_help_category", SpHelpCategoryParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_help_category", AgentUserRoleId);
        var categoryClass = AgentCategoryClass(call.Text(0), 30, "sp_verify_category");
        var type = (categoryClass, call.Text(1)?.Trim().ToUpperInvariant()) switch
        {
            (_, null) => (int?)null,
            (1, "LOCAL") => 1,
            (1, "MULTI-SERVER") => 2,
            (1, _) => throw SimulatedSqlException.AgentInvalidValue("@type", "LOCAL, MULTI-SERVER").AtAgentSite(54, "sp_verify_category"),
            (_, "NONE") => 3,
            _ => throw SimulatedSqlException.AgentInvalidValue("@type", "NONE").AtAgentSite(62, "sp_verify_category"),
        };
        var rows = this.AgentSql(batch, """
            select category_id, category_type, name from dbo.syscategories
            where category_class = @class and (@type is null or category_type = @type) and (@name is null or name = @name)
            order by category_type, name
            """, ("class", SqlValue.FromInt32(categoryClass)), ("type", type is { } t ? SqlValue.FromInt32(t) : SqlValue.Null(SqlType.Int32)), ("name", call.Values[2]));
        yield return this.AgentResultSet(["category_id int", "category_type tinyint", "name nvarchar(128)"], rows);
        AgentReturn(batch, returnCodeVar, 0);
    }

    private static readonly SystemProcedureParameter[] SpPurgeJobHistoryParameters =
    [
        AgentParameter("job_name", AgentName, 128),
        AgentParameter("job_id", SqlType.UniqueIdentifier),
        AgentParameter("oldest_date", SqlType.DateTime),
    ];

    /// <summary>
    /// <c>sp_purge_jobhistory</c>: a job's history, or every job's, older than
    /// <c>@oldest_date</c> when given, reporting the count (Msg 14226).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpPurgeJobHistory(BatchContext batch, string? returnCodeVar)
    {
        if (BindAgentCall(batch, "sp_purge_jobhistory", SpPurgeJobHistoryParameters) is not { } call)
            yield break;
        this.RequireAgentExecute(batch, "sp_purge_jobhistory", AgentOperatorRoleId);
        var jobId = SqlValue.Null(SqlType.UniqueIdentifier);
        if (!call.IsNull(0) || !call.IsNull(1))
            jobId = AgentJobId(this.ResolveAgentJob(batch, call.Guid(1), call.Text(0)));
        var purged = this.AgentScalar(batch, """
            delete dbo.sysjobhistory
            where (@job is null or job_id = @job)
                and (@oldest is null or run_date < convert(int, convert(char(8), @oldest, 112))
                    or (run_date = convert(int, convert(char(8), @oldest, 112)) and run_time < datepart(hour, @oldest) * 10000 + datepart(minute, @oldest) * 100 + datepart(second, @oldest)));
            select @@rowcount;
            """, ("job", jobId), ("oldest", call.Values[2]))!.Value.AsInt32;
        yield return AgentPrint(batch, "sp_purge_jobhistory", 79, 14226, string.Create(CultureInfo.InvariantCulture, $"{purged} history entries purged."));
        AgentReturn(batch, returnCodeVar, 0);
    }
}
