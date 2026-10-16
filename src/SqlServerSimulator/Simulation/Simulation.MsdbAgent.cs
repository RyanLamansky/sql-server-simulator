using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The SQL Server Agent objects msdb ships beside the job tables
// SeedMsdbHistoryAndAgentTables seeds: the category, target-server,
// notification, proxy, subsystem and session tables, the rows a fresh
// instance holds in them, the views and the function over them, and the
// grants the Agent roles hold on them, in SQL Server 2025's shapes (probed
// 2026-10-16). The procedures that fill them live in Simulation.Agent*.cs.
partial class Simulation
{
    private static readonly (string Name, string[] Columns)[] MsdbAgentTables =
    [
        ("syscategories", ["category_id int identity", "category_class int", "category_type tinyint", "name sysname"]),
        ("sysjobservers", [
            "job_id uniqueidentifier", "server_id int", "last_run_outcome tinyint", "last_outcome_message nvarchar(4000) null",
            "last_run_date int", "last_run_time int", "last_run_duration int",
        ]),
        ("sysnotifications", ["alert_id int", "operator_id int", "notification_method tinyint"]),
        ("sysoriginatingservers", ["originating_server_id int null", "originating_server sysname", "master_server bit null"]),
        ("sysproxies", [
            "proxy_id int identity", "name sysname", "credential_id int", "enabled tinyint", "description nvarchar(512) null",
            "user_sid varbinary(85)", "credential_date_created datetime",
        ]),
        ("syssessions", ["session_id int identity", "agent_start_date datetime"]),
        ("syssubsystems", [
            "subsystem_id int", "subsystem nvarchar(40)", "description_id int null", "subsystem_dll nvarchar(255) null",
            "agent_exe nvarchar(255) null", "start_entry_point nvarchar(30) null", "event_entry_point nvarchar(30) null",
            "stop_entry_point nvarchar(30) null", "max_worker_threads int null",
        ]),
        ("systargetservers", [
            "server_id int identity", "server_name sysname", "location nvarchar(200) null", "time_zone_adjustment int",
            "enlist_date datetime", "last_poll_date datetime", "status int", "local_time_at_last_poll datetime",
            "enlisted_by_nt_user nvarchar(100)", "poll_interval int",
        ]),
    ];

    /// <summary>
    /// The job, alert and operator categories a fresh msdb lists, as
    /// <c>(category_id, category_class, category_type, name)</c>: class 1 jobs
    /// (type 1 local, 2 multi-server), 2 alerts and 3 operators (type 3,
    /// none). User categories take ids from 100.
    /// </summary>
    private static readonly (int Id, int Class, byte Type, string Name)[] AgentCategorySeeds =
    [
        (0, 1, 1, "[Uncategorized (Local)]"),
        (1, 1, 1, "Jobs from MSX"),
        (2, 1, 2, "[Uncategorized (Multi-Server)]"),
        (3, 1, 1, "Database Maintenance"),
        (5, 1, 1, "Full-Text"),
        (6, 1, 1, "Log Shipping"),
        (7, 1, 1, "Database Engine Tuning Advisor"),
        (8, 1, 1, "Data Collector"),
        (10, 1, 1, "REPL-Distribution"),
        (11, 1, 1, "REPL-Distribution Cleanup"),
        (12, 1, 1, "REPL-History Cleanup"),
        (13, 1, 1, "REPL-LogReader"),
        (14, 1, 1, "REPL-Merge"),
        (15, 1, 1, "REPL-Snapshot"),
        (16, 1, 1, "REPL-Checkup"),
        (17, 1, 1, "REPL-Subscription Cleanup"),
        (18, 1, 1, "REPL-Alert Response"),
        (19, 1, 1, "REPL-QueueReader"),
        (20, 2, 3, "Replication"),
        (98, 2, 3, "[Uncategorized]"),
        (99, 3, 3, "[Uncategorized]"),
    ];

