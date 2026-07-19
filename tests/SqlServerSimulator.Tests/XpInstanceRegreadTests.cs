using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>xp_instance_regread</c> system procedure. SSMS reads the
/// instance <c>SQLPath</c> registry value on connect (via
/// <c>master.dbo.xp_instance_regread ... N'SQLPath', @out OUTPUT</c>) to derive
/// the SMO RootDirectory. The simulator returns a synthetic instance path
/// rooted at <c>/var/opt/mssql</c> (consistent with the physical paths surfaced
/// by <c>sys.master_files</c> / <c>sys.database_files</c>); values are
/// machine-specific on a real server. The OUTPUT form writes into the caller's
/// variable and yields no result set; the no-OUTPUT form returns a
/// <c>(Value, Data)</c> result set (probe-confirmed against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class XpInstanceRegreadTests
{
    [TestMethod]
    public void SqlPath_OutputForm_ReturnsSyntheticInstanceRoot()
        => AreEqual("/var/opt/mssql", new Simulation().ExecuteScalar("""
            declare @v nvarchar(512);
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'SQLPath', @v OUTPUT;
            select @v
            """));

    [TestMethod]
    public void UnknownValueName_OutputForm_YieldsNull()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            declare @v nvarchar(512);
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'DoesNotExist', @v OUTPUT;
            select case when @v is null then 1 else 0 end
            """));

    [TestMethod]
    public void NoOutputParameter_ReturnsValueDataResultSet()
    {
        using var reader = new Simulation().ExecuteReader(
            @"exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'SQLPath'");
        AreEqual(2, reader.FieldCount);
        AreEqual("Value", reader.GetName(0));
        AreEqual("Data", reader.GetName(1));
        IsTrue(reader.Read());
        AreEqual("SQLPath", reader.GetString(0));
        AreEqual("/var/opt/mssql", reader.GetString(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void CallableWithoutMasterDboQualifier()
        => AreEqual("/var/opt/mssql", new Simulation().ExecuteScalar("""
            declare @v nvarchar(512);
            exec xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'SQLPath', @v OUTPUT;
            select @v
            """));
}
