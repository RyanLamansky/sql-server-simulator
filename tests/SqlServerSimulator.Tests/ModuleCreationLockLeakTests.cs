using System.Data.Common;
using System.Diagnostics;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Creating and invoking a programmable object must leave no lock behind.
/// A body is parsed against a synthesized child <c>BatchContext</c>, which
/// takes Sch-S on everything the body names; that batch never reaches the
/// dispatch loop, so nothing releases those locks unless the inspection site
/// does it itself. A leak here is invisible until something later wants
/// Sch-M on the same object — a startup that re-applies its programmable
/// objects, say — and then blocks against a lock whose session no longer
/// exists.
/// </summary>
[TestClass]
public sealed class ModuleCreationLockLeakTests
{
    // CREATE FUNCTION has to open its own batch, so the seed runs as two.
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t_a (id int not null primary key, v int null);
            create table t_b (id int not null primary key, v int null);
            """);
        _ = sim.ExecuteNonQuery("create function dbo.f_leaf (@x int) returns int as begin return @x * 2 end");
        return sim;
    }

    private static int ResidualLocks(Simulation sim)
        => (int)sim.ExecuteScalar("select count(*) from sys.dm_tran_locks")!;

    // Each case creates one module over the same two tables (and, where a body
    // can call one, the leaf scalar function), then asserts nothing is held.
    [TestMethod]
    [DataRow("create view v_x as select a.id, a.v from t_a a join t_b b on b.id = a.id")]
    [DataRow("create function dbo.f_scalar (@x int) returns int as begin return (select count(*) from t_a) + dbo.f_leaf(@x) end")]
    [DataRow("create function dbo.f_inline (@x int) returns table as return (select a.id, dbo.f_leaf(a.v) as v from t_a a join t_b b on b.id = a.id)")]
    [DataRow("create function dbo.f_mstvf (@x int) returns @r table (id int) as begin insert @r select id from t_a; return end")]
    [DataRow("create procedure dbo.p_x as select a.id from t_a a join t_b b on b.id = a.id")]
    [DataRow("create trigger tr_x on t_a after insert as select count(*) from t_b")]
    public void CreatingAModule_LeavesNoLock(string create)
    {
        var sim = Seeded();
        AreEqual(0, ResidualLocks(sim));
        _ = sim.ExecuteNonQuery(create);
        AreEqual(0, ResidualLocks(sim));
    }

    [TestMethod]
    [DataRow("create view v_x as select id from t_a", "select * from v_x")]
    [DataRow("create function dbo.f_scalar (@x int) returns int as begin return (select count(*) from t_a) end", "select dbo.f_scalar(1)")]
    [DataRow("create function dbo.f_inline (@x int) returns table as return (select id from t_a where id > @x)", "select * from dbo.f_inline(0)")]
    [DataRow("create function dbo.f_mstvf (@x int) returns @r table (id int) as begin insert @r select id from t_a; return end", "select * from dbo.f_mstvf(0)")]
    [DataRow("create procedure dbo.p_x as select id from t_a", "exec dbo.p_x")]
    public void InvokingAModule_LeavesNoLock(string create, string invoke)
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery(create);
        using (var reader = sim.ExecuteReader(invoke))
        {
            while (reader.Read())
            {
            }
        }
        AreEqual(0, ResidualLocks(sim));
    }

    /// <summary>
    /// A body that fails its own validation must not leave the locks its
    /// partial parse already took — the failure path is the one that skips a
    /// release written after the work rather than in a finally.
    /// </summary>
    [TestMethod]
    public void ACreateThatFailsValidation_LeavesNoLock()
    {
        var sim = Seeded();
        // Msg 4514: an inline TVF's projection column has no name.
        _ = Throws<DbException>(() => sim.ExecuteNonQuery(
            "create function dbo.f_bad (@x int) returns table as return (select a.id + 1 from t_a a)"));
        AreEqual(0, ResidualLocks(sim));
    }

    /// <summary>
    /// The reported shape: the locks outlived the connection that took them,
    /// so a later one found them held by a session that no longer exists.
    /// </summary>
    [TestMethod]
    public void LocksDoNotOutliveTheCreatingConnection()
    {
        var sim = Seeded();
        using (var connection = sim.CreateOpenConnection())
        {
            _ = connection.CreateCommand(
                "create function dbo.f_inline (@x int) returns table as return (select id from t_a)").ExecuteNonQuery();
        }
        AreEqual(0, ResidualLocks(sim));
    }

    /// <summary>
    /// And the consequence that made it visible: with the lock leaked, a
    /// later ALTER wanting Sch-M on the same function blocked forever.
    /// </summary>
    [TestMethod]
    public void AlteringAfterCreate_IsNotBlockedByTheCreatesOwnLocks()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery(
            "create function dbo.f_inline (@x int) returns table as return (select a.id from t_a a join t_b b on b.id = a.id)");
        _ = sim.ExecuteNonQuery(
            "alter function dbo.f_inline (@x int) returns table as return (select a.id from t_a a)");
        AreEqual(0, ResidualLocks(sim));
    }
}

/// <summary>
/// A statement blocked on a lock has to notice its own
/// <c>CommandTimeout</c>. <c>SET LOCK_TIMEOUT</c> defaults to "wait
/// forever", so without this a blocked statement waits for the life of the
/// process — which is what turned a leaked schema lock from a nuisance into
/// a hang.
/// </summary>
/// <remarks>
/// The conflict is an <c>OBJECT X</c> held by an open transaction against an
/// insert wanting <c>IX</c> on the same resource. A schema lock would not do:
/// <c>Sch-M</c> and the data-family modes sit on <em>different</em> resources
/// (<c>SchemaLock</c> vs <c>TableDataLock</c>), so an <c>ALTER TABLE</c>
/// behind an open INSERT does not block at all.
/// </remarks>
[TestClass]
// Each case parks a connection on a held lock for its whole timeout, so the
// class blocks threads for a measurable time — the suite's rule is that such
// a test doesn't run alongside the lock-manager tests that assert on a
// deadline (see LockingTests' class comment).
[DoNotParallelize]
public sealed class LockWaitCommandTimeoutTests
{
    public TestContext TestContext { get; set; } = null!;

    private static DbConnection HolderOfTableX(Simulation sim)
    {
        var holder = sim.CreateOpenConnection();
        _ = holder.CreateCommand("create table t (id int not null primary key)").ExecuteNonQuery();
        _ = holder.CreateCommand("begin transaction").ExecuteNonQuery();
        using (var reader = holder.CreateCommand("select * from t with (tablockx)").ExecuteReader())
        {
            while (reader.Read())
            {
            }
        }
        return holder;
    }

    [TestMethod]
    public void BlockedStatement_HonoursCommandTimeout()
    {
        var sim = new Simulation();
        using var holder = HolderOfTableX(sim);

        using var blocked = sim.CreateOpenConnection();
        using var command = blocked.CreateCommand("insert t values (2)");
        command.CommandTimeout = 1;
        var started = Stopwatch.StartNew();
        var ex = Throws<DbException>(() => command.ExecuteNonQuery());
        started.Stop();

        // Msg -2 is SqlClient's timeout surface, the same split the command
        // layer already makes between a timeout and a caller's Cancel().
        AreEqual("-2", ex.Data["HelpLink.EvtID"]);
        // It has to have actually waited, and not run far past the deadline.
        IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(500), started.Elapsed);
        IsLessThan(TimeSpan.FromSeconds(20), started.Elapsed);

        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void BlockedStatement_StillReportsMsg1222WhenLockTimeoutIsShorter()
    {
        // SET LOCK_TIMEOUT is the session's own deadline and keeps its own
        // error; the command timeout only covers the case where nothing else
        // ever gives up.
        var sim = new Simulation();
        using var holder = HolderOfTableX(sim);

        using var blocked = sim.CreateOpenConnection();
        _ = blocked.CreateCommand("set lock_timeout 200").ExecuteNonQuery();
        using var command = blocked.CreateCommand("insert t values (2)");
        command.CommandTimeout = 30;
        var ex = Throws<DbException>(() => command.ExecuteNonQuery());
        AreEqual("1222", ex.Data["HelpLink.EvtID"]);

        _ = holder.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void UnblockedStatement_IsNotDelayedByThePoll()
    {
        // The slice only bounds how long a cancel goes unnoticed; a release
        // still Pulses, so a lock that frees promptly is granted promptly.
        var sim = new Simulation();
        using var holder = HolderOfTableX(sim);

        using var blocked = sim.CreateOpenConnection();
        using var command = blocked.CreateCommand("insert t values (2)");
        command.CommandTimeout = 30;
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(150, this.TestContext.CancellationToken);
                _ = holder.CreateCommand("commit").ExecuteNonQuery();
            },
            this.TestContext.CancellationToken);
        var started = Stopwatch.StartNew();
        _ = command.ExecuteNonQuery();
        started.Stop();
        release.GetAwaiter().GetResult();
        IsLessThan(TimeSpan.FromSeconds(5), started.Elapsed);
    }
}
