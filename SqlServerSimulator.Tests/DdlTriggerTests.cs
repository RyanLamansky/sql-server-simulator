using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for database-scope DDL triggers
/// (<c>CREATE TRIGGER … ON DATABASE FOR &lt;event_type_group&gt; AS &lt;body&gt;</c>).
/// The simulator stores DDL triggers for catalog-view round-trip but does
/// not fire them; these tests verify the storage + catalog visibility +
/// drop / disable paths.
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
}
