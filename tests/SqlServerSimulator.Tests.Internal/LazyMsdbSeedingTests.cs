using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// msdb's shipped content — the Agent, Mail and backup-history tables and the
/// policy views — is seeded on first use rather than at construction, so a
/// consumer that never touches msdb (the fast parallel-test path) pays none of
/// it. These reach msdb's <c>dbo</c> schema directly, which doesn't trigger the
/// seed, to observe the before/after state SQL never shows.
/// </summary>
[TestClass]
public sealed class LazyMsdbSeedingTests
{
    private static System.Collections.Concurrent.ConcurrentDictionary<string, Storage.HeapTable> MsdbDbo(Simulation simulation) =>
        simulation.Databases["msdb"].Schemas["dbo"].HeapTables;

    [TestMethod]
    public void AFreshSimulation_LeavesMsdbUnseeded()
    {
        var simulation = new Simulation();
        // The Agent / Mail / history tables aren't there yet; the three
        // seeded-at-construction system schemas (dbo / guest / …) are.
        IsFalse(MsdbDbo(simulation).ContainsKey("sysjobs"));
        IsFalse(MsdbDbo(simulation).ContainsKey("backupset"));
    }

    [TestMethod]
    [DataRow("select count(*) from msdb.dbo.sysjobs")]
    [DataRow("use msdb; select count(*) from sysoperators")]
    [DataRow("exec msdb.dbo.sp_help_job")]
    [DataRow("select count(*) from msdb.sys.objects where name = 'sysjobs'")]
    public void TouchingMsdb_SeedsItOnce(string batch)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = batch;
        using (var reader = command.ExecuteReader())
            while (reader.Read() || reader.NextResult()) { }
        // The seed ran: msdb now holds its shipped tables.
        IsTrue(MsdbDbo(simulation).ContainsKey("sysjobs"));
        IsTrue(MsdbDbo(simulation).ContainsKey("backupset"));
    }

    [TestMethod]
    public void AUserDatabaseWorkload_NeverSeedsMsdb()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "create table t (id int primary key); insert t values (1); select count(*) from t; select name from sys.tables";
        using (var reader = command.ExecuteReader())
            while (reader.Read() || reader.NextResult()) { }
        IsFalse(MsdbDbo(simulation).ContainsKey("sysjobs"));
    }
}
