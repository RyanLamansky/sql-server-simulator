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
    public void EngineEdition_Returns3AsInt()
        => AreEqual(3, new Simulation().ExecuteScalar("select serverproperty('EngineEdition')"));

    [TestMethod]
    public void EngineEdition_SurfacesAsIntType()
    {
        using var reader = new Simulation().ExecuteReader("select serverproperty('EngineEdition')");
        IsTrue(reader.Read());
        AreEqual("int", reader.GetDataTypeName(0));
        _ = Assert.IsInstanceOfType<int>(reader.GetValue(0));
        AreEqual(3, reader.GetValue(0));
    }

    [TestMethod]
    public void SqlCharSet_ReturnsTinyIntOne()
    {
        using var reader = new Simulation().ExecuteReader("select serverproperty('SqlCharSet')");
        IsTrue(reader.Read());
        AreEqual("tinyint", reader.GetDataTypeName(0));
        AreEqual((byte)1, reader.GetValue(0));
    }

    [TestMethod]
    public void SqlSortOrder_ReturnsTinyInt52OnDefaultCollation()
    {
        using var reader = new Simulation().ExecuteReader("select serverproperty('SqlSortOrder')");
        IsTrue(reader.Read());
        AreEqual("tinyint", reader.GetDataTypeName(0));
        AreEqual((byte)52, reader.GetValue(0));
    }

    [TestMethod]
    public void SqlSortOrderName_ReturnsNocaseIsoOnDefaultCollation()
        => AreEqual("nocase_iso", new Simulation().ExecuteScalar("select serverproperty('SqlSortOrderName')"));

    [TestMethod]
    public void ProductVersion_ReturnsReferenceBuild()
        => AreEqual("17.0.4065.4", new Simulation().ExecuteScalar("select serverproperty('ProductVersion')"));

    [TestMethod]
    public void ProductBuild_ReturnsReferenceBuild()
        => AreEqual("4065", new Simulation().ExecuteScalar("select serverproperty('ProductBuild')"));

    [TestMethod]
    public void ProductUpdateLevel_ReturnsCU7()
        => AreEqual("CU7", new Simulation().ExecuteScalar("select serverproperty('ProductUpdateLevel')"));

    [TestMethod]
    public void ProductUpdateReference_ReturnsKb()
        => AreEqual("KB5096981", new Simulation().ExecuteScalar("select serverproperty('ProductUpdateReference')"));

    [TestMethod]
    public void ResourceVersion_ReturnsReferenceBuild()
        => AreEqual("17.00.4065", new Simulation().ExecuteScalar("select serverproperty('ResourceVersion')"));

    [TestMethod]
    public void NonConstantName_FallsBackToNVarchar()
        => AreEqual("3", new Simulation().ExecuteScalar(
            "declare @p nvarchar(30) = 'EngineEdition'; select serverproperty(@p)"));

    // Typed values must survive set-op schema unification. int (EngineEdition)
    // and tinyint (SqlCharSet) promote to int per data-type precedence. Mixing
    // a numeric property with a string one instead raises a conversion error
    // (int outranks nvarchar) — a deliberate divergence from real SQL Server,
    // where every SERVERPROPERTY is sql_variant so no promotion occurs.
    [TestMethod]
    public void UnionOfNumericProperties_PromotesToInt()
        => AreEqual(3, new Simulation().ExecuteScalar(
            "SELECT SERVERPROPERTY('EngineEdition') UNION ALL SELECT SERVERPROPERTY('SqlCharSet')"));

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

    // Every version-bearing surface must agree on the reference build
    // 17.0.4065.4: SSMS gates report viewers and Activity Monitor on a
    // per-build feature check that chokes on the old build-0 identity.
    [TestMethod]
    public void VersionSurfaces_AgreeOnReferenceBuild()
    {
        var sim = new Simulation();
        AreEqual("17.0.4065.4", sim.ExecuteScalar("select serverproperty('ProductVersion')"));
        AreEqual("17", sim.ExecuteScalar("select serverproperty('ProductMajorVersion')"));
        AreEqual("0", sim.ExecuteScalar("select serverproperty('ProductMinorVersion')"));
        AreEqual("4065", sim.ExecuteScalar("select serverproperty('ProductBuild')"));
        // @@MICROSOFTVERSION = (major << 24) | build = (17 << 24) | 4065.
        AreEqual((17 << 24) | 4065, sim.ExecuteScalar("select @@microsoftversion"));
        Assert.Contains("17.0.4065.4", (string)sim.ExecuteScalar("select @@version")!);
    }

    // SSMS Activity Monitor reads both at startup and casts without a NULL
    // check — a NULL here surfaces as "Object cannot be cast from DBNull to
    // other types" before the window opens. Real reports the engine's OS
    // process id; the simulator's engine process is the host process.
    [TestMethod]
    public void ProcessIdAndNetBios_AreNonNull()
    {
        var sim = new Simulation();
        AreEqual(Environment.ProcessId, sim.ExecuteScalar("select serverproperty('ProcessID')"));
        AreEqual("SIMULATED", sim.ExecuteScalar("select serverproperty('ComputerNamePhysicalNetBIOS')"));
    }
}
