using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Application roles: <c>CREATE / ALTER / DROP APPLICATION ROLE</c>, the
/// <c>sys.database_principals</c> type-<c>A</c> projection, and the
/// <c>sp_setapprole</c> / <c>sp_unsetapprole</c> security-context swap — the
/// role replaces the session's database principal (the login is untouched),
/// pins the session to its database (Msg 505), and can only be unset with the
/// cookie the activation issued. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ApplicationRoleTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.t (id int not null primary key);
            insert dbo.t values (1), (2);
            create application role app1 with password = 'App!Pass123';
            grant select on object::dbo.t to app1
            """);
        return sim;
    }

    // ---- DDL + catalog projection ----

    [TestMethod]
    public void CreateApplicationRole_ProjectsTypeA()
    {
        var sim = Seeded();
        AreEqual("A", ((string)sim.ExecuteScalar("select type from sys.database_principals where name = 'app1'")!).Trim());
        AreEqual("APPLICATION_ROLE", sim.ExecuteScalar("select type_desc from sys.database_principals where name = 'app1'"));
        AreEqual("dbo", sim.ExecuteScalar("select default_schema_name from sys.database_principals where name = 'app1'"));
        IsFalse((bool)sim.ExecuteScalar("select is_fixed_role from sys.database_principals where name = 'app1'")!);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'app1' and owning_principal_id is null"));
    }

    [TestMethod]
    public void CreateApplicationRole_DefaultSchemaOption()
        => AreEqual("aps", new Simulation().ExecuteScalar("""
            create schema aps;
            create application role app2 with password = 'App!Pass123', default_schema = aps;
            select default_schema_name from sys.database_principals where name = 'app2'
            """));

    [TestMethod]
    public void OtherPrincipals_KeepNullDefaultSchema()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create user u without login;
            select count(*) from sys.database_principals where name = 'u' and default_schema_name is null
            """));

    [TestMethod]
    public void AlterApplicationRole_RenameAndDefaultSchema()
    {
        var sim = Seeded();
        var id = sim.ExecuteScalar("select principal_id from sys.database_principals where name = 'app1'");
        _ = sim.ExecuteNonQuery("create schema aps; alter application role app1 with name = app1b, default_schema = aps");
        AreEqual("aps", sim.ExecuteScalar("select default_schema_name from sys.database_principals where name = 'app1b'"));
        // The principal_id survives the rename, so grants follow the role.
        AreEqual(id, sim.ExecuteScalar("select principal_id from sys.database_principals where name = 'app1b'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'app1'"));
    }

    [TestMethod]
    public void AlterApplicationRole_PasswordChange_OldPasswordStopsWorking()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter application role app1 with password = 'New!Pass456'");
        _ = sim.AssertSqlError("exec sp_setapprole 'app1', 'App!Pass123'", 15161);
        AreEqual("app1", sim.ExecuteScalar("exec sp_setapprole 'app1', 'New!Pass456'; select user_name()"));
    }

    [TestMethod]
    public void DropApplicationRole_RemovesPrincipal()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("drop application role app1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'app1'"));
    }

    [TestMethod]
    public void DuplicateApplicationRole_Raises15023()
        => _ = Seeded().AssertSqlError("create application role app1 with password = 'App!Pass123'", 15023);

    [TestMethod]
    public void AlterUnknownApplicationRole_Raises15151()
        => _ = new Simulation().AssertSqlError("alter application role ghost with name = other", 15151);

    // ---- sp_setapprole: the context swap ----

    [TestMethod]
    public void SetAppRole_SwapsDatabasePrincipal_LoginUnchanged()
    {
        var sim = Seeded();
        AreEqual("app1", sim.ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select user_name()"));
        AreEqual("app1", sim.ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select current_user"));
        // SYSTEM_USER / ORIGINAL_LOGIN() keep reporting the login (probe-confirmed).
        AreEqual("dbo", sim.ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select suser_name()"));
        AreEqual("dbo", sim.ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select original_login()"));
    }

    [TestMethod]
    public void SetAppRole_UserIdFollowsTheRole()
    {
        var sim = Seeded();
        var roleId = sim.ExecuteScalar("select principal_id from sys.database_principals where name = 'app1'");
        AreEqual(roleId, sim.ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select user_id()"));
    }

    [TestMethod]
    public void SetAppRole_WrongPassword_Raises15161()
    {
        var ex = Seeded().AssertSqlError("exec sp_setapprole 'app1', 'Wrong!Pass'", 15161);
        AreEqual("Cannot set application role 'app1' because it does not exist or the password is incorrect.", ex.Message);
    }

    [TestMethod]
    public void SetAppRole_UnknownRole_Raises15161_SameWording()
    {
        // Real leaks no distinction between a missing role and a bad password.
        var ex = Seeded().AssertSqlError("exec sp_setapprole 'ghost', 'App!Pass123'", 15161);
        AreEqual("Cannot set application role 'ghost' because it does not exist or the password is incorrect.", ex.Message);
    }

    [TestMethod]
    public void SetAppRole_OnAPlainRole_Raises15161()
        => _ = new Simulation().AssertSqlError(
            "create role r; exec sp_setapprole 'r', 'App!Pass123'", 15161);

    [TestMethod]
    public void SetAppRole_Twice_Raises2762()
    {
        var ex = Seeded().AssertSqlError("""
            exec sp_setapprole 'app1', 'App!Pass123';
            exec sp_setapprole 'app1', 'App!Pass123'
            """, 2762);
        AreEqual("sp_setapprole was not invoked correctly. Refer to the documentation for more information.", ex.Message);
    }

    [TestMethod]
    public void SetAppRole_NamedParameters()
        => AreEqual("app1", Seeded().ExecuteScalar(
            "exec sp_setapprole @rolename = 'app1', @password = 'App!Pass123'; select user_name()"));

    // ---- Enforcement under the role ----

    [TestMethod]
    public void AfterActivation_RoleGrantsApply()
        => AreEqual(2, Seeded().ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select count(*) from dbo.t"));

    [TestMethod]
    public void AfterActivation_PreActivationUserGrantsAreGone()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("""
            create table dbo.only_u (id int not null);
            create user u without login;
            grant select on object::dbo.only_u to u
            """);
        // u can read its own table; the application role cannot.
        AreEqual(0, sim.ExecuteScalar("execute as user = 'u'; select count(*) from dbo.only_u"));
        _ = sim.AssertSqlError("exec sp_setapprole 'app1', 'App!Pass123'; select count(*) from dbo.only_u", 229);
    }

    [TestMethod]
    public void AfterActivation_PublicGrantsStillApply()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("""
            create table dbo.pub (id int not null);
            insert dbo.pub values (7);
            grant select on object::dbo.pub to public
            """);
        AreEqual(1, sim.ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select count(*) from dbo.pub"));
    }

    [TestMethod]
    public void AfterActivation_UngrantedTableIsDenied()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("create table dbo.other (id int not null)");
        _ = sim.AssertSqlError("exec sp_setapprole 'app1', 'App!Pass123'; select count(*) from dbo.other", 229);
    }

    // ---- The database pin (Msg 505) ----

    [TestMethod]
    public void UseAfterActivation_Raises505()
    {
        var sim = Seeded();
        var ex = sim.AssertSqlError("exec sp_setapprole 'app1', 'App!Pass123'; use master", 505);
        AreEqual("The current user account was invoked with SETUSER or SP_SETAPPROLE. Changing databases is not allowed.", ex.Message);
    }

    [TestMethod]
    public void ChangeDatabaseAfterActivation_Raises505()
    {
        var sim = Seeded();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("exec sp_setapprole 'app1', 'App!Pass123'").ExecuteNonQuery();
        var ex = Throws<SimulatedSqlException>(() => connection.ChangeDatabase("master"));
        AreEqual(505, ex.Number);
    }

    // ---- The cookie round trip ----

    [TestMethod]
    public void UnsetAppRole_WithCookie_RestoresThePriorPrincipal()
    {
        var sim = Seeded();
        AreEqual("dbo", sim.ExecuteScalar("""
            declare @c varbinary(8000);
            exec sp_setapprole 'app1', 'App!Pass123', @fCreateCookie = 1, @cookie = @c output;
            exec sp_unsetapprole @c;
            select user_name()
            """));
    }

    [TestMethod]
    public void CookieIsFiftyBytes()
        => AreEqual(50, Seeded().ExecuteScalar("""
            declare @c varbinary(8000);
            exec sp_setapprole 'app1', 'App!Pass123', @fCreateCookie = 1, @cookie = @c output;
            select datalength(@c)
            """));

    [TestMethod]
    public void AfterUnset_TheDatabasePinIsReleased()
        => AreEqual("master", Seeded().ExecuteScalar("""
            declare @c varbinary(8000);
            exec sp_setapprole 'app1', 'App!Pass123', @fCreateCookie = 1, @cookie = @c output;
            exec sp_unsetapprole @c;
            use master;
            select db_name()
            """));

    [TestMethod]
    public void UnsetAppRole_WrongCookie_Raises15592()
    {
        var ex = Seeded().AssertSqlError("""
            declare @c varbinary(8000);
            exec sp_setapprole 'app1', 'App!Pass123', @fCreateCookie = 1, @cookie = @c output;
            exec sp_unsetapprole 0x00
            """, 15592);
        AreEqual("Cannot unset application role because none was set or the cookie is invalid.", ex.Message);
    }

    [TestMethod]
    public void UnsetAppRole_WithNoRoleSet_Raises15592()
        => _ = new Simulation().AssertSqlError("exec sp_unsetapprole 0x00", 15592);

    [TestMethod]
    public void UnsetAppRole_WithoutACookieHavingBeenIssued_Raises15592()
        => _ = Seeded().AssertSqlError("""
            exec sp_setapprole 'app1', 'App!Pass123';
            exec sp_unsetapprole 0x00
            """, 15592);

    // ---- Role membership ----

    [TestMethod]
    public void ApplicationRole_CanBeADatabaseRoleMember()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("""
            create table dbo.readable (id int not null);
            insert dbo.readable values (5);
            alter role db_datareader add member app1
            """);
        AreEqual(1, sim.ExecuteScalar("exec sp_setapprole 'app1', 'App!Pass123'; select count(*) from dbo.readable"));
    }

    [TestMethod]
    public void DropApplicationRole_CascadesRoleMembership()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_datareader add member app1; drop application role app1");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_role_members where member_principal_id not in (select principal_id from sys.database_principals)"));
    }
}
