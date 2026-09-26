using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>sys.&lt;view&gt;</c> catalog views — <c>sys.schemas</c>,
/// <c>sys.tables</c>, <c>sys.objects</c> — plus the <c>SCHEMA_ID()</c>
/// scalar. Schemas pre-populate with conventional ids (dbo=1,
/// INFORMATION_SCHEMA=3, sys=4); user schemas start at 5. Rows project
/// from live metadata, so changes earlier in the same batch are visible
/// immediately. Probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class CatalogViewTests
{
    [TestMethod]
    public void SchemaId_NoArg_ReturnsDbo()
        => AreEqual(1, new Simulation().ExecuteScalar("select schema_id()"));

    [TestMethod]
    public void SchemaId_Dbo_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select schema_id('dbo')"));

    [TestMethod]
    public void SchemaId_InformationSchema_Returns3()
        => AreEqual(3, new Simulation().ExecuteScalar("select schema_id('INFORMATION_SCHEMA')"));

    [TestMethod]
    public void SchemaId_Sys_Returns4()
        => AreEqual(4, new Simulation().ExecuteScalar("select schema_id('sys')"));

    [TestMethod]
    public void SchemaId_FirstUserSchema_Returns5()
        => AreEqual(5, new Simulation().WithSchemas("audit").ExecuteScalar("""
            select schema_id('audit')
            """));

    [TestMethod]
    public void SchemaId_TwoUserSchemas_5And6()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create schema audit", "create schema staging");
        AreEqual(11, simulation.ExecuteScalar("select schema_id('audit') + schema_id('staging')"));
    }

    [TestMethod]
    public void SchemaId_Missing_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select schema_id('nope')");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void SchemaId_NullArg_ReturnsNull()
    {
        using var reader = new Simulation().ExecuteReader("select schema_id(null)");
        IsTrue(reader.Read());
        IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void SysSchemas_ListsThirteenFixedSchemas()
    {
        // Real SQL Server ships thirteen fixed schemas in every database:
        // dbo / guest / INFORMATION_SCHEMA / sys plus one per fixed database
        // role. Probe-confirmed rows (schema_id == principal_id) against
        // SQL Server 2025; JDBC's DatabaseMetaData.getSchemas reads these.
        using var reader = new Simulation().ExecuteReader(
            "select schema_id, name, principal_id from sys.schemas order by schema_id");
        var rows = new List<(int Id, string Name, int Principal)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));
        CollectionAssert.AreEqual(
            new[]
            {
                (1, "dbo", 1), (2, "guest", 2), (3, "INFORMATION_SCHEMA", 3), (4, "sys", 4),
                (16384, "db_owner", 16384), (16385, "db_accessadmin", 16385),
                (16386, "db_securityadmin", 16386), (16387, "db_ddladmin", 16387),
                (16389, "db_backupoperator", 16389), (16390, "db_datareader", 16390),
                (16391, "db_datawriter", 16391), (16392, "db_denydatareader", 16392),
                (16393, "db_denydatawriter", 16393),
            },
            rows);
    }

    [TestMethod]
    public void SysSchemas_CountIsThirteen()
        => AreEqual(13, new Simulation().ExecuteScalar("select count(*) from sys.schemas"));

    [TestMethod]
    public void SysSchemas_IncludesUserSchemas()
        => AreEqual(5, new Simulation().WithSchemas("audit").ExecuteScalar("""
            select schema_id from sys.schemas where name = 'audit'
            """));

    [TestMethod]
    public void SysSchemas_UserSchemaOwnedByDbo()
        // A user schema (schema_id 5) is owned by dbo (principal_id 1),
        // probe-confirmed against SQL Server 2025.
        => AreEqual(1, new Simulation().WithSchemas("audit").ExecuteScalar("""
            select principal_id from sys.schemas where name = 'audit'
            """));

    [TestMethod]
    public void SysTables_EmptyByDefault()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from sys.tables"));

    [TestMethod]
    public void SysTables_AfterCreateTable_HasRow()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table foo (id int);
            select count(*) from sys.tables where name = 'foo'
            """));

    [TestMethod]
    public void SysTables_ProjectsObjectIdMatchingObjectIdFunction()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select t.object_id, object_id('foo') as fn from sys.tables t where t.name = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual(reader.GetInt32(0), reader.GetInt32(1));
    }

    [TestMethod]
    public void SysTables_SchemaIdMatchesSchemaIdFunction()
    {
        using var reader = new Simulation().WithSchemas("audit").ExecuteReader("""
            create table audit.bar (id int);
            select t.schema_id, schema_id('audit') as fn from sys.tables t where t.name = 'bar'
            """);
        IsTrue(reader.Read());
        AreEqual(reader.GetInt32(0), reader.GetInt32(1));
        AreEqual(5, reader.GetInt32(0));
    }

    [TestMethod]
    public void SysTables_TypeIsCharTwoWithTrailingSpace()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select type, datalength(type) as dl, type_desc from sys.tables where name = 'foo'
            """);
        IsTrue(reader.Read());
        AreEqual("U ", reader.GetString(0));
        AreEqual(2, reader.GetInt32(1));
        AreEqual("USER_TABLE", reader.GetString(2));
    }

    [TestMethod]
    public void SysTables_IsMsShippedFalse()
        => IsFalse((bool)new Simulation().ExecuteScalar("""
            create table foo (id int);
            select is_ms_shipped from sys.tables where name = 'foo'
            """)!);

    [TestMethod]
    public void SysTables_CreateDateIsRecentUtc()
    {
        // Catalog view returns the captured CreateDate as datetime. Verify
        // it lands within a small window of "now" — exact equality isn't
        // possible (test setup latency), but membership of a ±5min window
        // proves the per-statement freeze surfaced correctly.
        var now = DateTime.UtcNow;
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            select create_date from sys.tables where name = 'foo'
            """);
        IsTrue(reader.Read());
        var createDate = reader.GetDateTime(0);
        IsLessThan(TimeSpan.FromMinutes(5), (now - createDate).Duration());
    }

    [TestMethod]
    public void SysTables_DropTableRemovesRow()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table foo (id int);
            drop table foo;
            select count(*) from sys.tables where name = 'foo'
            """));

    [TestMethod]
    public void SysObjects_IncludesUserTableAndPK()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int primary key);
            select type, type_desc, parent_object_id from sys.objects where schema_id = 1 order by type
            """);
        var rows = new List<(string Type, string Desc, int Parent)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        HasCount(2, rows);
        AreEqual("PK", rows[0].Type);
        AreEqual("PRIMARY_KEY_CONSTRAINT", rows[0].Desc);
        AreNotEqual(0, rows[0].Parent);
        AreEqual("U ", rows[1].Type);
        AreEqual("USER_TABLE", rows[1].Desc);
        AreEqual(0, rows[1].Parent);
    }

    [TestMethod]
    public void SysObjects_UniqueConstraintGetsUqRow()
        => AreEqual("UNIQUE_CONSTRAINT", new Simulation().ExecuteScalar("""
            create table foo (id int, code nvarchar(10) unique);
            select type_desc from sys.objects where type = 'UQ'
            """));

    [TestMethod]
    public void SysObjects_CheckConstraintGetsCRow()
        => AreEqual("CHECK_CONSTRAINT", new Simulation().ExecuteScalar("""
            create table foo (id int check (id > 0));
            select type_desc from sys.objects where type = 'C '
            """));

    [TestMethod]
    public void SysObjects_DefaultConstraintGetsDRow()
        => AreEqual("DEFAULT_CONSTRAINT", new Simulation().ExecuteScalar("""
            create table foo (id int, code int default 0);
            select type_desc from sys.objects where type = 'D '
            """));

    /// <summary>
    /// A tool enumerating a table's constraints through sys.objects alone sees
    /// all five families under the table's parent_object_id.
    /// </summary>
    [TestMethod]
    public void SysObjects_AllConstraintFamiliesHangOffTheTable()
        => AreEqual("C |D |F |PK|UQ", new Simulation().ExecuteScalar("""
            create table parent (id int not null primary key);
            create table foo (
                id int not null primary key,
                u int not null unique,
                a int default 0,
                p int not null references parent (id),
                check (a >= 0));
            select string_agg(cast(type as varchar(2)), '|') within group (order by type)
            from sys.objects where parent_object_id = object_id('foo')
            """));

    [TestMethod]
    public void SysObjects_PkParentLinksBackToTable()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int primary key);
            select parent_object_id from sys.objects where type = 'PK'
            """);
        IsTrue(reader.Read());
        var parent = reader.GetInt32(0);
        AreEqual(parent, new Simulation().ExecuteScalar("""
            create table foo (id int primary key);
            select object_id('foo')
            """));
    }

    [TestMethod]
    public void SysObjects_JoinSysTablesWorks()
    {
        // Common app idiom — join sys.objects with sys.tables (or filter by
        // object_id from each). Verify the catalog views interoperate.
        using var reader = new Simulation().ExecuteReader("""
            create table foo (id int);
            create table bar (id int);
            select o.name from sys.objects o inner join sys.tables t on o.object_id = t.object_id order by o.name
            """);
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "bar", "foo" }, names);
    }

    [TestMethod]
    public void SysSchemas_QualifiedFromCurrentDatabase()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select schema_id from simulated.sys.schemas where name = 'dbo'"));

    [TestMethod]
    public void SysTables_WrongDatabaseQualifier_Msg208()
        => new Simulation().AssertSqlError(
            "select count(*) from baddb.sys.tables",
            208);

    /// <summary>
    /// Real SQL Server requires sys.-qualification — bare `tables` hits
    /// the regular user-table-not-found path.
    /// </summary>
    [TestMethod]
    public void UnqualifiedTables_Msg208()
        => new Simulation().AssertSqlError("select * from tables", 208);

    [TestMethod]
    public void SysTables_UnknownView_Msg208()
        => new Simulation().AssertSqlError(
            "select * from sys.this_is_not_a_real_view",
            208);

    [TestMethod]
    public void SysTables_WithAlias_Works()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table foo (id int);
            select count(*) from sys.tables t where t.name = 'foo'
            """));

    [TestMethod]
    public void SysTables_OrdersTablesByObjectId()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table a (id int);
            create table b (id int);
            create table c (id int);
            select name from sys.tables order by object_id
            """);
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, names);
    }

    [TestMethod]
    public void CreateTable_CannotAddToSysSchema()
        => new Simulation().AssertSqlError(
            "create table sys.foo (id int)", 2760,
            "The specified schema name \"sys\" either does not exist or you do not have permission to use it.");

    [TestMethod]
    public void CreateTable_CannotAddToInformationSchema()
        => new Simulation().AssertSqlError(
            "create table INFORMATION_SCHEMA.foo (id int)", 2760,
            "The specified schema name \"INFORMATION_SCHEMA\" either does not exist or you do not have permission to use it.");

    [TestMethod]
    public void SysSchemas_DropSchemaNotModeled()
    {
        // Sanity: DROP SCHEMA isn't modeled (existing limitation). Schemas
        // can only be added; the thirteen fixed schemas are always there.
        using var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from sys.schemas";
        AreEqual(13, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void DmOsHostInfo_SingleRow()
        => AreEqual(1, new Simulation().ExecuteScalar("select count(*) from sys.dm_os_host_info"));

    [TestMethod]
    public void DmOsHostInfo_HostPlatformSelectableBySsmsQuery()
    {
        // SSMS issues exactly this on every connect.
        var platform = (string)new Simulation().ExecuteScalar(
            "SELECT host_platform FROM sys.dm_os_host_info")!;
        IsTrue(platform is "Windows" or "Linux" or "macOS", $"unexpected host_platform '{platform}'");
    }

    [TestMethod]
    public void DmOsHostInfo_ShapeReflectsHost()
    {
        // Values are host-dependent, so assert shape not exact strings.
        using var reader = new Simulation().ExecuteReader("""
            select host_platform, host_distribution, host_release, host_service_pack_level,
                   host_sku, os_language_version, host_architecture
            from sys.dm_os_host_info
            """);
        IsTrue(reader.Read());
        var platform = reader.GetString(0);
        IsTrue(platform is "Windows" or "Linux" or "macOS", $"unexpected host_platform '{platform}'");
        IsGreaterThan(0, reader.GetString(1).Length);
        AreEqual("", reader.GetString(3));
        AreEqual(1033, reader.GetInt32(5));
        IsGreaterThan(0, reader.GetString(6).Length);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void DmOsHostInfo_PlatformConsistentWithCurrentHost()
    {
        var platform = (string)new Simulation().ExecuteScalar(
            "select host_platform from sys.dm_os_host_info")!;
        if (OperatingSystem.IsWindows())
            AreEqual("Windows", platform);
        else if (OperatingSystem.IsLinux())
            AreEqual("Linux", platform);
        else if (OperatingSystem.IsMacOS())
            AreEqual("macOS", platform);
    }

    [TestMethod]
    public void DmOsHostInfo_HostSkuNullOffWindows()
    {
        using var reader = new Simulation().ExecuteReader(
            "select host_sku from sys.dm_os_host_info");
        IsTrue(reader.Read());
        if (OperatingSystem.IsWindows())
            AreEqual(48, reader.GetInt32(0));
        else
            IsTrue(reader.IsDBNull(0));
    }

    [TestMethod]
    public void SysConfigurations_AgentXps_ValueInUseIsVariantIntZero()
    {
        // The exact query SSMS's SMO issues during its Object-Explorer
        // database-node preamble. Msg 208 here aborts the request before the
        // database enumeration, so the row must resolve. value_in_use is
        // sql_variant carrying an int inner (like real).
        using var reader = new Simulation().ExecuteReader(
            "select value_in_use from sys.configurations where configuration_id = 16384");
        AreEqual("sql_variant", reader.GetDataTypeName(0));
        IsTrue(reader.Read());
        _ = Assert.IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(0, reader.GetValue(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SysConfigurations_HasHundredSixRows()
        => AreEqual(106, new Simulation().ExecuteScalar("select count(*) from sys.configurations"));

    [TestMethod]
    public void SysConfigurations_ColumnShape()
    {
        using var reader = new Simulation().ExecuteReader("""
            select configuration_id, name, value, minimum, maximum,
                   value_in_use, description, is_dynamic, is_advanced
            from sys.configurations where configuration_id = 16384
            """);
        AreEqual(9, reader.FieldCount);
        IsTrue(reader.Read());
        AreEqual(typeof(int), reader.GetFieldType(0));
        AreEqual(typeof(string), reader.GetFieldType(1));
        // value / minimum / maximum / value_in_use are sql_variant (object
        // field type), matching real — each wraps an int inner.
        AreEqual(typeof(object), reader.GetFieldType(2));
        AreEqual(typeof(object), reader.GetFieldType(3));
        AreEqual(typeof(object), reader.GetFieldType(4));
        AreEqual(typeof(object), reader.GetFieldType(5));
        AreEqual(typeof(string), reader.GetFieldType(6));
        AreEqual(typeof(bool), reader.GetFieldType(7));
        AreEqual(typeof(bool), reader.GetFieldType(8));
    }

    [TestMethod]
    public void SysConfigurations_AgentXpsRow_MatchesReference()
    {
        using var reader = new Simulation().ExecuteReader("""
            select configuration_id, name, value, minimum, maximum,
                   value_in_use, description, is_dynamic, is_advanced
            from sys.configurations where configuration_id = 16384
            """);
        IsTrue(reader.Read());
        AreEqual(16384, reader.GetInt32(0));
        AreEqual("Agent XPs", reader.GetString(1));
        // The four sql_variant columns wrap an int inner (probe-confirmed).
        AreEqual(0, reader.GetValue(2));
        AreEqual(0, reader.GetValue(3));
        AreEqual(1, reader.GetValue(4));
        AreEqual(0, reader.GetValue(5));
        AreEqual("Enable or disable Agent XPs", reader.GetString(6));
        IsTrue(reader.GetBoolean(7));
        IsTrue(reader.GetBoolean(8));
    }

    [TestMethod]
    public void SysConfigurations_ClrEnabled_ByName()
        => AreEqual(1562, new Simulation().ExecuteScalar(
            "select configuration_id from sys.configurations where name = 'clr enabled'"));

    [TestMethod]
    public void SysConfigurations_XpCmdshell_ByName()
        => AreEqual(16390, new Simulation().ExecuteScalar(
            "select configuration_id from sys.configurations where name = 'xp_cmdshell'"));

    [TestMethod]
    public void SysConfigurations_AgentXps_CountByNameIsOne()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select count(*) from sys.configurations where name = 'Agent XPs'"));

    [TestMethod]
    public void SysConfigurations_ReadableViaThreePartMasterName()
        => AreEqual(106, new Simulation().ExecuteScalar(
            "select count(*) from master.sys.configurations"));

    // === sys.databases: full 98-column projection (SQL Server 2025) ===

    [TestMethod]
    public void SysDatabases_Projects98Columns()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from sys.databases where name = 'simulated'");
        AreEqual(98, reader.FieldCount);
    }

    [TestMethod]
    public void SysDatabases_KeyColumnTypes_DatabaseIdIsInt_StateOnline()
    {
        using var reader = new Simulation().ExecuteReader("""
            select name, database_id, source_database_id, owner_sid, create_date,
                   state_desc, physical_database_name
            from sys.databases where name = 'simulated'
            """);
        AreEqual(typeof(string), reader.GetFieldType(0));
        AreEqual(typeof(int), reader.GetFieldType(1));
        AreEqual(typeof(int), reader.GetFieldType(2));
        AreEqual(typeof(byte[]), reader.GetFieldType(3));
        AreEqual(typeof(DateTime), reader.GetFieldType(4));
        IsTrue(reader.Read());
        AreEqual("simulated", reader.GetString(0));
        AreEqual(5, reader.GetInt32(1));
        IsTrue(reader.IsDBNull(2));
        AreEqual("ONLINE", reader.GetString(5));
        AreEqual("simulated", reader.GetString(6));
    }

    [TestMethod]
    public void SysDatabases_CodeDescPairs_InternallyConsistent()
    {
        using var reader = new Simulation().ExecuteReader("""
            select user_access, user_access_desc, state, state_desc,
                   recovery_model, recovery_model_desc,
                   snapshot_isolation_state, snapshot_isolation_state_desc
            from sys.databases where name = 'simulated'
            """);
        IsTrue(reader.Read());
        AreEqual((byte)0, reader.GetByte(0));
        AreEqual("MULTI_USER", reader.GetString(1));
        AreEqual((byte)0, reader.GetByte(2));
        AreEqual("ONLINE", reader.GetString(3));
        AreEqual((byte)1, reader.GetByte(4));
        AreEqual("FULL", reader.GetString(5));
        AreEqual((byte)0, reader.GetByte(6));
        AreEqual("OFF", reader.GetString(7));
    }

    // A user database inherits the model template's FULL recovery;
    // master / tempdb / msdb report SIMPLE (probe-confirmed).
    [TestMethod]
    public void SysDatabases_RecoveryModel_FullExceptTheSimpleSystemDatabases()
    {
        var sim = new Simulation();
        AreEqual("FULL", sim.ExecuteScalar("select recovery_model_desc from sys.databases where name = 'model'"));
        AreEqual("FULL", sim.ExecuteScalar("select recovery_model_desc from sys.databases where name = 'simulated'"));
        AreEqual(3, sim.ExecuteScalar("""
            select count(*) from sys.databases
            where name in ('master', 'tempdb', 'msdb') and recovery_model = 3 and recovery_model_desc = 'SIMPLE'
            """));
    }

    // Service Broker reads enabled everywhere but master and model
    // (probe-confirmed; tempdb, msdb and a created user database are all 1).
    [TestMethod]
    public void SysDatabases_IsBrokerEnabled_OffForMasterAndModelOnly()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database extra");
        AreEqual(2, sim.ExecuteScalar("""
            select count(*) from sys.databases where is_broker_enabled = 0
            """));
        AreEqual(2, sim.ExecuteScalar("""
            select count(*) from sys.databases
            where name in ('master', 'model') and is_broker_enabled = 0
            """));
        AreEqual(4, sim.ExecuteScalar("""
            select count(*) from sys.databases
            where name in ('tempdb', 'msdb', 'simulated', 'extra') and is_broker_enabled = 1
            """));
    }

    // SMO's Object-Explorer "Databases" node enumeration for a v17 server;
    // filtering out the four system databases leaves only the user database.
    [TestMethod]
    public void SysDatabases_SmoStyleEnumeration_ReturnsUserDatabaseRow()
    {
        using var reader = new Simulation().ExecuteReader("""
            select name, database_id, has_dbaccess(name), state_desc,
                   recovery_model_desc, owner_sid, create_date,
                   source_database_id, containment
            from sys.databases
            where name not in ('master', 'tempdb', 'model', 'msdb')
            order by name
            """);
        IsTrue(reader.Read());
        AreEqual("simulated", reader.GetString(0));
        AreEqual(5, reader.GetInt32(1));
        AreEqual(1, reader.GetInt32(2));
        AreEqual("ONLINE", reader.GetString(3));
        AreEqual("FULL", reader.GetString(4));
        AreEqual((byte)0, reader.GetByte(8));
        IsFalse(reader.Read());
    }

    // === sys.database_mirroring: one non-mirrored row per database ===

    [TestMethod]
    public void SysDatabaseMirroring_Projects21Columns()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from sys.database_mirroring where database_id = 1");
        AreEqual(21, reader.FieldCount);
    }

    [TestMethod]
    public void SysDatabaseMirroring_NonMirroredRow_OnlyDatabaseIdPopulated()
    {
        using var reader = new Simulation().ExecuteReader("""
            select database_id, mirroring_guid, mirroring_state, mirroring_role,
                   mirroring_role_desc, mirroring_failover_lsn
            from sys.database_mirroring where database_id = 5
            """);
        AreEqual(typeof(int), reader.GetFieldType(0));
        AreEqual(typeof(byte), reader.GetFieldType(2));
        AreEqual(typeof(decimal), reader.GetFieldType(5));
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
        IsTrue(reader.IsDBNull(1));
        IsTrue(reader.IsDBNull(2));
        IsTrue(reader.IsDBNull(3));
        IsTrue(reader.IsDBNull(4));
        IsTrue(reader.IsDBNull(5));
        IsFalse(reader.Read());
    }

    // One row per database, joining 1:1 to sys.databases on database_id.
    [TestMethod]
    public void SysDatabaseMirroring_OneRowPerDatabase_JoinsToDatabases()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            select count(*)
            from master.sys.databases dtb
            full join sys.database_mirroring dmi on dmi.database_id = dtb.database_id
            where dtb.database_id is null or dmi.database_id is null
            """));

    // The core of SSMS's Object-Explorer "Databases" enumeration: sys.databases
    // LEFT JOIN sys.database_mirroring, reading ISNULL(mirroring_role, 0) /
    // ISNULL(mirroring_state + 1, 0), filtered to user databases. Msg 208 on the
    // mirroring view would blank the folder.
    [TestMethod]
    public void SysDatabaseMirroring_SmoEnumeration_ReturnsUserDatabase()
    {
        using var reader = new Simulation().ExecuteReader("""
            select dtb.name, isnull(dmi.mirroring_role, 0), isnull(dmi.mirroring_state + 1, 0)
            from master.sys.databases dtb
            left join sys.database_mirroring dmi on dmi.database_id = dtb.database_id
            where dtb.name not in ('master', 'model', 'msdb', 'tempdb')
            """);
        IsTrue(reader.Read());
        AreEqual("simulated", reader.GetString(0));
        // ISNULL(mirroring_role, 0) inherits mirroring_role's tinyint type;
        // ISNULL(mirroring_state + 1, 0) is int (tinyint + int promotes).
        AreEqual((byte)0, reader.GetByte(1));
        AreEqual(0, reader.GetInt32(2));
        IsFalse(reader.Read());
    }

    // === AlwaysOn Availability-Group views: empty, server-scope ===

    [TestMethod]
    public void SysAvailabilityReplicas_Projects22Columns_ZeroRows()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from sys.availability_replicas");
        AreEqual(22, reader.FieldCount);
        AreEqual(typeof(Guid), reader.GetFieldType(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SysAvailabilityGroups_Projects19Columns_ZeroRows()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from sys.availability_groups");
        AreEqual(19, reader.FieldCount);
        AreEqual(typeof(Guid), reader.GetFieldType(0));
        AreEqual(typeof(string), reader.GetFieldType(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SysDmHadrDatabaseReplicaStates_Projects39Columns_ZeroRows()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from sys.dm_hadr_database_replica_states");
        AreEqual(39, reader.FieldCount);
        IsFalse(reader.Read());
    }

    // SSMS's enumeration seeds a #temp from the empty replica DMV; the
    // insert-from-empty-catalog-view path must resolve and add zero rows.
    [TestMethod]
    public void SysAvailabilityReplicas_InsertFromEmptyView_AddsNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table #r (a uniqueidentifier, b uniqueidentifier, c sysname);
            insert #r select replica_id, group_id, replica_server_name
            from master.sys.availability_replicas;
            select count(*) from #r
            """));

    // === sys.synonyms: schema-scoped, one row per CREATE SYNONYM ===
    // Row-level projection (values, base_object_name shapes) lives in SynonymTests.

    [TestMethod]
    public void SysSynonyms_Projects13Columns_ZeroRows()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from sys.synonyms");
        AreEqual(13, reader.FieldCount);
        AreEqual("name", reader.GetName(0));
        AreEqual("base_object_name", reader.GetName(12));
        AreEqual(typeof(string), reader.GetFieldType(0));
        AreEqual(typeof(int), reader.GetFieldType(1));
        IsFalse(reader.Read());
    }

    // SSMS's "Edit Top 200 Rows" commit reads [db].sys.synonyms via a
    // three-part name to test whether the edit target is a synonym; the
    // read must resolve and return zero rows rather than Msg 208.
    [TestMethod]
    public void SysSynonyms_ThreePartName_ResolvesEmpty()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from master.sys.synonyms"));

    // === sys.master_files: data + log file per database, no type-2 files ===

    [TestMethod]
    public void SysMasterFiles_Projects32Columns()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from sys.master_files where database_id = 1");
        AreEqual(32, reader.FieldCount);
    }

    [TestMethod]
    public void SysMasterFiles_DataAndLogFilePerDatabase()
    {
        using var reader = new Simulation().ExecuteReader("""
            select file_id, type, type_desc, name, physical_name
            from sys.master_files where database_id = 5 order by file_id
            """);
        AreEqual(typeof(int), reader.GetFieldType(0));
        AreEqual(typeof(byte), reader.GetFieldType(1));
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual((byte)0, reader.GetByte(1));
        AreEqual("ROWS", reader.GetString(2));
        AreEqual("simulated", reader.GetString(3));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual((byte)1, reader.GetByte(1));
        AreEqual("LOG", reader.GetString(2));
        AreEqual("simulated_log", reader.GetString(3));
        IsFalse(reader.Read());
    }

    // Two files (data + log) for each of the five hosted databases.
    [TestMethod]
    public void SysMasterFiles_TwoFilesPerDatabase()
        => AreEqual(10, new Simulation().ExecuteScalar(
            "select count(*) from master.sys.master_files"));

    // SSMS's in-memory-OLTP filegroup probe: no type-2 file exists, so the
    // bracket-escaped [type] filter must parse and return zero.
    [TestMethod]
    public void SysMasterFiles_NoType2Files()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from master.sys.master_files where [type] = 2"));

    [TestMethod]
    public void SysMasterFiles_TypeTwoJoinToDatabases_ReturnsNothing()
    {
        using var reader = new Simulation().ExecuteReader("""
            select db.name
            from master.sys.master_files mf
            join master.sys.databases db on mf.database_id = db.database_id
            where mf.[type] = 2
            """);
        IsFalse(reader.Read());
    }

    /// <summary>
    /// SMO's per-column property-bag query: <c>sys.all_columns</c> LEFT JOINed
    /// to <c>sys.types</c> (twice — user + base type), <c>sys.identity_columns</c>,
    /// and <c>sys.computed_columns</c>, filtered to one table. The catalog views
    /// are uncorrelated deferred sources, so each is materialized once per query
    /// and rides the equi-join hash path instead of being re-generated per outer
    /// row (the O(outer × Σ view-sizes) blowup that made this query ~300 ms).
    /// This pins the rowset: one row per column, in <c>column_id</c> order, with
    /// the type-name join resolving and the identity / computed LEFT JOINs
    /// matching only their respective columns (NULL elsewhere).
    /// </summary>
    [TestMethod]
    public void PerColumnBagQuery_MultiJoin_ReturnsOneRowPerColumnWithMatchedMetadata()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.Widget (
                id int identity(1, 1) not null,
                name nvarchar(50) not null,
                total as (id * 2))
            """);

        using var reader = sim.ExecuteReader("""
            select
                col.name,
                st.name as type_name,
                idc.column_id as identity_col,
                cmc.column_id as computed_col
            from sys.all_columns col
            left join sys.types st on st.user_type_id = col.user_type_id
            left join sys.types bt on bt.user_type_id = col.system_type_id
            left join sys.identity_columns idc on idc.object_id = col.object_id and idc.column_id = col.column_id
            left join sys.computed_columns cmc on cmc.object_id = col.object_id and cmc.column_id = col.column_id
            where col.object_id = object_id(N'dbo.Widget')
            order by col.column_id
            """);

        var rows = new List<(string Name, string TypeName, bool IsIdentity, bool IsComputed)>();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                !reader.IsDBNull(2),
                !reader.IsDBNull(3)));
        }

        HasCount(3, rows);
        AreEqual(("id", "int", true, false), rows[0]);
        AreEqual(("name", "nvarchar", false, false), rows[1]);
        AreEqual(("total", "int", false, true), rows[2]);
    }

    // === sys.indexes: real's 23-column shape and column order ===

    /// <summary>
    /// Real leads with object_id / name and carries no
    /// <c>statistics_incremental</c> column (selecting it is Msg 207 there).
    /// </summary>
    [TestMethod]
    public void SysIndexes_ColumnShape_MatchesRealsOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        using var reader = sim.ExecuteReader("select * from sys.indexes where object_id = object_id('t')");
        AreEqual(23, reader.FieldCount);
        AreEqual("object_id", reader.GetName(0));
        AreEqual("name", reader.GetName(1));
        AreEqual("index_id", reader.GetName(2));
        AreEqual("optimize_for_sequential_key", reader.GetName(22));
        IsTrue(reader.Read());
        AreEqual(sim.ExecuteScalar("select object_id('t')"), reader.GetInt32(0));
    }

    [TestMethod]
    public void SysIndexes_StatisticsIncremental_DoesNotExist()
        => _ = new Simulation().AssertSqlError("select statistics_incremental from sys.indexes", 207);

    // === sys.tables.lob_data_space_id ===

    /// <summary>
    /// The column names the filegroup holding the LOB allocation unit: 1 once
    /// any column is LOB-eligible, 0 otherwise (probe-confirmed —
    /// <c>hierarchyid</c> and <c>sql_variant</c> leave it 0).
    /// </summary>
    [TestMethod]
    public void SysTables_LobDataSpaceId_OneWhenALobColumnExists()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table plain (a int, b varchar(50), c nvarchar(100), d varbinary(200));
            create table variant_only (a int, b sql_variant, c hierarchyid);
            create table with_max (a int, b nvarchar(max));
            create table with_text (a int, b text);
            create table with_xml (a int, b xml);
            create table with_geo (a int, b geography)
            """);
        AreEqual(0, sim.ExecuteScalar("select lob_data_space_id from sys.tables where name = 'plain'"));
        AreEqual(0, sim.ExecuteScalar("select lob_data_space_id from sys.tables where name = 'variant_only'"));
        AreEqual(4, sim.ExecuteScalar("""
            select count(*) from sys.tables
            where name in ('with_max', 'with_text', 'with_xml', 'with_geo') and lob_data_space_id = 1
            """));
    }

    // === sys.stats.replica_role_id ===

    /// <summary>
    /// A stand-alone instance reports every statistic on the primary replica
    /// (probe-confirmed 1 / PRIMARY, with replica_name still NULL).
    /// </summary>
    [TestMethod]
    public void SysStats_ReplicaRole_IsPrimary()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null constraint pk_t primary key, a int); create index ix_a on t(a)");
        AreEqual(2, sim.ExecuteScalar("""
            select count(*) from sys.stats
            where object_id = object_id('t') and replica_role_id = 1 and replica_role_desc = 'PRIMARY' and replica_name is null
            """));
    }

    // === Stable column ids across the column-attached catalog views ===

    /// <summary>
    /// Every view keyed on a column projects the stable
    /// <c>sys.columns.column_id</c>, so the permanent hole a DROP COLUMN leaves
    /// shows up identically in all of them (probe-confirmed). The dropped
    /// column here is the second of six, so the survivors keep ids 1, 3..6
    /// while their positions shift down.
    /// </summary>
    [TestMethod]
    public void ColumnKeyedViews_ReportStableColumnIdsAfterDropColumn()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table parent (
                a int not null constraint pk_parent primary key,
                filler int null,
                b int null constraint df_b default 7,
                c int identity(1, 1),
                d as a * 2,
                e int not null constraint ck_e check (e > 0));
            create table child (x int not null primary key, junk int null, fa int not null);
            alter table parent drop column filler;
            alter table child drop column junk;
            alter table child add constraint fk_x foreign key (fa) references parent(a)
            """);
        AreEqual(5, sim.ExecuteScalar("select column_id from sys.computed_columns where object_id = object_id('parent')"));
        AreEqual(4, sim.ExecuteScalar("select column_id from sys.identity_columns where object_id = object_id('parent')"));
        AreEqual(6, sim.ExecuteScalar("select parent_column_id from sys.check_constraints where name = 'ck_e'"));
        AreEqual(3, sim.ExecuteScalar("select parent_column_id from sys.default_constraints where name = 'df_b'"));
        using var reader = sim.ExecuteReader("""
            select fkc.parent_column_id, fkc.referenced_column_id
            from sys.foreign_key_columns fkc
            join sys.foreign_keys fk on fk.object_id = fkc.constraint_object_id
            where fk.name = 'fk_x'
            """);
        IsTrue(reader.Read());
        AreEqual(3, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
        IsFalse(reader.Read());
    }

    // === sys.foreign_keys.key_index_id ===

    /// <summary>
    /// key_index_id names the referenced table's index that backs the FK, not
    /// a hardcoded 1: with a clustered index holding id 1, an FK pointing at a
    /// NONCLUSTERED PK and one pointing at a UNIQUE constraint each report
    /// their own backing index's id (probe-confirmed — real reported 3 and 2
    /// for this shape).
    /// </summary>
    [TestMethod]
    public void SysForeignKeys_KeyIndexId_ResolvesThroughTheReferencedIndex()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (
                id int not null constraint pk_p primary key nonclustered,
                x int not null,
                u int not null constraint uq_p unique);
            create clustered index ix_p_x on p(x);
            create table c1 (id int not null primary key, pid int not null constraint fk1 references p(id));
            create table c2 (id int not null primary key, uid int not null constraint fk2 references p(u))
            """);
        // The clustered index owns id 1, so neither FK may report it.
        AreEqual(1, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_p_x'"));
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.foreign_keys fk join sys.indexes i
                on i.object_id = fk.referenced_object_id and i.index_id = fk.key_index_id
            where fk.name = 'fk1' and i.name = 'pk_p' and i.index_id > 1
            """));
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.foreign_keys fk join sys.indexes i
                on i.object_id = fk.referenced_object_id and i.index_id = fk.key_index_id
            where fk.name = 'fk2' and i.name = 'uq_p' and i.index_id > 1
            """));
    }

    // === uses_database_collation ===

    /// <summary>
    /// Real reports 1 for every CHECK constraint and computed column it
    /// creates, purely numeric expressions included (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void UsesDatabaseCollation_IsOneOnChecksAndComputedColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (
                a int not null,
                s nvarchar(20) null,
                b as a * 2,
                d as s + N'x',
                constraint ck_a check (a > 0),
                constraint ck_s check (s <> N'no'))
            """);
        AreEqual(2, sim.ExecuteScalar(
            "select count(*) from sys.check_constraints where parent_object_id = object_id('t') and uses_database_collation = 1"));
        AreEqual(2, sim.ExecuteScalar(
            "select count(*) from sys.computed_columns where object_id = object_id('t') and uses_database_collation = 1"));
    }

    // === sys.parameters: max_length / precision / scale ===

    /// <summary>
    /// The triple comes from the same computation <c>sys.columns</c> uses
    /// (probe-confirmed: nvarchar(40) → 80 / 0 / 0, int → 4 / 10 / 0,
    /// decimal(12, 3) → 9 / 12 / 3, varchar(max) → -1, and a scalar UDF's
    /// parameter_id 0 return row reports its own return type's width).
    /// </summary>
    [TestMethod]
    public void SysParameters_ProjectsRealMetadataTriple()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure p @a nvarchar(40), @b int, @c decimal(12, 3), @d varchar(max), @e datetime2(3) output as select 1",
            "create function f (@x nvarchar(10), @y money) returns nvarchar(50) as begin return @x end");

        using var reader = sim.ExecuteReader("""
            select object_name(object_id), parameter_id, max_length, [precision], scale
            from sys.parameters
            where object_id in (object_id('p'), object_id('f'))
            order by object_name(object_id), parameter_id
            """);
        var rows = new List<(string, int, short, byte, byte)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4)));
        CollectionAssert.AreEqual(
            new[]
            {
                ("f", 0, (short)100, (byte)0, (byte)0),
                ("f", 1, (short)20, (byte)0, (byte)0),
                ("f", 2, (short)8, (byte)19, (byte)4),
                ("p", 1, (short)80, (byte)0, (byte)0),
                ("p", 2, (short)4, (byte)10, (byte)0),
                ("p", 3, (short)9, (byte)12, (byte)3),
                ("p", 4, (short)-1, (byte)0, (byte)0),
                ("p", 5, (short)7, (byte)23, (byte)3),
            },
            rows);
    }

    /// <summary>A table-valued parameter reports the MAX sentinel and no precision / scale.</summary>
    [TestMethod]
    public void SysParameters_TableValuedParameter_ReportsMaxSentinel()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type tt as table (a int)",
            "create procedure p @t tt readonly as select 1");
        using var reader = sim.ExecuteReader(
            "select max_length, [precision], scale from sys.parameters where object_id = object_id('p')");
        IsTrue(reader.Read());
        AreEqual((short)-1, reader.GetInt16(0));
        AreEqual((byte)0, reader.GetByte(1));
        AreEqual((byte)0, reader.GetByte(2));
    }

    // === sys.procedures.modify_date ===

    [TestMethod]
    public void SysProcedures_ModifyDate_AdvancesOnAlter()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create procedure p as select 1").ExecuteNonQuery();
        AreEqual(1, connection.CreateCommand(
            "select count(*) from sys.procedures where name = 'p' and modify_date = create_date").ExecuteScalar());
        var created = (DateTime)connection.CreateCommand("select create_date from sys.procedures where name = 'p'").ExecuteScalar()!;
        // datetime rounds to 1/300 s, so put the ALTER in a later tick.
        _ = connection.CreateCommand("waitfor delay '00:00:00.020'").ExecuteNonQuery();
        _ = connection.CreateCommand("alter procedure p as select 2").ExecuteNonQuery();
        using var reader = connection.CreateCommand("""
            select pr.create_date, pr.modify_date, o.modify_date
            from sys.procedures pr join sys.objects o on o.object_id = pr.object_id
            where pr.name = 'p'
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(created, reader.GetDateTime(0));
        IsGreaterThan(created, reader.GetDateTime(1));
        AreEqual(reader.GetDateTime(2), reader.GetDateTime(1));
    }

    // === sys.database_principals: default_schema_name / sid / authentication_type ===

    /// <summary>
    /// <c>type</c> is <c>char(1)</c>, not the <c>char(2)</c> that
    /// <c>sys.objects</c> uses for its own type column: a principal's
    /// discriminator is a single letter and real declares the column to match,
    /// so the value arrives unpadded (probe-confirmed against SQL Server 2025 —
    /// <c>DATALENGTH</c> is 1 there, and <c>sys.server_principals</c> already
    /// agreed). A char(2) declaration silently trailing-space pads every read.
    /// </summary>
    [TestMethod]
    [DataRow("sys.database_principals", "dbo")]
    [DataRow("sys.server_principals", "sa")]
    public void PrincipalTypeColumn_IsCharOne(string catalogView, string principal)
        => AreEqual(1, new Simulation().ExecuteScalar(
            $"select datalength(type) from {catalogView} where name = '{principal}'"));

    /// <summary>
    /// Probe-confirmed per-principal rules: dbo carries the well-known
    /// <c>0x01</c> sid and the only <c>INSTANCE</c> authentication_type, guest
    /// carries <c>0x00</c> and defaults to its own schema, the catalog
    /// principals report a NULL sid, and users / roles carry a 28-byte
    /// database-scoped sid.
    /// </summary>
    [TestMethod]
    public void SysDatabasePrincipals_FixedPrincipals_ReportRealsShape()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u without login; create role r");
        using var reader = sim.ExecuteReader("""
            select name, default_schema_name, sid, cast(authentication_type as int), authentication_type_desc
            from sys.database_principals
            where name in ('dbo', 'guest', 'sys', 'INFORMATION_SCHEMA', 'u', 'r', 'public')
            order by principal_id
            """);
        var rows = new List<(string Name, string? Schema, string Sid, int Auth, string AuthDesc)>();
        while (reader.Read())
        {
            // A database-scoped SID is 28 bytes; collapse it to a label so the
            // expectation names the shape rather than the hash bytes.
            var sid = reader.IsDBNull(2) ? "<null>" : Convert.ToHexString((byte[])reader.GetValue(2));
            rows.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                sid.Length == 56 ? "<database-scoped>" : sid,
                reader.GetInt32(3),
                reader.GetString(4)));
        }

        CollectionAssert.AreEqual(
            new[]
            {
                ("public", null, "<database-scoped>", 0, "NONE"),
                ("dbo", "dbo", "01", 1, "INSTANCE"),
                ("guest", "guest", "00", 0, "NONE"),
                ("INFORMATION_SCHEMA", null, "<null>", 0, "NONE"),
                ("sys", null, "<null>", 0, "NONE"),
                ("u", "dbo", "<database-scoped>", 0, "NONE"),
                ("r", null, "<database-scoped>", 0, "NONE"),
            },
            rows);
    }

    /// <summary>
    /// A fixed database role encodes its principal_id in the final
    /// sub-authority the way real does — <c>db_owner</c> (16384) ends
    /// <c>00400000</c>.
    /// </summary>
    [TestMethod]
    public void SysDatabasePrincipals_FixedRoleSid_EncodesPrincipalId()
        => AreEqual("0x01050000000000090400000000000000000000000000000000400000",
            new Simulation().ExecuteScalar("select convert(varchar(80), sid, 1) from sys.database_principals where name = 'db_owner'"));

    // === sys.master_files / sys.database_files: page-denominated growth ===

    /// <summary>
    /// growth and max_size are both in 8 KB pages when is_percent_growth is 0
    /// (probe-confirmed): 8192 pages of growth on both files, -1 (unlimited)
    /// max_size on the data file and the 2 TB ceiling on the log.
    /// </summary>
    [TestMethod]
    public void FileCatalogViews_GrowthAndMaxSize_AreInPages()
    {
        var sim = new Simulation();
        using var reader = sim.ExecuteReader(
            "select type, max_size, growth, cast(is_percent_growth as int) from sys.database_files order by file_id");
        IsTrue(reader.Read());
        AreEqual((byte)0, reader.GetByte(0));
        AreEqual(-1, reader.GetInt32(1));
        AreEqual(8192, reader.GetInt32(2));
        AreEqual(0, reader.GetInt32(3));
        IsTrue(reader.Read());
        AreEqual((byte)1, reader.GetByte(0));
        AreEqual(268435456, reader.GetInt32(1));
        AreEqual(8192, reader.GetInt32(2));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SysMasterFiles_GrowthAndMaxSize_AgreeWithDatabaseFiles()
        => AreEqual(10, new Simulation().ExecuteScalar("""
            select count(*) from master.sys.master_files
            where growth = 8192 and max_size = case when [type] = 1 then 268435456 else -1 end
            """));

    /// <summary>
    /// sp_helpfile renders the same values in KB — 65536 KB of growth, and the
    /// log's ceiling as 2147483648 KB rather than "Unlimited" (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void SpHelpfile_RendersGrowthAndMaxSizeInKilobytes()
    {
        using var reader = new Simulation().ExecuteReader("exec sp_helpfile");
        IsTrue(reader.Read());
        AreEqual("Unlimited", reader.GetString(5).TrimEnd());
        AreEqual("65536 KB", reader.GetString(6).TrimEnd());
        IsTrue(reader.Read());
        AreEqual("2147483648 KB", reader.GetString(5).TrimEnd());
        AreEqual("65536 KB", reader.GetString(6).TrimEnd());
        IsFalse(reader.Read());
    }

    // === sys.databases: the fresh-user-database option-flag block ===

    /// <summary>
    /// The ANSI / cursor / full-text / retention flags a freshly created SQL
    /// Server 2025 database reports (probe-confirmed): the whole ANSI family
    /// and is_local_cursor_default off, is_fulltext_enabled and
    /// is_temporal_history_retention_enabled on.
    /// </summary>
    [TestMethod]
    public void SysDatabases_FreshDatabaseOptionFlags_MatchReal()
    {
        using var reader = new Simulation().ExecuteReader("""
            select cast(is_ansi_null_default_on as int), cast(is_ansi_nulls_on as int),
                   cast(is_ansi_padding_on as int), cast(is_ansi_warnings_on as int),
                   cast(is_arithabort_on as int), cast(is_concat_null_yields_null_on as int),
                   cast(is_numeric_roundabort_on as int), cast(is_quoted_identifier_on as int),
                   cast(is_local_cursor_default as int), cast(is_fulltext_enabled as int),
                   cast(is_temporal_history_retention_enabled as int)
            from sys.databases where name = 'simulated'
            """);
        IsTrue(reader.Read());
        for (var i = 0; i <= 8; i++)
            AreEqual(0, reader.GetInt32(i), $"column {i}");
        AreEqual(1, reader.GetInt32(9));
        AreEqual(1, reader.GetInt32(10));
    }

    /// <summary>
    /// The pair is read together and must not contradict: is_query_store_on
    /// follows the state <c>sys.database_query_store_options</c> projects, on
    /// a fresh database and after a flip.
    /// </summary>
    [TestMethod]
    public void SysDatabases_IsQueryStoreOn_AgreesWithQueryStoreOptions()
    {
        var sim = new Simulation();
        IsTrue((bool)sim.ExecuteScalar("select is_query_store_on from sys.databases where name = 'simulated'")!);
        AreEqual("READ_WRITE", sim.ExecuteScalar("select actual_state_desc from sys.database_query_store_options"));

        _ = sim.ExecuteNonQuery("alter database simulated set query_store = off");
        IsFalse((bool)sim.ExecuteScalar("select is_query_store_on from sys.databases where name = 'simulated'")!);
        AreEqual("OFF", sim.ExecuteScalar("select actual_state_desc from sys.database_query_store_options"));
    }

    // ---- system objects (probed 2026-09-24 against SQL Server 2025) ----

    [TestMethod]
    public void AllObjects_IncludesTheSystemObjects()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select case when (select count(*) from sys.all_objects) = (select count(*) from sys.objects) + (select count(*) from sys.system_objects)
                and exists (select 1 from sys.all_objects where name = 'sp_help' and type = 'P') then 1 else 0 end
            """));

    [TestMethod]
    public void SystemObjectIds_ResolveToTheirNames()
        => AreEqual("tables|sys|sp_help|-784136858|1|1|0", new Simulation().ExecuteScalar("""
            select concat(object_name(object_id('sys.tables')), '|', object_schema_name(object_id('sys.tables')), '|',
                object_name(object_id('dbo.sp_help')), '|', object_id('sp_help'), '|',
                objectproperty(object_id('sys.tables'), 'IsView'), '|', objectproperty(object_id('sys.sp_help'), 'IsProcedure'), '|',
                objectproperty(object_id('sys.tables'), 'IsUserTable'))
            """));

    [TestMethod]
    public void ExtendedSystemProcedures_AreTypedX()
        => AreEqual(2, new Simulation().ExecuteScalar("select count(*) from sys.system_objects where name in ('sp_executesql', 'xp_msver', 'sp_help') and type = 'X'"));

    [TestMethod]
    public void SelectStar_ListsColumnsInRealsOrder()
    {
        using var reader = new Simulation().ExecuteReader("select top 0 * from sys.objects; select top 0 * from sys.columns");
        CollectionAssert.AreEqual(
            new[] { "name", "object_id", "principal_id", "schema_id", "parent_object_id", "type" },
            Enumerable.Range(0, 6).Select(reader.GetName).ToArray());
        IsTrue(reader.NextResult());
        CollectionAssert.AreEqual(
            new[] { "scale", "collation_name", "is_nullable", "is_ansi_padded" },
            Enumerable.Range(7, 4).Select(reader.GetName).ToArray());
    }

    [TestMethod]
    public void ReorderedView_KeepsEachValueUnderItsColumn()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int)");
        AreEqual("t|1|0", simulation.ExecuteScalar("select concat(name, '|', schema_id, '|', is_ms_shipped) from sys.objects where object_id = object_id('t')"));
        AreEqual("t|1|0", simulation.ExecuteScalar("select concat(name, '|', schema_id, '|', is_ms_shipped) from sys.objects where name = 't'"));
    }

    [TestMethod]
    public void ReorderedView_ColumnIdFollowsRealsOrder()
        => AreEqual(1, new Simulation().ExecuteScalar("select columnproperty(object_id('sys.objects'), 'name', 'ColumnId')"));

    [TestMethod]
    public void CatalogColumns_CarryRealsCatalogCollation()
        => AreEqual("Latin1_General_CI_AS_KS_WS", ExecuteScalar(
            "select top 1 cast(sql_variant_property(state_desc, 'Collation') as sysname) from sys.databases"));

    [TestMethod]
    public void CatalogColumnMeetingADatabaseCollation_NamesRealsCatalogCollation()
        => AssertSqlError(
            "create table t (a int); select top 1 name + type from sys.objects",
            451,
            "Cannot resolve collation conflict between \"Latin1_General_CI_AS_KS_WS\" and \"SQL_Latin1_General_CP1_CI_AS\" in add operator occurring in SELECT statement column 1.");

    [TestMethod]
    public void SqlVariantColumn_ReportsItsMaximumLength()
        => AreEqual("8016|0|0", ExecuteScalar("""
            create table t (v sql_variant);
            select concat(max_length, '|', precision, '|', scale) from sys.columns where object_id = object_id('t')
            """));

    [TestMethod]
    public void DescribeFirstResultSet_CoversASqlVariantCatalogColumn()
        => AreEqual("sql_variant", ExecuteScalar("""
            create table #d (is_hidden bit, column_ordinal int, name sysname, is_nullable bit, system_type_id int, system_type_name nvarchar(256),
                max_length smallint, precision tinyint, scale tinyint, collation_name sysname null, user_type_id int, user_type_database sysname null,
                user_type_schema sysname null, user_type_name sysname null, assembly_qualified_type_name nvarchar(4000), xml_collection_id int,
                xml_collection_database sysname null, xml_collection_schema sysname null, xml_collection_name sysname null, is_xml_document bit,
                is_case_sensitive bit, is_fixed_length_clr_type bit, source_server sysname null, source_database sysname null, source_schema sysname null,
                source_table sysname null, source_column sysname null, is_identity_column bit, is_part_of_unique_key bit, is_updateable bit,
                is_computed_column bit, is_sparse_column_set bit, ordinal_in_order_by_list smallint, order_by_is_descending smallint,
                order_by_list_length smallint, tds_type_id int, tds_length int, tds_collation_id int, tds_collation_sort_id tinyint);
            insert #d exec sp_describe_first_result_set N'select value from sys.configurations';
            select system_type_name from #d
            """));

    [TestMethod]
    public void CatalogColumnNullability_FollowsReal()
        => AreEqual("0|1|0", ExecuteScalar("""
            select concat(columnproperty(object_id('sys.objects'), 'name', 'AllowsNull'), '|',
                columnproperty(object_id('sys.columns'), 'name', 'AllowsNull'), '|',
                columnproperty(object_id('sys.servers'), 'provider', 'AllowsNull'))
            """));

    [TestMethod]
    public void LocalServerRow_NamesItsProviderAndDataSource()
        => AreEqual("SQLNCLI|SIMULATED", ExecuteScalar("select provider + '|' + data_source from sys.servers where server_id = 0"));

    [TestMethod]
    public void CatalogColumnTypes_CarryRealsLengths()
        => AreEqual("nvarchar|8000;nvarchar|40", ExecuteScalar("""
            select top 0 c.COLUMN_DEFAULT, r.ROUTINE_TYPE into #x from INFORMATION_SCHEMA.COLUMNS c cross join INFORMATION_SCHEMA.ROUTINES r;
            select string_agg(concat(type_name(system_type_id), '|', max_length), ';') within group (order by column_id)
            from tempdb.sys.columns where object_id = object_id('tempdb..#x')
            """));

    [TestMethod]
    public void CatalogColumnCollations_FollowReal()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS|Latin1_General_BIN", ExecuteScalar("""
            select (select top 1 cast(sql_variant_property(delayed_durability_desc, 'Collation') as sysname) from sys.databases)
                + '|' + (select top 1 cast(sql_variant_property(assembly_class, 'Collation') as sysname) from sys.assembly_types where assembly_class is not null)
            """));

    [TestMethod]
    public void GroupByDiagnostic_NamesTheCatalogViewAsWritten()
        => AssertSqlError("select o.name, count(*) from sys.objects o", 8120,
            "Column 'sys.objects.name' is invalid in the select list because it is not contained in either an aggregate function or the GROUP BY clause.");

    /// <summary>
    /// The catalog views are catalogued themselves, in the system halves of
    /// sys.all_views / sys.all_columns, each column in its type's own
    /// collation (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select concat(schema_name(schema_id), ':', cast(is_ms_shipped as int)) from sys.system_views where name = 'objects'", "sys:1")]
    [DataRow("select concat(schema_name(schema_id), ':', cast(is_ms_shipped as int)) from sys.all_views where name = 'COLUMNS' and schema_id <> 4", "INFORMATION_SCHEMA:1")]
    [DataRow("select collation_name from sys.system_columns where object_id = object_id('sys.objects') and name = 'name'", "SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("select collation_name from sys.all_columns where object_id = object_id('sys.objects') and name = 'type'", "Latin1_General_CI_AS_KS_WS")]
    [DataRow("select name from sys.all_columns where object_id = object_id('INFORMATION_SCHEMA.COLUMNS') and column_id = 3", "TABLE_NAME")]
    [DataRow("select rtrim(type) from sys.all_objects where name = 'fn_helpcollations'", "IF")]
    [DataRow("select cast(count(*) as varchar(5)) from sys.all_objects where name = 'spt_values'", "0")]
    public void CatalogViews_AreCataloguedThemselves(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    [TestMethod]
    [DataRow("select count(*) from sys.columns where object_id = object_id('sys.objects')")]
    [DataRow("select count(*) from sys.views where name = 'objects'")]
    public void UserCatalogs_ListNoSystemViews(string sql)
        => AreEqual(0, new Simulation().ExecuteScalar(sql));

    [TestMethod]
    public void SpTables_ListsTheCatalogViews()
    {
        using var reader = new Simulation().ExecuteReader("exec sp_tables @table_name = 'objects', @table_owner = 'sys'");
        IsTrue(reader.Read());
        AreEqual("sys", reader.GetString(1));
        AreEqual("VIEW", reader.GetString(3));
    }

    [TestMethod]
    public void SpColumns_ListsACatalogViewsColumns()
    {
        using var reader = new Simulation().ExecuteReader("exec sp_columns @table_name = 'objects', @table_owner = 'sys'");
        IsTrue(reader.Read());
        AreEqual("name", reader.GetString(3));
    }

    /// <summary>
    /// Each catalog view declares its columns' types as real does — a name
    /// column sysname or nvarchar, an LSN numeric — checked against SQL
    /// Server 2025's sys.all_columns (probed 2026-09-26), and the rows read
    /// back in the declared type.
    /// </summary>
    [TestMethod]
    [DataRow("INFORMATION_SCHEMA.TABLES", "TABLE_CATALOG", "nvarchar")]
    [DataRow("INFORMATION_SCHEMA.TABLES", "TABLE_SCHEMA", "sysname")]
    [DataRow("sys.stats", "name", "nvarchar")]
    [DataRow("sys.servers", "product", "sysname")]
    [DataRow("sys.master_files", "create_lsn", "numeric")]
    [DataRow("sys.sql_expression_dependencies", "referenced_entity_name", "nvarchar")]
    public void CatalogViewColumn_DeclaresRealsType(string view, string column, string type)
    {
        var simulation = new Simulation();
        AreEqual(type, simulation.ExecuteScalar($"select type_name(user_type_id) from sys.all_columns where object_id = object_id('{view}') and name = '{column}'"));
        _ = simulation.ExecuteScalar($"create table t (a int); create statistics s on t (a); select count(*) from (select {column} c from {view}) d");
    }

    /// <summary>
    /// The columns real lists that answer alike for everything the simulator
    /// holds — replication, CDC and ledger flags, retention, encryption — are
    /// declared, so selecting one isn't a Msg 207 (probed 2026-09-26 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select concat(parent_object_id, ':', data_retention_period, ':', data_retention_period_unit_desc, ':', ledger_type_desc) from sys.tables", "0:-1:INFINITE:NON_LEDGER_TABLE")]
    [DataRow("select concat(cast(is_published as int), ':', ledger_view_type_desc) from sys.views", "0:NON_LEDGER_VIEW")]
    [DataRow("select concat(rtrim(type), ':', type_desc) from sys.sequences", "SO:SEQUENCE_OBJECT")]
    [DataRow("select concat(isnull(principal_id, -1), ':', cast(is_execution_replicated as int)) from sys.procedures", "-1:0")]
    [DataRow("select ledger_view_type_desc from sys.system_views where name = 'objects'", "NON_LEDGER_VIEW")]
    public void ConstantCatalogColumns_AreDeclared(string query, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int)", "create view v as select a from t", "create procedure p as select 1", "create sequence sq");
        AreEqual(expected, simulation.ExecuteScalar(query));
    }

    /// <summary>
    /// The column-family views carry sys.columns' row (the vector columns
    /// aside) ahead of their own columns, as real's do (probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select concat(type_name(system_type_id), ':', max_length, ':', cast(is_identity as int), ':', cast(seed_value as int)) from sys.identity_columns where object_id = object_id('t')", "int:4:1:5")]
    [DataRow("select concat(type_name(user_type_id), ':', collation_name, ':', cast(is_computed as int), ':', definition) from sys.computed_columns where object_id = object_id('t') and name = 'c'", "varchar:SQL_Latin1_General_CP1_CI_AS:1:([a]+'x')")]
    [DataRow("select count(*) from sys.all_columns where object_id = object_id('sys.identity_columns')", "44")]
    public void ColumnFamilyViews_CarryTheColumnsRow(string query, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int identity(5, 2), a varchar(10), c as a + 'x')");
        AreEqual(expected, Convert.ToString(simulation.ExecuteScalar(query), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// INFORMATION_SCHEMA.ROUTINES carries real's 51 columns, describing a
    /// scalar function's return type as PARAMETERS does. Probed 2026-09-26
    /// against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("fv", "select concat(DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, CHARACTER_SET_NAME, IS_DETERMINISTIC, SQL_DATA_ACCESS, IS_NULL_CALL, MAX_DYNAMIC_RESULT_SETS)", "varchar10iso_1NOREADSNO0")]
    [DataRow("fd", "select concat(DATA_TYPE, NUMERIC_PRECISION, NUMERIC_PRECISION_RADIX, NUMERIC_SCALE, IS_DETERMINISTIC)", "decimal5102YES")]
    [DataRow("fi", "select concat(DATA_TYPE, ROUTINE_BODY, SCHEMA_LEVEL_ROUTINE)", "TABLESQLYES")]
    [DataRow("p", "select concat(ROUTINE_TYPE, '|', DATA_TYPE, SQL_DATA_ACCESS, IS_NULL_CALL, MAX_DYNAMIC_RESULT_SETS, IS_USER_DEFINED_CAST)", "PROCEDURE|MODIFIES-1NO")]
    public void InformationSchemaRoutines_TakesRealsShape(string routine, string select, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create procedure p as select 1",
            "create function fv(@a int) returns varchar(10) as begin return 'x' end",
            "create function fd() returns decimal(5, 2) with schemabinding as begin return 1 end",
            "create function fi() returns table as return select 1 a");
        AreEqual(expected, Convert.ToString(simulation.ExecuteScalar($"{select} from INFORMATION_SCHEMA.ROUTINES where ROUTINE_NAME = '{routine}'"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void InformationSchemaRoutines_HasRealsColumnCount()
    {
        using var reader = new Simulation().ExecuteReader("select * from INFORMATION_SCHEMA.ROUTINES");
        AreEqual(51, reader.FieldCount);
        AreEqual("LAST_ALTERED", reader.GetName(50));
    }

    /// <summary>
    /// A live file's LSN history is NULL, and a full-text index reports
    /// version 2 with no incremental timestamp (probed 2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select count(*) from sys.database_files where is_persistent_log_buffer = 0 and coalesce(create_lsn, backup_lsn, redo_start_lsn) is null and differential_base_guid is null", 2)]
    [DataRow("select index_version + isnull(datalength(incremental_timestamp), 0) from sys.fulltext_indexes", 2)]
    public void FileAndFullTextViews_CarryRealsHistoryColumns(string query, int expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create fulltext catalog c as default;
            create table t (id int not null constraint pk primary key, s nvarchar(100));
            create fulltext index on t (s) key index pk
            """);
        AreEqual(expected, simulation.ExecuteScalar(query));
    }

    /// <summary>
    /// The INFORMATION_SCHEMA views real defines as queries over sys.*, and
    /// the catalog cells a content diff against real turned up (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select concat(CONSTRAINT_NAME, '|', CHECK_CLAUSE) from INFORMATION_SCHEMA.CHECK_CONSTRAINTS", "ck_amt|([amt]>=(0))")]
    [DataRow("select concat(VIEW_NAME, '|', TABLE_NAME, '|', count(*) over ()) from INFORMATION_SCHEMA.VIEW_TABLE_USAGE", "v|p|1")]
    [DataRow("select string_agg(COLUMN_NAME, ',') within group (order by COLUMN_NAME) from INFORMATION_SCHEMA.VIEW_COLUMN_USAGE", "code,id")]
    [DataRow("select concat(TABLE_NAME, '|', COLUMN_NAME, '|', ORDINAL_POSITION, '|', DATA_TYPE, NUMERIC_PRECISION, ',', NUMERIC_SCALE) from INFORMATION_SCHEMA.ROUTINE_COLUMNS", "fn|x|1|decimal5,1")]
    [DataRow("select concat(SEQUENCE_NAME, '|', DATA_TYPE, NUMERIC_PRECISION, '|', cast(START_VALUE as bigint), '|', cast(INCREMENT as bigint), '|', DECLARED_DATA_TYPE) from INFORMATION_SCHEMA.SEQUENCES", "sq|bigint19|5|3|bigint")]
    [DataRow("select concat(DOMAIN_NAME, '|', TABLE_NAME, '|', COLUMN_NAME) from INFORMATION_SCHEMA.COLUMN_DOMAIN_USAGE", "flag|p|f")]
    [DataRow("select VIEW_DEFINITION from INFORMATION_SCHEMA.VIEWS", "create view v with schemabinding as select id, code from dbo.p")]
    [DataRow("select concat(uses_database_collation, is_schema_bound) from sys.sql_modules where object_id = object_id('v')", "11")]
    [DataRow("select concat(has_filter, filter_definition) from sys.stats where name = 'ix_c'", "1([note] IS NOT NULL)")]
    [DataRow("select count(*) from sys.index_columns where object_id = object_id('c') and column_store_order_ordinal = 0 and data_clustering_ordinal = 0", "3")]
    public void CatalogContents_MatchReal(string query, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create type flag from bit",
            """
            create table p (id int identity(10, 5) not null constraint pk_p primary key, code char(3) not null, amt decimal(9,2) null constraint ck_amt check (amt >= 0), f flag);
            create table c (id int not null, pid int not null, note nvarchar(200), constraint pk_c primary key (id, pid));
            create unique index ix_c on c (note) where note is not null;
            create sequence sq as bigint start with 5 increment by 3;
            """,
            "create view v with schemabinding as select id, code from dbo.p",
            "create function fn (@x decimal(5,1)) returns table as return select @x as x");
        AreEqual(expected, Convert.ToString(simulation.ExecuteScalar(query), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A view column that passes an identity column straight through reports as
    /// identity — through an alias, a derived table, TOP, GROUP BY, DISTINCT,
    /// APPLY or another view, but not an expression, a join or a UNION — and
    /// the view answers IDENT_SEED / IDENT_CURRENT from it (probed 2026-09-26
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select id as x from p", "1")]
    [DataRow("select d.id from (select top 5 id from p) d", "1")]
    [DataRow("select id from p group by id", "1")]
    [DataRow("select p.id from p cross apply (select 1 one) c", "1")]
    [DataRow("select id from v0", "1")]
    [DataRow("select id + 0 as x from p", "0")]
    [DataRow("select p.id from p join q on p.a = q.b", "0")]
    [DataRow("select id from p union all select qid from q", "0")]
    public void ViewColumn_PassesIdentityThrough(string body, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table p (id int identity(10, 5) not null primary key, a int); create table q (qid int identity, b int); insert p (a) values (1), (2)",
            "create view v0 as select id from p",
            $"create view v as {body}");
        AreEqual(expected, simulation.ExecuteScalar("""
            select concat(max(cast(c.is_identity as int)), '')
            from sys.columns c where c.object_id = object_id('v') and c.column_id = 1
            """));
        if (expected == "1")
        {
            AreEqual("10|5|15|1|NULL", simulation.ExecuteScalar("""
                select concat(ident_seed('v'), '|', ident_incr('v'), '|', ident_current('v'), '|', columnproperty(object_id('v'), col_name(object_id('v'), 1), 'IsIdentity'), '|',
                    isnull(cast((select seed_value from sys.identity_columns where object_id = object_id('v')) as varchar), 'NULL'))
                """));
        }
    }

    /// <summary>
    /// COL_NAME answers for any object with columns, by the stable column_id a
    /// dropped column leaves a hole in (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void ColName_CoversEveryColumnHost()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table p (id int not null primary key, a int, b int); alter table p drop column a",
            "create view v as select id from p group by id",
            "create function f() returns table as return select id, b from p",
            "create type tt as table (q int)");
        AreEqual("id||b|id|b|name|q", simulation.ExecuteScalar("""
            select concat(col_name(object_id('p'), 1), '|', col_name(object_id('p'), 2), '|', col_name(object_id('p'), 3), '|',
                col_name(object_id('v'), 1), '|', col_name(object_id('f'), 2), '|', col_name(object_id('sys.objects'), 1), '|',
                col_name((select type_table_object_id from sys.table_types where name = 'tt'), 1))
            """));
    }

    /// <summary>A view column reports its own collation in INFORMATION_SCHEMA.COLUMNS (probed 2026-09-26 against SQL Server 2025).</summary>
    [TestMethod]
    public void InformationSchemaColumns_ViewColumnKeepsItsCollation()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table p (code char(3) collate Latin1_General_BIN)", "create view v as select code from p");
        AreEqual("Latin1_General_BIN", simulation.ExecuteScalar("select COLLATION_NAME from INFORMATION_SCHEMA.COLUMNS where TABLE_NAME = 'v'"));
    }

    /// <summary>
    /// A table type's own constraints and indexes list as sys-schema, shipped
    /// children of its type table (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select string_agg(concat(left(name, 6), ':', type, ':', schema_name(schema_id), ':', cast(is_ms_shipped as int)) collate database_default, ',') within group (order by type) from sys.objects where parent_object_id = @id", "CK__TT:C :sys:1,DF__TT:D :sys:1,PK__TT:PK:sys:1,UQ__TT:UQ:sys:1")]
    [DataRow("select string_agg(concat(left(name, 6), ':', index_id), ',') within group (order by index_id) from sys.indexes where object_id = @id", "PK__TT:1,ix:2,UQ__TT:3")]
    [DataRow("select string_agg(concat(left(name, 6), ':', stats_id), ',') within group (order by stats_id) from sys.stats where object_id = @id", "PK__TT:1,ix:2,UQ__TT:3")]
    [DataRow("select concat(definition, ':', parent_column_id, ':', schema_name(schema_id)) from sys.default_constraints where parent_object_id = @id", "(N'x'):3:sys")]
    [DataRow("select concat(definition, ':', schema_name(schema_id)) from sys.check_constraints where parent_object_id = @id", "([k]>(0)):sys")]
    [DataRow("select string_agg(concat(CONSTRAINT_SCHEMA, ':', isnull(TABLE_NAME, 'NULL'), ':', CONSTRAINT_TYPE), ',') within group (order by CONSTRAINT_TYPE) from INFORMATION_SCHEMA.TABLE_CONSTRAINTS", "sys:NULL:CHECK,sys:NULL:PRIMARY KEY,sys:NULL:UNIQUE")]
    [DataRow("select concat(TABLE_SCHEMA, ':', left(TABLE_NAME, 5), ':', COLUMN_NAME) from INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE", "sys:TT_tt:k")]
    [DataRow("select concat(CONSTRAINT_SCHEMA, ':', CHECK_CLAUSE) from INFORMATION_SCHEMA.CHECK_CONSTRAINTS", "sys:([k]>(0))")]
    public void TableType_ListsItsOwnConstraints(string query, string expected)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create type tt as table (k int primary key, u int unique, v nvarchar(10) default N'x', check (k > 0), index ix (v))");
        AreEqual(expected, simulation.ExecuteScalar($"declare @id int = (select type_table_object_id from sys.table_types where name = 'tt'); {query}"));
    }
}
