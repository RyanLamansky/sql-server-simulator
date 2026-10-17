using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The provider's query timeout: the server's <c>query timeout</c> option, or
/// the instance's <c>remote query timeout (s)</c> while that is 0 (probed
/// 2026-10-17 against SQL Server 2025 through an MSOLEDBSQL loopback).
/// </summary>
[TestClass]
public sealed class LinkedServerTimeoutTests
{
    /// <summary>A loopback <c>SELF</c> whose provider gives up after a second.</summary>
    private static Simulation Loopback(string serverTimeout = "1")
    {
        var sim = new Simulation();
        sim.AddRemoteSimulation("SELF", sim);
        _ = sim.ExecuteNonQuery($"""
            create table t (id int primary key, v int); insert t values (1, 10), (2, 20);
            exec sp_addlinkedserver 'SELF', 'SQL Server';
            exec sp_serveroption 'SELF', 'query timeout', '{serverTimeout}'
            """);
        _ = sim.ExecuteNonQuery("create procedure slow @o int output as begin set @o = 7; print 'in proc'; waitfor delay '00:00:01.500' end");
        return sim;
    }

    private static List<string> CollectMessages(System.Data.Common.DbConnection connection)
    {
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SimulatedError>().Select(error => $"{error.Number}: {error.Message}"));
        return messages;
    }

    private const string TimeoutMessage = "7412: OLE DB provider \"MSOLEDBSQL19\" for linked server \"SELF\" returned message \"Query timeout expired\".";

    /// <summary>
    /// An <c>EXEC … AT</c> running past the timeout sends only the provider's
    /// message — the rows and messages the call produced ahead of it are
    /// discarded — and the batch goes on with <c>@@ERROR</c> 0 and its
    /// <c>@@ROWCOUNT</c> as it was; what the server's batch wrote by then
    /// stays.
    /// </summary>
    [TestMethod]
    public void ExecAt_PastTheTimeout_SendsOnlyTheProvidersMessage()
    {
        var sim = Loopback();
        using var connection = sim.CreateOpenConnection();
        var messages = CollectMessages(connection);
        AreEqual("0|2|3", connection.CreateCommand("""
            select id into #two from t;
            exec ('select 1 a; print ''mid''; insert simulated.dbo.t values (3, 30); waitfor delay ''00:00:01.500''; select 2 b') at SELF;
            select concat(@@error, '|', @@rowcount, '|', (select count(*) from t))
            """).ExecuteScalar());
        AreEqual(TimeoutMessage, string.Join(Environment.NewLine, messages));
    }

    /// <summary>
    /// A remote procedure call the provider gave up on leaves its OUTPUT
    /// variable as it was and its return code NULL, inside a <c>TRY</c> and
    /// under <c>XACT_ABORT</c> alike.
    /// </summary>
    [TestMethod]
    public void RemoteProcedureCall_PastTheTimeout_ReturnsNull()
    {
        using var connection = Loopback().CreateOpenConnection();
        var messages = CollectMessages(connection);
        AreEqual("5|-1|0", connection.CreateCommand("""
            set xact_abort on;
            declare @o int = 5, @rc int = 9;
            begin try exec @rc = SELF.simulated.dbo.slow @o output end try begin catch select 'caught' end catch;
            select concat(@o, '|', isnull(@rc, -1), '|', @@trancount)
            """).ExecuteScalar());
        AreEqual(TimeoutMessage, string.Join(Environment.NewLine, messages));
    }

    /// <summary>
    /// An <c>OPENQUERY</c> running past the timeout is the provider's Msg 7399
    /// and Msg 7320 after its message, ending the batch; a <c>TRY</c> catches
    /// Msg 7320.
    /// </summary>
    [TestMethod]
    public void OpenQuery_PastTheTimeout_EndsTheBatch()
    {
        using var connection = Loopback().CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("select * from openquery(SELF, 'waitfor delay ''00:00:01.500''; select 1 a'); select 2").ExecuteNonQuery());
        AreEqual("7399,7320,7412", string.Join(',', error.Errors.Cast<SimulatedError>().Select(entry => entry.Number)));
        AreEqual("Cannot execute the query \"waitfor delay '00:00:01.500'; select 1 a\" against OLE DB provider \"MSOLEDBSQL19\" for linked server \"SELF\". ", error.Errors[1].Message);
        AreEqual(7320, connection.CreateCommand("""
            declare @n int;
            begin try select @n = a from openquery(SELF, 'waitfor delay ''00:00:01.500''; select 1 a') end try
            begin catch set @n = error_number() end catch;
            select @n
            """).ExecuteScalar());
    }

    /// <summary>
    /// A four-part read or write waiting on another session's lock past the
    /// timeout ends the same way, an INSERT naming the table it couldn't open
    /// (Msg 7306), quoted twice over as real quotes it.
    /// </summary>
    [TestMethod]
    [DataRow("select count(*) from SELF.simulated.dbo.t", "7399,7320,7412")]
    [DataRow("update SELF.simulated.dbo.t set v = 5", "7399,7320,7412")]
    [DataRow("insert SELF.simulated.dbo.t values (1, 5)", "7399,7306,7412")]
    public void FourPartName_WaitingPastTheTimeout_EndsTheBatch(string statement, string numbers)
    {
        var sim = Loopback();
        using var holder = sim.CreateOpenConnection();
        _ = holder.CreateCommand("begin tran; update t with (tablockx) set v = v").ExecuteNonQuery();
        using var connection = sim.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand($"{statement}; select 2").ExecuteNonQuery());
        AreEqual(numbers, string.Join(',', error.Errors.Cast<SimulatedError>().Select(entry => entry.Number)));
        if (numbers.Contains("7306"))
            AreEqual("Cannot open the table \"\"simulated\".\"dbo\".\"t\"\" from OLE DB provider \"MSOLEDBSQL19\" for linked server \"SELF\". ", error.Errors[1].Message);
    }

    /// <summary>
    /// With the server's own option at 0 the instance's
    /// <c>remote query timeout (s)</c> applies, and a server's option outlasts
    /// it.
    /// </summary>
    [TestMethod]
    public void RemoteQueryTimeout_AppliesWhileTheServersOptionIsZero()
    {
        var sim = Loopback(serverTimeout: "0");
        _ = sim.ExecuteNonQuery("exec sp_configure 'remote query timeout', 1; reconfigure");
        using var connection = sim.CreateOpenConnection();
        var messages = CollectMessages(connection);
        AreEqual(0, connection.CreateCommand("exec ('waitfor delay ''00:00:01.500''; select 1 a') at SELF; select @@error").ExecuteScalar());
        AreEqual(TimeoutMessage, string.Join(Environment.NewLine, messages));
        _ = sim.ExecuteNonQuery("exec sp_serveroption 'SELF', 'query timeout', '5'");
        AreEqual(1, connection.CreateCommand("exec ('waitfor delay ''00:00:01.500''; select 1 a') at SELF").ExecuteScalar());
    }
}
