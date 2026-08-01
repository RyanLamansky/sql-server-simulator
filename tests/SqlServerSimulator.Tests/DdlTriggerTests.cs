using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Storage and catalog-view tests for database-scope DDL triggers
/// (<c>CREATE TRIGGER … ON DATABASE FOR &lt;event_type_group&gt; AS &lt;body&gt;</c>) —
/// the create / drop paths and the <c>sys.triggers</c> /
/// <c>sys.trigger_events</c> / <c>sys.trigger_event_types</c> projection.
/// Firing behavior lives in <see cref="DdlTriggerFiringTests"/>.
/// </summary>
[TestClass]
public sealed class DdlTriggerTests
{
    private const string CreateAwTrigger = """
        create trigger [ddlDatabaseTriggerLog]
        on database
        for ddl_database_level_events as
        begin
            set nocount on;
        end
        """;

    [TestMethod]
    public void CreateTrigger_OnDatabase_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CreateAwTrigger);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'ddlDatabaseTriggerLog'"));
    }

    [TestMethod]
    public void DdlTrigger_SysTriggersRow_HasDatabaseParentClass()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CreateAwTrigger);
        AreEqual((byte)0, sim.ExecuteScalar("select parent_class from sys.triggers where name = 'ddlDatabaseTriggerLog'"));
        AreEqual("DATABASE", sim.ExecuteScalar("select parent_class_desc from sys.triggers where name = 'ddlDatabaseTriggerLog'"));
        AreEqual(0, sim.ExecuteScalar("select parent_id from sys.triggers where name = 'ddlDatabaseTriggerLog'"));
        AreEqual("SQL_TRIGGER", sim.ExecuteScalar("select type_desc from sys.triggers where name = 'ddlDatabaseTriggerLog'"));
        IsFalse((bool)sim.ExecuteScalar("select is_instead_of_trigger from sys.triggers where name = 'ddlDatabaseTriggerLog'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.triggers where name = 'ddlDatabaseTriggerLog'")!);
    }

    [TestMethod]
    public void DropTrigger_OnDatabase_RemovesFromCatalog()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CreateAwTrigger);
        _ = sim.ExecuteNonQuery("drop trigger [ddlDatabaseTriggerLog] on database");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'ddlDatabaseTriggerLog'"));
    }

    [TestMethod]
    public void DropTrigger_OnDatabase_IfExists_SilentOnMissing()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("drop trigger if exists [missingDdlTrigger] on database");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'missingDdlTrigger'"));
    }

    [TestMethod]
    public void CreateTrigger_MultiEvent_StoresAllEventTypes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create trigger [multiEvent]
            on database
            for create_table, drop_table, alter_table as
            begin set nocount on; end
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'multiEvent'"));
    }

    [TestMethod]
    public void CreateTrigger_OnDatabase_AlreadyExists_Raises2714()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CreateAwTrigger);
        _ = sim.AssertSqlError(CreateAwTrigger, 2714);
    }

    [TestMethod]
    public void CreateOrAlterTrigger_OnDatabase_UpsertsBody()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CreateAwTrigger);
        _ = sim.ExecuteNonQuery("""
            create or alter trigger [ddlDatabaseTriggerLog]
            on database
            for ddl_table_events as
            begin set nocount on; end
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.triggers where name = 'ddlDatabaseTriggerLog'"));
    }

    [TestMethod]
    public void SysTriggers_DdlTrigger_ReportsTrAndSqlTriggerType()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create trigger trg_ddl on database for create_table as select 1");
        AreEqual("TR", sim.ExecuteScalar(
            "select rtrim(type) from sys.triggers where name = 'trg_ddl'"));
        AreEqual("SQL_TRIGGER", sim.ExecuteScalar(
            "select type_desc from sys.triggers where name = 'trg_ddl'"));
    }

    [TestMethod]
    public void TriggerEvents_DdlDatabaseLevelEvents_ExpandsTo158LeafRows()
    {
        // A DDL trigger created FOR DDL_DATABASE_LEVEL_EVENTS surfaces one
        // sys.trigger_events row per leaf event in the group's transitive
        // closure — 158 rows, each tagged with the group id (10016) and desc,
        // probe-confirmed against SQL Server 2025's AdventureWorks2025.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CreateAwTrigger);
        AreEqual(158, sim.ExecuteScalar("""
            select count(*) from sys.trigger_events e
            join sys.triggers t on t.object_id = e.object_id
            where t.parent_class = 0
            """));
        AreEqual(1, sim.ExecuteScalar("""
            select count(distinct event_group_type) from sys.trigger_events e
            join sys.triggers t on t.object_id = e.object_id
            where t.parent_class = 0
            """));
        AreEqual(10016, sim.ExecuteScalar("""
            select top 1 event_group_type from sys.trigger_events e
            join sys.triggers t on t.object_id = e.object_id
            where t.parent_class = 0
            """));
        AreEqual("DDL_DATABASE_LEVEL_EVENTS", sim.ExecuteScalar("""
            select top 1 event_group_type_desc from sys.trigger_events e
            join sys.triggers t on t.object_id = e.object_id
            where t.parent_class = 0
            """));
    }

    [TestMethod]
    public void TriggerEvents_DdlDatabaseLevelEvents_LeafRowShape()
    {
        // Sample leaf rows probe-confirmed on the reference: RENAME (241),
        // CREATE_COLUMN_MASTER_KEY (315), ALTER_DATABASE_SCOPED_CONFIGURATION
        // (320). is_first / is_last are 0, is_trigger_event is 1.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(CreateAwTrigger);
        AreEqual("RENAME", sim.ExecuteScalar(
            "select e.type_desc from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.parent_class = 0 and e.type = 241"));
        AreEqual("CREATE_COLUMN_MASTER_KEY", sim.ExecuteScalar(
            "select e.type_desc from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.parent_class = 0 and e.type = 315"));
        AreEqual("ALTER_DATABASE_SCOPED_CONFIGURATION", sim.ExecuteScalar(
            "select e.type_desc from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.parent_class = 0 and e.type = 320"));
        IsFalse((bool)sim.ExecuteScalar(
            "select is_first from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.parent_class = 0 and e.type = 241")!);
        IsFalse((bool)sim.ExecuteScalar(
            "select is_last from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.parent_class = 0 and e.type = 241")!);
        IsTrue((bool)sim.ExecuteScalar(
            "select is_trigger_event from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.parent_class = 0 and e.type = 241")!);
    }

    [TestMethod]
    public void TriggerEvents_IndividualEvent_HasNullGroup()
    {
        // A DDL trigger created FOR a single event (not a group) surfaces one
        // row with a NULL event_group_type.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create trigger [oneEvent]
            on database
            for create_table as
            begin set nocount on; end
            """);
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.name = 'oneEvent'"));
        AreEqual(21, sim.ExecuteScalar(
            "select e.type from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.name = 'oneEvent'"));
        AreEqual("CREATE_TABLE", sim.ExecuteScalar(
            "select e.type_desc from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.name = 'oneEvent'"));
        AreEqual(0, sim.ExecuteScalar(
            "select count(event_group_type) from sys.trigger_events e join sys.triggers t on t.object_id = e.object_id where t.name = 'oneEvent'"));
    }

    [TestMethod]
    public void SysTriggerEventTypes_ExposesStaticCatalog()
    {
        // The static sys.trigger_event_types catalog (312 rows, probe-confirmed
        // shape). DDL_DATABASE_LEVEL_EVENTS (10016) parents to DDL_EVENTS
        // (10001); CREATE_TABLE (21) parents to DDL_TABLE_EVENTS (10018).
        var sim = new Simulation();
        AreEqual(312, sim.ExecuteScalar("select count(*) from sys.trigger_event_types"));
        AreEqual("DDL_DATABASE_LEVEL_EVENTS", sim.ExecuteScalar(
            "select type_name from sys.trigger_event_types where type = 10016"));
        AreEqual(10001, sim.ExecuteScalar(
            "select parent_type from sys.trigger_event_types where type = 10016"));
        AreEqual(10018, sim.ExecuteScalar(
            "select parent_type from sys.trigger_event_types where type = 21"));
        AreEqual(0, sim.ExecuteScalar(
            "select count(parent_type) from sys.trigger_event_types where type = 10001"));
    }
}
