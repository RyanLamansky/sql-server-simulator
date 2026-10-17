using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The Windows principals SQL Server 2025 on Linux is installed with —
/// <c>BUILTIN\Administrators</c>, <c>NT AUTHORITY\NETWORK SERVICE</c> and
/// <c>NT AUTHORITY\SYSTEM</c> — across the login catalogs, the identity
/// functions, <c>xp_logininfo</c>, the legacy grant / deny / revoke trio and the
/// login DDL that refuses to make another (probed 2026-10-17).
/// </summary>
[TestClass]
public sealed class WindowsLoginTests
{
    private static List<string> Rows(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var rows = new List<string>();
        do
        {
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i) is byte[] bytes ? "0x" + Convert.ToHexString(bytes) : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }
        while (reader.NextResult());
        return rows;
    }

    [TestMethod]
    public void InstalledLogins_AppearInTheLoginCatalogs()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(
            new[]
            {
                @"BUILTIN\Administrators|259|G|WINDOWS_GROUP|0x01020000000000052000000020020000|master|us_english",
                @"NT AUTHORITY\NETWORK SERVICE|260|U|WINDOWS_LOGIN|0x010100000000000514000000|master|us_english",
                @"NT AUTHORITY\SYSTEM|261|U|WINDOWS_LOGIN|0x010100000000000512000000|master|us_english",
            },
            Rows(sim, "select name, principal_id, type, type_desc, sid, default_database_name, default_language_name from sys.server_principals where type in ('U', 'G') order by principal_id"));
        CollectionAssert.AreEqual(
            new[] { @"BUILTIN\Administrators|9|1|1|0|1", @"NT AUTHORITY\NETWORK SERVICE|9|1|0|1|1", @"NT AUTHORITY\SYSTEM|9|1|0|1|0" },
            Rows(sim, "select name, status, isntname, isntgroup, isntuser, sysadmin from sys.syslogins where isntname = 1 order by name"));
        CollectionAssert.AreEqual(new[] { "3|1", "3|259", "3|260" }, Rows(sim, "select role_principal_id, member_principal_id from sys.server_role_members order by member_principal_id"));
        AreEqual("AAES,CADB,COSQ,VWAD,VWSS", sim.ExecuteScalar("select string_agg(rtrim(type), ',') within group (order by type) from sys.server_permissions where grantee_principal_id = 261"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.sql_logins"));
        CollectionAssert.AreEqual(
            new[] { "sysadmin|sa|0x01", @"sysadmin|BUILTIN\Administrators|0x01020000000000052000000020020000", @"sysadmin|NT AUTHORITY\NETWORK SERVICE|0x010100000000000514000000" },
            Rows(sim, "exec sp_helpsrvrolemember 'sysadmin'"));
        // The first login created takes the id after them.
        _ = sim.ExecuteNonQuery("create login after_windows with password = 'P@ssw0rd1'");
        AreEqual(262, sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'after_windows'"));
    }

    [TestMethod]
    public void IdentityFunctions_ResolveTheWellKnownAccounts()
    {
        var sim = new Simulation();
        AreEqual(
            "0x010100000000000512000000|261|0x010100000000000513000000|NULL|0x01010000000000050B000000|NULL|NULL",
            Rows(sim, @"select suser_sid(N'nt authority\system '), suser_id(N'NT AUTHORITY\SYSTEM'), suser_sid(N'NT AUTHORITY\LOCAL SERVICE'), suser_id(N'NT AUTHORITY\LOCAL SERVICE'),
                suser_sid(N'NT AUTHORITY\Authenticated Users'), suser_sid(N'BUILTIN\Users'), suser_sid(N'\Everyone')")[0]);
        AreEqual(
            @"NT AUTHORITY\NETWORK SERVICE|NT AUTHORITY\LOCAL SERVICE|\Everyone|BUILTIN\Guests|NT AUTHORITY\SYSTEM",
            Rows(sim, "select suser_sname(0x010100000000000514000000), suser_sname(0x010100000000000513000000), suser_sname(0x010100000000000100000000), suser_sname(0x01020000000000052000000022020000), suser_name(261)")[0]);
    }

    [TestMethod]
    public void SystemIsASysadminThroughTheAdministratorsGroup()
    {
        var sim = new Simulation();
        AreEqual("1|1|1|1|NULL|NULL", Rows(sim, @"select is_srvrolemember('sysadmin', 'BUILTIN\Administrators'), is_srvrolemember('sysadmin', 'NT AUTHORITY\SYSTEM'),
            is_srvrolemember('sysadmin', 'NT AUTHORITY\NETWORK SERVICE'), is_srvrolemember('serveradmin', 'NT AUTHORITY\SYSTEM'),
            is_srvrolemember('sysadmin', 'NT AUTHORITY\LOCAL SERVICE'), is_srvrolemember('sysadmin', 'DOM\x')")[0]);
        // Out of the group, SYSTEM keeps only its own grants.
        _ = sim.ExecuteNonQuery(@"alter server role sysadmin drop member [BUILTIN\Administrators]");
        AreEqual(0, sim.ExecuteScalar(@"select is_srvrolemember('sysadmin', 'NT AUTHORITY\SYSTEM')"));
    }

    [TestMethod]
    public void ImpersonatedWindowsLogin_CarriesItsGroupsInItsToken()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(
            new[]
            {
                @"261|NT AUTHORITY\SYSTEM|WINDOWS LOGIN", "2|public|SERVER ROLE", "3|sysadmin|SERVER ROLE", @"259|BUILTIN\Administrators|WINDOWS GROUP",
                @"0|\Everyone|WINDOWS GROUP", @"0|NT AUTHORITY\Authenticated Users|WINDOWS GROUP", @"259|BUILTIN\Administrators|WINDOWS GROUP",
            },
            Rows(sim, @"execute as login = 'NT AUTHORITY\SYSTEM'; select principal_id, name, type from sys.login_token; revert"));
        AreEqual(@"NT AUTHORITY\SYSTEM|1|1|1|NULL|NULL|dbo", Rows(sim, @"execute as login = 'NT AUTHORITY\SYSTEM';
            select suser_name(), is_srvrolemember('sysadmin'), is_member('BUILTIN\Administrators'), is_member('nt authority\authenticated users'),
                is_member('\Everyone'), is_member('BUILTIN\Users'), user_name(); revert")[0]);
        AreEqual("0|1", Rows(sim, @"execute as login = 'NT AUTHORITY\NETWORK SERVICE'; select is_member('BUILTIN\Administrators'), is_member('NT AUTHORITY\SERVICE'); revert")[0]);
        // A SQL login's session answers NULL for any Windows group.
        AreEqual(DBNull.Value, sim.ExecuteScalar(@"select is_member('BUILTIN\Administrators')"));
        _ = sim.AssertSqlError(@"execute as login = 'BUILTIN\Administrators'", 15404);
    }

    [TestMethod]
    public void XpLoginInfo_ListsAndResolvesTheWindowsLogins()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(
            new[]
            {
                @"BUILTIN\Administrators|group|admin|BUILTIN\Administrators|NULL",
                @"NT AUTHORITY\NETWORK SERVICE|user|admin|NT AUTHORITY\NETWORK SERVICE|NULL",
                @"NT AUTHORITY\SYSTEM|user|user|NT AUTHORITY\SYSTEM|NULL",
            },
            Rows(sim, "exec xp_logininfo"));
        CollectionAssert.AreEqual(new[] { @"nt authority\system|user|admin|nt authority\system|BUILTIN\Administrators" }, Rows(sim, @"exec xp_logininfo 'nt authority\system'"));
        CollectionAssert.AreEqual(
            new[] { @"NT AUTHORITY\SYSTEM|user|admin|NT AUTHORITY\SYSTEM|BUILTIN\Administrators", @"NT AUTHORITY\SYSTEM|user|user|NT AUTHORITY\SYSTEM|NULL" },
            Rows(sim, @"exec xp_logininfo 'NT AUTHORITY\SYSTEM', 'ALL '"));
        IsEmpty(Rows(sim, @"exec xp_logininfo 'NT AUTHORITY\LOCAL SERVICE', 'all'"));
        IsEmpty(Rows(sim, @"exec xp_logininfo 'NT AUTHORITY\Authenticated Users', 'members'"));
        IsEmpty(Rows(sim, @"exec xp_logininfo 'DOM\x', 'members'"));
        AreEqual("admin|NULL|zz", Rows(sim, @"declare @p varchar(10), @q varchar(10), @r varchar(10) = 'zz';
            exec xp_logininfo 'NT AUTHORITY\SYSTEM', 'all', @p output;
            exec xp_logininfo 'NT AUTHORITY\LOCAL SERVICE', 'all', @q output;
            exec xp_logininfo 'NT AUTHORITY\SYSTEM', 'all', @r;
            select @p, @q, @r")[0]);
        using var reader = sim.ExecuteReader("exec xp_logininfo");
        AreEqual("permission path", reader.GetName(4));
        AreEqual("varchar", reader.GetDataTypeName(1));
        AreEqual("P ", sim.ExecuteScalar("select type from sys.all_objects where name = 'xp_logininfo'"));
    }

    [TestMethod]
    public void XpLoginInfo_RefusesAsTheLinuxServerDoes()
    {
        var sim = new Simulation();
        var refusal = sim.AssertSqlError(@"exec xp_logininfo 'BUILTIN\Administrators'", 15404);
        AreEqual(@"Could not obtain information about Windows NT group/user 'BUILTIN\Administrators', error code 0x54f.", refusal.Errors[0].Message);
        AreEqual(11, refusal.Errors[0].State);
        AreEqual(64, refusal.Errors[0].LineNumber);
        AreEqual(10, sim.AssertSqlError("exec xp_logininfo 'sa'", 15404).Errors[0].State);
        AreEqual(5, sim.AssertSqlError(@"exec xp_logininfo 'NT AUTHORITY\SYSTEM', 'members'", 15404).Errors[0].State);
        AreEqual(3, sim.AssertSqlError(@"exec xp_logininfo 'BUILTIN\Administrators', 'members'", 15404).Errors[0].State);
        // The lookup failure ends the batch and dooms a transaction a TRY catches it in.
        AreEqual("15404|-1", Rows(sim, @"begin tran; begin try exec xp_logininfo 'DOM\x'; end try begin catch select error_number(), xact_state(); end catch; if @@trancount > 0 rollback")[0]);
        // An option it doesn't know is Msg 15600 with return code 1, and the batch goes on.
        AreEqual("1", Rows(sim, @"declare @r int; begin try exec @r = xp_logininfo 'NT AUTHORITY\SYSTEM', 'bogus' end try begin catch end catch; select @r")[0]);
        _ = sim.AssertSqlError(@"exec xp_logininfo 'NT AUTHORITY\SYSTEM', 'bogus'", 15600);
        _ = sim.AssertSqlError(@"exec xp_logininfo null, 'members'", 15600);
        _ = sim.AssertSqlError(@"declare @p varchar(10); exec xp_logininfo 'NT AUTHORITY\SYSTEM', null, @p output", 15600);
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off");
        AreEqual(229, sim.AssertSqlError("use master; execute as login = 'lo'; exec xp_logininfo", 229).Number);
    }

    [TestMethod]
    public void CreateLoginFromWindows_MakesNone()
    {
        var sim = new Simulation();
        sim.AssertSqlError(@"create login [NT AUTHORITY\SYSTEM] from windows", 15025, @"The server principal 'NT AUTHORITY\SYSTEM' already exists.");
        AreEqual(2, sim.AssertSqlError(@"create login [NT AUTHORITY\SYSTEM] from windows", 15025).Errors[0].State);
        sim.AssertSqlError(@"create login [NT AUTHORITY\LOCAL SERVICE] from windows with default_language = us_english", 15151,
            @"Cannot create the login 'NT AUTHORITY\LOCAL SERVICE', because it does not exist or you do not have permission.");
        sim.AssertSqlError(@"create login [BUILTIN\Administrators] from windows", 15401, @"Windows NT user or group 'BUILTIN\Administrators' not found. Check the name again.");
        sim.AssertSqlError(@"create login [DOM\someone] from windows", 15401, @"Windows NT user or group 'DOM\someone' not found. Check the name again.");
        sim.AssertSqlError("create login someone from windows", 15407, "'someone' is not a valid Windows NT name. Give the complete name: <domain\\username>.");
        _ = sim.AssertSqlError(@"create login [DOM\someone] from windows with default_database = nosuchdb", 15010);
        sim.AssertSqlError(@"create login [DOM\someone] from windows with check_policy = off", 156, "Incorrect syntax near the keyword 'off'.");
        sim.AssertSqlError(@"create login [DOM\someone] from windows with password = 'x'", 102, "Incorrect syntax near 'x'.");
        sim.AssertSqlError(@"create login [DOM\someone] with password = 'x'", 15006, @"'DOM\someone' is not a valid name because it contains invalid characters.");
    }

    [TestMethod]
    public void AlterAndDropLogin_TakeTheWindowsLogins()
    {
        var sim = new Simulation();
        sim.AssertSqlError(@"alter login [NT AUTHORITY\SYSTEM] with password = 'Abcdefg!123'", 15080, "Cannot use parameter PASSWORD for a Windows login.");
        sim.AssertSqlError(@"alter login [NT AUTHORITY\SYSTEM] with check_policy = off", 15080, "Cannot use parameter CHECK_POLICY for a Windows login.");
        _ = sim.ExecuteNonQuery(@"alter login [NT AUTHORITY\SYSTEM] disable");
        IsTrue((bool)sim.ExecuteScalar(@"select is_disabled from sys.server_principals where principal_id = 261")!);
        sim.AssertSqlError(@"drop login [BUILTIN\Users]", 15401, @"Windows NT user or group 'BUILTIN\Users' not found. Check the name again.");
        sim.AssertSqlError(@"drop login [NT AUTHORITY\LOCAL SERVICE]", 15151, @"Cannot drop the login 'NT AUTHORITY\LOCAL SERVICE', because it does not exist or you do not have permission.");
        _ = sim.ExecuteNonQuery(@"drop login [NT AUTHORITY\NETWORK SERVICE]");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.server_principals where type in ('U', 'G')"));
    }

    [TestMethod]
    public void SpDenyAndGrantLogin_TurnConnectSqlOffAndOn()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar(@"declare @r int; exec @r = sp_denylogin 'NT AUTHORITY\SYSTEM'; select @r"));
        AreEqual("10|1|0", Rows(sim, @"select status, denylogin, hasaccess from sys.syslogins where name = 'NT AUTHORITY\SYSTEM'")[0]);
        AreEqual("D", sim.ExecuteScalar("select state from sys.server_permissions where grantee_principal_id = 261 and type = 'COSQ'"));
        AreEqual(0, sim.ExecuteScalar(@"declare @r int; exec @r = sp_grantlogin 'NT AUTHORITY\SYSTEM'; select @r"));
        AreEqual("9|0|1", Rows(sim, @"select status, denylogin, hasaccess from sys.syslogins where name = 'NT AUTHORITY\SYSTEM'")[0]);
    }

    [TestMethod]
    public void SpGrantAndDenyLogin_RefuseWhatTheyCantCreate()
    {
        var sim = new Simulation();
        var refusal = sim.AssertSqlError(@"exec sp_grantlogin 'DOM\someone'", 15401);
        AreEqual(11, refusal.Errors[0].Class);
        AreEqual(49, refusal.Errors[0].LineNumber);
        AreEqual("sp_grantlogin", refusal.Errors[0].Procedure);
        // A well-known account's CREATE LOGIN fails first, from line 1 in no procedure.
        var created = sim.AssertSqlError(@"exec sp_denylogin 'NT AUTHORITY\LOCAL SERVICE'", 15151);
        CollectionAssert.AreEqual(new[] { "15151||1", "15401|sp_denylogin|46" }, created.Errors.Select(static error => $"{error.Number}|{error.Procedure}|{error.LineNumber}").ToArray());
        AreEqual(28, sim.AssertSqlError("exec sp_grantlogin 'sa'", 15407).Errors[0].LineNumber);
        AreEqual(32, sim.AssertSqlError("exec sp_denylogin ''", 15407).Errors[0].LineNumber);
        AreEqual(21, sim.AssertSqlError(@"begin tran; exec sp_grantlogin 'DOM\x'", 15002).Errors[0].LineNumber);
        AreEqual(25, sim.AssertSqlError(@"begin tran; exec sp_denylogin 'DOM\x'", 15002).Errors[0].LineNumber);
        AreEqual("1", Rows(sim, @"declare @r int; begin try exec @r = sp_grantlogin 'DOM\someone' end try begin catch end catch; select @r")[0]);
        _ = sim.ExecuteNonQuery("create login lo with password = 'x', check_policy = off");
        AreEqual(13, sim.AssertSqlError(@"use master; execute as login = 'lo'; exec sp_grantlogin 'NT AUTHORITY\SYSTEM'", 15247).Errors[0].LineNumber);
    }

    [TestMethod]
    public void SpRevokeLogin_DropsTheLogin()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar(@"declare @r int; exec @r = sp_revokelogin 'NT AUTHORITY\NETWORK SERVICE'; select @r"));
        AreEqual(0, sim.ExecuteScalar(@"select count(*) from sys.server_principals where name = 'NT AUTHORITY\NETWORK SERVICE'"));
        sim.AssertSqlError(@"exec sp_revokelogin 'DOM\someone'", 15401, @"Windows NT user or group 'DOM\someone' not found. Check the name again.");
        AreEqual(25, sim.AssertSqlError("exec sp_revokelogin 'someone'", 15407).Errors[0].LineNumber);
        AreEqual("sys.sp_validname", sim.AssertSqlError("exec sp_revokelogin null", 15004).Errors[0].Procedure);
        AreEqual(13, sim.AssertSqlError(@"begin tran; exec sp_revokelogin 'DOM\x'", 15002).Errors[0].LineNumber);
    }

    [TestMethod]
    public void ValidateLogins_ListsTheGroupNoLookupFinds()
    {
        var sim = new Simulation();
        CollectionAssert.AreEqual(new[] { @"0x01020000000000052000000020020000|BUILTIN\Administrators" }, Rows(sim, "exec sp_validatelogins"));
        _ = sim.ExecuteNonQuery(@"drop login [BUILTIN\Administrators]");
        IsEmpty(Rows(sim, "exec sp_validatelogins"));
    }

    [TestMethod]
    public void WindowsLogins_LeaveAuthenticationOpen()
    {
        var sim = new Simulation();
        using (var open = sim.CreateDbConnection())
        {
            open.ConnectionString = "User ID=whoever;Password=whatever";
            open.Open();
        }
        _ = sim.ExecuteNonQuery("create login app with password = 'S3cret!Pass'");
        using DbConnection connection = sim.CreateDbConnection();
        connection.ConnectionString = @"User ID=NT AUTHORITY\SYSTEM;Password=whatever";
        AreEqual(18456, Throws<SimulatedSqlException>(connection.Open).Number);
    }
}
