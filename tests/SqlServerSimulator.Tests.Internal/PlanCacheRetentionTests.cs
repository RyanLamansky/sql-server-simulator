using System.Data.Common;
using System.Runtime.CompilerServices;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A plan-cache entry outlives the session that compiled it, so it must not
/// hold that session alive: an abandoned connection whose SELECT became a
/// cached plan is still finalized and reclaimed.
/// </summary>
[TestClass]
public sealed class PlanCacheRetentionTests
{
    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    // Not inlined, so no caller frame keeps the connection reachable.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SessionToken LeakCompilingConnection(Simulation simulation)
    {
#pragma warning disable CA2000 // Abandoning the connection undisposed is the scenario under test.
        var connection = simulation.CreateDbConnection();
#pragma warning restore CA2000
        connection.Open();
        _ = Scalar(connection, "BEGIN TRANSACTION; UPDATE dbo.t SET v = 99 WHERE id = 1");
        AreEqual(99, Scalar(connection, "SELECT v FROM dbo.t WHERE id = 1"));
        return connection.Session;
    }

    [TestMethod]
    public void CachedPlan_DoesNotKeepItsCompilingConnectionAlive()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateDbConnection();
        observer.Open();
        _ = Scalar(observer, "CREATE TABLE dbo.t (id int PRIMARY KEY, v int); INSERT dbo.t VALUES (1, 1)");

        var countBefore = simulation.PlanCacheCount;
        var session = LeakCompilingConnection(simulation);
        AreEqual(countBefore + 1, simulation.PlanCacheCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        AreEqual(1, simulation.ReclaimAbandonedSessions());
        IsTrue(session.Reclaimed);
        AreEqual(1, Scalar(observer, "SELECT v FROM dbo.t WHERE id = 1"));
    }
}
