using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A table's object lock is one resource carrying Sch-S / Sch-M beside the
/// intent and table modes, as real's is: a redefinition queued behind an open
/// writer lets that writer's own statements through and holds newcomers off,
/// and a batch compiles under Sch-S on what it names, so a table another
/// session is redefining stops the batch before anything in it runs (probed
/// 2026-10-07 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ObjectLockTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Tables()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int); insert t values (1, 1), (2, 2); create table o (x int)");
        return sim;
    }

    private static string WaitOf(DbConnection observer, DbConnection waiter) =>
        (string)observer.CreateCommand($"""
            select concat(w.wait_type, ' ', iif(w.blocking_session_id = b.spid, 'behind', 'elsewhere'))
            from sys.dm_os_waiting_tasks w cross join (select session_id spid from sys.dm_os_waiting_tasks where wait_type = 'LCK_M_SCH_M') b
            where w.session_id = {waiter.CreateCommand("select @@spid").ExecuteScalar()}
            """).ExecuteScalar()!;

    /// <summary>
    /// The open writer's next statement reads on ahead of the redefinition
    /// waiting for it, where the redefinition's half-taken Sch-M once
    /// deadlocked it; a newcomer waits behind the redefinition in Sch-S.
    /// </summary>
    [TestMethod]
    [DataRow("alter table t add c int null")]
    [DataRow("truncate table t")]
    public async Task OpenWriter_ReadsOnAheadOfAQueuedRedefinition(string ddl)
    {
        var sim = Tables();
        using var writer = sim.CreateOpenConnection();
        using var redefiner = sim.CreateOpenConnection();
        using var newcomer = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();

        _ = writer.CreateCommand("begin tran; insert t values (3, 3)").ExecuteNonQuery();
        var redefined = await sim.StartBlocked(redefiner, ddl, TestContext.CancellationToken);
        AreEqual(3, writer.CreateCommand("select count(*) from t").ExecuteScalar());
        var read = await sim.StartBlocked(newcomer, "select count(*) from t", TestContext.CancellationToken);
        AreEqual("LCK_M_SCH_S behind", WaitOf(observer, newcomer));

        _ = writer.CreateCommand("commit").ExecuteNonQuery();
        _ = await redefined;
        AreEqual(ddl.StartsWith("truncate", StringComparison.Ordinal) ? 0 : 3, (await read).Single());
    }

    /// <summary>
    /// A batch naming a table another session holds in Sch-M waits as it
    /// compiles, so a lock timeout there ends the batch before its first
    /// statement runs, with nothing for a <c>TRY</c> in it to catch, and
    /// leaves an open transaction as it was — an untaken branch's statement
    /// compiles too. The simulator once took the lock statement by statement,
    /// running the statements ahead and handing the timeout to the
    /// <c>CATCH</c>.
    /// </summary>
    [TestMethod]
    [DataRow("insert o values (1); begin try select count(*) from t end try begin catch select 'caught' end catch", DisplayName = "TRY around the read")]
    [DataRow("insert o values (1); if 1 = 0 select count(*) from t", DisplayName = "Untaken branch")]
    public void BatchCompile_WaitsOnASchemaModification(string batch)
    {
        var sim = Tables();
        using var redefiner = sim.CreateOpenConnection();
        using var session = sim.CreateOpenConnection();

        _ = redefiner.CreateCommand("begin tran; alter table t add c int null").ExecuteNonQuery();
        _ = session.CreateCommand("set lock_timeout 100; begin tran; insert o values (5)").ExecuteNonQuery();
        var error = Throws<SimulatedSqlException>(() => session.CreateCommand(batch).ExecuteNonQuery());
        AreEqual((1222, 56), (error.Number, error.State));

        AreEqual("1 1 1", session.CreateCommand("select concat(count(*), ' ', @@trancount, ' ', xact_state()) from o").ExecuteScalar());
        _ = session.CreateCommand("rollback").ExecuteNonQuery();
        _ = redefiner.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// A batch compiled before runs on its kept plan, as real's cached plan
    /// does: the statement ahead runs, and the one reading the table times
    /// out where it stands.
    /// </summary>
    [TestMethod]
    public void CompiledBatch_WaitsStatementByStatement()
    {
        var sim = Tables();
        using var redefiner = sim.CreateOpenConnection();
        using var session = sim.CreateOpenConnection();
        const string Batch = "insert o values (1); select count(*) from t where id = 1";

        _ = session.CreateCommand(Batch).ExecuteNonQuery();
        _ = session.CreateCommand("delete o; set lock_timeout 100").ExecuteNonQuery();
        _ = redefiner.CreateCommand("begin tran; truncate table t").ExecuteNonQuery();
        AreEqual(1222, Throws<SimulatedSqlException>(() => session.CreateCommand(Batch).ExecuteNonQuery()).Number);

        AreEqual(1, session.CreateCommand("select count(*) from o").ExecuteScalar());
        _ = redefiner.CreateCommand("rollback").ExecuteNonQuery();
    }

    /// <summary>
    /// An index build takes the table's S, a clustered one its Sch-M, and a
    /// drop Sch-M: each waits out an open writer, and only Sch-M holds a
    /// newcomer's read off behind it.
    /// </summary>
    [TestMethod]
    [DataRow("create index ix on t (v)", "LCK_M_S", true)]
    [DataRow("create clustered index cx on o (x)", "LCK_M_SCH_M", false)]
    [DataRow("drop index ixv on t", "LCK_M_SCH_M", false)]
    public async Task IndexDdl_WaitsForAnOpenWriter(string ddl, string waitType, bool readerGoesOn)
    {
        var sim = Tables();
        _ = sim.ExecuteNonQuery("create index ixv on t (v); insert o values (1)");
        using var writer = sim.CreateOpenConnection();
        using var builder = sim.CreateOpenConnection();
        using var newcomer = sim.CreateOpenConnection();
        using var observer = sim.CreateOpenConnection();
        var table = ddl.Contains(" o ", StringComparison.Ordinal) ? "o" : "t";

        _ = writer.CreateCommand($"begin tran; update {table} set {(table == "o" ? "x" : "v")} = 9").ExecuteNonQuery();
        var built = await sim.StartBlocked(builder, ddl, TestContext.CancellationToken);
        AreEqual(waitType, observer.CreateCommand($"select wait_type from sys.dm_os_waiting_tasks where session_id = {builder.CreateCommand("select @@spid").ExecuteScalar()}").ExecuteScalar());
        if (readerGoesOn)
        {
            AreEqual(2, newcomer.CreateCommand("select count(*) from t with (nolock)").ExecuteScalar());
        }
        else
        {
            var read = await sim.StartBlocked(newcomer, $"select count(*) from {table} with (nolock)", TestContext.CancellationToken);
            _ = writer.CreateCommand("commit").ExecuteNonQuery();
            _ = await read;
        }
        _ = writer.CreateCommand("if @@trancount > 0 commit").ExecuteNonQuery();
        _ = await built;
    }

    /// <summary>
    /// A nonclustered index built in a transaction keeps its S to the
    /// transaction's end: a writer waits, a reader doesn't.
    /// </summary>
    [TestMethod]
    public async Task IndexBuiltInATransaction_HoldsWritersOff()
    {
        var sim = Tables();
        using var builder = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();

        _ = builder.CreateCommand("begin tran; create index ix on t (v)").ExecuteNonQuery();
        AreEqual(2, writer.CreateCommand("select count(*) from t").ExecuteScalar());
        var written = await sim.StartBlocked(writer, "insert t values (3, 3)", TestContext.CancellationToken);
        _ = builder.CreateCommand("commit").ExecuteNonQuery();
        _ = await written;
    }

    /// <summary>
    /// Reads walking a table's index and statistics lists while other sessions
    /// create and drop an index and a statistic on it never fail: each list is
    /// published whole, where a read of one changed in place once failed with
    /// "Collection was modified" (a randomized stress run's finding, an
    /// <c>UPDATE … WHERE id = (SELECT TOP (1) … ORDER BY …)</c> over a table
    /// with a nonclustered index).
    /// </summary>
    [TestMethod]
    public void IndexDdl_BesideOrderedReads_NeverFails()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table q (id int identity primary key, status int not null, prio int not null); create index ix_q on q (status, prio); insert q (status, prio) select value % 3, value % 17 from generate_series(1, 300)");
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var stop = DateTime.UtcNow.AddMilliseconds(500);
        var threads = Enumerable.Range(0, 5).Select(index => new Thread(() =>
        {
            using var connection = sim.CreateOpenConnection();
            for (var i = 0; DateTime.UtcNow < stop; i++)
            {
                try
                {
                    _ = connection.CreateCommand(index switch
                    {
                        0 => i % 2 == 0 ? "create index ix_tmp on q (prio, status)" : "drop index ix_tmp on q",
                        1 => i % 2 == 0 ? "create statistics st_tmp on q (prio)" : "drop statistics q.st_tmp",
                        _ => $"update q set prio = (prio + 7) % 17 where id = (select top (1) id from q where status = {i % 3} order by prio)",
                    }).ExecuteNonQuery();
                }
                catch (SimulatedSqlException)
                {
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        })).ToList();
        threads.ForEach(static thread => thread.Start());
        threads.ForEach(static thread => thread.Join());
        IsEmpty(failures);
    }
}
