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

    private static List<string> ResultSets(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var sets = new List<string>();
        do
        {
            if (reader.FieldCount == 0)
                continue;
            var rows = new List<string> { string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i) + ":" + reader.GetDataTypeName(i))) };
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            sets.Add(string.Join("~", rows));
        }
        while (reader.NextResult());
        return sets;
    }

    /// <summary>
    /// The enumerators send one result set per value, its data an int for a
    /// DWORD and a string otherwise, a multi-string's strings a column each,
    /// and the subkeys as one; the instance forms name the instance's key as
    /// SOFTWARE\Microsoft\MSSQLServer (probed 2026-10-17 against SQL Server
    /// 2025 on Linux).
    /// </summary>
    [TestMethod]
    public void Enumerators_ListTheInstanceKeys()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(
            new[]
            {
                "Value:nvarchar|Data:nvarchar~SQLArg0|-d/var/opt/mssql/data/master.mdf",
                "Value:nvarchar|Data:nvarchar~SQLArg1|-l/var/opt/mssql/data/mastlog.ldf",
                "Value:nvarchar|Data:nvarchar~SQLArg2|-e/var/opt/mssql/log/errorlog",
            },
            ResultSets(sim, @"exec xp_instance_regenumvalues N'hkey_local_machine', N'software\microsoft\mssqlserver\mssqlserver\parameters\'"));
        CollectionAssert.AreEqual(
            new[] { "Value:nvarchar|Data:int~Enabled|1", "Value:nvarchar|Data:int~ListenOnAllIPs|1", "Value:nvarchar|Data:int~NoDelay|0", "Value:nvarchar|Data:int~KeepAlive|30000", "Value:nvarchar|Data:nvarchar~DisplayName|TCP/IP" },
            ResultSets(sim, @"exec master.dbo.xp_instance_regenumvalues N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Tcp'"));
        Assert.Contains("Value:nvarchar|Value:nvarchar|Data:nvarchar~EditionType|Enterprise Developer Edition|NULL",
            ResultSets(sim, @"exec xp_instance_regenumvalues N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup'"));
        CollectionAssert.AreEqual(new[] { "Value:nvarchar|Data:nvarchar~NULL|MSSQLSERVER" }, ResultSets(sim, @"exec xp_instance_regenumvalues N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer'"));
        CollectionAssert.AreEqual(
            new[] { "SubKeyName:nvarchar~CPE~MSSearch~MSSQLServer~Providers~Replication~SQLServerAgent~Setup" },
            ResultSets(sim, @"exec xp_instance_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer'"));
        CollectionAssert.AreEqual(new[] { "SubKeyName:nvarchar~IP1~IPAll" }, ResultSets(sim, @"exec xp_instance_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Tcp'"));
        // A key with no values or no subkeys sends no result set.
        IsEmpty(ResultSets(sim, @"exec xp_instance_regenumvalues N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\HADR'"));
        IsEmpty(ResultSets(sim, @"exec xp_instance_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup'"));
        // The machine forms read the instance under its id.
        CollectionAssert.AreEqual(new[] { "SubKeyName:nvarchar~170~Instance Names~MSSQL" }, ResultSets(sim, @"exec xp_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\Microsoft SQL Server'"));
        CollectionAssert.AreEqual(new[] { "Value:nvarchar|Data:nvarchar~MSSQLSERVER|MSSQL" }, ResultSets(sim, @"exec xp_regenumvalues N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'"));
        CollectionAssert.AreEqual(new[] { "SubKeyName:nvarchar~Client~MSSQLServer" }, ResultSets(sim, @"exec xp_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer'"));
        AreEqual(5, sim.ExecuteScalar("""
            create table #v (value nvarchar(255), data nvarchar(255));
            insert #v exec xp_instance_regenumvalues N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\SuperSocketNetLib\Tcp';
            select count(*) from #v
            """));
    }

    [TestMethod]
    public void Enumerators_RefuseAsRealDoes()
    {
        var sim = new Simulation();
        sim.AssertSqlError(@"exec xp_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\nosuch'", 22001, "RegOpenKeyEx() returned error 2, 'The system cannot find the file specified.'");
        sim.AssertSqlError(@"exec xp_instance_regenumvalues N'HKEY_LOCAL_MACHINE', N'\SOFTWARE\Microsoft\MSSQLServer\MSSQLServer\Parameters'", 22001, "RegOpenKeyEx() returned error 161, 'The specified path is invalid.'");
        sim.AssertSqlError(@"exec xp_regenumvalues N'HKLM', N'SOFTWARE'", 22001, "Error executing Read extended stored procedure: Invalid Predefined Registry system key");
        var count = sim.AssertSqlError(@"exec xp_regenumvalues N'HKEY_LOCAL_MACHINE'", 22001);
        AreEqual("Error executing Read extended stored procedure: Invalid Parameter|15|0", $"{count.Errors[0].Message}|{count.Class}|{count.State}");
        AreEqual(12, sim.AssertSqlError(@"exec xp_regenumvalues N'HKEY_LOCAL_MACHINE', null", 22001).Class);
        _ = sim.AssertSqlError(@"exec xp_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'x'", 22001);
        // The batch goes on, the call returning 1.
        using (var connection = sim.CreateOpenConnection())
        {
            _ = Throws<SimulatedSqlException>(() => connection.CreateCommand(@"create table #rc (r int); declare @r int; exec @r = xp_regenumvalues N'HKEY_LOCAL_MACHINE', N'SOFTWARE\nosuch'; insert #rc values (@r)").ExecuteNonQuery());
            AreEqual(1, connection.CreateCommand("select r from #rc").ExecuteScalar());
        }
        _ = Throws<NotSupportedException>(() => sim.ExecuteNonQuery(@"exec xp_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\Windows NT'"));
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off");
        _ = sim.AssertSqlError(@"use master; execute as login = 'lo'; exec xp_instance_regenumkeys N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer'", 229);
    }

    /// <summary>
    /// A value the key lacks, or a key the registry lacks, sends Msg 22001 at
    /// severity 1, leaves the variable as it was and sends no result set
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void MissingValue_SendsTheRegistryMessage()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SimulatedError>().Select(static error => $"{error.Number}/{error.Class}: {error.Message}"));
        AreEqual("zz", connection.CreateCommand("""
            declare @v nvarchar(100) = 'zz';
            exec xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'NoSuchValue', @v output;
            select @v
            """).ExecuteScalar());
        using (var reader = connection.CreateCommand(@"exec xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\NoSuchKey', N'x'").ExecuteReader())
            AreEqual(0, reader.FieldCount);
        CollectionAssert.AreEqual(
            new[] { "22001/1: RegQueryValueEx() returned error 2, 'The system cannot find the file specified.'", "22001/1: RegOpenKeyEx() returned error 2, 'The system cannot find the file specified.'" },
            messages);
        AreEqual("guest|Enterprise Developer Edition", connection.CreateCommand("""
            declare @login nvarchar(100), @edition nvarchar(100);
            exec xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\MSSQLServer', N'DefaultLogin', @login output;
            exec xp_instance_regread N'HKEY_LOCAL_MACHINE', N'SOFTWARE\Microsoft\MSSQLServer\Setup', N'EditionType', @edition output;
            select @login + '|' + @edition
            """).ExecuteScalar());
    }
}
