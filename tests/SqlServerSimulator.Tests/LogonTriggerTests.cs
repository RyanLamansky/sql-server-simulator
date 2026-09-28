using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Logon triggers (<c>CREATE TRIGGER … ON ALL SERVER FOR LOGON</c>) firing on
/// an in-process <see cref="SimulatedDbConnection.Open"/>: the body's context,
/// its <c>EVENTDATA()</c>, and every way it refuses the login with Msg 17892.
/// Probed 2026-09-28 against SQL Server 2025; the wire side is in
/// <c>SqlServerSimulator.Tests.SqlClient</c>.
/// </summary>
[TestClass]
public sealed class LogonTriggerTests
{
    private const string LoginSetup = """
        create login probe_x with password = 'Pr0be!xx12345';
        create table dbo.logon_log (id int identity, s nvarchar(max));
        """;

    /// <summary>
    /// A simulation with the probe login, the <c>logon_log</c> table in the
    /// default database, and a logon trigger whose body — gated to the probe
    /// login, as a real server's must be — is <paramref name="body"/>.
    /// </summary>
    private static Simulation WithTrigger(string body, string options = "with execute as 'sa'")
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            LoginSetup,
            $"""
            create trigger tr_logon on all server {options} for logon as
            begin
                if original_login() = 'probe_x'
                begin
                    {body}
                end
            end
            """);
        return simulation;
    }

    private static DbConnection OpenProbe(Simulation simulation, string extra = "")
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=probe_x;Password=Pr0be!xx12345;Initial Catalog=master;Application Name=probe-app" + extra;
        connection.Open();
        return connection;
    }

    private static SimulatedSqlException AssertRefused(Simulation simulation, string extra = "")
    {
        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=probe_x;Password=Pr0be!xx12345;Initial Catalog=master" + extra;
        var ex = Throws<SimulatedSqlException>(connection.Open);
        AreEqual(17892, ex.Number);
        AreEqual(14, ex.Class);
        AreEqual(1, ex.State);
        AreEqual("Logon failed for login 'probe_x' due to trigger execution.", ex.Message);
        AreEqual(ConnectionState.Closed, connection.State);
        return ex;
    }

    private static string Log(Simulation simulation) =>
        (string)simulation.ExecuteScalar("select isnull(string_agg(s, ';') within group (order by id), '') from simulated.dbo.logon_log")!;

    private static string Insert(string expression) =>
        $"insert simulated.dbo.logon_log (s) select convert(nvarchar(max), {expression});";

    [TestMethod]
    public void Open_FiresTheTrigger_WithTheLogonEventData()
    {
        var simulation = WithTrigger(Insert("eventdata()"));
        using (OpenProbe(simulation))
        {
        }
        var document = Log(simulation);
        StartsWith("<EVENT_INSTANCE><EventType>LOGON</EventType><PostTime>", document);
        Contains("<ServerName>SIMULATED</ServerName><LoginName>probe_x</LoginName><LoginType>SQL Login</LoginType><SID>", document);
        EndsWith("<ClientHost>&lt;local machine&gt;</ClientHost><IsPooled>0</IsPooled></EVENT_INSTANCE>", document);
    }

    [TestMethod]
    public void Body_RunsInMaster_AsTheExecuteAsLogin_ReportingTheSession()
        => AreEqual(
            "probe_x|probe-app|master|dbo|sa|1|1|1|tempdb",
            FiredWith(
                Insert("concat(original_login(), '|', app_name(), '|', db_name(), '|', user_name(), '|', suser_sname(), '|', @@trancount - 1, '|', trigger_nestlevel(), '|', trigger_nestlevel(@@procid, 'AFTER', 'DDL'), '|', original_db_name())"),
                ";Initial Catalog=tempdb"));

    [TestMethod]
    public void Body_WithoutExecuteAs_RunsAsTheLoginsUserInMaster()
    {
        var simulation = WithTrigger("""
            declare @s nvarchar(400) = concat(user_name(), '|', suser_sname(), '|', db_name());
            if @s <> 'guest|probe_x|master' throw 50000, @s, 1;
            """, options: "");
        using var connection = OpenProbe(simulation);
        AreEqual(ConnectionState.Open, connection.State);
    }

    [TestMethod]
    public void BodyWithoutExecuteAs_CannotWriteWhereTheLoginCant_RefusesTheLogin()
        => _ = AssertRefused(WithTrigger(Insert("'x'"), options: ""));

    [TestMethod]
    public void Rollback_RefusesTheLogin_KeepingWhatTheBodyWroteAfter()
    {
        var simulation = WithTrigger(Insert("'before'") + " rollback; " + Insert("'after'"));
        _ = AssertRefused(simulation);
        AreEqual("after", Log(simulation));
    }

    [TestMethod]
    public void Throw_RefusesTheLogin_AndRollsTheBodyBack()
    {
        var simulation = WithTrigger(Insert("'written'") + " throw 50001, 'nope', 1;");
        _ = AssertRefused(simulation);
        AreEqual("", Log(simulation));
    }

    [TestMethod]
    public void SwallowedError_StillRefusesTheLogin()
        => _ = AssertRefused(WithTrigger("begin try declare @z int = 1/0; end try begin catch end catch"));

    [TestMethod]
    public void SwallowedError_AfterXactAbortOff_LeavesTheLoginStanding()
        => AreEqual("survived", FiredWith("set xact_abort off; begin try declare @z int = 1/0; end try begin catch end catch; " + Insert("'survived'")));

    [TestMethod]
    public void Raiserror_LeavesTheLoginStanding()
        => AreEqual("after", FiredWith("raiserror('r16', 16, 1); " + Insert("'after'")));

    [TestMethod]
    public void ResultSet_RefusesTheLogin()
        => _ = AssertRefused(WithTrigger("select 1 as x;"));

    [TestMethod]
    public void ResultSetFromDynamicSql_RefusesTheLogin()
        => _ = AssertRefused(WithTrigger("exec ('select 1');"));

    [TestMethod]
    public void TransactionLeftOpen_RefusesTheLogin()
        => _ = AssertRefused(WithTrigger("begin tran; " + Insert("'x'")));

    /// <summary>
    /// A <c>COMMIT</c> commits the unit the login runs in: <c>@@TRANCOUNT</c>
    /// reads 0 after it, what the body wrote before and after it stays, and
    /// nothing that follows refuses the login.
    /// </summary>
    [TestMethod]
    [DataRow("", "before:1:1;after:0:0")]
    [DataRow("commit;", "before:1:1;after:0:0")]
    [DataRow("rollback;", "before:1:1;after:0:0")]
    [DataRow("throw 50001, 'nope', 1;", "before:1:1;after:0:0")]
    [DataRow("raiserror('r16', 16, 1);", "before:1:1;after:0:0")]
    [DataRow("select 1 as x;", "before:1:1;after:0:0")]
    [DataRow("begin tran; insert simulated.dbo.logon_log (s) values ('open');", "before:1:1;after:0:0;open")]
    public void Commit_LetsTheLoginStand(string then, string expected)
        => AreEqual(expected, FiredWith(
            "declare @t int = @@trancount, @x int = xact_state(); "
            + Insert("concat('before:', @t, ':', @x)")
            + " commit; select @t = @@trancount, @x = xact_state(); "
            + Insert("concat('after:', @t, ':', @x)")
            + " " + then));

    [TestMethod]
    public void Print_ReachesNoClient()
    {
        var simulation = WithTrigger("print 'hello from logon';");
        var connection = simulation.CreateDbConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        connection.ConnectionString = "User ID=probe_x;Password=Pr0be!xx12345;Initial Catalog=master";
        connection.Open();
        connection.Dispose();
        IsEmpty(messages);
    }

    [TestMethod]
    public void Body_RunsUnderNocount_ItCannotTurnOff()
        => AreEqual("512", FiredWith("set nocount off; " + Insert("@@options & 512")));

    [TestMethod]
    public void SessionOptions_DoNotFollowTheSessionOut_ButSessionContextDoes()
    {
        var simulation = WithTrigger("set language Deutsch; exec sp_set_session_context 'k', 'v';");
        using var connection = OpenProbe(simulation);
        AreEqual("us_english|v", connection.CreateCommand("select concat(@@language, '|', cast(session_context(N'k') as nvarchar(10)))").ExecuteScalar());
    }

    [TestMethod]
    public void SessionsDmv_ReadsPreconnect_WhileTheTriggerRuns()
        => AreEqual("preconnect", FiredWith(Insert("(select status from sys.dm_exec_sessions where session_id = @@spid)")));

    [TestMethod]
    public void DisabledTrigger_DoesNotFire_UntilEnabled()
    {
        var simulation = WithTrigger(Insert("'fired'"));
        _ = simulation.ExecuteNonQuery("disable trigger tr_logon on all server");
        using (OpenProbe(simulation))
        {
        }
        AreEqual("", Log(simulation));
        _ = simulation.ExecuteNonQuery("enable trigger tr_logon on all server");
        using (OpenProbe(simulation))
        {
        }
        AreEqual("fired", Log(simulation));
    }

    [TestMethod]
    public void UngatedTrigger_FiresForEveryOpen()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table dbo.opens (n int)",
            "create trigger tr_count on all server for logon as insert simulated.dbo.opens values (1)");
        _ = simulation.ExecuteScalar("select 1");
        _ = simulation.ExecuteScalar("select 1");
        AreEqual(3, simulation.ExecuteScalar("select count(*) from dbo.opens"));
    }

    [TestMethod]
    public void SeveralTriggers_FireInCreationOrder_BetweenThePinnedEnds()
    {
        var simulation = WithTrigger(Insert("'A'"));
        simulation.ExecuteBatches(
            "create trigger tr_b on all server with execute as 'sa' for logon as if original_login() = 'probe_x' " + Insert("'B'"),
            "create trigger tr_c on all server with execute as 'sa' for logon as if original_login() = 'probe_x' " + Insert("'C'"),
            "exec sp_settriggerorder 'tr_c', 'First', 'LOGON', 'SERVER'");
        using (OpenProbe(simulation))
        {
        }
        AreEqual("C;A;B", Log(simulation));
    }

    /// <summary>Opens the probe login against a trigger running <paramref name="body"/> and returns what it logged.</summary>
    private static string FiredWith(string body, string extra = "")
    {
        var simulation = WithTrigger(body);
        using (OpenProbe(simulation, extra))
        {
        }
        return Log(simulation);
    }
}
