using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The pre-DDL system procedures: <c>sp_addtype</c> / <c>sp_droptype</c>, the login,
/// user, role and server-role management set, the table / index / statistics option
/// procedures and the small report procedures.
/// </summary>
[TestClass]
public sealed class LegacySystemProcedureTests
{
    [TestMethod]
    public void AddType_CreatesAliasType()
        => AreEqual("varchar", new Simulation().ExecuteScalar("""
            exec sp_addtype zip, 'varchar(10)', 'not null';
            select type_name(system_type_id) from sys.types where name = 'zip'
            """));

    [TestMethod]
    public void DropType_RemovesAliasType()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            exec sp_addtype zip, 'varchar(10)';
            exec sp_droptype zip;
            select count(*) from sys.types where name = 'zip'
            """));

    [TestMethod]
    public void DropType_Missing_Raises15036()
        => _ = new Simulation().AssertSqlError("exec sp_droptype nosuch", 15036);

    [TestMethod]
    public void AddLogin_CreatesSqlLogin()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            exec sp_addlogin 'zl1', 'Passw0rd!x';
            select count(*) from sys.server_principals where name = 'zl1'
            """));

    [TestMethod]
    public void DropLogin_Missing_Raises15007()
        => _ = new Simulation().AssertSqlError("exec sp_droplogin 'nosuch'", 15007);

    [TestMethod]
    public void CheckPolicy_ShortPassword_Raises33062()
        => _ = new Simulation().AssertSqlError("create login zl2 with password = 'Ab1!'", 33062);

    [TestMethod]
    public void AddUser_CreatesUserWithLoginLink()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create login zl3 with password = 'Passw0rd!x';
            exec sp_adduser 'zl3';
            select count(*) from sys.database_principals where name = 'zl3'
            """));

    [TestMethod]
    public void AddRole_ThenAddRoleMember_ListsMember()
        => AreEqual("zr1", new Simulation().ExecuteScalar("""
            create user zu1 without login;
            exec sp_addrole 'zr1';
            exec sp_addrolemember 'zr1', 'zu1';
            select top 1 r.name from sys.database_role_members m join sys.database_principals r on r.principal_id = m.role_principal_id join sys.database_principals u on u.principal_id = m.member_principal_id where u.name = 'zu1'
            """));

    [TestMethod]
    public void TableOption_TextInRow_SetsCatalogColumn()
        => AreEqual(256, new Simulation().ExecuteScalar("""
            create table t (a int, b text);
            exec sp_tableoption 't', 'text in row', 'on';
            select text_in_row_limit from sys.tables where name = 't'
            """));

    [TestMethod]
    public void TableOption_UnknownTable_Raises15388()
        => _ = new Simulation().AssertSqlError("exec sp_tableoption 'nosuch', 'text in row', 'on'", 15388);

    [TestMethod]
    public void IndexOption_DisallowPageLocks_ShowsInCatalog()
        => IsFalse((bool)new Simulation().ExecuteScalar("""
            create table t (a int);
            create index ix on t (a);
            exec sp_indexoption 't.ix', 'DisallowPageLocks', 'true';
            select allow_page_locks from sys.indexes where name = 'ix'
            """)!);

    [TestMethod]
    public void AutoStats_Off_SetsNoRecompute()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table t (a int);
            create index ix on t (a);
            exec sp_autostats 't', 'OFF', 'ix';
            select no_recompute from sys.stats where name = 'ix'
            """)!);

    [TestMethod]
    public void CreateStats_CreatesAutoNamedStatistic()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (a int, b int);
            exec sp_createstats;
            select count(*) from sys.stats where object_id = object_id('t') and name = '_WA_Sys_a' or name like 'a'
            """));

    [TestMethod]
    public void HelpLanguage_ListsEnglishWithMonthNames()
        => AreEqual("January,February,March,April,May,June,July,August,September,October,November,December",
            new Simulation().ExecuteScalar("select months from sys.syslanguages where name = 'us_english'"));

    [TestMethod]
    public void HelpLanguage_Unknown_Raises15033()
        => _ = new Simulation().AssertSqlError("exec sp_helplanguage 'bogus'", 15033);

    [TestMethod]
    public void HelpServer_Unknown_Raises15015()
        => _ = new Simulation().AssertSqlError("exec sp_helpserver 'nope'", 15015);

    [TestMethod]
    public void HelpSort_DescribesDefaultCollation()
        => AreEqual("Latin1-General, case-insensitive, accent-sensitive, kanatype-insensitive, width-insensitive for Unicode Data, SQL Server Sort Order 52 on Code Page 1252 for non-Unicode Data",
            new Simulation().ExecuteScalar("exec sp_helpsort"));

    [TestMethod]
    public void Monitor_ReturnsFourResultSets()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var reader = connection.CreateCommand("exec sp_monitor").ExecuteReader();
        var sets = 1;
        while (reader.NextResult())
            sets++;
        AreEqual(4, sets);
    }

    [TestMethod]
    public void ForEachWorker_WithoutCommand_Raises201()
        => _ = new Simulation().AssertSqlError("exec sp_MSforeach_worker", 201);

    private static List<string> Messages(Simulation simulation, string sql)
    {
        using var connection = (SimulatedDbConnection)simulation.CreateOpenConnection();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Message));
        _ = connection.CreateCommand(sql).ExecuteNonQuery();
        return messages;
    }

    private static void AssertProcedureError(Simulation sim, string sql, int number, string procedure, int line, string message)
    {
        var ex = sim.AssertSqlError(sql, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual(procedure, ex.Procedure);
        AreEqual(line, ex.LineNumber);
    }

    private static Simulation WithLogins(params string[] names)
    {
        var sim = new Simulation();
        foreach (var name in names)
            _ = sim.ExecuteNonQuery($"create login {name} with password = 'Zc0v!Passw0rd', check_policy = off");
        return sim;
    }

    [TestMethod]
    public void GrantDbAccess_CreatesUserAndOwnSchema_RevokeDbAccessDropsBoth()
    {
        var sim = WithLogins("zl1", "zl2");
        _ = sim.ExecuteNonQuery("exec sp_grantdbaccess 'zl1'; exec sp_grantdbaccess 'zl2', 'zu2'");
        AreEqual("zl1:zl1,zu2:zu2", sim.ExecuteScalar("select string_agg(concat(name, ':', default_schema_name), ',') within group (order by name) from sys.database_principals where name in ('zl1', 'zu2')"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.schemas where name in ('zl1', 'zu2')"));
        _ = sim.ExecuteNonQuery("exec sp_dropuser 'zu2'; exec sp_revokedbaccess 'zl1'");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_principals where name in ('zl1', 'zu2')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.schemas where name in ('zl1', 'zu2')"));
    }

    [TestMethod]
    public void GrantDbAccess_UnknownLogin_Raises15007AtLine1()
    {
        var ex = new Simulation().AssertSqlError("exec sp_grantdbaccess 'nope'", 15007);
        AreEqual(1, ex.LineNumber);
        AreEqual("'nope' is not a valid login or you do not have permission.", ex.Errors[0].Message);
    }

    [TestMethod]
    public void DropUser_Refusals()
    {
        var sim = new Simulation();
        AssertProcedureError(sim, "exec sp_dropuser 'nope'", 15008, "sp_dropuser", 12, "User 'nope' does not exist in the current database.");
        AssertProcedureError(sim, "exec sp_dropuser null", 15008, "sp_dropuser", 12, "User '(null)' does not exist in the current database.");
        AssertProcedureError(sim, "exec sp_dropuser 'dbo'", 15150, "sys.sp_revokedbaccess", 51, "Cannot drop the user 'dbo'.");
        AssertProcedureError(sim, "exec sp_revokedbaccess 'nope'", 15151, "sp_revokedbaccess", 51, "Cannot drop the user 'nope', because it does not exist or you do not have permission.");
        AssertProcedureError(sim, "exec sp_revokedbaccess 'dbo'", 15150, "sp_revokedbaccess", 51, "Cannot drop the user 'dbo'.");
    }

    [TestMethod]
    public void DropRole_WithMembers_Raises15144_EmptyRoleDrops()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user zu without login; exec sp_addrole 'zr'; exec sp_addrolemember 'zr', 'zu'");
        var ex = sim.AssertSqlError("exec sp_droprole 'zr'", 15144);
        AreEqual(1, ex.LineNumber);
        AreEqual(0, sim.ExecuteScalar("exec sp_droprolemember 'zr', 'zu'; exec sp_droprole 'zr'; select count(*) from sys.database_principals where name = 'zr'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.schemas where name = 'zr'"));
    }

    [TestMethod]
    public void DropRole_Refusals()
    {
        var sim = new Simulation();
        AssertProcedureError(sim, "exec sp_droprole 'nope'", 15151, "sp_droprole", 28, "Cannot drop the role 'nope', because it does not exist or you do not have permission.");
        AssertProcedureError(sim, "exec sp_droprole 'db_owner'", 15150, "sp_droprole", 28, "Cannot drop the role 'db_owner'.");
        AssertProcedureError(sim, "exec sp_droprole 'public'", 15150, "sp_droprole", 28, "Cannot drop the role 'public'.");
    }

    [TestMethod]
    public void HelpRoleMember_ListsDboInDbOwnerAndEachMember()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user zu without login; exec sp_addrole 'zr'; exec sp_addrolemember 'zr', 'zu'");
        using var reader = sim.ExecuteReader("exec sp_helprolemember");
        AreEqual("DbRole,MemberName,MemberSID", string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        var rows = reader.EnumerateRecords().Select(record => record.GetString(0) + ":" + record.GetString(1)).ToList();
        CollectionAssert.AreEqual(new[] { "db_owner:dbo", "zr:zu" }, rows);
        AreEqual("zr", sim.ExecuteScalar("exec sp_helprolemember 'zr'"));
        _ = sim.ExecuteNonQuery("create user zv without login");
        var ex = sim.AssertSqlError("exec sp_helprolemember 'zv'", 15409);
        AreEqual("'zv' is not a role.", ex.Errors[0].Message);
        AreEqual((byte)11, ex.Class);
        AreEqual(9, ex.LineNumber);
    }

    [TestMethod]
    public void AddAndDropServerRoleMember()
    {
        var sim = WithLogins("zl1");
        _ = sim.ExecuteNonQuery("exec sp_addsrvrolemember 'zl1', 'dbcreator'");
        const string count = "select count(*) from sys.server_role_members m join sys.server_principals l on l.principal_id = m.member_principal_id where l.name = 'zl1'";
        AreEqual(1, sim.ExecuteScalar(count));
        _ = sim.ExecuteNonQuery("exec sp_dropsrvrolemember 'zl1', 'dbcreator'; exec sp_dropsrvrolemember 'zl1', 'dbcreator'");
        AreEqual(0, sim.ExecuteScalar(count));
        AssertProcedureError(sim, "exec sp_addsrvrolemember 'nope', 'dbcreator'", 15007, "sp_addsrvrolemember", 33, "'nope' is not a valid login or you do not have permission.");
        var ex = sim.AssertSqlError("exec sp_addsrvrolemember 'zl1', 'nope_role'", 15151);
        AreEqual(1, ex.LineNumber);
    }

    [TestMethod]
    public void ChangeUsersLogin_ReportListsUsersWhoseLoginIsGone()
    {
        var sim = WithLogins("zo1", "zo2", "zo3");
        _ = sim.ExecuteNonQuery("create user zo1 for login zo1; create user zo2 for login zo2; create user zx for login zo3; drop login zo1; drop login zo2");
        using var reader = sim.ExecuteReader("exec sp_change_users_login 'Report'");
        AreEqual("UserName,UserSID", string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        CollectionAssert.AreEqual(new[] { "zo1", "zo2" }, reader.EnumerateRecords().Select(record => record.GetString(0)).ToList());
    }

    [TestMethod]
    [DataRow("exec sp_change_users_login 'Report', 'zo1'", 15600, 59)]
    [DataRow("exec sp_change_users_login null", 15600, 206)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zo1'", 15600, 99)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zo1', 'nope'", 15600, 128)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zx', 'nope'", 15600, 128)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zabsent', 'nope'", 15291, 123)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zabsent', 'zo3'", 15291, 140)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zrole', 'zo3'", 15291, 140)]
    [DataRow("exec sp_change_users_login 'Update_One', 'znl', 'zo3'", 15291, 140)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zo1', 'zo3'", 15063, 175)]
    [DataRow("exec sp_change_users_login 'Auto_Fix', null", 15600, 206)]
    [DataRow("exec sp_change_users_login 'Auto_Fix', 'zo1', 'zo3'", 15600, 206)]
    [DataRow("exec sp_change_users_login 'Auto_Fix', 'zo1'", 15600, 239)]
    [DataRow("exec sp_change_users_login 'Bogus'", 15286, 186)]
    [DataRow("exec sp_change_users_login 'Update_One', 'zabsent', 'sa'", 15287, 36)]
    [DataRow("exec sp_change_users_login 'Bogus', 'DBO'", 15287, 41)]
    [DataRow("exec sp_change_users_login 'Report', 'guest'", 15287, 41)]
    [DataRow("exec sp_change_users_login 'Auto_Fix', 'sys'", 15287, 41)]
    [DataRow("exec sp_change_users_login 'Update_One', 'INFORMATION_SCHEMA', 'zo3'", 15287, 41)]
    public void ChangeUsersLogin_Refusals(string sql, int number, int line)
    {
        var sim = WithLogins("zo1", "zo3");
        _ = sim.ExecuteNonQuery("create user zo1 for login zo1; create user zx for login zo3; create role zrole; create user znl without login; drop login zo1");
        var ex = sim.AssertSqlError(sql, number);
        AreEqual(line, ex.LineNumber);
        AreEqual("sp_change_users_login", ex.Procedure);
    }

    [TestMethod]
    public void ChangeUsersLogin_RefusalTexts()
    {
        var sim = new Simulation();
        sim.AssertSqlError("exec sp_change_users_login 'Bogus'", 15286, "Terminating this procedure. The @action 'Bogus' is unrecognized. Try 'REPORT', 'UPDATE_ONE', or 'AUTO_FIX'.");
        sim.AssertSqlError("exec sp_change_users_login 'Update_One', 'zabsent', 'nope'", 15291, "Terminating this procedure. The User name 'zabsent' is absent or invalid.");
        sim.AssertSqlError("exec sp_change_users_login 'Update_One', 'zabsent', 'SA'", 15287, "Terminating this procedure. 'SA' is a forbidden value for the login name parameter in this procedure.");
    }

    [TestMethod]
    public void ChangeUsersLogin_UpdateOne_RelinksAMappedUser_OrphanOrNot()
        => AreEqual("zo1", WithLogins("zo1", "zo3").ExecuteScalar("""
            create user zx for login zo3;
            exec sp_change_users_login 'Update_One', 'zx', 'zo3';
            exec sp_change_users_login 'Update_One', 'zx', 'zo1';
            select sp.name from sys.database_principals dp join sys.server_principals sp on sp.sid = dp.sid where dp.name = 'zx'
            """));

    [TestMethod]
    public void ChangeUsersLogin_AutoFix_ReportsHowEachOrphanWasFixed()
    {
        var sim = WithLogins("zo1", "zo2");
        _ = sim.ExecuteNonQuery("create user zo1 for login zo1; create user zo2 for login zo2; drop login zo1; drop login zo2");
        CollectionAssert.AreEqual(new[]
        {
            "Barring a conflict, the row for user 'zo1' will be fixed by updating its link to a new login.",
            "The number of orphaned users fixed by updating users was 0.",
            "The number of orphaned users fixed by adding new logins and then updating users was 1.",
        }, Messages(sim, "exec sp_change_users_login 'Auto_Fix', 'zo1', null, 'Zc0v!Passw0rd'"));
        _ = sim.ExecuteNonQuery("create login zo2 with password = 'Zc0v!Passw0rd', check_policy = off");
        CollectionAssert.AreEqual(new[]
        {
            "The row for user 'zo2' will be fixed by updating its login link to a login already in existence.",
            "The number of orphaned users fixed by updating users was 1.",
            "The number of orphaned users fixed by adding new logins and then updating users was 0.",
        }, Messages(sim, "exec sp_change_users_login 'Auto_Fix', 'zo2'"));
        AreEqual(0, sim.ExecuteScalar("drop table if exists #r; create table #r (u sysname, s varbinary(85)); insert #r exec sp_change_users_login 'Report'; select count(*) from #r"));
    }

    [TestMethod]
    public void DefaultDbAndDefaultLanguage_AlterTheLogin()
    {
        var sim = WithLogins("zl5");
        AreEqual("tempdb|French", sim.ExecuteScalar("""
            exec sp_defaultdb 'zl5', 'tempdb';
            exec sp_defaultlanguage 'zl5', 'French';
            select default_database_name + '|' + default_language_name from sys.server_principals where name = 'zl5'
            """));
        AreEqual("us_english", sim.ExecuteScalar("exec sp_defaultlanguage 'zl5'; select default_language_name from sys.server_principals where name = 'zl5'"));
        AssertProcedureError(sim, "exec sp_defaultdb 'zl5', null", 15010, "sp_defaultdb", 26, "The database '(null)' does not exist. Supply a valid database name. To see available databases, use sys.databases.");
        AssertProcedureError(sim, "exec sp_defaultdb 'nope', 'master'", 15007, "sp_defaultdb", 41, "'nope' is not a valid login or you do not have permission.");
        AssertProcedureError(sim, "exec sp_defaultlanguage 'nope', 'French'", 15007, "sp_defaultlanguage", 34, "'nope' is not a valid login or you do not have permission.");
        AreEqual(1, sim.AssertSqlError("exec sp_defaultdb 'zl5', 'nodb'", 15010).LineNumber);
        sim.AssertSqlError("exec sp_defaultdb 'sysadmin', 'tempdb'", 15405, "Cannot use the special principal 'sysadmin'.");
    }

    [TestMethod]
    public void AddLogin_SkipEncryption_Raises15021()
    {
        var ex = new Simulation().AssertSqlError("exec sp_addlogin 'zl6', 'Zc0v!Passw0rd', @encryptopt = 'skip_encryption'", 15021);
        AreEqual("Invalid value given for parameter PASSWORD. Specify a valid parameter value.", ex.Errors[0].Message);
        AreEqual((byte)2, ex.State);
    }

    [TestMethod]
    public void UpdateStats_UpdatesWrittenTablesStatistics_ThenFindsThemCurrent()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int constraint pk primary key, b int); create index ix on t (b); create statistics st on t (b); insert t values (1, 1)");
        CollectionAssert.AreEqual(new[]
        {
            "Updating [dbo].[t]",
            "    [pk] has been updated...",
            "    [ix] has been updated...",
            "    [st] has been updated...",
            "    3 index(es)/statistic(s) have been updated, 0 did not require update.",
            " ",
        }, Messages(sim, "exec sp_updatestats").Take(6).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            "Updating [dbo].[t]",
            "    [pk], update is not necessary...",
            "    [ix], update is not necessary...",
            "    [st], update is not necessary...",
            "    0 index(es)/statistic(s) have been updated, 3 did not require update.",
            " ",
        }, Messages(sim, "exec sp_updatestats 'resample'").Take(6).ToArray());
        AssertProcedureError(sim, "exec sp_updatestats 'bogus'", 14138, "sp_updatestats", 27, "Invalid option name 'bogus   '.");
    }

    [TestMethod]
    public void AutoStats_Refusals()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int); create index ix on t (a)");
        AssertProcedureError(sim, "exec sp_autostats 'nope'", 15390, "sp_autostats", 46, "Input name 'nope' does not have a matching user table or indexed view in the current database.");
        AssertProcedureError(sim, "exec sp_autostats 'otherdb.dbo.t'", 15387, "sp_autostats", 32, "If the qualified object name specifies a database, that database must be the current database.");
        AssertProcedureError(sim, "exec sp_autostats 't', 'ON', 'nope'", 15323, "sp_autostats", 119, "The selected index does not exist on table 't'.");
    }

    [TestMethod]
    public void TableOption_TextInRow_Refusals()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b text); create table u (a int, v varchar(max))");
        const string invalid = "The third parameter for table option 'text in row' is invalid. It should be 'on', 'off', '0', '1' or a number from 24 through 7000.";
        AssertProcedureError(sim, "exec sp_tableoption 't', 'text in row', '20'", 15112, "sp_tableoption", 63, invalid);
        AssertProcedureError(sim, "exec sp_tableoption 't', 'text in row', '7001'", 15112, "sp_tableoption", 63, invalid);
        AreEqual(100, sim.ExecuteScalar("exec sp_tableoption 't', 'text in row', '100'; select text_in_row_limit from sys.tables where name = 't'"));
        foreach (var value in new[] { "on", "off", "0" })
            AssertProcedureError(sim, $"exec sp_tableoption 'u', 'text in row', '{value}'", 2599, "sp_tableoption", 102, "Cannot switch to in row text in table \"u\".");
    }

    [TestMethod]
    public void AddType_Refusals()
    {
        var sim = new Simulation();
        AssertProcedureError(sim, "exec sp_addtype zt1, 'int', 'maybe'", 15085, "sp_addtype", 51, "Usage: sp_addtype name, 'data type' [,'NULL' | 'NOT NULL']");
        const string max = "sp_addtype cannot be used to define user-defined data types for varchar(max), nvarchar(max) or varbinary(max) data types. Use CREATE TYPE for this purpose.";
        AssertProcedureError(sim, "exec sp_addtype zt2, 'varchar(max)'", 15108, "sp_addtype", 139, max);
        AssertProcedureError(sim, "exec sp_addtype zt4, 'nvarchar(max)', 'not null'", 15108, "sp_addtype", 139, max);
        AssertProcedureError(sim, "exec sp_addtype zt3, 'xml'", 15656, "sp_addtype", 146, "Cannot create user defined types from XML data type.");
    }

    [TestMethod]
    public void AddUser_UnknownGroup_Raises15014()
        => AssertProcedureError(WithLogins("zl1"), "exec sp_adduser 'zl1', 'zu', 'nogroup'", 15014, "sp_adduser", 23, "The role 'nogroup' does not exist in the current database.");

    /// <summary>
    /// <c>xp_cmdshell</c> and the OLE Automation procedures are blocked
    /// components on a stock server: Msg 15281 attributed to the procedure as
    /// called, the batch running on (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec master..xp_cmdshell 'dir'", "master..xp_cmdshell", "xp_cmdshell", "xp_cmdshell")]
    [DataRow("declare @o int, @hr int; exec @hr = sp_OACreate 'MSXML2.XMLHTTP', @o out", "sp_OACreate", "sp_OACreate", "Ole Automation Procedures")]
    [DataRow("exec sp_OAStop", "sp_OAStop", "sp_OAStop", "Ole Automation Procedures")]
    public void BlockedComponent_RaisesMsg15281(string sql, string calledAs, string procedure, string component)
    {
        var ex = new Simulation().AssertSqlError(sql, 15281);
        AreEqual(
            ($"SQL Server blocked access to procedure 'sys.{procedure}' of component '{component}' because this component is turned off as part of the security configuration for this server. A system administrator can enable the use of '{component}' by using sp_configure. For more information about enabling '{component}', search for '{component}' in SQL Server Books Online.", calledAs),
            (ex.Errors[0].Message, ex.Errors[0].Procedure));
    }

    [TestMethod]
    public void BlockedComponent_LeavesTheBatchRunning()
    {
        var simulation = new Simulation();
        _ = simulation.AssertSqlError("exec xp_cmdshell 'dir'; create table after_it (a int)", 15281);
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.tables where name = 'after_it'"));
    }

    [TestMethod]
    public void BlockedComponent_IsAnExtendedProcedure()
        => AreEqual("X|X", new Simulation().ExecuteScalar("""
            select concat_ws('|', (select rtrim(type) from master.sys.all_objects where object_id = object_id('sys.xp_cmdshell')),
                (select rtrim(type) from master.sys.all_objects where name = 'sp_OACreate'))
            """));

    [TestMethod]
    public void EnabledComponent_RaisesNotSupported()
        => _ = Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery("""
            exec sp_configure 'show advanced options', 1; reconfigure;
            exec sp_configure 'xp_cmdshell', 1; reconfigure;
            exec xp_cmdshell 'dir'
            """));

    /// <summary>
    /// <c>sp_dbcmptlevel</c> sets, reports and lists compatibility levels with
    /// real's messages from real's lines in the procedure (probed 2026-10-15
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void DbCmptLevel_SetsTheLevel()
        => AreEqual("0|150", new Simulation().ExecuteScalar("""
            create database d;
            declare @r int;
            exec @r = sp_dbcmptlevel 'd', 150;
            select concat_ws('|', @r, (select compatibility_level from sys.databases where name = 'd'))
            """));

    [TestMethod]
    [DataRow("exec sp_dbcmptlevel 'd', 55", 15416, "Usage: sp_dbcmptlevel [dbname [, compatibilitylevel]]", 77)]
    [DataRow("exec sp_dbcmptlevel 'nosuch', 150", 15010, "The database 'nosuch' does not exist. Supply a valid database name. To see available databases, use sys.databases.", 54)]
    [DataRow("begin tran; exec sp_dbcmptlevel 'd', 140", 15002, "The procedure 'sys.sp_dbcmptlevel' cannot be executed within a transaction.", 97)]
    public void DbCmptLevel_Refusals(string sql, int number, string message, int line)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create database d");
        var ex = simulation.AssertSqlError(sql, number);
        AreEqual((message, "sp_dbcmptlevel", line), (ex.Errors[0].Message, ex.Errors[0].Procedure, ex.Errors[0].LineNumber));
    }

    [TestMethod]
    public void DbCmptLevel_MissingDatabase_IsCaught()
        => AreEqual(15010, new Simulation().ExecuteScalar("""
            declare @caught int;
            begin try exec sp_dbcmptlevel 'nosuch', 150 end try begin catch set @caught = error_number() end catch;
            select @caught
            """));

    [TestMethod]
    public void DbCmptLevel_ReportsAndLists()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static m => $"{m.Number}|{m.LineNumber}|{m.Message}"));
        _ = connection.CreateCommand("exec sp_dbcmptlevel 'simulated'; exec sp_dbcmptlevel").ExecuteNonQuery();
        AreEqual(
            "15054|69|The current compatibility level is 170. 15048|42|Valid values of the database compatibility level are 100, 110, 120, 130, 140, 150, 160 or 170.",
            string.Join(" ", messages));
    }

    [TestMethod]
    public void RenameDb_RenamesWithModifyNamesNotice()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static m => $"{m.Number}|{m.Message}"));
        _ = connection.CreateCommand("create database d; exec sp_renamedb 'd', 'e'").ExecuteNonQuery();
        AreEqual("5021|The database name 'e' has been set.", string.Join(" ", messages));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from sys.databases where name = 'e'"));
        var ex = simulation.AssertSqlError("exec sp_renamedb 'e', 'master'", 15032);
        AreEqual(("sp_renamedb", 36), (ex.Errors[0].Procedure, ex.Errors[0].LineNumber));
    }

    /// <summary>
    /// The application-role procedures: <c>sp_addapprole</c> gives the role a
    /// schema of its own name, which <c>sp_dropapprole</c> removes with it
    /// (probed 2026-10-15 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AppRoleProcedures()
    {
        var simulation = new Simulation();
        AreEqual("ar|APPLICATION_ROLE|ar", simulation.ExecuteScalar("""
            exec sp_addapprole 'ar', 'Pw!12345678';
            exec sp_approlepassword 'ar', 'Pw!87654321';
            select concat_ws('|', default_schema_name, type_desc collate database_default, (select user_name(principal_id) from sys.schemas where name = 'ar'))
            from sys.database_principals where name = 'ar'
            """));
        AreEqual(15023, simulation.AssertSqlError("exec sp_addapprole 'ar', 'Pw!12345678'", 15023).Number);
        AreEqual(0, simulation.ExecuteScalar("exec sp_dropapprole 'ar'; select count(*) from sys.schemas where name = 'ar'"));
        var missing = simulation.AssertSqlError("exec sp_dropapprole 'nosuch'", 15151);
        AreEqual(("sp_dropapprole", 28), (missing.Errors[0].Procedure, missing.Errors[0].LineNumber));
        simulation.AssertSqlError("exec sp_approlepassword 'nosuch', 'x'", 15151, "Cannot alter the application role 'nosuch', because it does not exist or you do not have permission.");
    }
}
