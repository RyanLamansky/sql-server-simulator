using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the <c>xp_instance_regread</c> system procedure. SSMS reads the
/// instance <c>SQLPath</c> registry value on connect (via
/// <c>master.dbo.xp_instance_regread ... N'SQLPath', @out OUTPUT</c>) to derive
/// the SMO RootDirectory, which SQL Server 2025 on Linux answers <c>C:\</c>
/// (probed 2026-09-30). The OUTPUT form writes into the caller's
/// variable and yields no result set; the no-OUTPUT form returns a
/// <c>(Value, Data)</c> result set (probe-confirmed against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class XpInstanceRegreadTests
{
    [TestMethod]
    public void SqlPath_OutputForm_ReturnsInstanceRoot()
        => AreEqual(@"C:\", new Simulation().ExecuteScalar("""
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
        AreEqual(@"C:\", reader.GetString(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void CallableWithoutMasterDboQualifier()
        => AreEqual(@"C:\", new Simulation().ExecuteScalar("""
            declare @v nvarchar(512);
            exec xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'SQLPath', @v OUTPUT;
            select @v
            """));

    /// <summary>
    /// The instance registry values SMO's server bag reads, as SQL Server 2025
    /// on Linux answers them (probed 2026-09-30): mixed authentication, audit
    /// level 2, named pipes off and TCP on, the administrators group, and no
    /// value where the key has none. A DWORD answers an int Data column.
    /// </summary>
    [TestMethod]
    public void InstanceValues_MatchReference()
    {
        AreEqual("2|2|0|1|S-1-5-32-544|0|null", new Simulation().ExecuteScalar("""
            declare @login int, @audit int, @np int, @tcp int, @group nvarchar(512), @fs int, @logs int;
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'LoginMode', @login OUTPUT;
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'AuditLevel', @audit OUTPUT;
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Np', N'Enabled', @np OUTPUT;
            exec master.sys.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Tcp', N'Enabled', @tcp OUTPUT;
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'SQLGroup', @group OUTPUT;
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\Filestream', N'EnableLevel', @fs OUTPUT;
            exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'NumErrorLogs', @logs OUTPUT;
            select concat_ws('|', @login, @audit, @np, @tcp, @group, @fs, isnull(cast(@logs as varchar), 'null'))
            """));
        using var reader = new Simulation().ExecuteReader(@"exec master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'LoginMode'");
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(1));
    }

    /// <summary>xp_regread reads machine keys: the instance id, and nothing for the absent Browser service.</summary>
    [TestMethod]
    public void XpRegread_MachineValues()
        => AreEqual("MSSQL|null", new Simulation().ExecuteScalar("""
            declare @id nvarchar(512), @browser nvarchar(512);
            exec master.sys.xp_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL', N'MSSQLSERVER', @id OUTPUT;
            exec master.sys.xp_regread N'HKEY_LOCAL_MACHINE', N'SYSTEM\CurrentControlSet\Services\SQLBrowser', N'ObjectName', @browser OUTPUT;
            select concat_ws('|', @id, isnull(@browser, 'null'))
            """));

    /// <summary>
    /// xp_getnetname answers the machine name, form 1 its fully qualified name
    /// under a host outside a domain, and a variable too narrow for the name
    /// NULL; without a variable it answers a Server Net Name row (probed
    /// 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void XpGetNetName_Forms()
    {
        AreEqual("SIMULATED|SIMULATED.DOMAIN|null", new Simulation().ExecuteScalar("""
            declare @name nvarchar(256), @fqdn nvarchar(256), @short varchar(5);
            exec master.dbo.xp_getnetname @name OUTPUT;
            exec master.dbo.xp_getnetname @fqdn OUTPUT, 1;
            exec master.dbo.xp_getnetname @short OUTPUT;
            select concat_ws('|', @name, @fqdn, isnull(@short, 'null'))
            """));
        using var reader = new Simulation().ExecuteReader("exec master.dbo.xp_getnetname");
        AreEqual("Server Net Name", reader.GetName(0));
        IsTrue(reader.Read());
        AreEqual("SIMULATED", reader.GetString(0));
    }
}