    /// <summary>
    /// The schedules the data collector installs, which a fresh msdb ships as
    /// ids 1 through 7 (so a user's first schedule is 8), as <c>(uid, name,
    /// freq_type, freq_interval, freq_subday_type, freq_subday_interval)</c>.
    /// </summary>
    private static readonly (string Uid, string Name, int FreqType, int FreqInterval, int SubdayType, int SubdayInterval)[] AgentScheduleSeeds =
    [
        ("3EC51AB9-555E-494C-BF1F-413B08B1EC91", "RunAsSQLAgentServiceStartSchedule", 64, 0, 0, 0),
        ("97714154-C4A9-441E-AC59-11BD391ADC14", "CollectorSchedule_Every_5min", 4, 1, 4, 5),
        ("795C5D9F-2567-4AFF-8A29-4BA0C64985E5", "CollectorSchedule_Every_10min", 4, 1, 4, 10),
        ("8C8CA75B-93CC-4044-8947-6283B3B94DC9", "CollectorSchedule_Every_15min", 4, 1, 4, 15),
        ("6CAA3035-E1A5-438E-AAD0-A9BF506FB784", "CollectorSchedule_Every_30min", 4, 1, 4, 30),
        ("E2F5BF6F-73F3-4FD1-ACDC-E2EB059F7C11", "CollectorSchedule_Every_60min", 4, 1, 4, 60),
        ("4BEEFA9C-78A6-43D4-A6B3-5C74FE14592B", "CollectorSchedule_Every_6h", 4, 1, 8, 6),
    ];

