using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Server roles and server-scope grants: fixed-role seeding, the 262+ user id
/// space, CREATE / ALTER / DROP SERVER ROLE, IS_SRVROLEMEMBER truth, the
/// sysadmin → dbo mapping (incl. DENY bypass), server-scope GRANT / DENY /
/// REVOKE (master-only, CONNECT SQL auto-seed). Probe-confirmed against SQL
/// Server 2025 (probe6 N1–N8).
/// </summary>
[TestClass]
public sealed class ServerRoleAndScopeTests
{
    // ---- Fixed roles + id space ----

    [TestMethod]
    public void FixedServerRoles_SeededAtRealIds()
    {
        var sim = new Simulation();
        AreEqual("sysadmin", sim.ExecuteScalar("select name from sys.server_principals where principal_id = 3"));
        AreEqual("bulkadmin", sim.ExecuteScalar("select name from sys.server_principals where principal_id = 10"));
        AreEqual("##MS_ServerPerformanceStateReader##", sim.ExecuteScalar("select name from sys.server_principals where principal_id = 20"));
        AreEqual(18, sim.ExecuteScalar("select count(*) from sys.server_principals where is_fixed_role = 1"));
    }

    [TestMethod]
    public void CustomServerRole_GetsUserRangeId()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create server role customsrv");
        AreEqual(262, sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'customsrv'"));
        AreEqual("R", ((string)sim.ExecuteScalar("select type from sys.server_principals where name = 'customsrv'")!).Trim());
        IsFalse((bool)sim.ExecuteScalar("select is_fixed_role from sys.server_principals where name = 'customsrv'")!);
    }

    // ---- Membership + DDL ----

