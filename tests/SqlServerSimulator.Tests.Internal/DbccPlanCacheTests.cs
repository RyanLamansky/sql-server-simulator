using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>DBCC FREEPROCCACHE</c> and <c>DBCC FREESYSTEMCACHE</c> empty the plan
/// cache and the token memo for the scope they name — which no SQL surface
/// observes, since the simulator exposes no cached plans.
/// </summary>
[TestClass]
public sealed class DbccPlanCacheTests
{
    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static (Simulation Simulation, DbConnection Connection) Cached()
    {
        var simulation = new Simulation();
        var connection = simulation.CreateDbConnection();
        connection.Open();
        _ = Scalar(connection, "CREATE TABLE dbo.t (id int PRIMARY KEY)");
        _ = Scalar(connection, "SELECT id FROM dbo.t");
        _ = Scalar(connection, "SELECT count(*) FROM dbo.t");
        AreEqual(2, simulation.PlanCacheCount);
        IsGreaterThan(0, simulation.TokenMemo.Count);
        return (simulation, connection);
    }

    [TestMethod]
    public void FreeProcCache_EmptiesThePlanCacheAndTokenMemo()
    {
        var (simulation, connection) = Cached();
        using (connection)
            _ = Scalar(connection, "DBCC FREEPROCCACHE WITH NO_INFOMSGS");
        AreEqual(0, simulation.PlanCacheCount);
        AreEqual(0, simulation.TokenMemo.Count);
    }

    [TestMethod]
    public void FreeProcCache_DefaultPoolEmptiesItAndInternalPoolDoesNot()
    {
        var (simulation, connection) = Cached();
        using (connection)
        {
            _ = Scalar(connection, "DBCC FREEPROCCACHE ('internal') WITH NO_INFOMSGS");
            AreEqual(2, simulation.PlanCacheCount);
            _ = Scalar(connection, "DBCC FREEPROCCACHE ('default') WITH NO_INFOMSGS");
        }
        AreEqual(0, simulation.PlanCacheCount);
    }

    [TestMethod]
    public void FreeProcCache_SqlHandleRemovesOnlyThatTextsPlan()
    {
        var (simulation, connection) = Cached();
        using (connection)
        {
            var handle = BuiltInResources.SqlHandleOf("SELECT id FROM dbo.t");
            _ = Scalar(connection, $"DBCC FREEPROCCACHE (0x{Convert.ToHexString(handle)}) WITH NO_INFOMSGS");
        }
        AreEqual(1, simulation.PlanCacheCount);
        IsGreaterThan(0, simulation.TokenMemo.Count);
    }

    [TestMethod]
    public void FreeSystemCache_AllAndSqlPlansEmptyIt()
    {
        foreach (var store in new[] { "ALL", "SQL Plans" })
        {
            var (simulation, connection) = Cached();
            using (connection)
            {
                _ = Scalar(connection, "DBCC FREESYSTEMCACHE ('Object Plans') WITH NO_INFOMSGS");
                AreEqual(2, simulation.PlanCacheCount);
                _ = Scalar(connection, $"DBCC FREESYSTEMCACHE ('{store}') WITH NO_INFOMSGS");
            }
            AreEqual(0, simulation.PlanCacheCount);
        }
    }

    [TestMethod]
    public void AfterAClear_TheCacheRefills()
    {
        var (simulation, connection) = Cached();
        using (connection)
        {
            _ = Scalar(connection, "DBCC FREEPROCCACHE WITH NO_INFOMSGS");
            _ = Scalar(connection, "SELECT id FROM dbo.t");
        }
        AreEqual(1, simulation.PlanCacheCount);
    }

    [TestMethod]
    public void ClearProcedureCache_RemovesOnlyTheDatabasesPlans()
    {
        var (simulation, connection) = Cached();
        using (connection)
        {
            _ = Scalar(connection, "CREATE DATABASE other");
            _ = Scalar(connection, "USE other; ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE");
            AreEqual(2, simulation.PlanCacheCount);
            _ = Scalar(connection, "USE simulated; ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE");
        }
        AreEqual(0, simulation.PlanCacheCount);
        IsGreaterThan(0, simulation.TokenMemo.Count);
    }
}
