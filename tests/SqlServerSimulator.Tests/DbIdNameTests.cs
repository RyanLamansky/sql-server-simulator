using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>DB_ID([name])</c> and <c>DB_NAME([id])</c>: round-trip the
/// simulator's database list through the id allocation that
/// <c>sys.databases.database_id</c> uses — the four system databases carry
/// their fixed reserved ids (master = 1, tempdb = 2, model = 3, msdb = 4) and
/// user databases take 5, 6, … in case-insensitive name order (system-database
/// coverage lives in <c>SystemDatabaseTests</c>). NULL argument / unknown name
/// / unknown id all return NULL.
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
}
