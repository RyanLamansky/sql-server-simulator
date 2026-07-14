using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the always-present <c>master</c> system database: <c>USE master</c>
/// in-session (previously Msg 911), its <c>database_id</c> (1) surfacing through
/// <c>sys.databases</c> / <c>DB_ID</c> / <c>DB_NAME</c>, and three-part reads
/// routed through master. User databases take ids from 5 (real SQL Server
/// reserves 2-4 for tempdb / model / msdb, which the simulator doesn't model).
/// </summary>
[TestClass]
public sealed class MasterDatabaseTests
{
    [TestMethod]
    public void UseMaster_SwitchesCurrentDatabase()
        => AreEqual("master", new Simulation().ExecuteScalar("use master; select db_name()"));

    [TestMethod]
    public void UseMaster_ThenBackToDefault_Works()
        => AreEqual("simulated", new Simulation().ExecuteScalar("use master; use simulated; select db_name()"));

    [TestMethod]
    public void Master_HasDatabaseIdOne()
        => AreEqual((short)1, new Simulation().ExecuteScalar("select database_id from sys.databases where name = 'master'"));

    [TestMethod]
    public void SimulatedUserDatabase_HasDatabaseIdFive()
        => AreEqual((short)5, new Simulation().ExecuteScalar("select database_id from sys.databases where name = 'simulated'"));

    [TestMethod]
    public void SysDatabases_ListsMasterFirstThenUserDatabase()
    {
        using var reader = new Simulation().ExecuteReader(
            "select name, database_id from sys.databases order by database_id");
        List<(string Name, short Id)> rows =
        [
            .. reader.EnumerateRecords().Select(r => (Name: r.GetString(0), Id: r.GetInt16(1))),
        ];
        HasCount(2, rows);
        AreEqual("master", rows[0].Name);
        AreEqual((short)1, rows[0].Id);
        AreEqual("simulated", rows[1].Name);
        AreEqual((short)5, rows[1].Id);
    }

    [TestMethod]
    public void ThreePartRead_MasterSysDatabases_Resolves()
        => AreEqual((short)1, new Simulation().ExecuteScalar(
            "select database_id from master.sys.databases where name = 'master'"));

    [TestMethod]
    public void ThreePartRead_MasterSysDatabases_CountsAllDatabases()
        => AreEqual(2, new Simulation().ExecuteScalar("select count(*) from master.sys.databases"));
}
