using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The stored server permissions and the fixed server roles, enforced for a
/// login that isn't <c>sysadmin</c>: the login gate, the database checks a
/// server permission implies, the server-scope statement gates, and the
/// catalog and scalar surfaces that read the same model (probed 2026-09-29
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ServerPermissionEnforcementTests
{
    /// <summary>A simulation with login <c>app</c>, a user database <c>d</c> holding table <c>t</c>, and <paramref name="setup"/> run in <c>master</c>.</summary>
    private static Simulation WithLogin(string setup = "")
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create database d",
            "use d; create table t (a int); insert t values (1)",
            "create login app with password = 'S3cret!Pass'; create login other with password = 'S3cret!Pass'");
        if (setup.Length > 0)
            sim.ExecuteBatches("use master; " + setup);
        return sim;
    }

    // ---- login gate (in-process front door) ----

    private static SimulatedSqlException OpenFails(Simulation sim, string user, string password)
    {
        var connection = sim.CreateDbConnection();
        connection.ConnectionString = $"User ID={user};Password={password};Database=master";
        return Throws<SimulatedSqlException>(connection.Open);
    }

    [TestMethod]
    public void DeniedConnectSql_RefusesLogin18456() =>
        AreEqual(18456, OpenFails(WithLogin("deny connect sql to app"), "app", "S3cret!Pass").Number);

    [TestMethod]
    public void DisabledLogin_Refuses18470_WrongPasswordStill18456()
    {
        var sim = WithLogin("alter login app disable");
        var disabled = OpenFails(sim, "app", "S3cret!Pass");
        AreEqual(18470, disabled.Number);
        AreEqual("Login failed for user 'app'. Reason: The account is disabled.", disabled.Message);
        AreEqual(18456, OpenFails(sim, "app", "wrong").Number);
    }

    [TestMethod]
    public void DisabledLogin_ProjectsIsDisabled_AndStaysImpersonable()
    {
        var sim = WithLogin("alter login app disable");
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.server_principals where name = 'app'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_disabled from sys.sql_logins where name = 'app'")!);
        AreEqual("app", sim.ExecuteScalar("use master; execute as login = 'app'; select suser_name(); revert"));
        sim.ExecuteBatches("alter login app enable");
        IsFalse((bool)sim.ExecuteScalar("select is_disabled from sys.server_principals where name = 'app'")!);
    }

    // ---- CONNECT ANY DATABASE / CONTROL SERVER identity ----

    [TestMethod]
    public void ConnectAnyDatabase_RunsAsTheLoginsNameAtPrincipalZero() =>
        AreEqual("app|0|public|0", WithLogin("grant connect any database to app").ExecuteScalar(
            "use d; execute as login = 'app'; select user_name() + '|' + cast(user_id() as varchar) + '|' + user_name(0) + '|' + cast((select count(*) from sys.tables) as varchar); revert"));

    [TestMethod]
    public void ConnectAnyDatabase_ReadsNothingItIsNotGranted() =>
        _ = WithLogin("grant connect any database to app").AssertSqlError("use d; execute as login = 'app'; select * from t", 229);

    [TestMethod]
    public void ConnectAnyDatabase_BeatsGuestInMaster() =>
        AreEqual("app", WithLogin("grant connect any database to app").ExecuteScalar("use master; execute as login = 'app'; select user_name(); revert"));

    [TestMethod]
    public void ControlServer_ReadsEverything_ButIsNoSysadmin() =>
        AreEqual("1|0|0", WithLogin("grant control server to app").ExecuteScalar(
            "use d; execute as login = 'app'; select cast((select a from t) as varchar) + '|' + cast(is_srvrolemember('sysadmin') as varchar) + '|' + cast(is_member('db_owner') as varchar); revert"));

    [TestMethod]
    public void ControlServer_DatabaseDenyStillBinds()
    {
        var sim = WithLogin("grant control server to app");
        sim.ExecuteBatches("use d; create user app for login app; deny select on t to app");
        _ = sim.AssertSqlError("use d; execute as login = 'app'; select * from t", 229);
    }

    [TestMethod]
    public void ControlServer_DoesNotReachThroughExecuteAsUser()
    {
        var sim = WithLogin("grant control server to app");
        sim.ExecuteBatches("use d; create user app for login app");
        AreEqual(0, sim.ExecuteScalar("use d; execute as user = 'app'; select has_perms_by_name('dbo.t', 'OBJECT', 'SELECT'); revert"));
    }

    [TestMethod]
    public void ControlServer_RunsDatabaseDdl() =>
        AreEqual(1, WithLogin("grant control server to app").ExecuteScalar(
            "use d; execute as login = 'app'; create table t2 (a int); exec('create schema s2'); drop schema s2; drop table t2; select has_perms_by_name(null, 'DATABASE', 'ALTER'); revert"));

    // ---- server permissions implied in a database ----

    [TestMethod]
    public void SelectAllUserSecurables_ReadsAndReveals_ButDoesNotWrite()
    {
        var sim = WithLogin("grant select all user securables to app");
        sim.ExecuteBatches("use d; create user app for login app");
        AreEqual("1|1", sim.ExecuteScalar("use d; execute as login = 'app'; select cast((select a from t) as varchar) + '|' + cast((select count(*) from sys.tables) as varchar); revert"));
        _ = sim.AssertSqlError("use d; execute as login = 'app'; insert t values (2)", 229);
    }

    [TestMethod]
    public void SelectAllUserSecurables_WithoutAUser_IsRefused916() =>
        _ = WithLogin("grant select all user securables to app").AssertSqlError("use d; execute as login = 'app'", 916);

    [TestMethod]
    public void SelectAllUserSecurables_DatabaseDenyBinds()
    {
        var sim = WithLogin("grant select all user securables to app");
        sim.ExecuteBatches("use d; create user app for login app; deny select on t to app");
        _ = sim.AssertSqlError("use d; execute as login = 'app'; select * from t", 229);
    }

    [TestMethod]
    public void ViewAnyDefinition_RevealsMetadata_ButNotData()
    {
        var sim = WithLogin("grant view any definition to app");
        sim.ExecuteBatches("use d; create user app for login app");
        AreEqual(1, sim.ExecuteScalar("use d; execute as login = 'app'; select count(*) from sys.tables; revert"));
        _ = sim.AssertSqlError("use d; execute as login = 'app'; select * from t", 229);
    }

    [TestMethod]
    public void AlterAnyDatabase_ImpliesEachDatabasesAlter()
    {
        var sim = WithLogin("grant alter any database to app");
        sim.ExecuteBatches("use d; create user app for login app");
        AreEqual("1|1|1", sim.ExecuteScalar("""
            use d; execute as login = 'app';
            create table t2 (a int); drop table t2;
            select cast((select count(*) from sys.tables) as varchar) + '|' + cast(has_perms_by_name(null, 'DATABASE', 'ALTER') as varchar)
                + '|' + cast(has_perms_by_name(null, 'DATABASE', 'CREATE TABLE') as varchar);
            revert
            """));
    }

    // ---- server-level DENY and covering ----

    [TestMethod]
    public void ServerDeny_BeatsControlServer() =>
        _ = WithLogin("grant control server to app; deny view server state to app").AssertSqlError(
            "use master; execute as login = 'app'; select count(*) from sys.dm_tran_locks", 300);

    [TestMethod]
    public void ControlServer_ImpersonatesSa() =>
        AreEqual(1, WithLogin("grant control server to app").ExecuteScalar(
            "use master; execute as login = 'app'; execute as login = 'sa'; select is_srvrolemember('sysadmin'); revert; revert"));

    // ---- fixed server roles ----

    [TestMethod]
    [DataRow("serveradmin", "ALTER ANY ENDPOINT, ALTER RESOURCES, ALTER SERVER STATE, ALTER SETTINGS, CONNECT SQL, CREATE ENDPOINT, SHUTDOWN, VIEW ANY DATABASE, VIEW SERVER PERFORMANCE STATE, VIEW SERVER SECURITY STATE, VIEW SERVER STATE")]
    [DataRow("securityadmin", "ALTER ANY LOGIN, CONNECT SQL, CREATE LOGIN, VIEW ANY DATABASE")]
    [DataRow("dbcreator", "CONNECT SQL, CREATE ANY DATABASE, VIEW ANY DATABASE")]
    [DataRow("##MS_DefinitionReader##", "CONNECT SQL, VIEW ANY DATABASE, VIEW ANY DEFINITION, VIEW ANY PERFORMANCE DEFINITION, VIEW ANY SECURITY DEFINITION")]
    [DataRow("##MS_DatabaseManager##", "ALTER ANY DATABASE, CONNECT SQL, CREATE ANY DATABASE, VIEW ANY DATABASE")]
    public void FixedRole_GrantsWhatRealGrants(string role, string permissions) =>
        AreEqual(permissions, WithLogin($"alter server role [{role}] add member app").ExecuteScalar(
            "use master; execute as login = 'app'; select string_agg(permission_name, ', ') within group (order by permission_name) from fn_my_permissions(null, 'SERVER'); revert"));

    [TestMethod]
    public void Sysadmin_HoldsAllFiftyOneServerPermissions() =>
        AreEqual(51, new Simulation().ExecuteScalar("select count(*) from fn_my_permissions(null, 'SERVER')"));

    [TestMethod]
    public void PlainLogin_HoldsConnectSqlAndViewAnyDatabase() =>
        AreEqual("CONNECT SQL, VIEW ANY DATABASE", WithLogin().ExecuteScalar(
            "use master; execute as login = 'app'; select string_agg(permission_name, ', ') from fn_my_permissions(null, 'SERVER'); revert"));

    [TestMethod]
    public void ServerStateReader_SeesEverySession() =>
        AreEqual(1, WithLogin("alter server role [##MS_ServerStateReader##] add member app").ExecuteScalar(
            "use master; execute as login = 'app'; select has_perms_by_name(null, null, 'VIEW SERVER STATE'); revert"));

    // ---- server-scope statement gates ----

    [TestMethod]
    public void SpConfigure_ReadIsOpen_WriteTakesAlterSettings()
    {
        var sim = WithLogin();
        AreEqual("nested triggers", sim.ExecuteScalar("use master; execute as login = 'app'; exec sp_configure 'nested triggers'; revert"));
        var ex = sim.AssertSqlError("use master; execute as login = 'app'; exec sp_configure 'nested triggers', 1", 15247);
        AreEqual("sp_configure", ex.Procedure);
        AreEqual(105, ex.LineNumber);
    }

    [TestMethod]
    public void Reconfigure_WithoutAlterSettings_Raises5812() =>
        WithLogin().AssertSqlError("use master; execute as login = 'app'; reconfigure", 5812, "You do not have permission to run the RECONFIGURE statement.");

    [TestMethod]
    public void Reconfigure_ServeradminRuns() =>
        AreEqual(1, WithLogin("alter server role serveradmin add member app").ExecuteScalar(
            "use master; execute as login = 'app'; exec sp_configure 'nested triggers', 1; reconfigure; select 1; revert"));

    [TestMethod]
    public void LinkedServerProcedures_TakeAlterAnyLinkedServer()
    {
        var sim = WithLogin();
        AreEqual("sp_dropserver", sim.AssertSqlError("use master; execute as login = 'app'; exec sp_dropserver 'nowhere'", 15247).Procedure);
        AreEqual("sys.sp_MSaddserver_internal", sim.AssertSqlError("use master; execute as login = 'app'; exec sp_addlinkedserver @server = 'nowhere'", 15247).Procedure);
        sim.ExecuteBatches("use master; alter server role setupadmin add member app");
        _ = sim.AssertSqlError("use master; execute as login = 'app'; exec sp_dropserver 'nowhere'", 15015);
    }

    [TestMethod]
    public void DbccFreeProcCache_TakesAlterServerState()
    {
        var sim = WithLogin();
        _ = sim.AssertSqlError("use master; execute as login = 'app'; dbcc freeproccache with no_infomsgs", 2571);
        sim.ExecuteBatches("use master; grant alter server state to app");
        AreEqual(1, sim.ExecuteScalar("use master; execute as login = 'app'; dbcc freeproccache with no_infomsgs; select 1; revert"));
    }

    [TestMethod]
    public void DbccTraceOn_StaysSysadminOnly_NamingPublicForControlServer() =>
        WithLogin("grant control server to app").AssertSqlError("use master; execute as login = 'app'; dbcc traceon(3604)", 2571, "User 'public' does not have permission to run DBCC TRACEON.");

    [TestMethod]
    public void RaiserrorWithLog_TakesAlterTrace()
    {
        var sim = WithLogin();
        _ = sim.AssertSqlError("use master; execute as login = 'app'; raiserror('x', 10, 1) with log", 2778);
        sim.ExecuteBatches("use master; grant alter trace to app");
        AreEqual(1, sim.ExecuteScalar("use master; execute as login = 'app'; raiserror('x', 10, 1) with log; select 1; revert"));
    }

    [TestMethod]
    public void ServerTrigger_TakesControlServer() =>
        AreEqual(1, WithLogin("grant control server to app").ExecuteScalar(
            "use master; execute as login = 'app'; exec('create trigger trg on all server for create_database as print 1'); select count(*) from sys.server_triggers; exec('drop trigger trg on all server'); revert"));

    [TestMethod]
    public void CreateServerRole_Takes15247() =>
        _ = WithLogin().AssertSqlError("use master; execute as login = 'app'; create server role r", 15247);

    [TestMethod]
    public void CustomServerRoleMembers_TakeAlterAnyServerRole()
    {
        var sim = WithLogin("create server role r");
        _ = sim.AssertSqlError("use master; execute as login = 'app'; alter server role r add member other", 15151);
        _ = sim.AssertSqlError("use master; execute as login = 'app'; drop server role r", 15151);
        sim.ExecuteBatches("use master; grant alter any server role to app");
        AreEqual(1, sim.ExecuteScalar("use master; execute as login = 'app'; alter server role r add member other; select is_srvrolemember('r', 'other'); revert"));
    }

    [TestMethod]
    [DataRow("grant control server to app")]
    [DataRow("grant alter any server role to app")]
    [DataRow("alter server role securityadmin add member app")]
    public void FixedRoleMembers_ClosedToAllButSysadminAndTheRolesOwnMembers(string setup) =>
        WithLogin(setup).AssertSqlError("use master; execute as login = 'app'; alter server role dbcreator add member other", 15151,
            "Cannot alter the server role 'dbcreator', because it does not exist or you do not have permission.");

    [TestMethod]
    public void FixedRoleMember_AddsToItsOwnRole() =>
        AreEqual(1, WithLogin("alter server role dbcreator add member app").ExecuteScalar(
            "use master; execute as login = 'app'; alter server role dbcreator add member other; select is_srvrolemember('dbcreator', 'other'); revert"));

    [TestMethod]
    public void CreateLoginPermission_CreatesButDoesNotAlter()
    {
        var sim = WithLogin("grant create login to app");
        sim.ExecuteBatches("use master; execute as login = 'app'; create login made with password = 'S3cret!Pass'; revert");
        _ = sim.AssertSqlError("use master; execute as login = 'app'; alter login made disable", 15151);
    }

    [TestMethod]
    public void DatabaseManager_CreatesAltersAndDropsDatabases() =>
        AreEqual(1, WithLogin("alter server role [##MS_DatabaseManager##] add member app").ExecuteScalar(
            "use master; execute as login = 'app'; create database d2; alter database d2 set recovery simple; drop database d2; select is_srvrolemember('##MS_DatabaseManager##'); revert"));

    // ---- catalog and scalar visibility ----

    [TestMethod]
    public void DeniedViewAnyDatabase_HidesDatabasesButMasterTempdbAndCurrent()
    {
        var sim = WithLogin("deny view any database to app");
        AreEqual("master,tempdb", sim.ExecuteScalar("use master; execute as login = 'app'; select string_agg(name, ',') within group (order by database_id) from sys.databases; revert"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("use master; execute as login = 'app'; select db_id('msdb'); revert"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("use master; execute as login = 'app'; select db_name(4); revert"));
        sim.ExecuteBatches("use d; create user app for login app");
        AreEqual("master,tempdb,d", sim.ExecuteScalar("use d; execute as login = 'app'; select string_agg(name, ',') within group (order by database_id) from sys.databases; revert"));
    }

    [TestMethod]
    public void DatabaseScopedFrame_SeesOnlyMasterTempdbAndItsOwnDatabase()
    {
        var sim = WithLogin();
        sim.ExecuteBatches("use d; create user u without login");
        AreEqual(3, sim.ExecuteScalar("use d; execute as user = 'u'; select count(*) from sys.databases; revert"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("use d; execute as user = 'u'; select db_id('msdb'); revert"));
    }

    [TestMethod]
    public void ServerPermissions_ShowOnlyVisibleGrantees()
    {
        var sim = WithLogin("grant view server state to other");
        AreEqual("CONNECT SQL,CONNECT SQL,VIEW ANY DATABASE", sim.ExecuteScalar(
            "use master; execute as login = 'app'; select string_agg(permission_name, ',') within group (order by permission_name) from sys.server_permissions; revert"));
        AreEqual(5, sim.ExecuteScalar("select count(*) from sys.server_permissions"));
    }

    [TestMethod]
    public void ServerRoleMembers_HideAnInvisibleCustomRole()
    {
        var sim = WithLogin("create server role r; alter server role r add member other; alter server role dbcreator add member other");
        // sa's sysadmin row and other's dbcreator row: a fixed role's members show whoever they are.
        AreEqual(2, sim.ExecuteScalar("use master; execute as login = 'app'; select count(*) from sys.server_role_members; revert"));
        sim.ExecuteBatches("use master; alter server role r add member app");
        // The custom role shows now, but only the member app can see.
        AreEqual(3, sim.ExecuteScalar("use master; execute as login = 'app'; select count(*) from sys.server_role_members; revert"));
    }

    [TestMethod]
    public void HasPermsByName_NullClassIsTheServer()
    {
        var sim = WithLogin();
        AreEqual("0|1|1", sim.ExecuteScalar("""
            use master; execute as login = 'app';
            select cast(has_perms_by_name(null, null, 'VIEW SERVER STATE') as varchar) + '|' + cast(has_perms_by_name(null, null, 'CONNECT SQL') as varchar)
                + '|' + cast(has_perms_by_name(null, null, 'VIEW ANY DATABASE') as varchar);
            revert
            """));
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select has_perms_by_name(null, null, 'CREATE TABLE')"));
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select has_perms_by_name(null, 'SERVER', 'CONNECT SQL')"));
        AreEqual(1, new Simulation().ExecuteScalar("select has_perms_by_name(null, null, 'CONTROL SERVER')"));
        AreEqual(1, new Simulation().ExecuteScalar("select has_perms_by_name('anything', 'SERVER', 'CONNECT SQL')"));
    }

    [TestMethod]
    public void HasPermsByName_LoginClass()
    {
        var sim = WithLogin();
        AreEqual(0, sim.ExecuteScalar("use master; execute as login = 'app'; select has_perms_by_name('other', 'LOGIN', 'IMPERSONATE'); revert"));
        AreEqual(0, sim.ExecuteScalar("select has_perms_by_name('nosuch', 'LOGIN', 'ALTER')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select has_perms_by_name('sa', 'LOGIN', 'SELECT')"));
        sim.ExecuteBatches("use master; grant impersonate on login::other to app");
        AreEqual("1|0", sim.ExecuteScalar("use master; execute as login = 'app'; select cast(has_perms_by_name('other', 'LOGIN', 'IMPERSONATE') as varchar) + '|' + cast(has_perms_by_name('other', 'LOGIN', 'ALTER') as varchar); revert"));
        AreEqual("IMPERSONATE", sim.ExecuteScalar("use master; execute as login = 'app'; select string_agg(permission_name, ',') from fn_my_permissions('other', 'LOGIN'); revert"));
        AreEqual("IMPERSONATE,VIEW DEFINITION,ALTER,CONTROL", sim.ExecuteScalar("select string_agg(permission_name, ',') from fn_my_permissions('other', 'LOGIN')"));
    }

    [TestMethod]
    public void FnMyPermissions_ServerRowShape() =>
        AreEqual("server||CONNECT SQL", new Simulation().ExecuteScalar("select top 1 entity_name + '|' + subentity_name + '|' + permission_name from fn_my_permissions(null, 'SERVER')"));

    [TestMethod]
    public void FnMyPermissions_DatabaseClass_NotModeledYet() =>
        _ = Throws<NotSupportedException>(() => new Simulation().ExecuteScalar("select count(*) from fn_my_permissions(null, 'DATABASE')"));

    [TestMethod]
    [DataRow("default", 301)]
    [DataRow("null", 301)]
    [DataRow("''", 301)]
    [DataRow("'server'", 51)]
    [DataRow("'LOGIN'", 4)]
    [DataRow("'bogus'", 0)]
    public void FnBuiltinPermissions_FiltersByClass(string argument, int count) =>
        AreEqual(count, new Simulation().ExecuteScalar($"select count(*) from sys.fn_builtin_permissions({argument})"));

    [TestMethod]
    public void FnBuiltinPermissions_RowShape() =>
        AreEqual("LOGIN|VIEW DEFINITION|VW  |CONTROL|SERVER|VIEW ANY SECURITY DEFINITION", new Simulation().ExecuteScalar(
            "select class_desc + '|' + permission_name + '|' + type + '|' + covering_permission_name + '|' + parent_class_desc + '|' + parent_covering_permission_name from fn_builtin_permissions('login') where permission_name = 'VIEW DEFINITION'"));

    [TestMethod]
    public void SpHelpSrvRoleMember_ListsAFixedRolesMembers()
    {
        var sim = WithLogin("alter server role bulkadmin add member app");
        using var reader = sim.ExecuteReader("exec sp_helpsrvrolemember 'bulkadmin'");
        IsTrue(reader.Read());
        AreEqual("bulkadmin", reader.GetString(0));
        AreEqual("app", reader.GetString(1));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void SpHelpSrvRoleMember_CustomRole_Raises15412()
    {
        var ex = WithLogin("create server role r").AssertSqlError("exec sp_helpsrvrolemember 'r'", 15412);
        AreEqual("'r' is not a known fixed role.", ex.Message);
        AreEqual(10, ex.LineNumber);
    }

    [TestMethod]
    public void SpHelpSrvRole_ListsTheEighteenFixedRoles()
    {
        var sim = new Simulation();
        using (var reader = sim.ExecuteReader("exec sp_helpsrvrole"))
        {
            var rows = 0;
            while (reader.Read())
                rows++;
            AreEqual(18, rows);
        }
        using (var reader = sim.ExecuteReader("exec sp_helpsrvrole 'dbcreator'"))
        {
            IsTrue(reader.Read());
            AreEqual("Database Creators", reader.GetString(1));
        }
        _ = sim.AssertSqlError("exec sp_helpsrvrole 'bogus'", 15412);
    }
}
