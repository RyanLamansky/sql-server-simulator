using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which way <c>RESTORE DATABASE</c> goes, which SQL can't see: in place over
/// a database whose definitions are still the backup's, rebuilt otherwise.
/// </summary>
[TestClass]
public sealed class RestoreInPlaceTests
{
    private static Simulation Backed()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        foreach (var sql in new[] { "create database d", "use d; create table t (id int primary key); insert t values (1)", "backup database d to disk = N'd.bak'" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            _ = command.ExecuteNonQuery();
        }
        return simulation;
    }

    private static void Run(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    [TestMethod]
    public void UnchangedDefinitions_RestoreInPlace_KeepingTheObjects()
    {
        var simulation = Backed();
        var database = simulation.Databases["d"];
        var table = database.Schemas["dbo"].HeapTables["t"];
        Run(simulation, "use d; insert t values (2); create statistics s on t (id)");
        Run(simulation, "restore database d from disk = N'd.bak' with replace");
        AreSame(database, simulation.Databases["d"]);
        AreSame(table, database.Schemas["dbo"].HeapTables["t"]);
        AreEqual(1, table.Heap.RowCount);
    }

    [TestMethod]
    public void SessionStillUsingTheDatabase_AfterTheWait_IsMsg3101()
    {
        var simulation = Backed();
        simulation.RestoreExclusiveAccessWaitMillis = 200;
        using var other = simulation.CreateDbConnection();
        other.Open();
        using (var use = other.CreateCommand())
        {
            use.CommandText = "use d";
            _ = use.ExecuteNonQuery();
        }
        var error = ThrowsExactly<SimulatedSqlException>(() => Run(simulation, "restore database d from disk = N'd.bak' with replace"));
        AreEqual(3101, error.Number);
        AreEqual("Exclusive access could not be obtained because the database is in use.", error.Errors[0].Message);
        AreEqual(3013, error.Errors[1].Number);
    }

    [TestMethod]
    public void ChangedDefinitions_Rebuild()
    {
        var simulation = Backed();
        var database = simulation.Databases["d"];
        Run(simulation, "use d; alter table t add extra int");
        Run(simulation, "restore database d from disk = N'd.bak' with replace");
        AreNotSame(database, simulation.Databases["d"]);
        AreEqual(database.Id, simulation.Databases["d"].Id);
        AreEqual(DatabaseState.Offline, database.State);
    }
}
