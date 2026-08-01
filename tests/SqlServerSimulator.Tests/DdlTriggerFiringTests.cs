using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Firing behavior for database-scope DDL triggers: which statements raise
/// which event, the <c>EVENTDATA()</c> document a body reads, and the
/// nesting / atomicity / disable rules around the fire.
/// Storage + catalog projection live in <see cref="DdlTriggerTests"/>.
/// Probed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DdlTriggerFiringTests
{
    /// <summary>
    /// Audit table + a trigger logging one row per event it sees. Callers append
    /// the DDL batches that should fire it.
    /// </summary>
    private static Simulation NewLoggingSimulation(string eventDeclaration = "ddl_database_level_events")
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ddl_log (id int identity primary key, ev nvarchar(200), obj nvarchar(200), doc nvarchar(max))",
            $"""
            create trigger tg on database for {eventDeclaration} as
            begin
                declare @e xml = eventdata();
                insert ddl_log (ev, obj, doc) values (
                    @e.value('(/EVENT_INSTANCE/EventType)[1]', 'nvarchar(200)'),
                    @e.value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(200)'),
                    cast(@e as nvarchar(max)));
            end
            """);
        return sim;
    }

    [TestMethod]
    public void CreateTable_FiresTrigger_WithCreateTableEvent()
    {
        var sim = NewLoggingSimulation();
        _ = sim.ExecuteNonQuery("create table t1 (a int)");
        AreEqual("CREATE_TABLE", sim.ExecuteScalar("select ev from ddl_log"));
        AreEqual("t1", sim.ExecuteScalar("select obj from ddl_log"));
    }

    [TestMethod]
    public void EventData_CarriesRealsElementShape()
    {
        var sim = NewLoggingSimulation();
        _ = sim.ExecuteNonQuery("create table dbo.t1 (a int)");
        var document = (string)sim.ExecuteScalar("select doc from ddl_log")!;
        Assert.Contains("<EVENT_INSTANCE><EventType>CREATE_TABLE</EventType>", document);
        Assert.Contains("<ServerName>SIMULATED</ServerName>", document);
        Assert.Contains("<LoginName>dbo</LoginName><UserName>dbo</UserName>", document);
        Assert.Contains("<DatabaseName>simulated</DatabaseName>", document);
        Assert.Contains("<SchemaName>dbo</SchemaName><ObjectName>t1</ObjectName><ObjectType>TABLE</ObjectType>", document);
        Assert.Contains("<CommandText>create table dbo.t1 (a int)</CommandText>", document);
        Assert.Contains("QUOTED_IDENTIFIER=\"ON\"", document);
    }

    [TestMethod]
    public void EventData_EscapesMarkupInCommandText()
    {
        var sim = NewLoggingSimulation();
        _ = sim.ExecuteNonQuery("create table t1 (a int, constraint ck check (a > 0))");
        Assert.Contains("a &gt; 0", (string)sim.ExecuteScalar("select doc from ddl_log")!);
    }

    [TestMethod]
    public void EventData_OutsideTrigger_IsNull()
        => IsTrue(new Simulation().ExecuteScalar("select eventdata()") is null or DBNull);

    [TestMethod]
    public void Body_SeesTheCompletedChange()
    {
        var sim = NewLoggingSimulation();
        sim.ExecuteBatches(
            "create table seen (present int)",
            """
            create or alter trigger tg on database for create_table as
            begin
                declare @e xml = eventdata();
                declare @n nvarchar(200) = @e.value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(200)');
                if @n <> 'seen' insert seen values (case when object_id(@n) is null then 0 else 1 end);
            end
            """,
            "create table t1 (a int)");
        AreEqual(1, sim.ExecuteScalar("select present from seen"));
    }

    [TestMethod]
    public void GroupDeclaration_ExpandsToItsLeafEvents()
    {
        var sim = NewLoggingSimulation("ddl_table_events");
        sim.ExecuteBatches(
            "create table t1 (a int)",
            "alter table t1 add b int",
            "create view v1 as select a from t1",
            "drop table t1");
        AreEqual("CREATE_TABLE,ALTER_TABLE,DROP_TABLE", sim.ExecuteScalar(
            "select string_agg(ev, ',') within group (order by id) from ddl_log"));
    }

    [TestMethod]
    public void IndividualEventDeclaration_IgnoresOtherEvents()
    {
        var sim = NewLoggingSimulation("create_table");
        sim.ExecuteBatches("create table t1 (a int)", "drop table t1");
        AreEqual(1, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void IndexAndTriggerEvents_CarryTargetObject()
    {
        var sim = NewLoggingSimulation();
        sim.ExecuteBatches(
            "create table t1 (a int)",
            "create index ix1 on t1 (a)",
            "create trigger tr1 on t1 after insert as select 1");
        AreEqual("CREATE_INDEX", sim.ExecuteScalar("select ev from ddl_log where obj = 'ix1'"));
        Assert.Contains(
            "<ObjectType>INDEX</ObjectType><TargetObjectName>t1</TargetObjectName><TargetObjectType>TABLE</TargetObjectType>",
            (string)sim.ExecuteScalar("select doc from ddl_log where obj = 'ix1'")!);
        AreEqual("CREATE_TRIGGER", sim.ExecuteScalar("select ev from ddl_log where obj = 'tr1'"));
    }

    [TestMethod]
    public void PrincipalEvents_OmitSchemaName()
    {
        var sim = NewLoggingSimulation();
        sim.ExecuteBatches("create user u1 without login", "create role r1");
        AreEqual("CREATE_USER,CREATE_ROLE", sim.ExecuteScalar(
            "select string_agg(ev, ',') within group (order by id) from ddl_log"));
        Assert.DoesNotContain("SchemaName", (string)sim.ExecuteScalar("select doc from ddl_log where obj = 'u1'")!);
        Assert.Contains("<ObjectType>SQL USER</ObjectType>", (string)sim.ExecuteScalar("select doc from ddl_log where obj = 'u1'")!);
    }

    [TestMethod]
    public void SelectInto_RaisesCreateTable_TempTableDoesNot()
    {
        var sim = NewLoggingSimulation();
        sim.ExecuteBatches(
            "create table src (a int)",
            "select a into copied from src",
            "create table #scratch (a int)");
        AreEqual("src,copied", sim.ExecuteScalar("select string_agg(obj, ',') within group (order by id) from ddl_log"));
    }

    [TestMethod]
    public void SpRename_RaisesRenameEvent()
    {
        var sim = NewLoggingSimulation();
        sim.ExecuteBatches("create table t1 (a int)", "exec sp_rename 't1', 't2'");
        AreEqual("RENAME", sim.ExecuteScalar("select ev from ddl_log where obj = 't1' and ev = 'RENAME'"));
    }

    [TestMethod]
    public void DropList_RaisesOneEventPerName_EachCarryingTheWholeStatement()
    {
        var sim = NewLoggingSimulation("drop_table");
        sim.ExecuteBatches("create table d1 (a int)", "create table d2 (a int)", "drop table d1, d2");
        AreEqual("d1,d2", sim.ExecuteScalar("select string_agg(obj, ',') within group (order by id) from ddl_log"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from ddl_log where doc like '%<CommandText>drop table d1, d2</CommandText>%'"));
    }

    [TestMethod]
    public void FailedDdl_RaisesNoEvent()
    {
        var sim = NewLoggingSimulation();
        _ = sim.ExecuteNonQuery("create table t1 (a int)");
        _ = sim.AssertSqlError("create table t1 (a int)", 2714);
        AreEqual(1, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void SkippedBranchDdl_RaisesNoEvent()
    {
        var sim = NewLoggingSimulation();
        _ = sim.ExecuteNonQuery("if 1 = 0 create table t1 (a int)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void BodyThrow_PropagatesAndUndoesTheBodysOwnWrites()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ddl_log (note nvarchar(50))",
            """
            create trigger tg on database for create_table as
            begin
                insert ddl_log values ('before');
                throw 51000, 'vetoed', 1;
            end
            """);
        var error = sim.AssertSqlError("create table t1 (a int)", 51000);
        AreEqual("tg", error.Procedure);
        AreEqual(0, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void BodySwallowedError_StillAbortsWithMsg3616()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ddl_log (note nvarchar(50))",
            """
            create trigger tg on database for create_table as
            begin
                begin try throw 51000, 'caught', 1; end try begin catch insert ddl_log values ('swallowed'); end catch
            end
            """);
        _ = sim.AssertSqlError("create table t1 (a int)", 3616);
        AreEqual(0, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void DisabledTrigger_DoesNotFire_AndReEnableRestores()
    {
        var sim = NewLoggingSimulation();
        sim.ExecuteBatches(
            "disable trigger tg on database",
            "create table t1 (a int)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from ddl_log"));
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.triggers where name = 'tg'")!);
        sim.ExecuteBatches("enable trigger tg on database", "create table t2 (a int)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void DisableTriggerAll_OnDatabase_SuppressesEveryDdlTrigger()
    {
        var sim = NewLoggingSimulation();
        sim.ExecuteBatches("disable trigger all on database", "create table t1 (a int)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void NestedDdl_FiresAnotherTrigger_AtDepthTwo()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ddl_log (id int identity primary key, note nvarchar(100))",
            """
            create trigger tg_table on database for create_table as
            begin
                declare @e xml = eventdata();
                if @e.value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(200)') = 'seed'
                    exec ('create view nested_v as select 1 as c');
            end
            """,
            """
            create trigger tg_view on database for create_view as
            begin
                insert ddl_log (note) values ('view nest=' + cast(trigger_nestlevel() as varchar(10)));
            end
            """,
            "create table seed (a int)");
        AreEqual("view nest=2", sim.ExecuteScalar("select note from ddl_log"));
    }

    [TestMethod]
    public void SelfRecursion_IsSuppressed()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ddl_log (id int identity primary key, note nvarchar(100))",
            """
            create trigger tg on database for create_table as
            begin
                declare @lvl int = trigger_nestlevel();
                insert ddl_log (note) values ('nest=' + cast(@lvl as varchar(10)));
                declare @sql nvarchar(200) = N'create table rr' + cast(@lvl as nvarchar(10)) + N' (z int)';
                if @lvl < 5 exec (@sql);
            end
            """,
            "create table seed (a int)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void SecondBodyThrow_RollsBackTheFirstBodysWrites()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table ddl_log (note nvarchar(50))",
            "create trigger tg_a on database for create_table as insert ddl_log values ('a')",
            "create trigger tg_b on database for create_table as throw 51000, 'veto', 1");
        _ = sim.AssertSqlError("create table t1 (a int)", 51000);
        AreEqual(0, sim.ExecuteScalar("select count(*) from ddl_log"));
    }

    [TestMethod]
    public void BodySelect_BecomesTheStatementsResultSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create trigger tg on database for create_table as select 42 as answer");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand("create table t1 (a int)");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(42, reader.GetInt32(0));
    }
}
