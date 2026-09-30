using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Resolution + value tests for the catalog surface added to unblock SMO's
/// property-bag / scripting queries (the SMO API sweep campaign): the
/// sys.types-derived columns on <c>sys.table_types</c>, <c>sys.all_parameters</c>,
/// the encryption-key / server-permission empty views SMO's Login / User bags
/// LEFT JOIN, <c>sys.endpoints</c>, <c>sys.numbered_procedures</c>, and the
/// default-language columns on <c>sys.database_principals</c>. Shapes / values
/// probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SmoSweepCatalogTests
{
    /// <summary>
    /// sys.table_types carries the sys.types-inherited columns SMO's UDTT bag
    /// reads (tt.max_length / is_nullable / collation_name / principal_id), all
    /// constant for a table type: system_type_id 243, max_length -1, precision
    /// 0, scale 0, collation_name NULL, is_nullable 0, is_table_type 1.
    /// </summary>
    [TestMethod]
    public void TableTypes_ExposesSysTypesInheritedColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type MyList as table (id int, name nvarchar(50))");
        AreEqual((short)-1, sim.ExecuteScalar("select max_length from sys.table_types where name = 'MyList'"));
        AreEqual((byte)243, sim.ExecuteScalar("select system_type_id from sys.table_types where name = 'MyList'"));
        AreEqual((byte)0, sim.ExecuteScalar("select precision from sys.table_types where name = 'MyList'"));
        AreEqual((byte)0, sim.ExecuteScalar("select scale from sys.table_types where name = 'MyList'"));
        IsTrue((bool)sim.ExecuteScalar("select is_table_type from sys.table_types where name = 'MyList'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_nullable from sys.table_types where name = 'MyList'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_assembly_type from sys.table_types where name = 'MyList'")!);
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select collation_name from sys.table_types where name = 'MyList'"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select principal_id from sys.table_types where name = 'MyList'"));
    }

    /// <summary>
    /// sys.all_parameters shares sys.parameters' rows (user objects only) and
    /// exposes the columns SMO's function/proc scripting reads — including
    /// is_cursor_ref / has_default_value / is_xml_document / default_value /
    /// xml_collection_id, all at their non-parameterized defaults.
    /// </summary>
    [TestMethod]
    public void AllParameters_SharesParametersRows_WithFullShape()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.f1(@a int, @b nvarchar(10)) returns int as begin return @a end");
        // Scalar UDF: parameter_id=0 return row + 2 declared params.
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.all_parameters where object_id = object_id('dbo.f1')"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.parameters where object_id = object_id('dbo.f1')"));
        IsFalse((bool)sim.ExecuteScalar("select is_cursor_ref from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1")!);
        IsFalse((bool)sim.ExecuteScalar("select has_default_value from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1")!);
        IsFalse((bool)sim.ExecuteScalar("select is_xml_document from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1")!);
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select default_value from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1"));
        AreEqual(0, sim.ExecuteScalar("select xml_collection_id from sys.all_parameters where object_id = object_id('dbo.f1') and parameter_id = 1"));
    }

    /// <summary>
    /// The encryption-key / numbered-proc views resolve and start empty, and
    /// server_permissions and server_role_members hold only the rows every
    /// instance starts with. SMO's Login /
    /// User bags LEFT JOIN certificates / asymmetric_keys / credentials /
    /// server_permissions / server_role_members; sys.endpoints backs
    /// Server.Endpoints, which lists the five system endpoints.
    /// </summary>
    [TestMethod]
    public void UnmodeledSecurityViews_ResolveEmpty()
    {
        var sim = new Simulation();
        foreach (var view in new[]
        {
            "asymmetric_keys", "certificates", "credentials",
            "numbered_procedures",
        })
        {
            AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.{view}"), view);
        }
        AreEqual(5, sim.ExecuteScalar("select count(*) from sys.endpoints"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.server_permissions"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_role_members"));
        // Login / User scripting reaches certificates / asymmetric_keys through
        // three-part master.sys names too.
        AreEqual(0, sim.ExecuteScalar("select count(*) from master.sys.certificates"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from master.sys.asymmetric_keys"));
    }

    /// <summary>
    /// sys.database_principals exposes the default-language columns SMO's User
    /// bag reads via ISNULL(u.default_language_lcid, -1) /
    /// ISNULL(u.default_language_name, N''); both are always NULL (untracked).
    /// </summary>
    [TestMethod]
    public void DatabasePrincipals_ExposesDefaultLanguageColumns()
    {
        var sim = new Simulation();
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select default_language_name from sys.database_principals where name = 'dbo'"));
        _ = IsInstanceOfType<DBNull>(sim.ExecuteScalar("select default_language_lcid from sys.database_principals where name = 'dbo'"));
    }

    /// <summary>
    /// Every modeled system object carries SQL Server 2025's own object id
    /// (probed 2026-09-30), the later-added procedures and views included.
    /// </summary>
    [TestMethod]
    [DataRow("sys.sp_addlogin", -222378892)]
    [DataRow("sys.sp_MSforeach_worker", -391278113)]
    [DataRow("sys.sp_query_store_flush_db", -730592741)]
    [DataRow("INFORMATION_SCHEMA.CHECK_CONSTRAINTS", -266402733)]
    [DataRow("sys.messages", -225)]
    [DataRow("sys.system_views", -390)]
    [DataRow("sys.syscolumns", -106)]
    [DataRow("sys.sysprocesses", -210)]
    [DataRow("sys.tcp_endpoints", -228)]
    [DataRow("sys.xp_regread", -34491504)]
    public void SystemObject_CarriesRealObjectId(string name, int objectId)
        => AreEqual(objectId, new Simulation().ExecuteScalar($"select object_id('{name}')"));

    /// <summary>
    /// OBJECTPROPERTY over system objects (probed 2026-09-30 against SQL Server
    /// 2025): a view answers the trigger and index members 0 where a procedure
    /// answers NULL, the Exec* family is 0 but for the module SET-option pair,
    /// the owner is the schema's principal, and an extended procedure answers
    /// the module-scoped members NULL. SMO's view and procedure bags read these.
    /// </summary>
    [TestMethod]
    public void ObjectProperty_SystemObjects()
    {
        var sim = new Simulation();
        AreEqual("0,0,0,1,1,4", sim.ExecuteScalar("""
            declare @id int = object_id('sys.tables');
            select concat_ws(',', objectproperty(@id, 'HasAfterTrigger'), objectproperty(@id, 'IsIndexed'), objectproperty(@id, 'IsIndexable'),
                objectproperty(@id, 'ExecIsAnsiNullsOn'), objectproperty(@id, 'ExecIsQuotedIdentOn'), objectproperty(@id, 'OwnerId'))
            """));
        AreEqual("1,0,3", sim.ExecuteScalar("""
            declare @id int = object_id('INFORMATION_SCHEMA.TABLES');
            select concat_ws(',', cast(objectpropertyex(@id, 'ExecIsAnsiNullsOn') as int), cast(objectpropertyex(@id, 'ExecIsQuotedIdentOn') as int), objectproperty(@id, 'OwnerId'))
            """));
        AreEqual("null,0,0,1,0", sim.ExecuteScalar("""
            declare @id int = object_id('sys.sp_help');
            select concat_ws(',', isnull(cast(objectproperty(@id, 'HasAfterTrigger') as varchar), 'null'), objectproperty(@id, 'ExecIsStartup'),
                objectproperty(@id, 'IsEncrypted'), objectproperty(@id, 'IsProcedure'), objectproperty(@id, 'IsTableFunction'))
            """));
        AreEqual("null,null,1,0", sim.ExecuteScalar("""
            declare @id int = object_id('sys.sp_executesql');
            select concat_ws(',', isnull(cast(objectproperty(@id, 'IsQuotedIdentOn') as varchar), 'null'), isnull(cast(objectproperty(@id, 'IsSchemaBound') as varchar), 'null'),
                objectproperty(@id, 'IsExtendedProc'), objectproperty(@id, 'ExecIsAnsiNullsOn'))
            """));
        AreEqual("1,0,1", sim.ExecuteScalar("""
            declare @id int = object_id('sys.fn_helpcollations');
            select concat_ws(',', objectproperty(@id, 'IsInlineFunction'), objectproperty(@id, 'TableHasIdentity'), objectproperty(@id, 'IsTableFunction'))
            """));
    }

    /// <summary>
    /// sys.system_sql_modules lists every system module but an extended
    /// procedure with real's flags — SMO reads a system procedure's Recompile
    /// from it — and sys.all_sql_modules adds the user modules to it. The
    /// definition is NULL: the simulator doesn't carry real's system text.
    /// </summary>
    [TestMethod]
    public void SystemSqlModules_CarryFlags()
    {
        var sim = new Simulation();
        AreEqual("1,1,0,1", sim.ExecuteScalar("""
            select concat_ws(',', cast(uses_ansi_nulls as int), cast(uses_quoted_identifier as int), cast(is_recompiled as int), iif(definition is null, 1, 0))
            from sys.system_sql_modules where object_id = object_id('sys.sp_help')
            """));
        AreEqual(0, sim.ExecuteScalar("select cast(uses_quoted_identifier as int) from sys.system_sql_modules where object_id = object_id('INFORMATION_SCHEMA.TABLES')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.system_sql_modules where object_id = object_id('sys.sp_executesql')"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.all_sql_modules where object_id = object_id('sys.objects')"));
    }

    /// <summary>
    /// The five system endpoints every instance ships, the two over TCP also
    /// in sys.tcp_endpoints (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SystemEndpoints_Listed()
    {
        var sim = new Simulation();
        AreEqual("Dedicated Admin Connection|TSQL Local Machine|TSQL Named Pipes|TSQL Default TCP|TSQL Default VIA",
            sim.ExecuteScalar("select string_agg(name, '|') within group (order by endpoint_id) from sys.endpoints"));
        AreEqual("1:1:0:1,4:0:0:1", sim.ExecuteScalar("select string_agg(concat_ws(':', endpoint_id, cast(is_admin_endpoint as int), port, cast(is_dynamic_port as int)), ',') within group (order by endpoint_id) from sys.tcp_endpoints"));
        AreEqual("sa", sim.ExecuteScalar("select p.name from sys.endpoints e join sys.server_principals p on p.principal_id = e.principal_id where e.endpoint_id = 4"));
    }

    /// <summary>
    /// The compatibility views resolve under dbo too — master.dbo.sysprocesses
    /// is SMO's per-database connection count — where a modern catalog view
    /// doesn't (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CompatibilityViews_ResolveThroughDbo()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select count(*) from master.dbo.sysprocesses where spid = @@spid"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from master..sysprocesses where spid = @@spid"));
        AreEqual(1, sim.ExecuteScalar("create table t (id int); select count(*) from dbo.sysobjects where name = 't'"));
        _ = sim.AssertSqlError("select count(*) from dbo.tables", 208);
    }

    /// <summary>
    /// sysprocesses lists the sessions, the reading one runnable on its SELECT
    /// in its own database.
    /// </summary>
    [TestMethod]
    public void Sysprocesses_ListsSessions()
        => AreEqual("runnable|SELECT|1", new Simulation().ExecuteScalar("""
            select concat_ws('|', rtrim(status), rtrim(cmd), iif(dbid = db_id(), 1, 0)) from sys.sysprocesses where spid = @@spid
            """));

    /// <summary>
    /// The runtime views SMO's server and database bags join resolve: one row
    /// of process memory, no encryption keys, a persistent version store row
    /// per database on PRIMARY, and the database's data files' space.
    /// </summary>
    [TestMethod]
    public void RuntimeDmvs_Resolve()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_os_process_memory where physical_memory_in_use_kb > 0"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from master.sys.dm_database_encryption_keys"));
        AreEqual("1,0", sim.ExecuteScalar("select concat(pvs_filegroup_id, ',', persistent_version_store_size_kb) from sys.dm_tran_persistent_version_store_stats where database_id = db_id()"));
        AreEqual(sim.ExecuteScalar<int>("select count(*) from sys.databases"), sim.ExecuteScalar<int>("select count(*) from sys.dm_tran_persistent_version_store_stats"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_db_file_space_usage where file_id = 1 and total_page_count = allocated_extent_page_count + unallocated_extent_page_count"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.dm_io_virtual_file_stats(db_id(), default)"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.dm_io_virtual_file_stats(default, default) f join sys.database_files d on f.database_id = db_id() and d.file_id = f.file_id and f.size_on_disk_bytes = d.size * 8192 and f.file_id = 2"));
    }

    /// <summary>A restricted session is refused the server-state runtime views as real refuses them.</summary>
    [TestMethod]
    public void RuntimeDmvs_GatedForRestrictedLogin()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off; create user lo for login lo");
        _ = sim.AssertSqlError("execute as login = 'lo'; select count(*) from sys.dm_os_process_memory", 300);
        var ex = sim.AssertSqlError("execute as login = 'lo'; select count(*) from sys.dm_database_encryption_keys", 300);
        Assert.Contains("VIEW SERVER SECURITY STATE", ex.Errors[0].Message);
        _ = sim.AssertSqlError("execute as login = 'lo'; select count(*) from sys.dm_tran_persistent_version_store_stats", 262);
        _ = sim.AssertSqlError("execute as login = 'lo'; select count(*) from sys.dm_io_virtual_file_stats(null, null)", 300);
        AreEqual(1, sim.ExecuteScalar("execute as login = 'lo'; select count(*) from sys.sysprocesses"));
    }

    /// <summary>
    /// sys.database_recovery_status carries a row per database, a never-restored
    /// one's family and recovery fork a single GUID and no log backup.
    /// </summary>
    [TestMethod]
    public void DatabaseRecoveryStatus_RowPerDatabase()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select count(*) from sys.database_recovery_status
            where database_id = db_id() and database_guid is not null and family_guid = recovery_fork_guid and last_log_backup_lsn is null
            """));

    /// <summary>msdb's backup history table resolves, empty — SMO's last-backup dates read it.</summary>
    [TestMethod]
    public void MsdbBackupSet_ResolvesEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from msdb..backupset where type in ('D', 'L', 'I')"));

    /// <summary>
    /// SID_BINARY converts a SID's string form, the authority decimal or hex,
    /// and anything else — a login name — is NULL; SUSER_SNAME names the
    /// built-in Windows groups (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SidBinary_ParsesStringSids()
    {
        var sim = new Simulation();
        AreEqual("0x01020000000000052000000020020000", sim.ExecuteScalar("select convert(varchar(100), sid_binary(N'S-1-5-32-544'), 1)"));
        AreEqual("0x010100000000000512000000", sim.ExecuteScalar("select convert(varchar(100), sid_binary(N's-1-0x0000000005-18'), 1)"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select sid_binary(N'sa')"));
        AreEqual(@"BUILTIN\Administrators", sim.ExecuteScalar("select suser_sname(sid_binary(N'S-1-5-32-544'))"));
        AreEqual(@"NT AUTHORITY\SYSTEM", sim.ExecuteScalar("select suser_sname(0x010100000000000512000000)"));
    }
}
