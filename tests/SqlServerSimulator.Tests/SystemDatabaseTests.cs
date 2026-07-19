using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the four always-present system databases (master = 1, tempdb = 2,
/// model = 3, msdb = 4), seeded at construction so <c>USE</c>, three-part
/// reads, <c>DB_ID</c> / <c>DB_NAME</c>, <c>sys.databases</c>, and SSMS's
/// connect-time <c>has_dbaccess</c> / msdb catalog probes all resolve without
/// an import. User databases take ids from 5 in name order. A fresh connection
/// still lands on <c>simulated</c>, never a system database.
/// </summary>
[TestClass]
public sealed class SystemDatabaseTests
{
    [TestMethod]
    public void UseMaster_SwitchesCurrentDatabase()
        => AreEqual("master", new Simulation().ExecuteScalar("use master; select db_name()"));

    [TestMethod]
    public void UseTempdb_SwitchesCurrentDatabase()
        => AreEqual("tempdb", new Simulation().ExecuteScalar("use tempdb; select db_name()"));

    [TestMethod]
    public void UseModel_SwitchesCurrentDatabase()
        => AreEqual("model", new Simulation().ExecuteScalar("use model; select db_name()"));

    [TestMethod]
    public void UseMsdb_SwitchesCurrentDatabase()
        => AreEqual("msdb", new Simulation().ExecuteScalar("use msdb; select db_name()"));

    [TestMethod]
    public void UseMaster_ThenBackToDefault_Works()
        => AreEqual("simulated", new Simulation().ExecuteScalar("use master; use simulated; select db_name()"));

    [TestMethod]
    public void FreshConnection_LandsOnSimulated_NotASystemDatabase()
        => AreEqual("simulated", new Simulation().ExecuteScalar("select db_name()"));

    [TestMethod]
    public void SystemDatabases_HaveFixedIds()
    {
        var sim = new Simulation();
        AreEqual((short)1, sim.ExecuteScalar("select db_id('master')"));
        AreEqual((short)2, sim.ExecuteScalar("select db_id('tempdb')"));
        AreEqual((short)3, sim.ExecuteScalar("select db_id('model')"));
        AreEqual((short)4, sim.ExecuteScalar("select db_id('msdb')"));
    }

    [TestMethod]
    public void SimulatedUserDatabase_HasDatabaseIdFive()
        => AreEqual(5, new Simulation().ExecuteScalar("select database_id from sys.databases where name = 'simulated'"));

    [TestMethod]
    public void SysDatabases_ListsFourSystemDatabasesThenUserDatabase_AllOnline()
    {
        using var reader = new Simulation().ExecuteReader(
            "select name, database_id, state_desc from sys.databases order by database_id");
        List<(string Name, int Id, string State)> rows =
        [
            .. reader.EnumerateRecords().Select(r => (Name: r.GetString(0), Id: r.GetInt32(1), State: r.GetString(2))),
        ];
        HasCount(5, rows);
        AreEqual(("master", 1), (rows[0].Name, rows[0].Id));
        AreEqual(("tempdb", 2), (rows[1].Name, rows[1].Id));
        AreEqual(("model", 3), (rows[2].Name, rows[2].Id));
        AreEqual(("msdb", 4), (rows[3].Name, rows[3].Id));
        AreEqual(("simulated", 5), (rows[4].Name, rows[4].Id));
        IsTrue(rows.All(r => r.State == "ONLINE"));
    }

    [TestMethod]
    public void ThreePartRead_MasterSysDatabases_Resolves()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select database_id from master.sys.databases where name = 'master'"));

    [TestMethod]
    public void ThreePartRead_MasterSysDatabases_CountsAllDatabases()
        => AreEqual(5, new Simulation().ExecuteScalar("select count(*) from master.sys.databases"));

    [TestMethod]
    public void TempTable_StillRoutesThroughSession_NotTempdb()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "create table #t (id int); insert #t values (1); select count(*) from #t"));

    // === has_dbaccess: accessibility-aware, not existence-based ===

    [TestMethod]
    public void HasDbAccess_Master_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_dbaccess('master')"));

    [TestMethod]
    public void HasDbAccess_Tempdb_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_dbaccess('tempdb')"));

    // SSMS's Policy Health feature calls has_dbaccess('msdb') at connect; the
    // simulator seeds msdb, so it answers 1 and the feature renders.
    [TestMethod]
    public void HasDbAccess_Msdb_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_dbaccess('msdb')"));

    // model is the restricted template database — inaccessible even to a
    // normal login (probe-confirmed 2026-07-14). It exists (sys.databases /
    // DB_ID resolve it) but has_dbaccess reports 0.
    [TestMethod]
    public void HasDbAccess_Model_Returns0_DespiteBeingSeeded()
    {
        var sim = new Simulation();
        AreEqual((short)3, sim.ExecuteScalar("select db_id('model')"));
        AreEqual(0, sim.ExecuteScalar("select has_dbaccess('model')"));
    }

    [TestMethod]
    public void HasDbAccess_UserDatabase_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_dbaccess('simulated')"));

    [TestMethod]
    public void HasDbAccess_CaseInsensitive()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_dbaccess('MsDb')"));

    [TestMethod]
    public void HasDbAccess_VariableArgument_Resolves()
        => AreEqual(1, new Simulation().ExecuteScalar("declare @n sysname = 'master' select has_dbaccess(@n)"));

    [TestMethod]
    public void HasDbAccess_UnknownDatabase_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select has_dbaccess('no_such_db')"));

    [TestMethod]
    public void HasDbAccess_EmptyOrNullName_ReturnsNull()
    {
        var sim = new Simulation();
        AreEqual(DBNull.Value, sim.ExecuteScalar("select has_dbaccess('')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select has_dbaccess(null)"));
    }

    [TestMethod]
    public void HasDbAccess_NoArgument_RaisesMsg174()
    {
        var ex = new Simulation().AssertSqlError("select has_dbaccess()", 174);
        AreEqual("The has_dbaccess function requires 1 argument(s).", ex.Message);
    }

    // === msdb.dbo.syspolicy_system_health_state (SSMS Policy Health) ===

    [TestMethod]
    public void SyspolicyHealthState_UseMsdb_ReturnsNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "use msdb; select count(*) from dbo.syspolicy_system_health_state"));

    [TestMethod]
    public void SyspolicyHealthState_ThreePartRead_FromAnotherDatabase_ReturnsNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select count(*) from msdb.dbo.syspolicy_system_health_state"));

    [TestMethod]
    public void SyspolicyHealthState_HasSixColumnsInReferenceOrderAndTypes()
    {
        using var reader = new Simulation().ExecuteReader(
            "select * from msdb.dbo.syspolicy_system_health_state");
        AreEqual(6, reader.FieldCount);
        AreEqual("health_state_id", reader.GetName(0));
        AreEqual(typeof(long), reader.GetFieldType(0));
        AreEqual("policy_id", reader.GetName(1));
        AreEqual(typeof(int), reader.GetFieldType(1));
        AreEqual("last_run_date", reader.GetName(2));
        AreEqual(typeof(DateTime), reader.GetFieldType(2));
        AreEqual("target_query_expression_with_id", reader.GetName(3));
        AreEqual(typeof(string), reader.GetFieldType(3));
        AreEqual("target_query_expression", reader.GetName(4));
        AreEqual(typeof(string), reader.GetFieldType(4));
        AreEqual("result", reader.GetName(5));
        AreEqual(typeof(bool), reader.GetFieldType(5));
        IsFalse(reader.Read());
    }

    // The policy-health view lives only in msdb — it must not leak into other
    // databases' object namespace.
    [TestMethod]
    public void SyspolicyHealthState_DoesNotExistInUserDatabase()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select object_id('dbo.syspolicy_system_health_state')"));

    // === msdb.dbo.syspolicy_configuration (SSMS PolicyStore) ===

    [TestMethod]
    public void SyspolicyConfiguration_HasFourRows()
        => AreEqual(4, new Simulation().ExecuteScalar(
            "select count(*) from msdb.dbo.syspolicy_configuration"));

    [TestMethod]
    public void SyspolicyConfiguration_EnabledCurrentValue_IsOne()
        => AreEqual("1", new Simulation().ExecuteScalar(
            "select current_value from msdb.dbo.syspolicy_configuration where name = 'Enabled'"));

    [TestMethod]
    public void SyspolicyConfiguration_EnabledCurrentValue_CastsToBit()
        => IsTrue((bool)new Simulation().ExecuteScalar(
            "select cast((select current_value from msdb.dbo.syspolicy_configuration where name = 'Enabled') as bit)")!);

    // The three named integer rows SSMS reads, each through the exact
    // (SELECT current_value …) CAST expression the PolicyStore setup applies.
    [TestMethod]
    public void SyspolicyConfiguration_PolicyStoreCastExpressions_Resolve()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar(
            "select cast((select current_value from msdb.dbo.syspolicy_configuration where name = 'Enabled') as int)"));
        AreEqual(0, sim.ExecuteScalar(
            "select cast((select current_value from msdb.dbo.syspolicy_configuration where name = 'HistoryRetentionInDays') as int)"));
        IsFalse((bool)sim.ExecuteScalar(
            "select cast((select current_value from msdb.dbo.syspolicy_configuration where name = 'LogOnSuccess') as bit)")!);
    }

    [TestMethod]
    public void SyspolicyConfiguration_PurgeHistoryJobGuidRow_Present()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select count(*) from msdb.dbo.syspolicy_configuration where name = 'PurgeHistoryJobGuid'"));

    // === msdb.dbo.fn_syspolicy_is_automation_enabled (SSMS PolicyHealth) ===

    [TestMethod]
    public void SyspolicyAutomationEnabled_ReturnsOne()
        => IsTrue((bool)new Simulation().ExecuteScalar(
            "select msdb.dbo.fn_syspolicy_is_automation_enabled()")!);

    // The three-part call resolves from any current database, not just msdb.
    [TestMethod]
    public void SyspolicyAutomationEnabled_ResolvesFromUserDatabase()
        => IsTrue((bool)new Simulation().ExecuteScalar(
            "use simulated; select msdb.dbo.fn_syspolicy_is_automation_enabled()")!);

    // The exact SSMS PolicyHealth CASE expression: automation is enabled but
    // syspolicy_system_health_state is empty, so the result is 0.
    [TestMethod]
    public void SyspolicyPolicyHealth_CaseExpression_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "select case when 1 = msdb.dbo.fn_syspolicy_is_automation_enabled() " +
            "and exists (select * from msdb.dbo.syspolicy_system_health_state " +
            "where target_query_expression_with_id like 'Server%') then 1 else 0 end"));
}
