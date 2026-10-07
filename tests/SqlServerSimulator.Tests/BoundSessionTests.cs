using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_getbindtoken</c> / <c>sp_bindsession</c>: sessions sharing one
/// transaction and one lock space, as SQL Server 2025 binds them (probed
/// 2026-10-07).
/// </summary>
[TestClass]
public class BoundSessionTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int); insert t values (1, 10), (2, 20)");
        return sim;
    }

    private static object? Scalar(DbConnection connection, string sql) => connection.CreateCommand(sql).ExecuteScalar();

    // Begins a transaction on `owner` that inserts row 3, and binds `bound` to it.
    private static string Bind(DbConnection owner, DbConnection bound)
    {
        var token = (string)Scalar(owner, "declare @t varchar(255); begin tran; exec sp_getbindtoken @t output; insert t values (3, 30); select @t")!;
        // The token's alphabet, '-' through 'l', holds no quote.
        _ = bound.CreateCommand($"exec sp_bindsession '{token}'").ExecuteNonQuery();
        return token;
    }

    // The informational messages a batch on `connection` reports.
    private static List<int> Messages(DbConnection connection, string sql)
    {
        List<int> numbers = [];
        void Collect(object? sender, SimulatedInfoMessageEventArgs e) => numbers.AddRange(e.Errors.Cast<SimulatedError>().Select(error => error.Number));
        ((SimulatedDbConnection)connection).InfoMessage += Collect;
        try
        {
            _ = connection.CreateCommand(sql).ExecuteNonQuery();
        }
        finally
        {
            ((SimulatedDbConnection)connection).InfoMessage -= Collect;
        }
        return numbers;
    }

    /// <summary>
    /// Another session's report reads each bound member's own nesting: the
    /// owner's two levels, and the member's own one plus the level it began.
    /// </summary>
    [TestMethod]
    public void OpenTransactionCount_IsEachMembersOwn()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        _ = owner.CreateCommand("begin tran").ExecuteNonQuery();
        _ = bound.CreateCommand("begin tran").ExecuteNonQuery();
        AreEqual("2,2", Scalar(observer, $"select string_agg(open_transaction_count, ',') within group (order by session_id) from sys.dm_exec_sessions where session_id in ({Scalar(owner, "select @@spid")}, {Scalar(bound, "select @@spid")})"));
        _ = owner.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void Token_IsTheTransactions_InRealsShape()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        var token = Bind(owner, bound);
        AreEqual(32, token.Length);
        AreEqual("5---", token[22..26]);
        AreEqual("--", token[30..]);
        IsTrue(token.All(static c => c is >= '-' and <= 'l'), token);
        AreEqual(token, Scalar(owner, "declare @t varchar(255); exec sp_getbindtoken @t output; select @t"));
        AreEqual(token, Scalar(bound, "declare @t varchar(255); exec sp_getbindtoken @t output; select @t"));
        _ = owner.CreateCommand("rollback; begin tran").ExecuteNonQuery();
        AreNotEqual(token, Scalar(owner, "declare @t varchar(255); exec sp_getbindtoken @t output; select @t"));
    }

    [TestMethod]
    public void BoundSession_SharesTheTransactionAndItsLocks()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        AreEqual("1|1|3", Scalar(bound, "select concat(@@trancount, '|', xact_state(), '|', (select count(*) from t))"));
        _ = bound.CreateCommand("update t set v = 31 where id = 3; insert t values (4, 40)").ExecuteNonQuery();
        AreEqual(71, Scalar(owner, "select sum(v) from t where id >= 3"));
        _ = owner.CreateCommand("update t set v = 41 where id = 4").ExecuteNonQuery();
        _ = other.CreateCommand("set lock_timeout 0").ExecuteNonQuery();
        var blocked = Throws<SimulatedSqlException>(() => Scalar(other, "select count(*) from t"));
        AreEqual(1222, blocked.Number);
        _ = owner.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(2, Scalar(other, "select count(*) from t"));
        CollectionAssert.AreEqual(new[] { 3926 }, Messages(bound, "select 1"));
        AreEqual(0, Scalar(bound, "select @@trancount"));
    }

    [TestMethod]
    public void EachSessionNestsOnItsOwnCount_AndEitherOutermostCommitCommits()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        AreEqual(2, Scalar(owner, "begin tran; select @@trancount"));
        AreEqual(1, Scalar(bound, "select @@trancount"));
        AreEqual(2, Scalar(bound, "begin tran; select @@trancount"));
        AreEqual(1, Scalar(owner, "commit; select @@trancount"));
        AreEqual(2, Scalar(bound, "select @@trancount"));
        AreEqual(0, Scalar(owner, "commit; select @@trancount"));
        CollectionAssert.AreEqual(new[] { 3926 }, Messages(bound, "select 1"));
        AreEqual(3, Scalar(bound, "select @@trancount + count(*) from t"));
        IsEmpty(Messages(bound, "select 1"));
    }

    [TestMethod]
    public void BoundSessionsCommit_EndsItForTheSessionThatBeganIt()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        _ = bound.CreateCommand("insert t values (4, 40); commit").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 3926 }, Messages(owner, "select 1"));
        AreEqual(0, Scalar(owner, "select @@trancount"));
        AreEqual(4, Scalar(sim.CreateOpenConnection(), "select count(*) from t"));
    }

    [TestMethod]
    public void BindingInsideATransaction_DefectsFromIt()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        var token = (string)Scalar(owner, "declare @t varchar(255); begin tran; exec sp_getbindtoken @t output; select @t")!;
        _ = bound.CreateCommand("begin tran; insert t values (5, 50)").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { 3924 }, Messages(bound, $"exec sp_bindsession '{token}'"));
        AreEqual("1|2", Scalar(bound, "select concat(@@trancount, '|', (select count(*) from t))"));
        _ = owner.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(2, Scalar(sim.CreateOpenConnection(), "select count(*) from t"));
    }

    [TestMethod]
    public void UnbindingLeavesTheTransactionToTheOthers()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        AreEqual(0, Scalar(owner, "exec sp_bindsession null; select @@trancount"));
        AreEqual("1|3", Scalar(bound, "insert t values (4, 40); select concat(@@trancount, '|', (select count(*) from t where id < 4))"));
        owner.Dispose();
        _ = bound.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(4, Scalar(sim.CreateOpenConnection(), "select count(*) from t"));
    }

    [TestMethod]
    public void TheSessionThatBeganItClosing_LeavesItToTheOthers()
    {
        var sim = Seeded();
        var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        owner.Dispose();
        AreEqual(1, Scalar(bound, "select @@trancount"));
        _ = bound.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(2, Scalar(bound, "select count(*) from t"));
        AreEqual(0, Scalar(bound, "select count(*) from sys.dm_tran_locks where resource_type <> 'DATABASE'"));
    }

    /// <summary>
    /// The locks the closing session took more than once pass to the others
    /// whole: the rows stay locked until the transaction ends, and then every
    /// lock goes.
    /// </summary>
    [TestMethod]
    public void TheSessionThatBeganItClosing_PassesItsRepeatedLocks()
    {
        var sim = Seeded();
        var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        _ = owner.CreateCommand("insert t values (5, 50); update t set v = 31 where id = 3").ExecuteNonQuery();
        owner.Dispose();
        AreEqual(1222, Throws<SimulatedSqlException>(() => other.CreateCommand("set lock_timeout 0; update t set v = 0 where id = 5").ExecuteNonQuery()).Number);
        _ = bound.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(2, Scalar(bound, "select count(*) from t"));
        AreEqual(0, Scalar(bound, "select count(*) from sys.dm_tran_locks where resource_type <> 'DATABASE'"));
    }

    [TestMethod]
    public async Task BoundSessionsRunningAtOnce_TakeTurnsInTheTransaction()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        static void Insert(DbConnection connection, int first)
        {
            for (var id = first; id < first + 200; id++)
                _ = connection.CreateCommand(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"insert t values ({id}, @@trancount)")).ExecuteNonQuery();
        }
        await Task.WhenAll(
            Task.Run(() => Insert(owner, 100), TestContext.CancellationToken),
            Task.Run(() => Insert(bound, 1000), TestContext.CancellationToken));
        // Each insert read its own session's count, 1, with its statement's own on top.
        AreEqual("403|400", Scalar(bound, "select concat(count(*), '|', sum(case when id < 100 then 0 when v = 2 then 1 end)) from t"));
        _ = owner.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(403, Scalar(sim.CreateOpenConnection(), "select count(*) from t"));
        AreEqual(0, Scalar(bound, "select @@trancount"));
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ABoundSessionsLockWait_IsReportedAsItsOwn()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        _ = other.CreateCommand("begin tran; update t set v = 11 where id = 1").ExecuteNonQuery();
        _ = Bind(owner, bound);
        var ownerSpid = (short)Scalar(owner, "select @@spid")!;
        var blocked = await sim.StartBlocked(bound, "select v from t where id = 1", TestContext.CancellationToken);
        AreEqual(0, Scalar(other, $"select count(*) from sys.dm_os_waiting_tasks where session_id = {ownerSpid}"));
        _ = other.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(10, (await blocked)[0]);
        _ = owner.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    [DataRow("exec sp_bindsession 'abc'", 3909, 1)]
    [DataRow("exec sp_bindsession '-------------------------------'", 3909, 1)]
    [DataRow("exec sp_bindsession '--------------------------------'", 3909, 3)]
    [DataRow("exec sp_bindsession '------------------------------------'", 3909, 3)]
    [DataRow("exec sp_bindsession 'zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz'", 3909, 2)]
    [DataRow("exec sp_bindsession 'aaaaaaaaaaaaaaaaaaaaaa5---aaaa--'", 3909, 2)]
    [DataRow("exec sp_bindsession N'aaaaaaaaaaaaaaaaaaaaaa5---aaaa--'", 257, 5)]
    [DataRow("exec sp_bindsession 1", 257, 5)]
    public void TokensThatAreNoTokens_AreRefused(string sql, int number, int state)
    {
        var error = new Simulation().AssertSqlError(sql, number);
        AreEqual(state, error.State);
        AreEqual("sp_bindsession", error.Procedure);
    }

    [TestMethod]
    public void AnEndedTransactionsToken_BindsNothing_OrIsMsg3922()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        var token = (string)Scalar(owner, "declare @t varchar(255); begin tran; exec sp_getbindtoken @t output; select @t")!;
        // The session that began it binding to its own token defects from it
        // first, which leaves nothing to bind to.
        var refused = Throws<SimulatedSqlException>(() => owner.CreateCommand($"exec sp_bindsession '{token}'").ExecuteNonQuery());
        AreEqual(3922, refused.Number);
        AreEqual(0, Scalar(owner, "select @@trancount"));
        AreEqual(0, Scalar(bound, $"exec sp_bindsession '{token}'; select @@trancount"));
        owner.Dispose();
        AreEqual(3922, Throws<SimulatedSqlException>(() => bound.CreateCommand($"exec sp_bindsession '{token}'").ExecuteNonQuery()).Number);
    }

    [TestMethod]
    public void SessionTransactionsDmv_ReportsEachSessionsPart()
    {
        var sim = Seeded();
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        _ = Bind(owner, bound);
        var ownerSpid = (short)Scalar(owner, "select @@spid")!;
        var boundSpid = (short)Scalar(bound, "select @@spid")!;
        AreEqual(
            $"{ownerSpid}:0:1:0|{boundSpid}:0:0:1",
            Scalar(observer, "select string_agg(concat(session_id, ':', enlist_count, ':', cast(is_local as int), ':', cast(is_bound as int)), '|') within group (order by session_id) from sys.dm_tran_session_transactions"));
        AreEqual(
            $"{ownerSpid}:1|{boundSpid}:0",
            Scalar(owner, "select string_agg(concat(session_id, ':', enlist_count), '|') within group (order by session_id) from sys.dm_tran_session_transactions"));
        AreEqual(1, Scalar(observer, "select count(*) from sys.dm_tran_active_transactions where transaction_type = 1"));
        _ = owner.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void LockDmv_ReportsTheLocksUnderTheSessionThatRanLast()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create database other");
        using var owner = sim.CreateOpenConnection();
        using var bound = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        _ = bound.CreateCommand("use other").ExecuteNonQuery();
        _ = Bind(owner, bound);
        var ownerSpid = (short)Scalar(owner, "select @@spid")!;
        var boundSpid = (short)Scalar(bound, "select @@spid")!;
        const string Holders = "select string_agg(concat(request_session_id, ' ', resource_type), ',') within group (order by resource_type) from sys.dm_tran_locks where resource_database_id = db_id('simulated') and resource_type <> 'DATABASE'";
        AreEqual($"{boundSpid} KEY,{boundSpid} OBJECT", Scalar(observer, Holders));
        _ = Scalar(owner, "select 1");
        AreEqual($"{ownerSpid} KEY,{ownerSpid} OBJECT", Scalar(observer, Holders));
        _ = Scalar(bound, "select 1");
        AreEqual($"{boundSpid} KEY,{boundSpid} OBJECT", Scalar(observer, Holders));
        // The bound session's database lock is the shared workspace's, listed
        // under the session that began the transaction.
        AreEqual(
            $"{ownerSpid} other,{ownerSpid} simulated",
            Scalar(observer, $"select string_agg(concat(request_session_id, ' ', db_name(resource_database_id)), ',') within group (order by db_name(resource_database_id)) from sys.dm_tran_locks where resource_type = 'DATABASE' and request_session_id in ({ownerSpid}, {boundSpid})"));
        _ = owner.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(
            $"{boundSpid} other",
            Scalar(observer, $"select string_agg(concat(request_session_id, ' ', db_name(resource_database_id)), ',') from sys.dm_tran_locks where resource_type = 'DATABASE' and request_session_id = {boundSpid}"));
    }
}
