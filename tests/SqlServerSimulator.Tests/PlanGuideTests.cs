using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_create_plan_guide</c>, <c>sp_control_plan_guide</c>,
/// <c>sys.plan_guides</c> and <c>sys.fn_validate_plan_guide</c>, with the
/// refusal to drop or alter a module a guide names. Shapes, numbers, states
/// and lines probed 2026-10-15 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class PlanGuideTests
{
    private static Simulation WithTable()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int, b varchar(10))",
            "create procedure dbo.p as select * from t where a = 1 option (maxdop 2)",
            "create trigger trg on t after insert as select * from t where a = 1");
        return simulation;
    }

    private static string Guides(Simulation simulation) => simulation.ExecuteScalar("""
        select string_agg(concat(plan_guide_id, ':', name, ':', scope_type_desc, ':', object_name(scope_object_id), ':', scope_batch, ':', parameters, ':', hints, ':', is_disabled), '|')
            within group (order by plan_guide_id)
        from sys.plan_guides
        """).ToInvariantString();

    [TestMethod]
    public void CreatePlanGuide_StoresEachScopeUnderRealsIds()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("""
            exec sp_create_plan_guide N'g1', N'select * from t where a = 1', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1)';
            exec sp_create_plan_guide N'g2', N'select * from t where a = 1 option (maxdop 2)', N'object', N'dbo.p', NULL, N'OPTION (RECOMPILE)';
            exec sp_create_plan_guide N'g3', N'select * from t where a = @0', N'TEMPLATE', NULL, N'@0 int', N'OPTION(PARAMETERIZATION FORCED)';
            exec sp_create_plan_guide @name = N'g4', @stmt = N'select * from t where a = 1', @type = N'SQL', @module_or_batch = N'select 2; select * from t where a = 1';
            """);
        AreEqual(
            "65537:g1:SQL::select * from t where a = 1::OPTION (MAXDOP 1):0"
            + "|65538:g2:OBJECT:p:::OPTION (RECOMPILE):0"
            + "|65540:g3:TEMPLATE::select * from t where a = @0:@0 int:OPTION(PARAMETERIZATION FORCED):0"
            + "|65542:g4:SQL::select 2; select * from t where a = 1:::0",
            Guides(simulation));
    }

    [TestMethod]
    public void CreatePlanGuide_AnObjectGuideStepsTheIdByOne()
        => AreEqual(65536, WithTable().ExecuteScalar("""
            exec sp_create_plan_guide N'o1', N'select * from t where a = 1 option (maxdop 2)', N'OBJECT', N'dbo.p', NULL, NULL;
            select plan_guide_id from sys.plan_guides
            """));

    [TestMethod]
    public void CreatePlanGuide_MatchesAModuleStatementWhateverItsCase()
        => AreEqual("h4", WithTable().ExecuteScalar("""
            exec sp_create_plan_guide N'h4', N'SELECT * from t where a = 1 option (maxdop 2);', N'OBJECT', N'p', NULL, NULL;
            select name from sys.plan_guides
            """));

    [TestMethod]
    public void CreatePlanGuide_ParameterCheckingRefusalsCarryRealsStatesAndLines()
    {
        var simulation = WithTable();
        void Refused(string call, int number, byte state, int line, string procedure)
        {
            var error = simulation.AssertSqlError(call, number).Errors[0];
            AreEqual((state, line, procedure), (error.State, error.LineNumber, error.Procedure), call);
        }
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 1', N'BOGUS'", 10501, 1, 20, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'OBJECT'", 10521, 1, 15, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'OBJECT', N'dbo.p', N'@x int'", 10520, 3, 15, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'TEMPLATE', N'b', N'@0 int'", 10520, 5, 20, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'TEMPLATE'", 10521, 3, 20, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', NULL, N'SQL'", 10521, 4, 20, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'#x', N'select * from t', N'SQL'", 10528, 1, 20, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'OBJECT', N'd.dbo.p'", 10505, 2, 15, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'OBJECT', N'#tmp'", 10527, 1, 15, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'OBJECT', N'dbo.t'", 10515, 1, 15, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide NULL, N'select * from t', N'SQL'", 214, 100, 20, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'', N'select * from t', N'SQL'", 1038, 4, 20, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t'", 201, 4, 0, "sp_create_plan_guide");
        Refused("exec sp_create_plan_guide N'x', N'select * from t', N'SQL', NULL, NULL, NULL, 7", 8144, 2, 0, "sp_create_plan_guide");
    }

    [TestMethod]
    public void CreatePlanGuide_StatementRefusalsComeFromTheCompile()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("exec sp_create_plan_guide N'g1', N'select * from t where a = 1', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1)'");
        void Refused(string call, params int[] numbers)
        {
            var error = simulation.AssertSqlError(call, numbers[0]);
            CollectionAssert.AreEqual(numbers, error.Errors.Select(static entry => entry.Number).ToArray(), call);
        }
        Refused("exec sp_create_plan_guide N'x', N'select 1', N'SQL', NULL, NULL, NULL", 10519);
        Refused("exec sp_create_plan_guide N'x', N'exec dbo.p', N'SQL', NULL, NULL, NULL", 10509);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 9; select 2', N'SQL'", 10506);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 2', N'SQL', N'select * from t where a = 3'", 10507);
        Refused("exec sp_create_plan_guide N'x', N'select * from t  where a = 1', N'OBJECT', N'dbo.p'", 10507);
        Refused("exec sp_create_plan_guide N'x', N'select nosuch from t', N'SQL'", 207, 10516);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = @x', N'SQL'", 137, 10516);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 7', N'SQL', NULL, N'bogus'", 102, 10516);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 2', N'SQL', NULL, NULL, N'MAXDOP 1'", 102, 10504);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 2', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1);'", 102, 10504);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 2', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1, MAXDOP 2)'", 1042, 10504);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 2', N'SQL', NULL, NULL, N''", 10504);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = 1', N'SQL', NULL, NULL, NULL", 10502);
        Refused("exec sp_create_plan_guide N'G1', N'select * from t where a = 5', N'SQL'", 10510);
        Refused("exec sp_create_plan_guide N'x', N'select * from t where a = @0', N'TEMPLATE', NULL, N'@0 int', NULL", 10522);
    }

    [TestMethod]
    public void CreatePlanGuide_AModuleStatementRefusalIsTheModulesOwn()
    {
        var error = WithTable().AssertSqlError("exec sp_create_plan_guide N'x', N'select * from t where a = 22', N'OBJECT', N'dbo.p'", 10507).Errors[0];
        AreEqual(("dbo.p", 1), (error.Procedure, error.LineNumber));
    }

    [TestMethod]
    public void CreatePlanGuide_AGuideOnlyItsParametersSetApartIsAnotherGuide()
        => AreEqual(2, WithTable().ExecuteScalar("""
            exec sp_create_plan_guide N'a', N'select * from t where a = 7', N'SQL', NULL, N'@0 int', NULL;
            exec sp_create_plan_guide N'b', N'select * from t where a = 7', N'SQL', NULL, NULL, NULL;
            select count(*) from sys.plan_guides
            """));

    [TestMethod]
    public void CreatePlanGuide_AnEmptyStatementIsTakenAsItStands()
        => AreEqual(1, WithTable().ExecuteScalar("exec sp_create_plan_guide N'e', N'', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1)'; select count(*) from sys.plan_guides where query_text = ''"));

    [TestMethod]
    public void CreatePlanGuide_ARefusalEndsTheBatchAndRollsTheTransactionBack()
    {
        var simulation = WithTable();
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; exec sp_create_plan_guide N'x', N'select 1', N'SQL'; select 1").ExecuteNonQuery());
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void CreatePlanGuide_ACaughtRefusalLeavesItsOwnTransactionDoomed()
        => AreEqual("10519:1:-1", WithTable().ExecuteScalar("""
            declare @r varchar(30);
            begin try exec sp_create_plan_guide N'x', N'select 1', N'SQL'; end try
            begin catch set @r = concat(error_number(), ':', @@trancount, ':', xact_state()); end catch
            rollback;
            select @r
            """));

    [TestMethod]
    public void CreatePlanGuide_RollsBackWithTheTransaction()
        => AreEqual(0, WithTable().ExecuteScalar("""
            begin tran;
            exec sp_create_plan_guide N'x', N'select * from t where a = 101', N'SQL';
            rollback;
            select count(*) from sys.plan_guides
            """));

    [TestMethod]
    public void ControlPlanGuide_DisablesEnablesAndDrops()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("""
            exec sp_create_plan_guide N'g1', N'select * from t where a = 1', N'SQL';
            exec sp_create_plan_guide N'g2', N'select * from t where a = 2', N'SQL';
            exec sp_control_plan_guide N'DISABLE', N'g1';
            """);
        AreEqual("g1:1|g2:0", simulation.ExecuteScalar("select string_agg(concat(name, ':', is_disabled), '|') within group (order by name) from sys.plan_guides"));
        _ = simulation.ExecuteNonQuery("exec sp_control_plan_guide N'disable all '; exec sp_control_plan_guide N'ENABLE', N'G2'");
        AreEqual("g1:1|g2:0", simulation.ExecuteScalar("select string_agg(concat(name, ':', is_disabled), '|') within group (order by name) from sys.plan_guides"));
        _ = simulation.ExecuteNonQuery("exec sp_control_plan_guide N'DROP', N'[g1]'");
        AreEqual("g2", simulation.ExecuteScalar("select string_agg(name, '|') from sys.plan_guides"));
        _ = simulation.ExecuteNonQuery("exec sp_control_plan_guide N'DROP ALL', N'ignored'");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.plan_guides"));
    }

    [TestMethod]
    public void ControlPlanGuide_RefusalsComeFromTheInternalProcedure()
    {
        var simulation = WithTable();
        void Refused(string call, int number, byte state)
        {
            var error = simulation.AssertSqlError(call, number).Errors[0];
            AreEqual((state, 1, "sys.sp_control_plan_guide_int"), (error.State, error.LineNumber, error.Procedure), call);
        }
        Refused("exec sp_control_plan_guide N'BOGUS', N'g'", 10503, 1);
        Refused("exec sp_control_plan_guide N'DROP', N'nosuch'", 10508, 1);
        Refused("exec sp_control_plan_guide N'ENABLE', N'nosuch'", 10508, 2);
        Refused("exec sp_control_plan_guide N'DISABLE', N'nosuch'", 10508, 3);
        Refused("exec sp_control_plan_guide NULL, N'g'", 214, 2);
        Refused("exec sp_control_plan_guide N'DROP'", 214, 2);
    }

    [TestMethod]
    public void ControlPlanGuide_ADisableRollsBackWithTheTransaction()
        => IsFalse(WithTable().ExecuteScalar<bool>("""
            exec sp_create_plan_guide N'g1', N'select * from t where a = 1', N'SQL';
            begin tran;
            exec sp_control_plan_guide N'DISABLE', N'g1';
            rollback;
            select is_disabled from sys.plan_guides
            """));

    [TestMethod]
    public void ValidatePlanGuide_ReportsWhatTheGuidesCompileNowMeets()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("""
            exec sp_create_plan_guide N'ok', N'select * from t where a = 1', N'SQL';
            exec sp_create_plan_guide N'missing', N'select * from nosuch where a = 13', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1)';
            """);
        AreEqual("missing:208:16:1", simulation.ExecuteScalar("""
            select string_agg(concat(pg.name, ':', v.msgnum, ':', v.severity, ':', v.state), '|')
            from sys.plan_guides pg cross apply sys.fn_validate_plan_guide(pg.plan_guide_id) v
            """));
        _ = simulation.AssertSqlError("select * from sys.fn_validate_plan_guide(1)", 10538);
    }

    [TestMethod]
    public void DropProcedure_ReferencedByAPlanGuideIsRefused()
        => WithTable().AssertSqlError(
            "exec sp_create_plan_guide N'g2', N'select * from t where a = 1 option (maxdop 2)', N'OBJECT', N'dbo.p'; drop procedure dbo.p",
            10513,
            "Cannot drop procedure 'dbo.p' because it is referenced by plan guide 'g2'. Use sp_control_plan_guide to drop the plan guide first. Record the plan guide definition for future use if needed.");

    [TestMethod]
    public void AlterProcedure_ReferencedByAPlanGuideIsRefused()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("exec sp_create_plan_guide N'g2', N'select * from t where a = 1 option (maxdop 2)', N'OBJECT', N'dbo.p'");
        AreEqual(2, simulation.AssertSqlError("alter procedure dbo.p as select 3", 10513).Errors[0].State);
    }

    [TestMethod]
    public void DropTable_WhoseTriggerAPlanGuideNamesIsRefused()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("exec sp_create_plan_guide N'g9', N'select * from t where a = 1', N'OBJECT', N'dbo.trg'");
        simulation.AssertSqlError("drop trigger trg", 10513, "Cannot drop trigger 'trg' because it is referenced by plan guide 'g9'. Use sp_control_plan_guide to drop the plan guide first. Record the plan guide definition for future use if needed.");
        simulation.AssertSqlError("drop table t", 10526, "Cannot drop table 't' because its trigger 'trg' is referenced by plan guide 'g9'. Use sp_control_plan_guide to drop the plan guide first. Record the plan guide definition for future use if needed.");
    }

    [TestMethod]
    public void PlanGuideEvents_ReachADatabaseDdlTrigger()
    {
        var simulation = WithTable();
        simulation.ExecuteBatches(
            "create table ev (e xml)",
            "create trigger dt on database for DDL_PLAN_GUIDE_EVENTS as insert ev select eventdata()");
        _ = simulation.ExecuteNonQuery("""
            exec sp_create_plan_guide N'g1', N'select * from t where a = 1', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1)';
            exec sp_control_plan_guide N'DISABLE', N'g1';
            exec sp_control_plan_guide N'DROP ALL';
            """);
        AreEqual(
            "CREATE_PLAN_GUIDE:g1:OPTION (MAXDOP 1)|ALTER_PLAN_GUIDE:g1:g1|DROP_PLAN_GUIDE::",
            simulation.ExecuteScalar("""
                select string_agg(concat(e.value('(/EVENT_INSTANCE/EventType)[1]', 'sysname'), ':', e.value('(/EVENT_INSTANCE/ObjectName)[1]', 'sysname'), ':',
                    e.value('(/EVENT_INSTANCE/Parameters/Param)[last()]', 'nvarchar(100)')), '|')
                from ev
                """));
    }

    [TestMethod]
    public void PlanGuides_SurviveBackupAndRestore()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "use d; create table t (a int)",
            "use d; exec sp_create_plan_guide N'g1', N'select * from t where a = 1', N'SQL', NULL, NULL, N'OPTION (MAXDOP 1)'; exec sp_control_plan_guide N'DISABLE', N'g1'");
        _ = simulation.ExecuteNonQuery("backup database d to disk = N'd.bak'; use d; exec sp_control_plan_guide N'DROP ALL'");
        _ = simulation.ExecuteNonQuery("restore database d from disk = N'd.bak' with replace");
        AreEqual("65537:g1:1:OPTION (MAXDOP 1)", simulation.ExecuteScalar("use d; select concat(plan_guide_id, ':', name, ':', is_disabled, ':', hints) from sys.plan_guides"));
    }

    [TestMethod]
    public void PlanGuides_NeedAlterOnTheDatabaseAndViewDefinitionToList()
    {
        var simulation = WithTable();
        _ = simulation.ExecuteNonQuery("exec sp_create_plan_guide N'g1', N'select * from t where a = 1', N'SQL'; create user u without login");
        _ = simulation.AssertSqlError("execute as user = 'u'; exec sp_create_plan_guide N'x', N'select * from t where a = 2', N'SQL'", 10517);
        _ = simulation.AssertSqlError("execute as user = 'u'; exec sp_control_plan_guide N'DISABLE', N'g1'", 10518);
        AreEqual(0, simulation.ExecuteScalar("execute as user = 'u'; select count(*) from sys.plan_guides"));
        AreEqual(1, simulation.ExecuteScalar("grant view definition to u; execute as user = 'u'; select count(*) from sys.plan_guides"));
    }

    [TestMethod]
    public void PlanGuideProcedures_ListAmongTheSystemObjects()
        => AreEqual("fn_validate_plan_guide:IF|sp_control_plan_guide:P |sp_create_plan_guide:P |sp_get_query_template:X ", new Simulation().ExecuteScalar("""
            select string_agg(concat(name, ':', type) collate database_default, '|') within group (order by name) from sys.all_objects
            where name in ('fn_validate_plan_guide', 'sp_control_plan_guide', 'sp_create_plan_guide', 'sp_get_query_template')
            """));
}