    [TestMethod]
    public void AlterServerRole_AddMember_ProjectsToServerRoleMembers()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login srvlogin with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("alter server role sysadmin add member srvlogin");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.server_role_members where role_principal_id = 3 and member_principal_id = 262"));
        _ = sim.ExecuteNonQuery("alter server role sysadmin drop member srvlogin");
        // The sysadmin memberships every instance lists — sa's and two
        // installed Windows logins' — remain.
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.server_role_members"));
    }

    [TestMethod]
    public void CustomServerRole_MembershipAndDrop()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login m with password = 'P@ss1word'; create server role customsrv");
        _ = sim.ExecuteNonQuery("alter server role customsrv add member m");
        AreEqual(1, sim.ExecuteScalar("select is_srvrolemember('customsrv', 'm')"));
        _ = sim.ExecuteNonQuery("drop server role customsrv");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_principals where name = 'customsrv'"));
    }

    [TestMethod]
    public void DropFixedServerRole_Raises15150()
    {
        var ex = new Simulation().AssertSqlError("drop server role sysadmin", 15150);
        AreEqual("Cannot drop the server role 'sysadmin'.", ex.Message);
    }

    [TestMethod]
    public void AlterServerRole_UnknownRole_Raises15151()
    {
        var ex = new Simulation().AssertSqlError("alter server role nosuchrole add member sa", 15151);
        Contains("Cannot alter the server role 'nosuchrole'", ex.Message);
    }

    [TestMethod]
    public void AlterServerRole_UnknownMember_Raises15151()
    {
        var ex = new Simulation().AssertSqlError("alter server role sysadmin add member nosuchlogin", 15151);
        Contains("Cannot add the server principal 'nosuchlogin'", ex.Message);
    }

    // ---- IS_SRVROLEMEMBER truth ----

    [TestMethod]
    public void IsSrvRoleMember_TruthTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login srvlogin with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("alter server role sysadmin add member srvlogin");
        // A sysadmin member reports 1 for every fixed server role.
        AreEqual(1, sim.ExecuteScalar("select is_srvrolemember('sysadmin', 'srvlogin')"));
        AreEqual(1, sim.ExecuteScalar("select is_srvrolemember('serveradmin', 'srvlogin')"));
        // public is always 1; a non-member reports 0; a non-role name is NULL.
        AreEqual(1, sim.ExecuteScalar("select is_srvrolemember('public', 'srvlogin')"));
        _ = sim.ExecuteNonQuery("create login plain with password = 'P@ss1word'");
        AreEqual(0, sim.ExecuteScalar("select is_srvrolemember('sysadmin', 'plain')"));
        IsTrue(sim.ExecuteScalar("select is_srvrolemember('notarole', 'plain')") is DBNull);
        IsTrue(sim.ExecuteScalar("select is_srvrolemember('sysadmin', 'nosuchlogin')") is DBNull);
    }

    // ---- sysadmin → dbo mapping ----

    [TestMethod]
    public void SysadminLogin_MapsToDbo_OverridingUserMapping_AndBypassesDeny()
    {
        var simulation = new Simulation();
        // A login with an explicit non-dbo user mapping AND a DENY on that user;
        // adding it to sysadmin resolves it to dbo everywhere, bypassing DENY.
        simulation.ExecuteBatches(
            "create table dbo.t (id int)",
            "create login boss with password = 'P@ss1word'",
            "create user bossuser for login boss",
            "deny select on object::dbo.t to bossuser",
            "alter server role sysadmin add member boss");

        var connection = simulation.CreateDbConnection();
        connection.ConnectionString = "User ID=boss;Password=P@ss1word";
        connection.Open();
        using (connection)
        {
            AreEqual("dbo", connection.CreateCommand("select current_user").ExecuteScalar());
            // DENY on bossuser is bypassed because the session runs as dbo.
            AreEqual(0, connection.CreateCommand("select count(*) from dbo.t").ExecuteScalar());
        }
    }

    // ---- Server-scope grants ----

    [TestMethod]
    public void CreateLogin_AutoSeedsConnectSql()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.server_permissions where grantee_principal_id = 262 and type = 'COSQ' and state = 'G'"));
        AreEqual("SERVER", ((string)sim.ExecuteScalar(
            "select class_desc from sys.server_permissions where grantee_principal_id = 262")!).Trim());
    }

    [TestMethod]
    public void ServerScopeGrant_InMaster_ProjectsRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("use master; grant view server state to app");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.server_permissions where grantee_principal_id = 262 and type = 'VWSS' and state = 'G'"));
    }

    [TestMethod]
    public void ServerScopeGrant_OutsideMaster_Raises4621()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        var ex = sim.AssertSqlError("grant view server state to app", 4621);
        AreEqual("Permissions at the server scope can only be granted when the current database is master", ex.Message);
    }

    [TestMethod]
    public void ServerScopeDeny_ReplacesGrant()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("use master; grant view server state to app");
        _ = sim.ExecuteNonQuery("use master; deny view server state to app");
        // Server-scope DENY replaces the prior G row (no coexisting G + D).
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_permissions where type = 'VWSS' and grantee_principal_id = 262"));
        AreEqual("D", ((string)sim.ExecuteScalar("select state from sys.server_permissions where type = 'VWSS' and grantee_principal_id = 262")!).Trim());
    }

    [TestMethod]
    public void ServerScopeRevoke_RemovesRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("use master; grant view server state to app");
        _ = sim.ExecuteNonQuery("use master; revoke view server state from app");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_permissions where type = 'VWSS' and grantee_principal_id = 262"));
        // The auto-seeded CONNECT SQL row survives an unrelated revoke.
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_permissions where grantee_principal_id = 262 and type = 'COSQ'"));
    }

    [TestMethod]
    public void ServerScopeGrant_UnknownLogin_Raises15151()
    {
        var ex = new Simulation().AssertSqlError("use master; grant view server state to nosuchlogin", 15151);
        Contains("Cannot find the login 'nosuchlogin'", ex.Message);
    }

    // ---- ON SERVER:: / ON LOGIN:: securables ----

    [TestMethod]
    public void OnServerSecurable_IsAnAliasOfTheOnLessForm()
    {
        // Real accepts any name after SERVER:: and stores the ordinary
        // class-100 row (probe-confirmed — the name is ignored).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("use master; grant view server state on server::anything to app");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.server_permissions where class = 100 and major_id = 0 and type = 'VWSS' and state = 'G' and grantee_principal_id = 262"));
    }

    [TestMethod]
    public void OnLoginSecurable_ProjectsClass101()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login target with password = 'P@ss1word'; create login grantee with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("use master; grant impersonate on login::target to grantee");
        AreEqual((byte)101, sim.ExecuteScalar("select class from sys.server_permissions where type = 'IM'"));
        AreEqual("SERVER_PRINCIPAL", ((string)sim.ExecuteScalar(
            "select class_desc from sys.server_permissions where type = 'IM'")!).Trim());
        // major_id is the target login's principal_id, not the grantee's.
        AreEqual(sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'target'"),
            sim.ExecuteScalar("select major_id from sys.server_permissions where type = 'IM'"));
        AreEqual(sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'grantee'"),
            sim.ExecuteScalar("select grantee_principal_id from sys.server_permissions where type = 'IM'"));
    }

    [TestMethod]
    public void OnLoginSecurable_TypeCodesMatchTheCatalog()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login target with password = 'P@ss1word'; create login grantee with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("""
            use master;
            grant alter on login::target to grantee;
            grant view definition on login::target to grantee
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_permissions where class = 101 and type = 'AL'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_permissions where class = 101 and type = 'VW'"));
        AreEqual("VIEW DEFINITION", sim.ExecuteScalar("select permission_name from sys.server_permissions where type = 'VW'"));
    }

    [TestMethod]
    public void OnLoginSecurable_UnknownLogin_Raises15151()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login grantee with password = 'P@ss1word'");
        var ex = sim.AssertSqlError("use master; grant impersonate on login::ghost to grantee", 15151);
        Contains("Cannot find the login 'ghost'", ex.Message);
    }

    [TestMethod]
    public void OnLoginSecurable_OutsideMaster_Raises4621()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login target with password = 'P@ss1word'; create login grantee with password = 'P@ss1word'");
        _ = sim.AssertSqlError("grant impersonate on login::target to grantee", 4621);
    }

    [TestMethod]
    public void OnLoginSecurable_Revoke_RemovesOnlyItsOwnRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create login a with password = 'P@ss1word';
            create login b with password = 'P@ss1word';
            create login grantee with password = 'P@ss1word'
            """);
        _ = sim.ExecuteNonQuery("""
            use master;
            grant impersonate on login::a to grantee;
            grant impersonate on login::b to grantee;
            revoke impersonate on login::a from grantee
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_permissions where class = 101 and type = 'IM'"));
        AreEqual(sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'b'"),
            sim.ExecuteScalar("select major_id from sys.server_permissions where class = 101 and type = 'IM'"));
    }

    // ---- EXECUTE AS LOGIN enforcement ----

    /// <summary>Two logins, each with a database user, so EXECUTE AS LOGIN can map either way.</summary>
    private static Simulation TwoMappedLogins()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create login caller with password = 'P@ss1word';
            create login target with password = 'P@ss1word';
            create user ucaller for login caller;
            create user utarget for login target
            """);
        return sim;
    }

    [TestMethod]
    public void ExecuteAsLogin_WithoutImpersonate_Raises15406()
    {
        var ex = TwoMappedLogins().AssertSqlError(
            "execute as user = 'ucaller'; execute as login = 'target'", 15406);
        Contains("Cannot execute as the server principal", ex.Message);
    }

    [TestMethod]
    public void ExecuteAsLogin_WithImpersonateOnLogin_Succeeds()
    {
        var sim = TwoMappedLogins();
        _ = sim.ExecuteNonQuery("use master; grant impersonate on login::target to caller");
        AreEqual("target", sim.ExecuteScalar(
            "execute as user = 'ucaller'; execute as login = 'target'; select suser_name()"));
    }

    [TestMethod]
    public void ExecuteAsLogin_WithImpersonateAnyLogin_Succeeds()
    {
        var sim = TwoMappedLogins();
        _ = sim.ExecuteNonQuery("use master; grant impersonate any login to caller");
        AreEqual("target", sim.ExecuteScalar(
            "execute as user = 'ucaller'; execute as login = 'target'; select suser_name()"));
    }

    [TestMethod]
    public void ExecuteAsLogin_DenyOnLogin_BeatsImpersonateAnyLogin()
    {
        // Probe-confirmed: a class-101 DENY overrides the class-100 blanket grant.
        var sim = TwoMappedLogins();
        _ = sim.ExecuteNonQuery("""
            use master;
            grant impersonate any login to caller;
            deny impersonate on login::target to caller
            """);
        _ = sim.AssertSqlError("execute as user = 'ucaller'; execute as login = 'target'", 15406);
    }

    [TestMethod]
    public void ExecuteAsLogin_ImpersonateViaServerRole_Succeeds()
    {
        var sim = TwoMappedLogins();
        _ = sim.ExecuteNonQuery("""
            use master;
            create server role impersonators;
            alter server role impersonators add member caller;
            grant impersonate on login::target to impersonators
            """);
        AreEqual("target", sim.ExecuteScalar(
            "execute as user = 'ucaller'; execute as login = 'target'; select suser_name()"));
    }

    [TestMethod]
    public void ExecuteAsLogin_ControlOnLogin_CoversImpersonate()
    {
        var sim = TwoMappedLogins();
        _ = sim.ExecuteNonQuery("use master; grant control on login::target to caller");
        AreEqual("target", sim.ExecuteScalar(
            "execute as user = 'ucaller'; execute as login = 'target'; select suser_name()"));
    }

    // ---- Server-principal metadata visibility ----

    /// <summary>Three logins; only <c>caller</c> has a database user, so it is the session principal under test.</summary>
    private static Simulation ThreeLogins()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create login caller with password = 'P@ss1word';
            create login other1 with password = 'P@ss1word';
            create login other2 with password = 'P@ss1word';
            create user ucaller for login caller
            """);
        return sim;
    }

    [TestMethod]
    public void RestrictedSession_SeesOnlyItsOwnLoginRow()
    {
        var sim = ThreeLogins();
        AreEqual("caller", sim.ExecuteScalar(
            "execute as user = 'ucaller'; select name from sys.server_principals where principal_id > 20"));
        AreEqual(1, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where principal_id > 20"));
    }

    [TestMethod]
    public void RestrictedSession_StillSeesTheFixedBlock()
        // sa (1) + public (2) + the 18 fixed server roles (3-20).
        => AreEqual(20, ThreeLogins().ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where principal_id <= 20"));

    [TestMethod]
    public void ViewDefinitionOnLogin_RevealsThatRow()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("use master; grant view definition on login::other1 to caller");
        AreEqual(2, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where principal_id > 20"));
        AreEqual(1, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where name = 'other1'"));
        AreEqual(0, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where name = 'other2'"));
    }

    [TestMethod]
    public void AlterOnLogin_AlsoRevealsThatRow()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("use master; grant alter on login::other1 to caller");
        AreEqual(1, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where name = 'other1'"));
    }

    [TestMethod]
    public void ViewAnyDefinition_RevealsEveryLogin()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("use master; grant view any definition to caller");
        AreEqual(3, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where principal_id > 261"));
    }

    [TestMethod]
    public void DenyOnLogin_ReHidesUnderABlanketGrant()
    {
        // Probe-confirmed: the class-101 DENY nullifies the class-100 reveal.
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("""
            use master;
            grant view any definition to caller;
            deny view definition on login::other1 to caller
            """);
        AreEqual(0, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where name = 'other1'"));
        AreEqual(1, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where name = 'other2'"));
    }

    [TestMethod]
    public void SqlLogins_IsFilteredTheSameWay()
    {
        var sim = ThreeLogins();
        AreEqual(1, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.sql_logins where principal_id > 20"));
        _ = sim.ExecuteNonQuery("use master; grant view definition on login::other1 to caller");
        AreEqual(2, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.sql_logins where principal_id > 20"));
    }

    [TestMethod]
    public void ServerRoleMembership_RevealsTheRole()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("use master; create server role srv1; alter server role srv1 add member caller");
        AreEqual(1, sim.ExecuteScalar(
            "execute as user = 'ucaller'; select count(*) from sys.server_principals where name = 'srv1'"));
    }

    [TestMethod]
    public void DboSession_SeesEveryServerPrincipal()
        => AreEqual(3, ThreeLogins().ExecuteScalar("select count(*) from sys.server_principals where principal_id > 261"));

    // ---- Login DDL gating ----

    [TestMethod]
    public void CreateLogin_ByRestrictedPrincipal_Raises15247()
    {
        var ex = ThreeLogins().AssertSqlError(
            "execute as user = 'ucaller'; create login zz with password = 'P@ss1word'", 15247);
        AreEqual("User does not have permission to perform this action.", ex.Message);
    }

    [TestMethod]
    public void CreateLogin_WithAlterAnyLogin_Succeeds()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("use master; grant alter any login to caller");
        AreEqual(1, sim.ExecuteScalar("""
            execute as user = 'ucaller';
            create login zz with password = 'P@ss1word';
            revert;
            select count(*) from sys.server_principals where name = 'zz'
            """));
    }

    [TestMethod]
    public void AlterLogin_ByRestrictedPrincipal_Raises15151()
    {
        var ex = ThreeLogins().AssertSqlError(
            "execute as user = 'ucaller'; alter login other1 with password = 'P@ss2word'", 15151);
        Contains("Cannot alter the login 'other1'", ex.Message);
    }

    [TestMethod]
    public void DropLogin_ByRestrictedPrincipal_Raises15151()
    {
        var ex = ThreeLogins().AssertSqlError(
            "execute as user = 'ucaller'; drop login other1", 15151);
        Contains("Cannot drop the login 'other1'", ex.Message);
    }

    [TestMethod]
    public void AlterLogin_WithAlterOnThatLogin_Succeeds()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("use master; grant alter on login::other1 to caller");
        _ = sim.ExecuteNonQuery("execute as user = 'ucaller'; alter login other1 with password = 'P@ss2word'");
        // The unrelated login stays out of reach.
        _ = sim.AssertSqlError("execute as user = 'ucaller'; alter login other2 with password = 'P@ss2word'", 15151);
    }

    [TestMethod]
    public void DropLogin_WithAlterAnyLogin_Succeeds()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("use master; grant alter any login to caller");
        _ = sim.ExecuteNonQuery("execute as user = 'ucaller'; drop login other1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_principals where name = 'other1'"));
    }

    [TestMethod]
    public void DboLoginDdl_IsUngated()
    {
        var sim = ThreeLogins();
        _ = sim.ExecuteNonQuery("alter login other1 with password = 'P@ss2word'; drop login other2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_principals where name = 'other2'"));
    }

    [TestMethod]
    public void DropServerRole_Missing_IsMsg15151()
        => new Simulation().AssertSqlError("drop server role nosuchrole", 15151, "Cannot drop the server role 'nosuchrole', because it does not exist or you do not have permission.");

    [TestMethod]
    public void OnServerRoleSecurable_GrantsClass101_AndHasPermsByNameReadsIt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l with password = 'P@ss1word'; create server role r");
        _ = sim.ExecuteNonQuery("use master; grant view definition on server role::r to l");
        AreEqual("SERVER_PRINCIPAL:VIEW DEFINITION", sim.ExecuteScalar(
            "select class_desc + ':' + permission_name from sys.server_permissions where class = 101 and major_id = suser_id('r')"));
        AreEqual("1|0|0", sim.ExecuteScalar("""
            use master;
            execute as login = 'l';
            declare @r varchar(20) = concat(has_perms_by_name('r', 'SERVER ROLE', 'VIEW DEFINITION'), '|', has_perms_by_name('r', 'SERVER ROLE', 'ALTER'), '|', has_perms_by_name('nosuch', 'SERVER ROLE', 'ALTER'));
            revert;
            select @r;
            """));
        _ = sim.ExecuteNonQuery("use master; drop server role r");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_permissions where class = 101"));
    }

    [TestMethod]
    [DataRow("grant view definition on server role::nosuch to l", "Cannot find the server role 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("grant view definition on server role::sysadmin to l", "Cannot find the object 'sysadmin', because it does not exist or you do not have permission.")]
    [DataRow("grant control on login::r to l", "Cannot find the login 'r', because it does not exist or you do not have permission.")]
    public void ServerPrincipalSecurables_FindOnlyTheirOwnKind(string grant, string message)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l with password = 'P@ss1word'; create server role r");
        sim.AssertSqlError("use master; " + grant, 15151, message);
    }

    [TestMethod]
    public void AServerRolesOwner_IsItsAuthorization_AndHoldsEveryPermissionOnIt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l with password = 'P@ss1word'; create login m with password = 'P@ss1word'; create server role r authorization l; create server role q authorization r");
        AreEqual("q:r,r:l", sim.ExecuteScalar("""
            select string_agg(p.name + ':' + o.name, ',') within group (order by p.name)
            from sys.server_principals p join sys.server_principals o on o.principal_id = p.owning_principal_id where p.name in ('q', 'r')
            """));
        AreEqual("1|1|1|1|added", sim.ExecuteScalar("""
            use master;
            execute as login = 'l';
            declare @r varchar(40) = concat(has_perms_by_name('r', 'SERVER ROLE', 'ALTER'), '|', has_perms_by_name('r', 'SERVER ROLE', 'CONTROL'), '|',
                has_perms_by_name('r', 'SERVER ROLE', 'VIEW DEFINITION'), '|', has_perms_by_name('r', 'SERVER ROLE', 'TAKE OWNERSHIP'));
            begin try alter server role r add member m; set @r += '|added'; end try begin catch set @r += concat('|', error_number()); end catch;
            revert;
            select @r;
            """));
        sim.AssertSqlError("drop login l", 15141, "The server principal owns one or more server role(s) and cannot be dropped.");
        _ = sim.ExecuteNonQuery("alter authorization on server role::r to m");
        AreEqual("m", sim.ExecuteScalar("select suser_name(owning_principal_id) from sys.server_principals where name = 'r'"));
    }

    [TestMethod]
    [DataRow("create server role x authorization nosuch", "Cannot find the server principal 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on server role::nosuch to l", "Cannot find the server role 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter authorization on server role::r to nosuch", "Cannot find the principal 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("alter server role nosuch with name = x", "Cannot alter the server role 'nosuch', because it does not exist or you do not have permission.")]
    public void ServerRoleOwnershipAndRename_RefuseAsRealDoes(string statement, string message)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l with password = 'P@ss1word'; create server role r");
        sim.AssertSqlError(statement, 15151, message);
    }

    [TestMethod]
    public void AServerRole_RenamesWithName_AndTakesNoIfExistsToDrop()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login l with password = 'P@ss1word'; create server role r; create server role q");
        _ = sim.ExecuteNonQuery("alter server role r with name = r2");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_principals where name = 'r2'"));
        sim.AssertSqlError("alter server role r2 with name = l", 15023, "User, group, or role 'l' already exists in the current database.");
        _ = sim.AssertSqlError("drop server role if exists q", 156);
    }
}