    /// <summary>
    /// Seeds the Agent tables of <see cref="MsdbAgentTables"/>, the categories
    /// and the data collector's schedules, <c>sysjobs_view</c>,
    /// <c>sysoriginatingservers_view</c>, <c>sysschedules_localserver_view</c>
    /// and <c>dbo.agent_datetime</c>, and the <c>SELECT</c> grants the Agent
    /// roles hold on them: <c>SQLAgentUserRole</c> the category table and the
    /// job and schedule views, <c>SQLAgentOperatorRole</c> the alert,
    /// notification and operator tables. Each view lists to a login outside
    /// <c>sysadmin</c>, <c>SQLAgentReaderRole</c> and <c>TargetServersRole</c>
    /// only the rows it owns.
    /// </summary>
    private void SeedMsdbAgentObjects(Database msdb)
    {
        var schema = msdb.Schemas[Database.DefaultSchemaName];
        foreach (var (name, specs) in MsdbAgentTables)
        {
            var columns = new HeapColumn[specs.Length];
            for (var i = 0; i < specs.Length; i++)
                columns[i] = MsdbColumn(msdb, specs[i]);
            schema.HeapTables[name] = new HeapTable(name, columns, msdb.AllocateObjectId()) { OwningDatabase = msdb, IsMsShipped = true };
        }

        var categories = schema.HeapTables["syscategories"];
        foreach (var (id, @class, type, name) in AgentCategorySeeds)
            SeedMsdbRow(categories, [SqlValue.FromInt32(id), SqlValue.FromInt32(@class), SqlValue.FromByte(type), SqlValue.FromNVarchar(name)]);
        categories.StoredColumns[0].Identity!.ObserveExplicit(99);

        var schedules = schema.HeapTables["sysschedules"];
        var installed = WholeMilliseconds(this.SeedDate);
        var installedDate = AgentDate(installed);
        var id2 = 0;
        foreach (var (uid, name, freqType, freqInterval, subdayType, subdayInterval) in AgentScheduleSeeds)
        {
            SeedMsdbRow(schedules,
            [
                SqlValue.FromInt32(++id2), SqlValue.FromGuid(Guid.Parse(uid)), SqlValue.FromInt32(0), SqlValue.FromNVarchar(name),
                SqlValue.FromVarbinary(VarbinarySqlType.Get(85), [0x01]), SqlValue.FromInt32(1), SqlValue.FromInt32(freqType),
                SqlValue.FromInt32(freqInterval), SqlValue.FromInt32(subdayType), SqlValue.FromInt32(subdayInterval),
                SqlValue.FromInt32(0), SqlValue.FromInt32(0), SqlValue.FromInt32(installedDate), SqlValue.FromInt32(99991231),
                SqlValue.FromInt32(0), SqlValue.FromInt32(235959), SqlValue.FromDateTime(installed), SqlValue.FromDateTime(installed),
                SqlValue.FromInt32(1),
            ]);
        }
        schedules.StoredColumns[0].Identity!.ObserveExplicit(id2);

        _ = SeedMsdbView(schema, "sysoriginatingservers_view",
            [
                new("originating_server_id", SqlType.Int32, maxLength: null, nullable: true),
                new("originating_server", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
                new("master_server", SqlType.Int32, maxLength: null, nullable: true),
            ],
            "select 0 as originating_server_id, upper(convert(nvarchar(128), serverproperty('ServerName'))) as originating_server, 0 as master_server "
            + "union select originating_server_id, originating_server, convert(int, master_server) from dbo.sysoriginatingservers");

        const string visibleToCaller = "(owner_sid = suser_sid() or isnull(is_srvrolemember(N'sysadmin'), 0) = 1 "
            + "or isnull(is_member(N'SQLAgentReaderRole'), 0) = 1 or isnull(is_member(N'TargetServersRole'), 0) = 1)";
        var jobs = schema.HeapTables["sysjobs"];
        var jobsView = SeedMsdbView(schema, "sysjobs_view",
            [
                .. ViewColumns(jobs, ["job_id"]),
                new("originating_server", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
                .. ViewColumns(jobs, ["name", "enabled", "description", "start_step_id", "category_id", "owner_sid", "notify_level_eventlog",
                    "notify_level_email", "notify_level_netsend", "notify_level_page", "notify_email_operator_id", "notify_netsend_operator_id",
                    "notify_page_operator_id", "delete_level", "date_created", "date_modified", "version_number", "originating_server_id"]),
                new("master_server", SqlType.Int32, maxLength: null, nullable: true),
            ],
            "select jobs.job_id, svr.originating_server, jobs.name, jobs.enabled, jobs.description, jobs.start_step_id, jobs.category_id, "
            + "jobs.owner_sid, jobs.notify_level_eventlog, jobs.notify_level_email, jobs.notify_level_netsend, jobs.notify_level_page, "
            + "jobs.notify_email_operator_id, jobs.notify_netsend_operator_id, jobs.notify_page_operator_id, jobs.delete_level, "
            + "jobs.date_created, jobs.date_modified, jobs.version_number, jobs.originating_server_id, svr.master_server "
            + "from dbo.sysjobs as jobs join dbo.sysoriginatingservers_view as svr on jobs.originating_server_id = svr.originating_server_id "
            + "where " + visibleToCaller);

        var schedulesView = SeedMsdbView(schema, "sysschedules_localserver_view",
            [
                .. ViewColumns(schedules, ["schedule_id", "schedule_uid", "originating_server_id"]),
                new("name", SqlType.SystemName, maxLength: 128, nullable: false),
                .. ViewColumns(schedules, ["owner_sid", "enabled", "freq_type", "freq_interval", "freq_subday_type", "freq_subday_interval",
                    "freq_relative_interval", "freq_recurrence_factor", "active_start_date", "active_end_date", "active_start_time",
                    "active_end_time", "date_created", "date_modified", "version_number"]),
                new("originating_server", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
                new("master_server", SqlType.Int32, maxLength: null, nullable: true),
            ],
            "select sched.schedule_id, sched.schedule_uid, sched.originating_server_id, sched.name, sched.owner_sid, sched.enabled, "
            + "sched.freq_type, sched.freq_interval, sched.freq_subday_type, sched.freq_subday_interval, sched.freq_relative_interval, "
            + "sched.freq_recurrence_factor, sched.active_start_date, sched.active_end_date, sched.active_start_time, sched.active_end_time, "
            + "sched.date_created, sched.date_modified, sched.version_number, svr.originating_server, svr.master_server "
            + "from dbo.sysschedules as sched join dbo.sysoriginatingservers_view as svr on sched.originating_server_id = svr.originating_server_id "
            + "where svr.master_server = 0 and " + visibleToCaller);

        // agent_datetime(@date, @time) reads the two integers Agent stores as
        // yyyymmdd and hhmmss through a style-120 string, so a component out of
        // range is the conversion's own Msg 241 / 242 and an hour past 99 the
        // two-character cut's Msg 8115.
        const string agentDateTimeBody =
            "return convert(datetime, convert(nvarchar(4), @date / 10000) + N'-' + convert(nvarchar(2), (@date % 10000) / 100) + N'-' "
            + "+ convert(nvarchar(2), @date % 100) + N' ' + convert(nvarchar(2), @time / 10000) + N':' "
            + "+ convert(nvarchar(2), (@time % 10000) / 100) + N':' + convert(nvarchar(2), @time % 100), 120)";
        var agentDateTime = new ScalarFunction(
            schema,
            "agent_datetime",
            msdb.AllocateObjectId(),
            [new("date", SqlType.Int32, defaultExpression: null), new("time", SqlType.Int32, defaultExpression: null)],
            SqlType.DateTime,
            returnsNullOnNullInput: false,
            agentDateTimeBody,
            createDate: DateTime.UtcNow)
        {
            DefinitionText = $"CREATE FUNCTION dbo.agent_datetime(@date int, @time int) RETURNS datetime AS BEGIN {agentDateTimeBody} END",
            IsMsShipped = true,
        };
        schema.Functions[agentDateTime.Name] = agentDateTime;

        foreach (var objectId in (ReadOnlySpan<int>)[categories.ObjectId, jobsView.ObjectId, schedulesView.ObjectId])
            msdb.Permissions.Add(new(PermissionChecker.ClassObject, objectId, 0, AgentUserRoleId, Database.DboPrincipalId, Permission.Select, PermissionState.Grant));
        foreach (var table in (ReadOnlySpan<string>)["sysalerts", "sysnotifications", "sysoperators"])
            msdb.Permissions.Add(new(PermissionChecker.ClassObject, schema.HeapTables[table].ObjectId, 0, AgentOperatorRoleId, Database.DboPrincipalId, Permission.Select, PermissionState.Grant));
    }

    /// <summary>msdb's <c>SQLAgentUserRole</c>, whose members manage the jobs they own.</summary>
    internal const int AgentUserRoleId = 6;

    /// <summary>msdb's <c>SQLAgentReaderRole</c>, whose members also read every job.</summary>
    internal const int AgentReaderRoleId = 7;

    /// <summary>msdb's <c>SQLAgentOperatorRole</c>, whose members also start, stop, enable and disable every job.</summary>
    internal const int AgentOperatorRoleId = 8;

    private static void SeedMsdbRow(HeapTable table, SqlValue[] values)
    {
        for (var i = 0; i < values.Length; i++)
            values[i] = values[i].CoerceTo(table.StoredColumns[i].Type);
        _ = table.Heap.Insert(RowEncoder.EncodeRow(table.StoredColumns, values, table.Heap));
    }

    private static HeapColumn[] ViewColumns(HeapTable table, string[] names) =>
        Array.ConvertAll(names, name =>
        {
            var column = table.StoredColumns[MsdbOrdinal(table, name)];
            return new HeapColumn(column.Name, column.Type, column.MaxLength, column.Nullable);
        });

    private static View SeedMsdbView(Schema schema, string name, HeapColumn[] outputColumns, string bodyText)
    {
        var view = new View(
            schema,
            name,
            schema.Database.AllocateObjectId(),
            outputColumns,
            bodyText,
            withCheckOption: false,
            isSchemaBound: false,
            createDate: DateTime.UtcNow,
            baseTable: null,
            baseColumnOrdinals: [],
            rejectionReason: ViewUpdatabilityRejection.UnsupportedShape,
            visibilityCheck: null,
            checkOptionCheck: null,
            isJoinUpdatable: false)
        {
            DefinitionText = $"CREATE VIEW dbo.{name} AS {bodyText}",
            IsMsShipped = true,
        };
        schema.Views[view.Name] = view;
        return view;
    }

    /// <summary>A time cut to <c>datetime</c>'s whole milliseconds.</summary>
    private static DateTime WholeMilliseconds(DateTime value) => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Kind);

    /// <summary>A date as the <c>yyyymmdd</c> integer Agent's tables store.</summary>
    internal static int AgentDate(DateTime value) => int.Parse(value.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
