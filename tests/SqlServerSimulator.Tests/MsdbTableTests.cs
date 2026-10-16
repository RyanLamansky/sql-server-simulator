using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The msdb tables beside <c>backupset</c>: the backup and restore history,
/// SQL Server Agent's job tables and log shipping's, in SQL Server 2025's
/// column shapes (probed 2026-10-16).
/// </summary>
[TestClass]
public sealed class MsdbTableTests
{
    [TestMethod]
    [DataRow("backupmediafamily", "media_set_id int, family_sequence_number tinyint, media_family_id uniqueidentifier null, media_count int null, logical_device_name nvarchar(128) null, physical_device_name nvarchar(260) null, device_type tinyint null, physical_block_size int null, mirror tinyint")]
    [DataRow("restorehistory", "restore_history_id int identity, restore_date datetime null, destination_database_name nvarchar(128) null, user_name nvarchar(128) null, backup_set_id int, restore_type char(1) null, replace bit null, recovery bit null, restart bit null, stop_at datetime null, device_count tinyint null, stop_at_mark_name nvarchar(128) null, stop_before bit null")]
    [DataRow("sysjobsteps", "job_id uniqueidentifier, step_id int, step_name sysname, subsystem nvarchar(40), command nvarchar(max) null, flags int, additional_parameters nvarchar(max) null, cmdexec_success_code int, on_success_action tinyint, on_success_step_id int, on_fail_action tinyint, on_fail_step_id int, server sysname null, database_name sysname null, database_user_name sysname null, retry_attempts int, retry_interval int, os_run_priority int, output_file_name nvarchar(200) null, last_run_outcome int, last_run_duration int, last_run_retries int, last_run_date int, last_run_time int, proxy_id int null, step_uid uniqueidentifier null")]
    public void SeededTable_CarriesRealColumnShape(string table, string shape)
        => AreEqual(shape, new Simulation().ExecuteScalar($"""
            select string_agg(cast(c.name + ' ' + t.name
                + case when t.name in ('nvarchar', 'char', 'varbinary') then '(' + case when c.max_length = -1 then 'max' else cast(case when t.name = 'nvarchar' then c.max_length / 2 else c.max_length end as varchar(10)) end + ')'
                       when t.name = 'numeric' then '(' + cast(c.precision as varchar(10)) + ')' else '' end
                + case when c.is_identity = 1 then ' identity' when c.is_nullable = 1 then ' null' else '' end as nvarchar(max)), ', ') within group (order by c.column_id)
            from msdb.sys.objects o join msdb.sys.columns c on c.object_id = o.object_id join msdb.sys.types t on t.user_type_id = c.user_type_id
            where o.name = '{table}'
            """));

    [TestMethod]
    public void AgentJobSteps_AreEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from msdb.dbo.sysjobsteps where command like '%DatabaseBackup%'"));
}
