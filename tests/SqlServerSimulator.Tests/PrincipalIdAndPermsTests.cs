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
    public void HasPermsByName_NullSecurable_Returns1()
    {
        // NULL securable = "the current database/server"; DacFx's bacpac-export
        // permission gate reads both of these and requires 1 (probe-confirmed).
        AreEqual(1, new Simulation().ExecuteScalar("select has_perms_by_name(null, N'DATABASE', N'VIEW DEFINITION')"));
        AreEqual(1, new Simulation().ExecuteScalar("select has_perms_by_name(null, N'DATABASE', N'VIEW DATABASE STATE')"));
        AreEqual(1, new Simulation().ExecuteScalar("select has_perms_by_name(null, null, 'CONNECT SQL')"));
    }

    [TestMethod]
    public void HasPermsByName_NullPermission_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select has_perms_by_name('dbo.t', 'OBJECT', null)"));

    [TestMethod]
    public void IsMember_Public_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select is_member('public')"));

    [TestMethod]
    public void IsMember_DbOwner_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select is_member('db_owner')"));

    [TestMethod]
    public void IsMember_OtherFixedRole_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select is_member('db_datareader')"));

    [TestMethod]
    public void IsMember_UnknownName_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select is_member('not_a_role')"));

    [TestMethod]
    public void IsMember_CreatedRole_Returns0WithoutMembership()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create role app_role").ExecuteNonQuery();
        AreEqual(0, connection.CreateCommand("select is_member('app_role')").ExecuteScalar());
    }

    [TestMethod]
    public void IsRoleMember_Public_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select is_rolemember('public')"));

    [TestMethod]
    public void IsSrvRoleMember_Public_Returns1()
        => AreEqual(1, new Simulation().ExecuteScalar("select is_srvrolemember('public')"));

    [TestMethod]
    public void IsSrvRoleMember_Sysadmin_Returns0()
        => AreEqual(0, new Simulation().ExecuteScalar("select is_srvrolemember('sysadmin')"));

    [TestMethod]
    public void IsSrvRoleMember_DatabaseRole_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select is_srvrolemember('db_owner')"));

    // permissions() — the legacy deprecated bitmap. The simulator's session
    // principal is always the database-owning dbo, so these return the fixed
    // privileged (owner) masks probed against SQL Server 2025.

    [TestMethod]
    public void Permissions_Niladic_ReturnsDbOwnerStatementMask()
        => AreEqual(50201342, new Simulation().ExecuteScalar("select permissions()"));

    [TestMethod]
    public void Permissions_Niladic_UnaffectedByCreateUserAndGrant()
    {
        // The simulator has no EXECUTE AS principal switching; the session is
        // always dbo, so granting a statement permission to another user does
        // not change dbo's privileged mask.
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create user app_user without login").ExecuteNonQuery();
        _ = connection.CreateCommand("grant create table to app_user").ExecuteNonQuery();
        AreEqual(50201342, connection.CreateCommand("select permissions()").ExecuteScalar());
    }

    [TestMethod]
    public void Permissions_Object_ResolvableTable_ReturnsOwnerMask()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table dbo.t (id int, name nvarchar(20))").ExecuteNonQuery();
        AreEqual(1948217375, connection.CreateCommand("select permissions(object_id('dbo.t'))").ExecuteScalar());
    }

    [TestMethod]
    public void Permissions_Object_NullArgument_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select permissions(null)"));

    [TestMethod]
    public void Permissions_Object_UnresolvedId_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select permissions(999999999)"));

    [TestMethod]
    public void Permissions_Column_ExistingColumn_ReturnsColumnMask()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table dbo.t (id int, name nvarchar(20))").ExecuteNonQuery();
        AreEqual(1082605703, connection.CreateCommand("select permissions(object_id('dbo.t'), 'name')").ExecuteScalar());
    }

    [TestMethod]
    public void Permissions_Column_UnknownColumn_ReturnsNull()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table dbo.t (id int)").ExecuteNonQuery();
        AreEqual(DBNull.Value, connection.CreateCommand("select permissions(object_id('dbo.t'), 'nope')").ExecuteScalar());
    }

    [TestMethod]
    public void Permissions_Column_NullColumnArgument_ReturnsNull()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("create table dbo.t (id int)").ExecuteNonQuery();
        AreEqual(DBNull.Value, connection.CreateCommand("select permissions(object_id('dbo.t'), null)").ExecuteScalar());
    }

    [TestMethod]
    public void Permissions_SsmsTableDesignerProbeBatch_ExecutesAllColumns()
    {
        // The exact pre-open probe SSMS's Table Designer issues; it failed on
        // the unrecognized permissions() built-in. ExecuteScalar returns the
        // first column (user_name()), proving the whole 7-column SELECT — the
        // permissions() call included — parsed and executed.
        AreEqual("dbo", new Simulation().ExecuteScalar(
            "select user_name(), @@MAX_PRECISION, is_member('db_owner'), permissions(), "
            + "DatabasePropertyEx(db_name(), N'collation'), SERVERPROPERTY('IsFullTextInstalled'), schema_name()"));
    }
}
