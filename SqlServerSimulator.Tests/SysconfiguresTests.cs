using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the legacy <c>sysconfigures</c> compatibility view — the
/// SQL-Server-2000-shaped projection of the server configuration catalog that
/// DacFx's bacpac-export preamble reads. Four columns (<c>value int</c>,
/// <c>config int</c>, <c>comment nvarchar</c>, <c>status smallint</c>);
/// <c>status = is_dynamic + 2 * is_advanced</c>. Rows mirror
/// <c>sys.configurations</c>. Probe-confirmed against SQL Server 2025: it
/// resolves from every database under the bare leaf, the <c>sys.</c>
/// qualifier, the <c>dbo.</c> qualifier, and the three-part
/// <c>master.dbo.sysconfigures</c> form.
/// </summary>
[TestClass]
public sealed class SysconfiguresTests
{
    [TestMethod]
    public void DacFxExportQuery_ReadsDefaultFullTextLanguageValue()
        => AreEqual(1033, new Simulation().ExecuteScalar(
            "select [c].[value] from [master].[dbo].[sysconfigures] as [c] with (nolock) where [c].[config] = 1126"));

    [TestMethod]
    public void RowCount_Matches_SysConfigurations()
    {
        var sim = new Simulation();
        AreEqual(
            sim.ExecuteScalar("select count(*) from sys.configurations"),
            sim.ExecuteScalar("select count(*) from master.dbo.sysconfigures"));
    }

    [TestMethod]
    public void Status_DynamicAndAdvanced_Is3()
        => AreEqual((short)3, new Simulation().ExecuteScalar(
            "select status from master.dbo.sysconfigures where config = 1126"));

    [TestMethod]
    public void Status_DynamicOnly_Is1()
        => AreEqual((short)1, new Simulation().ExecuteScalar(
            "select status from master.dbo.sysconfigures where config = 102"));

    [TestMethod]
    public void Comment_MirrorsDescription()
        => AreEqual("default full-text language", new Simulation().ExecuteScalar(
            "select comment from master.dbo.sysconfigures where config = 1126"));

    [TestMethod]
    public void ColumnShape_ValueAndConfigInt_StatusSmallint()
    {
        using var reader = new Simulation().ExecuteReader(
            "select value, config, comment, status from master.dbo.sysconfigures where config = 1126");
        AreEqual(typeof(int), reader.GetFieldType(0));
        AreEqual(typeof(int), reader.GetFieldType(1));
        AreEqual(typeof(string), reader.GetFieldType(2));
        AreEqual(typeof(short), reader.GetFieldType(3));
    }

    [TestMethod]
    public void ResolvesUnqualified()
        => AreEqual(1033, new Simulation().ExecuteScalar(
            "select value from sysconfigures where config = 1126"));

    [TestMethod]
    public void ResolvesViaSysQualifier()
        => AreEqual(1033, new Simulation().ExecuteScalar(
            "select value from sys.sysconfigures where config = 1126"));

    [TestMethod]
    public void ResolvesViaDboQualifier()
        => AreEqual(1033, new Simulation().ExecuteScalar(
            "select value from dbo.sysconfigures where config = 1126"));

    [TestMethod]
    public void NoNameColumn_RaisesMsg207()
        => _ = new Simulation().AssertSqlError("select name from master.dbo.sysconfigures", 207);
}
