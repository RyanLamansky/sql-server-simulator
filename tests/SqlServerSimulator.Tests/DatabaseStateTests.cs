using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER DATABASE … SET { ONLINE | OFFLINE | EMERGENCY }</c>, probed
/// 2026-10-10 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DatabaseStateTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation WithDatabase(string state)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database zz");
        simulation.ExecuteBatches(
            "use zz; create table t (a int); insert t values (1)",
            "use zz; exec('create procedure p as select 7'); exec('create function f() returns int as begin return 3 end')",
            $"alter database zz set {state}");
        return simulation;
    }

    [TestMethod]
    [DataRow("offline", "6|OFFLINE|OFFLINE|READ_WRITE|0")]
    [DataRow("emergency", "5|EMERGENCY|EMERGENCY|READ_ONLY|1")]
    [DataRow("online", "0|ONLINE|ONLINE|READ_WRITE|1")]
    public void State_ShowsInTheCatalog(string state, string expected)
        => AreEqual(expected, WithDatabase(state).ExecuteScalar("""
            select concat_ws('|', state, state_desc collate database_default, cast(databasepropertyex('zz', 'Status') as nvarchar(60)),
                cast(databasepropertyex('zz', 'Updateability') as nvarchar(60)), has_dbaccess('zz'))
            from sys.databases where name = 'zz'
            """));

    [TestMethod]
    [DataRow("select count(*) from zz.dbo.t")]
    [DataRow("insert zz.dbo.t values (2)")]
    [DataRow("select count(*) from zz.sys.tables")]
    [DataRow("use zz")]
    [DataRow("exec('use zz')")]
    [DataRow("exec zz.dbo.p")]
    [DataRow("select zz.dbo.f()")]
    [DataRow("select object_id('zz.dbo.t')")]
    [DataRow("alter database zz modify name = zz2")]
    public void Offline_EveryReferenceRaises942(string sql)
        => WithDatabase("offline").AssertSqlError(sql, 942, "Database 'zz' cannot be opened because it is offline.");

    [TestMethod]
    public void Offline_942_EndsTheBatchPastTry_AndRollsBackTheTransaction()
    {
        var simulation = WithDatabase("offline");
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            begin tran
            begin try select count(*) from zz.dbo.t end try begin catch select 'caught' end catch
            select 'after'
            """).ExecuteNonQuery());
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void Offline_AnUntakenBranchNamingIt_Runs()
        => AreEqual(2, WithDatabase("offline").ExecuteScalar("if 1 = 0 select count(*) from zz.dbo.t; select 2"));

    [TestMethod]
    public void Offline_OptionsStillMove_AndOnlineRestoresIt()
        => AreEqual("SIMPLE|1", WithDatabase("offline").ExecuteScalar("""
            alter database zz set recovery simple;
            alter database zz set online;
            select concat(cast(databasepropertyex('zz', 'Recovery') as nvarchar(60)), '|', (select count(*) from zz.dbo.t))
            """));

    [TestMethod]
    public void Offline_ConnectionNamingIt_IsRefused4060()
    {
        var simulation = WithDatabase("offline");
        foreach (var connectionString in (string[])["Initial Catalog=zz", "User ID=sa;Password=x;Initial Catalog=zz"])
        {
            var connection = simulation.CreateDbConnection();
            connection.ConnectionString = connectionString;
            AreEqual(4060, Throws<SimulatedSqlException>(connection.Open).Number);
        }
    }

    [TestMethod]
    public void Offline_TheSessionsOwnDatabase_SwitchesItToMaster()
    {
        var simulation = WithDatabase("online");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Number}:{error.Message}"));
        connection.ChangeDatabase("zz");
        AreEqual("master", connection.CreateCommand("alter database zz set offline; select db_name()").ExecuteScalar());
        Contains("5068:Failed to restart the current database. The current database is switched to master.", messages);
    }

    [TestMethod]
    public void Offline_SpHelpDb_ReportsNoPermission()
    {
        using var connection = (SimulatedDbConnection)WithDatabase("offline").CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand("exec sp_helpdb 'zz'").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 15622 }, messages);
    }

    [TestMethod]
    public void Emergency_ReadsAndCallsRun()
        => AreEqual("1|7|3", WithDatabase("emergency").ExecuteScalar("""
            declare @p table (v int); insert @p exec zz.dbo.p;
            select concat_ws('|', (select count(*) from zz.dbo.t), (select v from @p), zz.dbo.f())
            """));

    [TestMethod]
    [DataRow("update zz.dbo.t set a = 2 where 1 = 0")]
    [DataRow("delete zz.dbo.t where a = 99")]
    public void Emergency_AWriteChangingNoRows_Runs(string sql)
        => AreEqual(0, WithDatabase("emergency").ExecuteNonQuery(sql));

    [TestMethod]
    public void Emergency_AWrite_Raises3908_EndingTheBatch_AndLeavesTheTransactionOpen()
    {
        using var connection = WithDatabase("emergency").CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            begin tran
            insert zz.dbo.t values (2)
            select 'after'
            """).ExecuteNonQuery());
        AreEqual(3908, error.Number);
        AreEqual("Could not run BEGIN TRANSACTION in database 'zz' because the database is in emergency mode or is damaged and must be restarted.", error.Errors[0].Message);
        AreEqual(3621, error.Errors[1].Number);
        AreEqual("1|1", connection.CreateCommand("select concat(@@trancount, '|', xact_state())").ExecuteScalar());
    }

    [TestMethod]
    public void Emergency_3908_IsCaught()
        => AreEqual("3908|1", WithDatabase("emergency").ExecuteScalar("""
            begin tran;
            begin try insert zz.dbo.t values (2) end try begin catch select concat(error_number(), '|', xact_state()) end catch
            """));

    [TestMethod]
    [DataRow("truncate table zz.dbo.t")]
    [DataRow("select * into zz.dbo.t2 from (select 1 a) x")]
    public void Emergency_TableWrites_Raise3908Alone(string sql)
    {
        var error = WithDatabase("emergency").AssertSqlError(sql, 3908);
        AreEqual(1, error.Errors.Count);
    }

    [TestMethod]
    public void StateChange_NoWait_WithAnotherSessionInIt_Raises5070()
    {
        var simulation = WithDatabase("online");
        using var other = simulation.CreateOpenConnection();
        other.ChangeDatabase("zz");
        var error = simulation.AssertSqlError("alter database zz set offline with no_wait", 5070);
        AreEqual("Database state cannot be changed while other users are using the database 'zz'", error.Errors[0].Message);
        AreEqual(5069, error.Errors[1].Number);
        AreEqual("ONLINE", simulation.ExecuteScalar("select state_desc from sys.databases where name = 'zz'"));
    }

    [TestMethod]
    public void Offline_RollbackImmediate_EndsEveryOtherSessionInIt()
    {
        var simulation = WithDatabase("online");
        using var other = simulation.CreateOpenConnection();
        other.ChangeDatabase("zz");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand("alter database zz set offline with rollback immediate").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 5060, 5060 }, messages);
        AreEqual(20, Throws<SimulatedSqlException>(() => other.CreateCommand("select 1").ExecuteScalar()).Class);
    }

    [TestMethod]
    public void Emergency_RollbackImmediate_EndsOnlyTheSessionsHoldingATransaction()
    {
        var simulation = WithDatabase("online");
        using var idle = simulation.CreateOpenConnection();
        idle.ChangeDatabase("zz");
        using var writing = simulation.CreateOpenConnection();
        writing.ChangeDatabase("zz");
        _ = writing.CreateCommand("begin tran; insert t values (5)").ExecuteNonQuery();
        _ = simulation.ExecuteNonQuery("alter database zz set emergency with rollback immediate");
        AreEqual(20, Throws<SimulatedSqlException>(() => writing.CreateCommand("select 1").ExecuteScalar()).Class);
        AreEqual("zz", idle.CreateCommand("select db_name()").ExecuteScalar());
        AreEqual(1, idle.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    [TestMethod]
    public async Task StateChange_WaitsForTheOtherSessionsToLeave()
    {
        var simulation = WithDatabase("online");
        using var other = simulation.CreateOpenConnection();
        other.ChangeDatabase("zz");
        var change = Task.Run(() => simulation.ExecuteNonQuery("alter database zz set offline"), TestContext.CancellationToken);
        await Task.Delay(200, TestContext.CancellationToken);
        IsFalse(change.IsCompleted);
        other.ChangeDatabase("master");
        _ = await change.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        AreEqual("OFFLINE", simulation.ExecuteScalar("select state_desc from sys.databases where name = 'zz'"));
    }

    [TestMethod]
    [DataRow("master", "OFFLINE")]
    [DataRow("tempdb", "EMERGENCY")]
    public void SystemDatabase_RefusesAState(string database, string state)
        => new Simulation().AssertSqlError($"alter database {database} set {state}", 5058, $"Option '{state}' cannot be set in database '{database}'.");
}
