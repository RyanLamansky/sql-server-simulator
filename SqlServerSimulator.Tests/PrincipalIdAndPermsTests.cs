using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the principal-id / permission-check scalar family:
/// <c>USER_ID</c>, <c>SUSER_ID</c>, <c>DATABASE_PRINCIPAL_ID</c>,
/// <c>HAS_PERMS_BY_NAME</c>, <c>IS_MEMBER</c>, <c>IS_ROLEMEMBER</c>,
/// <c>IS_SRVROLEMEMBER</c>. The simulator returns sensible placeholders
/// (dbo's id, "yes" for the public role, "yes" for any non-NULL
/// HAS_PERMS_BY_NAME) since it doesn't enforce permissions.
/// </summary>
[TestClass]
public sealed class PrincipalIdAndPermsTests
{
    [TestMethod]
    public void UserId_NoArg_ReturnsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("select user_id()"));

    [TestMethod]
    public void UserId_Dbo_ReturnsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("select user_id('dbo')"));

    [TestMethod]
    public void UserId_Public_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select user_id('public')"));

    [TestMethod]
    public void UserId_Unknown_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select user_id('not_a_user')"));

    [TestMethod]
    public void SuserId_NoArg_ReturnsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("select suser_id()"));

    [TestMethod]
    public void DatabasePrincipalId_Dbo_ReturnsOne()
        => AreEqual(1, new Simulation().ExecuteScalar("select database_principal_id('dbo')"));

    [TestMethod]
    public void HasPermsByName_AnyValidInputs_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_perms_by_name('dbo.t', 'OBJECT', 'SELECT')"));

    [TestMethod]
    public void HasPermsByName_NullSecurable_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select has_perms_by_name(null, 'OBJECT', 'SELECT')"));

    [TestMethod]
    public void IsMember_Public_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select is_member('public')"));

    [TestMethod]
    public void IsMember_OtherRole_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select is_member('db_owner')"));

    [TestMethod]
    public void IsRoleMember_Public_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select is_rolemember('public')"));

    [TestMethod]
    public void IsSrvRoleMember_Public_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select is_srvrolemember('public')"));
}
