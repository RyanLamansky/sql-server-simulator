using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>DB_ID([name])</c> and <c>DB_NAME([id])</c>: round-trip the
/// simulator's database list through the id allocation that
/// <c>sys.databases.database_id</c> uses — <c>master</c> is always 1, user
/// databases take 5, 6, … in case-insensitive name order (real SQL Server
/// reserves 2-4 for tempdb / model / msdb, unmodeled here). NULL argument /
/// unknown name / unknown id all return NULL.
/// </summary>
[TestClass]
public sealed class DbIdNameTests
{
    [TestMethod]
    public void DbName_NoArg_ReturnsCurrentDatabase()
        => AreEqual("simulated", new Simulation().ExecuteScalar("select db_name()"));

    [TestMethod]
    public void DbId_NoArg_ReturnsCurrentDatabaseId()
        => AreEqual((short)5, new Simulation().ExecuteScalar("select db_id()"));

    [TestMethod]
    public void DbId_KnownName_ReturnsId()
        => AreEqual((short)5, new Simulation().ExecuteScalar("select db_id('simulated')"));

    [TestMethod]
    public void DbId_KnownNameCaseInsensitive_ReturnsId()
        => AreEqual((short)5, new Simulation().ExecuteScalar("select db_id('SIMULATED')"));

    [TestMethod]
    public void DbId_Master_ReturnsOne()
        => AreEqual((short)1, new Simulation().ExecuteScalar("select db_id('master')"));

    [TestMethod]
    public void DbId_UnknownName_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select db_id('nonexistent')"));

    [TestMethod]
    public void DbId_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select db_id(null)"));

    [TestMethod]
    public void DbName_KnownId_ReturnsName()
        => AreEqual("simulated", new Simulation().ExecuteScalar("select db_name(5)"));

    [TestMethod]
    public void DbName_MasterId_ReturnsMaster()
        => AreEqual("master", new Simulation().ExecuteScalar("select db_name(1)"));

    [TestMethod]
    public void DbName_UnknownId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select db_name(99)"));

    [TestMethod]
    public void DbName_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select db_name(null)"));

    [TestMethod]
    public void DbId_DbName_RoundTrip()
        => AreEqual("simulated", new Simulation().ExecuteScalar("select db_name(db_id())"));

    [TestMethod]
    public void HasDbAccess_HostedDatabase_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_dbaccess('master')"));

    [TestMethod]
    public void HasDbAccess_CaseInsensitive()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_dbaccess('SiMuLaTeD')"));

    // SSMS probes msdb at connect to decide whether to surface Agent
    // features; the simulator doesn't model msdb, so NULL — same as a real
    // server where the login can't see it.
    [TestMethod]
    public void HasDbAccess_UnknownDatabase_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select has_dbaccess('msdb')"));

    [TestMethod]
    public void HasDbAccess_EmptyOrNullName_ReturnsNull()
    {
        var simulation = new Simulation();
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select has_dbaccess('')"));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select has_dbaccess(null)"));
    }

    [TestMethod]
    public void HasDbAccess_NoArgument_RaisesMsg174()
    {
        var ex = new Simulation().AssertSqlError("select has_dbaccess()", 174);
        AreEqual("The has_dbaccess function requires 1 argument(s).", ex.Message);
    }

    [TestMethod]
    public void HasDbAccess_VariableArgument_Resolves()
        => AreEqual(1, new Simulation().ExecuteScalar("declare @n sysname = 'master' select has_dbaccess(@n)"));
}
