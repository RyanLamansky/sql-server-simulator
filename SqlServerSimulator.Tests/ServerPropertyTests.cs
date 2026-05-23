using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>SERVERPROPERTY('name')</c>: instance-level configuration
/// values. The simulator returns plausible placeholder constants for
/// each documented property; unknown properties return NULL.
/// </summary>
[TestClass]
public sealed class ServerPropertyTests
{
    [TestMethod]
    public void Edition_ReturnsDeveloper()
        => AreEqual("Developer Edition (64-bit)", new Simulation().ExecuteScalar("select serverproperty('Edition')"));

    [TestMethod]
    public void ProductLevel_ReturnsRTM()
        => AreEqual("RTM", new Simulation().ExecuteScalar("select serverproperty('ProductLevel')"));

    [TestMethod]
    public void EngineEdition_Returns3()
        => AreEqual("3", new Simulation().ExecuteScalar("select serverproperty('EngineEdition')"));

    [TestMethod]
    public void Collation_ReturnsServerCollation()
        => AreEqual("SQL_Latin1_General_CP1_CI_AS", new Simulation().ExecuteScalar("select serverproperty('Collation')"));

    [TestMethod]
    public void Unknown_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select serverproperty('NotAProperty')"));

    [TestMethod]
    public void NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select serverproperty(cast(null as nvarchar(128)))"));

    [TestMethod]
    public void CaseInsensitive_Works()
        => AreEqual("RTM", new Simulation().ExecuteScalar("select serverproperty('PRODUCTLEVEL')"));
}
