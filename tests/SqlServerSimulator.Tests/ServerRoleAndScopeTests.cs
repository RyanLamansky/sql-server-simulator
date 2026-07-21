using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Server roles and server-scope grants: fixed-role seeding, the 258+ user id
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
        AreEqual(258, sim.ExecuteScalar("select principal_id from sys.server_principals where name = 'customsrv'"));
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
            "select count(*) from sys.server_role_members where role_principal_id = 3 and member_principal_id = 258"));
        _ = sim.ExecuteNonQuery("alter server role sysadmin drop member srvlogin");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_role_members"));
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
            "select count(*) from sys.server_permissions where grantee_principal_id = 258 and type = 'COSQ' and state = 'G'"));
        AreEqual("SERVER", ((string)sim.ExecuteScalar(
            "select class_desc from sys.server_permissions where grantee_principal_id = 258")!).Trim());
    }

    [TestMethod]
    public void ServerScopeGrant_InMaster_ProjectsRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("use master; grant view server state to app");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.server_permissions where grantee_principal_id = 258 and type = 'VWSS' and state = 'G'"));
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
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_permissions where type = 'VWSS'"));
        AreEqual("D", ((string)sim.ExecuteScalar("select state from sys.server_permissions where type = 'VWSS'")!).Trim());
    }

    [TestMethod]
    public void ServerScopeRevoke_RemovesRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login app with password = 'P@ss1word'");
        _ = sim.ExecuteNonQuery("use master; grant view server state to app");
        _ = sim.ExecuteNonQuery("use master; revoke view server state from app");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_permissions where type = 'VWSS'"));
        // The auto-seeded CONNECT SQL row survives an unrelated revoke.
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.server_permissions where type = 'COSQ'"));
    }

    [TestMethod]
    public void ServerScopeGrant_UnknownLogin_Raises15151()
    {
        var ex = new Simulation().AssertSqlError("use master; grant view server state to nosuchlogin", 15151);
        Contains("Cannot find the login 'nosuchlogin'", ex.Message);
    }
}
