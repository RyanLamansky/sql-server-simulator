using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET DEADLOCK_PRIORITY</c>: its accepted values, where it reads back, and
/// the victim a deadlock cycle picks by it. Every expectation probed
/// 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DeadlockPriorityTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Priority = "select deadlock_priority from sys.dm_exec_sessions where session_id = @@spid";

    [TestMethod]
    [DataRow("low", -5)]
    [DataRow("NORMAL", 0)]
    [DataRow("high", 5)]
    [DataRow("-7", -7)]
    [DataRow("10", 10)]
    [DataRow("'low'", -5)]
    public void Value_ReadsBack(string value, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"set deadlock_priority {value}; {Priority}"));

    [TestMethod]
    [DataRow("declare @p int = 3", 3)]
    [DataRow("declare @p varchar(10) = 'HIGH'", 5)]
    [DataRow("declare @p bigint = -2", -2)]
    [DataRow("declare @p int", 0)]
    public void Variable_ReadsBack(string declaration, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"set deadlock_priority high; {declaration}; set deadlock_priority @p; {Priority}"));

    [TestMethod]
    [DataRow("set deadlock_priority 11")]
    [DataRow("set deadlock_priority -11")]
    [DataRow("set deadlock_priority medium")]
    [DataRow("set deadlock_priority 2.5")]
    [DataRow("declare @p int = 20; set deadlock_priority @p")]
    [DataRow("declare @p varchar(10) = 'x'; set deadlock_priority @p")]
    public void InvalidValue_RaisesMsg2755(string batch)
        => new Simulation().AssertSqlError(batch, 2755, "SET DEADLOCK_PRIORITY option is invalid. Valid options are {HIGH | NORMAL | LOW | [-10 ... 10] of type integer}.");

    [TestMethod]
    public void DynamicSqlSet_Reverts()
        => AreEqual(0, new Simulation().ExecuteScalar($"exec('set deadlock_priority high'); {Priority}"));

    [TestMethod]
    [DataRow("normal", "normal", "B")]
    [DataRow("low", "high", "A")]
    [DataRow("high", "low", "B")]
    [DataRow("low", "normal", "A")]
    [DataRow("-5", "5", "A")]
    public async Task Deadlock_VictimIsTheLowerPriority_TheRequesterOnATie(string priorityA, string priorityB, string expectedVictim)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t1 (a int); create table t2 (a int); insert t1 values (1); insert t2 values (1)");
        using var connA = sim.CreateOpenConnection();
        using var connB = sim.CreateOpenConnection();
        _ = connA.CreateCommand($"set deadlock_priority {priorityA}; begin tran; update t1 set a = 2").ExecuteNonQuery();
        _ = connB.CreateCommand($"set deadlock_priority {priorityB}; begin tran; update t2 set a = 2").ExecuteNonQuery();

        static int? Run(DbConnection connection, string sql)
        {
            try
            {
                _ = connection.CreateCommand(sql).ExecuteNonQuery();
                return null;
            }
            catch (SimulatedSqlException ex)
            {
                return ex.Number;
            }
        }

        var taskA = Task.Run(() => Run(connA, "update t2 set a = 3"), TestContext.CancellationToken);
        // A must be waiting before B closes the cycle, so B is the requester.
        _ = await Extensions.PollUntil(
            () => sim.ExecuteScalar("select count(*) from sys.dm_exec_requests where blocking_session_id <> 0"),
            count => count is 1,
            TestContext.CancellationToken);
        var taskB = Task.Run(() => Run(connB, "update t1 set a = 3"), TestContext.CancellationToken);
        var results = await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        AreEqual(expectedVictim == "A" ? 1205 : null, results[0]);
        AreEqual(expectedVictim == "B" ? 1205 : null, results[1]);
    }
}
