using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>GRANT</c> / <c>REVOKE</c> / <c>DENY</c> + the
/// principal DDL (<c>CREATE USER</c>, <c>CREATE ROLE</c>,
/// <c>ALTER ROLE … ADD MEMBER</c>, <c>DROP USER</c>, <c>DROP ROLE</c>).
/// Writer-side only — that the parsed permissions and principals land in the
/// catalog views correctly. Enforcement lives in <c>PermissionEnforcementTests</c>
/// and <c>StatementPermissionGateTests</c>.
/// </summary>
[TestClass]
public sealed class PermissionStatementTests
{
    [TestMethod]
    public void Grant_AwShape_ToPublic_LandsInCatalog()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("grant view any column encryption key definition to public");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_permissions where permission_name = 'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION'"));
        AreEqual("GRANT", sim.ExecuteScalar("select state_desc from sys.database_permissions where permission_name = 'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION'"));
        AreEqual(0, sim.ExecuteScalar("select cast(class as int) from sys.database_permissions where permission_name = 'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION'"));
        // Grantee = public (principal_id 0, pre-seeded)
        AreEqual(0, sim.ExecuteScalar("select grantee_principal_id from sys.database_permissions where permission_name = 'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION'"));
    }

    [TestMethod]
    public void Grant_SelectOnTable_StoresObjectScope()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            grant select on t to public
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_permissions where permission_name = 'SELECT'"));
    }

    [TestMethod]
    public void Revoke_RemovesPriorGrant()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            grant view any column master key definition to public;
            revoke view any column master key definition from public;
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_permissions where permission_name = 'VIEW ANY COLUMN MASTER KEY DEFINITION'"));
    }

    [TestMethod]
    public void Deny_StoresWithDenyState()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("deny view any column master key definition to public");
        AreEqual("DENY", sim.ExecuteScalar("select state_desc from sys.database_permissions where permission_name = 'VIEW ANY COLUMN MASTER KEY DEFINITION'"));
    }

    [TestMethod]
    public void Grant_WithGrantOption_StoresWState()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("grant view any column master key definition to public with grant option");
        AreEqual("GRANT_WITH_GRANT_OPTION", sim.ExecuteScalar("select state_desc from sys.database_permissions where permission_name = 'VIEW ANY COLUMN MASTER KEY DEFINITION'"));
    }

    [TestMethod]
    public void Grant_UnknownPrincipal_Raises15151()
        => new Simulation().AssertSqlError("grant select to no_such_principal", 15151);

    [TestMethod]
    public void CreateUser_StoresInPrincipalsDict()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user alice");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'alice'"));
        AreEqual("SQL_USER", sim.ExecuteScalar("select type_desc from sys.database_principals where name = 'alice'"));
    }

    [TestMethod]
    public void CreateRole_StoresInPrincipalsDict()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create role data_reader");
        AreEqual("DATABASE_ROLE", sim.ExecuteScalar("select type_desc from sys.database_principals where name = 'data_reader'"));
    }

    [TestMethod]
    public void CreateUser_Duplicate_Raises15023()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user alice");
        _ = sim.AssertSqlError("create user alice", 15023);
    }

    [TestMethod]
    public void AlterRole_AddMember_LandsInRoleMembersView()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create user alice;
            create role data_reader;
            alter role data_reader add member alice;
            """);
        AreEqual(1, sim.ExecuteScalar("""
            select count(*)
            from sys.database_role_members rm
            join sys.database_principals r on r.principal_id = rm.role_principal_id
            join sys.database_principals m on m.principal_id = rm.member_principal_id
            where r.name = 'data_reader' and m.name = 'alice'
            """));
    }

    [TestMethod]
    public void DropUser_RemovesFromCatalog()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create user alice;
            drop user alice;
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'alice'"));
    }

    [TestMethod]
    public void DropUser_IfExists_SilentOnMissing()
        => AreEqual(0, new Simulation().ExecuteScalar("drop user if exists alice; select count(*) from sys.database_principals where name = 'alice'"));

    [TestMethod]
    public void DropRole_CascadeDropsMembership()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create user alice;
            create role data_reader;
            alter role data_reader add member alice;
            drop role data_reader
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_role_members"));
    }

    [TestMethod]
    public void SysDatabasePrincipals_HasFixedPrincipalsPreSeeded()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'public' and is_fixed_role = 1"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'dbo'"));
        AreEqual(0, sim.ExecuteScalar("select principal_id from sys.database_principals where name = 'public'"));
        AreEqual(1, sim.ExecuteScalar("select principal_id from sys.database_principals where name = 'dbo'"));
    }

    [TestMethod]
    public void Grant_MultiplePermissionsCommaList_StoresEach()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int);
            grant select, update, delete on t to public
            """);
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.database_permissions where grantee_principal_id = 0 and class = 1"));
    }
}
