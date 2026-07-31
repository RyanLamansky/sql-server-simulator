using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

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
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create schema audit;
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
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create schema audit;
            select schema_id from sys.schemas where name = 'audit'
            """));

    [TestMethod]
    public void SysSchemas_UserSchemaOwnedByDbo()
        // A user schema (schema_id 5) is owned by dbo (principal_id 1),
        // probe-confirmed against SQL Server 2025.
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create schema audit;
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
        using var reader = new Simulation().ExecuteReader("""
            create schema audit;
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
        AreEqual((byte)3, reader.GetByte(4));
        AreEqual("SIMPLE", reader.GetString(5));
        AreEqual((byte)0, reader.GetByte(6));
        AreEqual("OFF", reader.GetString(7));
    }

    // The model template reports FULL recovery (per the reference instance);
    // every other database reports SIMPLE.
    [TestMethod]
    public void SysDatabases_ModelRecoveryModel_IsFull()
    {
        using var reader = new Simulation().ExecuteReader(
            "select recovery_model, recovery_model_desc from sys.databases where name = 'model'");
        IsTrue(reader.Read());
        AreEqual((byte)1, reader.GetByte(0));
        AreEqual("FULL", reader.GetString(1));
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
        AreEqual("SIMPLE", reader.GetString(4));
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
        AreEqual("simulated_Data", reader.GetString(3));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual((byte)1, reader.GetByte(1));
        AreEqual("LOG", reader.GetString(2));
        AreEqual("simulated_Log", reader.GetString(3));
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
}
