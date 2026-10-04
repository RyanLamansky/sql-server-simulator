using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>KILL &lt;session id&gt;</c> against the simulation's own sessions, every
/// refusal and effect probed 2026-09-30 against SQL Server 2025 except where a
/// test says otherwise.
/// </summary>
[TestClass]
public sealed class KillTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("kill 0", 6101)]
    [DataRow("kill -1", 6101)]
    [DataRow("kill 32768", 6101)]
    [DataRow("kill 2147483647", 6101)]
    [DataRow("kill 9999", 6106)]
    [DataRow("kill 32767", 6106)]
    [DataRow("kill 7", 6107)]
    [DataRow("kill 9999 with commit", 6108)]
    [DataRow("kill 32768 with rollback", 6108)]
    [DataRow("kill 'D0F1B6C4-0000-0000-0000-000000000000'", 6110)]
    [DataRow("kill 'abc'", 8169)]
    [DataRow("kill 1.5", 1080)]
    [DataRow("kill 2147483648", 1080)]
    [DataRow("kill", 102)]
    [DataRow("kill @@spid", 102)]
    [DataRow("declare @s int = 9999; kill @s", 102)]
    [DataRow("kill 9999 statusonly", 102)]
    [DataRow("kill 9999 with statusonly, commit", 102)]
    [DataRow("kill 1e3", 102)]
    [DataRow("kill null", 156)]
    public void Refusal_CarriesRealsNumber(string statement, int number)
        => _ = new Simulation().AssertSqlError(statement, number);

    [TestMethod]
    public void OwnSession_Is6104_WithOrWithoutStatusOnly()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        var spid = connection.CreateCommand("select @@spid").ExecuteScalar();
        foreach (var statement in new[] { $"kill {spid}", $"kill {spid} with statusonly" })
        {
            var error = Throws<SimulatedSqlException>(() => connection.CreateCommand(statement).ExecuteNonQuery());
            AreEqual(6104, error.Number);
        }
    }

    [TestMethod]
    public void OpenTransaction_Is6115_AheadOfTheTargetsOwnErrors()
    {
        var simulation = new Simulation();
        var error = simulation.AssertSqlError("begin tran; kill 9999", 6115);
        AreEqual("KILL command cannot be used inside user transactions.", error.Errors[0].Message);
        _ = simulation.AssertSqlError("begin tran; kill 32768", 6115);
        _ = simulation.AssertSqlError("begin tran; kill 'D0F1B6C4-0000-0000-0000-000000000000'", 6115);
        _ = simulation.AssertSqlError("begin tran; kill 'abc'", 8169);
        _ = simulation.AssertSqlError("begin tran; kill 9999 with commit", 6108);
    }

    [TestMethod]
    public void ErrorContinuesTheBatch_AndTryCatchSeesIt()
    {
        var simulation = new Simulation();
        AreEqual("6106|16|2", simulation.ExecuteScalar("""
            begin try kill 9999 end try
            begin catch select concat(error_number(), '|', error_severity(), '|', error_state()) end catch
            """));
        AreEqual(6106, simulation.AssertSqlError("exec('kill 9999'); select 'after'", 6106).Number);
    }

    [TestMethod]
    public void StatusOnly_OfALiveSession_Is6120()
    {
        var simulation = new Simulation();
        using var victim = simulation.CreateOpenConnection();
        var spid = victim.CreateCommand("select @@spid").ExecuteScalar();
        var error = simulation.AssertSqlError($"kill {spid} with statusonly", 6120);
        AreEqual($"Status report cannot be obtained. Rollback operation for Process ID {spid} is not in progress.", error.Errors[0].Message);
    }

    [TestMethod]
    public void IdleVictim_RollsBackAtOnce_LeavesTheSessionList_AndItsNextCommandFindsTheConnectionBroken()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int)");
        using var victim = simulation.CreateOpenConnection();
        var spid = victim.CreateCommand("select @@spid").ExecuteScalar();
        _ = victim.CreateCommand("begin tran; insert t values (1)").ExecuteNonQuery();

        _ = simulation.ExecuteNonQuery($"kill {spid}");

        // The killed session's locks are gone with its transaction, so this doesn't block.
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t"));
        AreEqual(0, simulation.ExecuteScalar($"select count(*) from sys.dm_exec_sessions where session_id = {spid}"));
        var broken = Throws<SimulatedSqlException>(() => victim.CreateCommand("select 1").ExecuteScalar());
        AreEqual(0, broken.Number);
        AreEqual(20, broken.Class);
        Contains("recovery is not possible", broken.Message);
        _ = Throws<InvalidOperationException>(() => victim.CreateCommand("select 1").ExecuteScalar());
        AreEqual(6106, simulation.AssertSqlError($"kill {spid}", 6106).Number);
    }

    [TestMethod]
    public void RunningVictim_AnswersMsg596_AndItsTransactionRollsBack()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int)");
        using var victim = simulation.CreateOpenConnection();
        var spid = victim.CreateCommand("select @@spid").ExecuteScalar();
        var running = Task.Run(() => victim.CreateCommand("begin tran; insert t values (1); waitfor delay '00:00:30'").ExecuteNonQuery(), TestContext.CancellationToken);

        IsTrue(SpinWait.SpinUntil(() => (int)simulation.ExecuteScalar($"select count(*) from sys.dm_exec_requests where session_id = {spid}")! == 1, TimeSpan.FromSeconds(10)));
        _ = simulation.ExecuteNonQuery($"kill {spid}");

        var error = Throws<SimulatedSqlException>(() => running.GetAwaiter().GetResult());
        AreEqual(596, error.Number);
        AreEqual(21, error.Class);
        AreEqual(2, error.Errors.Count);
        AreEqual(0, error.Errors[1].Number);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from t"));
        AreEqual(0, simulation.ExecuteScalar($"select count(*) from sys.dm_exec_sessions where session_id = {spid}"));
    }

    [TestMethod]
    public void RunningVictim_InTryCatch_IsNotCaught()
    {
        var simulation = new Simulation();
        using var victim = simulation.CreateOpenConnection();
        var spid = victim.CreateCommand("select @@spid").ExecuteScalar();
        var running = Task.Run(() => victim.CreateCommand("begin try waitfor delay '00:00:30' end try begin catch select 'caught' end catch").ExecuteScalar(), TestContext.CancellationToken);

        IsTrue(SpinWait.SpinUntil(() => (int)simulation.ExecuteScalar($"select count(*) from sys.dm_exec_requests where session_id = {spid}")! == 1, TimeSpan.FromSeconds(10)));
        _ = simulation.ExecuteNonQuery($"kill {spid}");

        AreEqual(596, Throws<SimulatedSqlException>(() => running.GetAwaiter().GetResult()).Number);
    }

    /// <summary>
    /// A login without ALTER ANY CONNECTION meets Msg 6102 for a session that
    /// exists — an id nothing holds still answers Msg 6106 first — and one
    /// holding it kills the session, sysadmin's included (probed 2026-09-30).
    /// </summary>
    [TestMethod]
    public void Permission_AlterAnyConnection_GatesTheKill()
    {
        var simulation = new Simulation();
        using var victim = simulation.CreateOpenConnection();
        var spid = victim.CreateCommand("select @@spid").ExecuteScalar();
        _ = simulation.ExecuteNonQuery("create login zk with password = 'S3cret!Pass', check_policy = off");

        using var killer = simulation.CreateDbConnection();
        killer.ConnectionString = "User ID=zk;Password=S3cret!Pass;Initial Catalog=master";
        killer.Open();
        AreEqual(6102, Throws<SimulatedSqlException>(() => killer.CreateCommand($"kill {spid}").ExecuteNonQuery()).Number);
        AreEqual(6102, Throws<SimulatedSqlException>(() => killer.CreateCommand($"kill {spid} with statusonly").ExecuteNonQuery()).Number);
        AreEqual(6106, Throws<SimulatedSqlException>(() => killer.CreateCommand("kill 9999").ExecuteNonQuery()).Number);

        _ = simulation.ExecuteNonQuery("use master; grant alter any connection to zk");
        _ = killer.CreateCommand($"kill {spid}").ExecuteNonQuery();
        AreEqual(0, Throws<SimulatedSqlException>(() => victim.CreateCommand("select 1").ExecuteScalar()).Number);
    }
}
