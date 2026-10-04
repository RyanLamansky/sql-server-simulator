using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the principal/identity scalar functions: <c>USER_NAME</c>,
/// <c>SUSER_NAME</c>, <c>SUSER_SNAME</c>, <c>ORIGINAL_LOGIN</c>,
/// <c>HOST_NAME</c>, <c>APP_NAME</c>, and the parens-less keywords
/// <c>CURRENT_USER</c>, <c>SESSION_USER</c>, <c>SYSTEM_USER</c>, <c>USER</c>.
/// All converge on the simulator's fixed-principal placeholder (<c>dbo</c>);
/// <c>HOST_NAME</c> and <c>APP_NAME</c> return the empty string.
/// </summary>
[TestClass]
public sealed class PrincipalScalarTests
{
    [TestMethod]
    public void UserName_NoArg_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select user_name()"));

    [TestMethod]
    public void UserName_DboId_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select user_name(1)"));

    [TestMethod]
    public void UserName_PublicId_ReturnsPublic()
        => AreEqual("public", new Simulation().ExecuteScalar("select user_name(0)"));

    [TestMethod]
    public void UserName_SysId_ReturnsSys()
        => AreEqual("sys", new Simulation().ExecuteScalar("select user_name(4)"));

    [TestMethod]
    public void UserName_UnknownId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select user_name(99)"));

    [TestMethod]
    public void UserName_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select user_name(null)"));

    [TestMethod]
    public void SuserName_NoArg_ReturnsSa()
        => AreEqual("sa", new Simulation().ExecuteScalar("select suser_name()"));

    [TestMethod]
    public void SuserName_OfAnId_NamesThatServerPrincipal()
        => AreEqual("sa", new Simulation().ExecuteScalar("select suser_name(1)"));

    [TestMethod]
    public void SuserName_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select suser_name(null)"));

    [TestMethod]
    public void SuserSname_NoArg_ReturnsSa()
        => AreEqual("sa", new Simulation().ExecuteScalar("select suser_sname()"));

    [TestMethod]
    public void OriginalLogin_NoArg_ReturnsSa()
        => AreEqual("sa", new Simulation().ExecuteScalar("select original_login()"));

    [TestMethod]
    public void HostName_ReturnsEmptyString()
        => AreEqual("", new Simulation().ExecuteScalar("select host_name()"));

    [TestMethod]
    public void AppName_ReturnsEmptyString()
        => AreEqual("", new Simulation().ExecuteScalar("select app_name()"));

    [TestMethod]
    public void CurrentUser_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select current_user"));

    [TestMethod]
    public void SessionUser_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select session_user"));

    [TestMethod]
    public void SystemUser_ReturnsSa()
        => AreEqual("sa", new Simulation().ExecuteScalar("select system_user"));

    [TestMethod]
    public void User_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select user"));

    [TestMethod]
    public void Combined_UsersAndLoginsEachConverge()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select iif(current_user = user_name() and user = current_user and original_login() = system_user and system_user = suser_sname(), 1, 0)
            """));

    // === SUSER_SID / SID_BINARY ===

    [TestMethod]
    public void SuserSid_NoArg_ReturnsWellKnownSid()
        => CollectionAssert.AreEqual(new byte[] { 0x01 }, (byte[]?)new Simulation().ExecuteScalar("select suser_sid()"));

    [TestMethod]
    public void SuserSid_Sa_ReturnsWellKnownSid()
        => CollectionAssert.AreEqual(new byte[] { 0x01 }, (byte[]?)new Simulation().ExecuteScalar("select suser_sid(N'sa')"));

    [TestMethod]
    public void SuserSid_RegistryLogin_MatchesServerPrincipalsSid()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login probe_login with password = 'P@ssw0rd1'");
        AreEqual(1, sim.ExecuteScalar(
            "select iif(suser_sid(N'probe_login') = (select sid from sys.server_principals where name = N'probe_login'), 1, 0)"));
    }

    [TestMethod]
    public void SuserSid_Unknown_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select suser_sid(N'nosuchlogin')"));

    [TestMethod]
    public void SuserSid_SecondParameter_Accepted()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select suser_sid(N'nosuchlogin', 0)"));

    [TestMethod]
    public void SidBinary_AlwaysNull_EvenForExistingLogin()
    {
        // Probe-confirmed against SQL Server 2025: SID_BINARY resolves only
        // Windows / Entra-ID directory principals — it returns NULL even for
        // an existing SQL-auth login, so constant NULL is faithful here.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create login probe_login2 with password = 'P@ssw0rd1'");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select sid_binary(N'probe_login2')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select sid_binary(N'')"));
    }

    // ---- Membership answers a restricted principal gets (probed 2026-10-04 against SQL Server 2025) ----

    [TestMethod]
    public void IsMember_OfAUserName_AsksWhetherItIsTheEffectiveUser()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u1 without login; create user u2 without login");
        AreEqual("0|1", sim.ExecuteScalar("execute as user = 'u2'; select concat_ws('|', is_member('u1'), is_member('u2'))"));
        AreEqual(0, sim.ExecuteScalar("select is_member('u1')"));
    }

    [TestMethod]
    public void IsRoleMember_AHiddenMember_IsZeroForAUserAndNullForARole()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u1 without login; create user u2 without login; create role r1; create role r2; alter role r1 add member u1; alter role r2 add member r1; alter role db_datareader add member u2");
        AreEqual("0|NULL", sim.ExecuteScalar("execute as user = 'u1'; select concat_ws('|', is_rolemember('db_datareader', 'u2'), isnull(cast(is_rolemember('r2', 'r9') as varchar), 'NULL'))"));
        AreEqual("NULL", sim.ExecuteScalar("execute as user = 'u2'; select isnull(cast(is_rolemember('r2', 'r1') as varchar), 'NULL')"));
    }

    [TestMethod]
    public void TakenPrincipalNames_ReportTheStatementsState()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u1 without login; create role r1; create application role ar with password = 'Pw!12345678'");
        AreEqual((byte)6, sim.AssertSqlError("create user u1 without login", 15023).State);
        AreEqual((byte)6, sim.AssertSqlError("create user r1 without login", 15023).State);
        AreEqual((byte)7, sim.AssertSqlError("alter user u1 with name = r1", 15023).State);
        AreEqual((byte)13, sim.AssertSqlError("alter application role ar with name = u1", 15023).State);
        _ = sim.AssertSqlError("create user guest without login", 15062);
        _ = sim.AssertSqlError("create user u1", 15007);
    }
}
