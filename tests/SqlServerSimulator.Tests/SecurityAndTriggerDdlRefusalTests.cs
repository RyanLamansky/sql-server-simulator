using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Refusals of principal, synonym and trigger-toggling DDL, and how far each
/// reaches (probed 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class SecurityAndTriggerDdlRefusalTests
{
    [TestMethod]
    [DataRow("alter user dbo with name = zz", "dbo")]
    [DataRow("alter user dbo with login = sa", "dbo")]
    [DataRow("alter user dbo with default_schema = dbo", "dbo")]
    [DataRow("alter user guest with name = zz", "guest")]
    public void AlterUser_OfDboOrGuest_IsMsg15150(string sql, string user) =>
        new Simulation().AssertSqlError(sql, 15150, $"Cannot alter the user '{user}'.");

    [TestMethod]
    public void CreateSynonym_InAMissingSchema_EndsOnlyTheStatement()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table log (a int)");
        _ = simulation.AssertSqlError("create synonym nosch.s for dbo.x; insert log values (1)", 2760);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    [DataRow("disable trigger nosuchtrg on database", 119)]
    [DataRow("enable trigger nosuchtrg on all server", 119)]
    [DataRow("disable trigger nosuchtrg on t", 119)]
    [DataRow("disable trigger nosuchtrg on nosuchtable", 21)]
    public void ToggleOfAMissingTrigger_EndsTheBatch(string toggle, int state)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int); create table log (a int)");
        var ex = simulation.AssertSqlError(toggle + "; insert log values (1)", 1088);
        AreEqual<byte>((byte)state, ex.Errors[0].State);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    public void ToggleOfAMissingTrigger_IsCaughtByTry() =>
        AreEqual(1088, new Simulation().ExecuteScalar("begin try disable trigger nosuchtrg on database end try begin catch select error_number() end catch"));
}
