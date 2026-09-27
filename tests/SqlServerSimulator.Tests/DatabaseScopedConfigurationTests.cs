using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER DATABASE SCOPED CONFIGURATION</c> and the
/// <c>sys.database_scoped_configurations</c> rows it drives. Probed 2026-09-27
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DatabaseScopedConfigurationTests
{
    private const string Row = "select concat(cast(value as nvarchar(max)), '|', isnull(cast(value_for_secondary as nvarchar(max)), 'NULL'), '|', is_value_default, '|', cast(sql_variant_property(value, 'BaseType') as sysname)) from sys.database_scoped_configurations where configuration_id = ";

    [TestMethod]
    [DataRow(1, "MAXDOP", "0|NULL|1|int")]
    [DataRow(3, "PARAMETER_SNIFFING", "1|NULL|1|bit")]
    [DataRow(11, "ELEVATE_ONLINE", "OFF|NULL|1|nvarchar")]
    [DataRow(23, "VERBOSE_TRUNCATION_WARNINGS", "1|NULL|1|bit")]
    [DataRow(25, "PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES", "1440|NULL|1|int")]
    [DataRow(38, "LEDGER_DIGEST_STORAGE_ENDPOINT", "OFF|NULL|1|nvarchar")]
    [DataRow(44, "FULLTEXT_INDEX_VERSION", "2|NULL|1|smallint")]
    [DataRow(48, "PREVIEW_FEATURES", "0|NULL|1|bit")]
    public void Defaults(int id, string name, string expected)
    {
        var sim = new Simulation();
        AreEqual(name, sim.ExecuteScalar($"select name from sys.database_scoped_configurations where configuration_id = {id}"));
        AreEqual(expected, sim.ExecuteScalar(Row + id));
    }

    [TestMethod]
    [DataRow("simulated", 41)]
    [DataRow("master", 40)]
    [DataRow("model", 40)]
    public void RowCount_SystemDatabasesOmitPreviewFeatures(string database, int count)
        => AreEqual(count, new Simulation().ExecuteScalar($"select count(*) from {database}.sys.database_scoped_configurations"));

    [TestMethod]
    [DataRow("set maxdop = 4", 1, "4|NULL|0|int")]
    [DataRow("set maxdop = 32767", 1, "32767|NULL|0|int")]
    [DataRow("set legacy_cardinality_estimation = on", 2, "1|NULL|0|bit")]
    [DataRow("set PARAMETER_SNIFFING = OFF", 3, "0|NULL|0|bit")]
    [DataRow("set elevate_online = when_supported", 11, "WHEN_SUPPORTED|NULL|0|nvarchar")]
    [DataRow("set ELEVATE_RESUMABLE = FAIL_UNSUPPORTED", 12, "FAIL_UNSUPPORTED|NULL|0|nvarchar")]
    [DataRow("set paused_resumable_index_abort_duration_minutes = 0", 25, "0|NULL|0|int")]
    [DataRow("set fulltext_index_version = 1", 44, "1|NULL|0|smallint")]
    [DataRow("for secondary set maxdop = 2", 1, "0|2|1|int")]
    [DataRow("for secondary set parameter_sniffing = off", 3, "1|0|1|bit")]
    [DataRow("for secondary set fulltext_index_version = 1", 44, "1|NULL|0|smallint")]
    [DataRow("for secondary set ledger_digest_storage_endpoint = off", 38, "OFF|NULL|1|nvarchar")]
    public void Set_IsReflected(string clause, int id, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"alter database scoped configuration {clause}; {Row}{id}"));

    [TestMethod]
    public void BackToDefault_AndPrimaryForSecondary()
        => AreEqual("0|NULL|1|int", new Simulation().ExecuteScalar($"""
            alter database scoped configuration set maxdop = 4;
            alter database scoped configuration set maxdop = 0;
            alter database scoped configuration for secondary set maxdop = 3;
            alter database scoped configuration for secondary set maxdop = primary;
            {Row}1
            """));

    // Compile-time refusals: nothing in the batch runs.
    [TestMethod]
    [DataRow("set maxdop = 32768", 12108, 16, 1)]
    [DataRow("set maxdop = -1", 12108, 16, 1)]
    [DataRow("set maxdop = 2147483648", 1080, 15, 1)]
    [DataRow("set maxdop = 4.5", 1080, 15, 1)]
    [DataRow("set maxdop = primary", 12109, 16, 1)]
    [DataRow("set paused_resumable_index_abort_duration_minutes = 71583", 12121, 16, 1)]
    [DataRow("set fulltext_index_version = 3", 31207, 16, 1)]
    [DataRow("for secondary set identity_cache = off", 12110, 16, 1)]
    [DataRow("for secondary set elevate_online = off", 12110, 16, 2)]
    [DataRow("for secondary set elevate_resumable = off", 12110, 16, 3)]
    [DataRow("for secondary set global_temporary_table_auto_drop = off", 12110, 16, 4)]
    [DataRow("for secondary set paused_resumable_index_abort_duration_minutes = 5", 12110, 16, 5)]
    [DataRow("for secondary set preview_features = primary", 12110, 16, 6)]
    [DataRow("set maxdop = '4'", 102, 15, 1)]
    [DataRow("set maxdop = on", 156, 15, 1)]
    [DataRow("set legacy_cardinality_estimation = 1", 102, 15, 1)]
    [DataRow("set elevate_online = on", 156, 15, 1)]
    [DataRow("set elevate_online = primary", 156, 15, 1)]
    [DataRow("set bogus = on", 102, 15, 1)]
    [DataRow("set [maxdop] = 1", 102, 15, 1)]
    [DataRow("for primary set maxdop = 1", 156, 15, 1)]
    [DataRow("for secondary clear procedure_cache", 102, 15, 1)]
    [DataRow("clear procedure_cache 'abc'", 102, 15, 1)]
    [DataRow("set maxdop = 2 foo", 102, 15, 1)]
    public void CompileTimeRefusals(string clause, int number, int @class, int state)
    {
        var sim = new Simulation();
        var ex = sim.AssertSqlError($"create table t (a int); alter database scoped configuration {clause}", number);
        AreEqual(((byte)@class, (byte)state), (ex.Class, ex.State));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    // Run-time refusals end the batch, and a TRY block catches them.
    [TestMethod]
    [DataRow("clear procedure_cache 0x0600060001", 12117, 1)]
    [DataRow("set dw_compatibility_level = 10", 102, 1)]
    [DataRow("set ledger_digest_storage_endpoint = 'http://a.blob.core.windows.net'", 12136, 1)]
    [DataRow("set ledger_digest_storage_endpoint = 'https://a.blob.core.windows.net/x'", 12136, 2)]
    [DataRow("set ledger_digest_storage_endpoint = 'https://A.BLOB.core.windows.net'", 12136, 3)]
    [DataRow("set ledger_digest_storage_endpoint = 'https://a.blob.core.windows.net/'", 37531, 1)]
    public void RunTimeRefusals_EndTheBatch(string clause, int number, int state)
    {
        var sim = new Simulation();
        var ex = sim.AssertSqlError($"alter database scoped configuration {clause}; create table t (a int)", number);
        AreEqual((byte)state, ex.State);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
        AreEqual(number, sim.ExecuteScalar($"begin try alter database scoped configuration {clause} end try begin catch select error_number() end catch"));
    }

    [TestMethod]
    public void LedgerEndpoint_UnreachableNamesItWithoutTheTrailingSlash()
        => new Simulation().AssertSqlError(
            "alter database scoped configuration set ledger_digest_storage_endpoint = 'https://a.blob.core.windows.net/'",
            37531,
            "Failed to set the ledger digest storage endpoint to 'https://a.blob.core.windows.net'. Verify that you have created a credential object to provide SQL Server access to the 'sqldbledgerdigests' container in this Azure Storage account.");

    [TestMethod]
    public void ReadOnlyDatabase_RefusesSet_ButClearsTheCache()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database current set read_only");
        _ = sim.AssertSqlError("alter database scoped configuration set maxdop = 1", 3906);
        _ = sim.ExecuteNonQuery("alter database scoped configuration clear procedure_cache");
    }

    [TestMethod]
    public void WithoutPermission_IsMsg15247()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u without login");
        var ex = sim.AssertSqlError("execute as user = 'u'; alter database scoped configuration set maxdop = 2", 15247);
        AreEqual((byte)13, ex.State);
        AreEqual(2, sim.ExecuteScalar("""
            alter role db_owner add member u;
            execute as user = 'u';
            alter database scoped configuration set maxdop = 2;
            revert;
            select value from sys.database_scoped_configurations where configuration_id = 1
            """));
    }

    [TestMethod]
    public void NewDatabase_StartsFromModel()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            use model;
            alter database scoped configuration set maxdop = 3;
            create database d;
            select value from d.sys.database_scoped_configurations where configuration_id = 1
            """));

    [TestMethod]
    public void InATransaction_RollsItBack_AndCaughtDoomsIt()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; alter database scoped configuration set maxdop = 3; select 1").ExecuteScalar());
        AreEqual((226, (byte)7), (ex.Number, ex.State));
        AreEqual("0:0", connection.CreateCommand("select concat(@@trancount, ':', xact_state())").ExecuteScalar());
        AreEqual("1:-1", connection.CreateCommand("""
            begin tran;
            begin try alter database scoped configuration set maxdop = 3 end try begin catch end catch;
            select concat(@@trancount, ':', xact_state());
            rollback
            """).ExecuteScalar());
    }

    [TestMethod]
    public void DdlTrigger_FiresForSetAndClear()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table log (e sysname, has_object bit)",
            "create trigger dt on database for alter_database_scoped_configuration as insert log select eventdata().value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'), eventdata().exist('/EVENT_INSTANCE/ObjectName')",
            "alter database scoped configuration set maxdop = 3",
            "alter database scoped configuration clear procedure_cache");
        AreEqual("2:0", sim.ExecuteScalar("select concat(count(*), ':', sum(cast(has_object as int))) from log where e = 'ALTER_DATABASE_SCOPED_CONFIGURATION'"));
    }
}
