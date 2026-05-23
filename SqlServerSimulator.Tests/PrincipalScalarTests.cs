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
    public void SuserName_NoArg_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select suser_name()"));

    [TestMethod]
    public void SuserName_AnyId_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select suser_name(1)"));

    [TestMethod]
    public void SuserName_NullArg_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select suser_name(null)"));

    [TestMethod]
    public void SuserSname_NoArg_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select suser_sname()"));

    [TestMethod]
    public void OriginalLogin_NoArg_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select original_login()"));

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
    public void SystemUser_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select system_user"));

    [TestMethod]
    public void User_ReturnsDbo()
        => AreEqual("dbo", new Simulation().ExecuteScalar("select user"));

    [TestMethod]
    public void Combined_AllConverge_ReturnDbo()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select iif(current_user = user_name() and user_name() = original_login() and user = current_user, 1, 0)
            """));
}
