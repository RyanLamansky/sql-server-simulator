using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_addumpdevice</c> / <c>sp_dropdevice</c>, the catalog surfaces that
/// list logical backup devices, and <c>BACKUP</c> / <c>RESTORE</c> naming one
/// in place of <c>DISK = path</c>, probed 2026-10-15 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class BackupDeviceTests
{
    private static List<string> Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        return [.. reader.EnumerateRecords().Select(static record => string.Join('|', Enumerable.Range(0, record.FieldCount).Select(i => record.GetValue(i).ToInvariantString())))];
    }

    [TestMethod]
    public void AddUmpDevice_ListsTheDeviceInEveryCatalogSurface()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            exec sp_addumpdevice 'disk', 'dev1', '/var/opt/mssql/data/dev1.bak';
            exec sp_addumpdevice 'TAPE', 'tp', '\\.\tape0', 5, 'ignored';
            exec sp_addumpdevice 'virtual_device ', 'vd', 'vdname';
            """);
        CollectionAssert.AreEqual(new[] { "dev1|2|DISK|/var/opt/mssql/data/dev1.bak", @"tp|5|TAPE|\\.\tape0", "vd|7|VIRTUAL_DEVICE|vdname" },
            Rows(simulation, "select * from sys.backup_devices"));
        AreEqual("dev1|0|0|0|16|2|/var/opt/mssql/data/dev1.bak",
            simulation.ExecuteScalar("select concat(name, '|', size, '|', low, '|', high, '|', status, '|', cntrltype, '|', phyname) from master.dbo.sysdevices where name = 'dev1'"));

        CollectionAssert.AreEqual(new[]
        {
            "dev1|/var/opt/mssql/data/dev1.bak|disk, backup device|16|2|0",
            @"tp|\\.\tape0|tape, backup device|16|5|0",
            "vd|vdname|virtual_device, backup device|16|7|0",
        }, Rows(simulation, "exec sp_helpdevice"));
        CollectionAssert.AreEqual(new[] { "dev1|/var/opt/mssql/data/dev1.bak|disk, backup device|16|2|0" }, Rows(simulation, "exec sp_helpdevice 'DEV1'"));
    }

    [TestMethod]
    [DataRow("exec sp_addumpdevice 'disk', 'DEV1', '/x.bak'", 15026, 74, "Logical device 'DEV1' already exists.")]
    [DataRow("exec sp_addumpdevice 'disk', 'dev2', '/A.BAK'", 15061, 82, "The add device request was denied. A physical device named \"/A.BAK\" already exists. Only one backup device may refer to any physical device name.")]
    [DataRow("exec sp_addumpdevice ' disk', 'dev2', '/b.bak'", 15044, 37, "The type \" disk\" is an unknown backup device type. Use the type \"disk\" or \"tape\".")]
    [DataRow("exec sp_addumpdevice 'disk', null, '/b.bak'", 15045, 44, "The logical name cannot be NULL.")]
    [DataRow("exec sp_addumpdevice 'disk', 'dev2', null", 15046, 55, "The physical name cannot be NULL.")]
    [DataRow("exec sp_addumpdevice 'disk', 'Tape', '/b.bak'", 15285, 62, "The special word 'Tape' cannot be used for a logical device name.")]
    [DataRow("begin tran; exec sp_addumpdevice 'disk', 'dev2', '/b.bak'", 15002, 17, "The procedure 'sys.sp_addumpdevice' cannot be executed within a transaction.")]
    [DataRow("exec sp_dropdevice 'dev1', 'nope'", 15216, 13, "'nope' is not a valid option for the @delfile parameter.")]
    [DataRow("exec sp_dropdevice 'nosuch'", 15012, 37, "The device 'nosuch' does not exist. Use sys.backup_devices to show available devices.")]
    [DataRow("exec sp_dropdevice null", 15012, 37, "The device '(null)' does not exist. Use sys.backup_devices to show available devices.")]
    [DataRow("begin tran; exec sp_dropdevice 'dev1'", 15002, 22, "The procedure 'sys.sp_dropdevice' cannot be executed within a transaction.")]
    public void DeviceProcedures_RefuseAsRealDoes(string sql, int number, int line, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("exec sp_addumpdevice 'disk', 'dev1', '/a.bak'");
        var error = simulation.AssertSqlError(sql, number).Errors[0];
        AreEqual(message, error.Message);
        AreEqual(line, error.LineNumber);
        AreEqual(sql.Contains("sp_dropdevice", StringComparison.Ordinal) ? "sp_dropdevice" : "sp_addumpdevice", error.Procedure);
    }

    [TestMethod]
    public void AddUmpDevice_EmptyName_IsSpValidnamesRefusal()
    {
        var error = new Simulation().AssertSqlError("exec sp_addumpdevice 'disk', '', '/b.bak'", 15004).Errors[0];
        AreEqual("sys.sp_validname", error.Procedure);
        AreEqual(17, error.LineNumber);
    }

    [TestMethod]
    public void DeviceProcedures_ReturnZeroOnSuccess_AndOpenNoImplicitTransaction()
        => AreEqual("0,0,0", new Simulation().ExecuteScalar("""
            set implicit_transactions on;
            declare @a int, @b int;
            exec @a = sp_addumpdevice 'disk', 'dev1', '/a.bak';
            exec @b = sp_dropdevice 'dev1';
            select concat(@a, ',', @b, ',', @@trancount);
            """));

    [TestMethod]
    public void DeviceProcedures_RequireDiskadminOrSysadmin()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create login plain with password = 'Zz!Passw0rd1', check_policy = off;
            create login disky with password = 'Zz!Passw0rd1', check_policy = off;
            alter server role diskadmin add member disky;
            exec sp_addumpdevice 'disk', 'dev1', '/a.bak';
            """);
        var refusal = simulation.AssertSqlError("use master; execute as login = 'plain'; exec sp_addumpdevice 'disk', 'dev2', '/b.bak'", 15247).Errors[0];
        AreEqual(24, refusal.LineNumber);
        AreEqual(30, simulation.AssertSqlError("use master; execute as login = 'plain'; exec sp_dropdevice 'dev1'", 15247).Errors[0].LineNumber);
        AreEqual(1, simulation.ExecuteScalar("use master; execute as login = 'plain'; select count(*) from sys.backup_devices"));
        _ = simulation.ExecuteNonQuery("use master; execute as login = 'disky'; exec sp_addumpdevice 'disk', 'dev2', '/b.bak'; exec sp_dropdevice 'dev1'; revert");
        AreEqual("dev2", simulation.ExecuteScalar("select string_agg(name, ',') from sys.backup_devices"));
    }

    [TestMethod]
    public void DropDevice_SendsDeviceDropped_AndDelfileDeletesTheContent()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d; exec sp_addumpdevice 'disk', 'dev1', '/a.bak'; exec sp_addumpdevice 'disk', 'dev2', '/b.bak'; backup database d to dev1");
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => $"{error.Number}:{error.Procedure}:{error.LineNumber}:{error.Message}"));
        _ = connection.CreateCommand("exec sp_dropdevice 'dev1', 'DELFILE'; exec sp_dropdevice 'dev2', 'delfile'").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[]
        {
            "2503:sp_dropdevice:43:Successfully deleted the physical file '/a.bak'.",
            "15463:sp_dropdevice:52:Device dropped.",
            "15463:sp_dropdevice:52:Device dropped.",
        }, messages);
        _ = simulation.AssertSqlError("restore database d from disk = '/a.bak' with replace", 3201);
    }

    [TestMethod]
    public void BackupAndRestore_ThroughALogicalDevice()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d; exec sp_addumpdevice 'disk', 'dev1', '/var/opt/mssql/data/d.bak'",
            "use d; create table t (id int); insert t values (1)");
        _ = simulation.ExecuteNonQuery("backup database d to dev1 with init; use d; insert t values (2)");
        _ = simulation.ExecuteNonQuery("declare @dev sysname = N'DEV1'; backup database d to @dev");
        _ = simulation.ExecuteNonQuery("use master; restore database d from dev1 with replace, file = 1");
        AreEqual(1, simulation.ExecuteScalar("select count(*) from d.dbo.t"));
        _ = simulation.ExecuteNonQuery("restore database d from disk = '/var/opt/mssql/data/d.bak' with replace, file = 2");
        AreEqual(2, simulation.ExecuteScalar("select count(*) from d.dbo.t"));
    }

    [TestMethod]
    [DataRow("backup database d to tp", 5, "Cannot open backup device 'tp(\\\\.\\tape0)'. Operating system error 2(The system cannot find the file specified.).")]
    [DataRow("restore database d from tp", 5, "Cannot open backup device 'tp(\\\\.\\tape0)'. Operating system error 2(The system cannot find the file specified.).")]
    [DataRow("backup database d to vd", 7, "Cannot open backup device 'vdname'. Operating system error 0x80070002(The system cannot find the file specified.).")]
    [DataRow("restore database d from dev1 with replace", 2, "Cannot open backup device 'dev1(/nodir/d.bak)'. Operating system error 2(The system cannot find the file specified.).")]
    public void LogicalDevice_IsNamedLogicalThenPhysical_InItsErrors(string sql, int state, string message)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create database d;
            exec sp_addumpdevice 'disk', 'dev1', '/nodir/d.bak';
            exec sp_addumpdevice 'tape', 'tp', '\\.\tape0';
            exec sp_addumpdevice 'virtual_device', 'vd', 'vdname';
            """);
        var error = simulation.AssertSqlError(sql, 3201);
        AreEqual(message, error.Errors[0].Message);
        AreEqual((byte)state, error.Errors[0].State);
        AreEqual(3013, error.Errors[1].Number);
    }

    [TestMethod]
    public void LogicalDevice_NamedInEmptyAndFileErrors()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d; exec sp_addumpdevice 'disk', 'dev1', '/d.bak'; backup database d to dev1");
        AreEqual("The file ID 5 on device 'dev1(/d.bak)' is incorrectly formed and can not be read.",
            simulation.AssertSqlError("restore database d from dev1 with replace, file = 5", 3287).Errors[0].Message);
    }

    [TestMethod]
    public void MissingLogicalDevice_IsMsg3206()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d");
        AreEqual("Backup device 'nope' does not exist. To view existing backup devices, use the sys.backup_devices catalog view. To create a new backup device use either sp_addumpdevice or SQL Server Management Studio.",
            simulation.AssertSqlError("declare @n sysname = 'nope'; restore database d from @n with replace", 3206).Errors[0].Message);
    }
}
