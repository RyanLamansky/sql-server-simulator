using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How far a permission refusal reaches and whom it names. Probed 2026-09-27
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class PermissionErrorScopeTests
{
    /// <summary>A denied CREATE ends the batch and rolls back as under XACT_ABORT, and a TRY catches it.</summary>
    [TestMethod]
    public void ADeniedCreate_EndsTheBatchAndRollsBack()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u without login; create table t (a int); grant insert, select on t to u");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "execute as user = 'u'; begin tran; insert t values (1); create table t2 (a int); insert t values (2)";
        AreEqual(262, Throws<SimulatedSqlException>(() => command.ExecuteNonQuery()).Number);
        command.CommandText = "select concat(@@trancount, ':', (select count(*) from t))";
        AreEqual("0:0", command.ExecuteScalar());
    }

    [TestMethod]
    public void ADeniedCreateView_EndsTheCallingBatchToo()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u without login");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "create table log (a int); execute as user = 'u'; exec ('create view v as select 1 a'); revert; insert log values (1)";
        AreEqual(262, Throws<SimulatedSqlException>(() => command.ExecuteNonQuery()).Number);
        command.CommandText = "revert; select count(*) from log";
        AreEqual(0, command.ExecuteScalar());
    }

    [TestMethod]
    public void ExecuteAsALoginWithoutAUser_IsMsg916AndRollsBack()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login zz with password = 'Pw!12345678x'; create table t (a int)");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "begin tran; insert t values (1); execute as login = 'zz'; select 'after'";
        var error = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery()).Errors[0];
        AreEqual((916, (byte)4), (error.Number, error.State));
        command.CommandText = "select concat(@@trancount, ':', (select count(*) from t))";
        AreEqual("0:0", command.ExecuteScalar());
    }

    [TestMethod]
    public void ARollbackInADdlTrigger_IsMsg3609State2()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create trigger dt on database for create_table as rollback");
        AreEqual(2, sim.AssertSqlError("create table t (a int)", 3609).State);
    }

    [TestMethod]
    [DataRow("exec p", "p")]
    [DataRow("exec [P]", "P")]
    [DataRow("exec dbo.p", "dbo.p")]
    public void ADeniedExec_NamesTheProcedureAsWrittenAtLineOne(string call, string named)
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create user u without login", "create procedure p as select 1");
        var error = sim.AssertSqlError($"execute as user = 'u';\nselect 1;\n{call}", 229).Errors[0];
        AreEqual((named, 1), (error.Procedure, error.LineNumber));
    }
}
