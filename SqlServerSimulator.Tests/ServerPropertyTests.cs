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
    public void ProductUpdateLevel_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select serverproperty('ProductUpdateLevel')"));

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
}
