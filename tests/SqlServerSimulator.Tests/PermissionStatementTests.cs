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
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_permissions where permission_name = 'SELECT' and major_id = object_id('t')"));
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
        _ = sim.ExecuteNonQuery("create user alice without login");
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
        _ = sim.ExecuteNonQuery("create user alice without login");
        _ = sim.AssertSqlError("create user alice without login", 15023);
    }

    [TestMethod]
    public void AlterRole_AddMember_LandsInRoleMembersView()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create user alice without login;
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
            create user alice without login;
            drop user alice;
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'alice'"));
    }

    [TestMethod]
    public void DropUser_IfExists_SilentOnMissing()
        => AreEqual(0, new Simulation().ExecuteScalar("drop user if exists alice; select count(*) from sys.database_principals where name = 'alice'"));

    [TestMethod]
    public void DropRole_WithMembers_RaisesMsg15144_WhileDropUserTakesItsMemberships()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create user alice without login;
            create role data_reader;
            alter role data_reader add member alice
            """);
        sim.AssertSqlError("drop role data_reader", 15144, "The role has members. It must be empty before it can be dropped.");
        _ = sim.ExecuteNonQuery("drop user alice; drop role data_reader");
        // dbo's own db_owner membership is the one row left.
        AreEqual("16384:1", sim.ExecuteScalar("select string_agg(concat(role_principal_id, ':', member_principal_id), ',') from sys.database_role_members"));
    }

    [TestMethod]
    public void SysDatabasePrincipals_HasFixedPrincipalsPreSeeded()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.database_principals where name = 'public' and is_fixed_role = 0"));
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
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.database_permissions where grantee_principal_id = 0 and class = 1 and major_id = object_id('t')"));
    }

    /// <summary>
    /// <c>CREATE / ALTER USER … WITH</c> take their option lists, <c>ALTER
    /// ROLE … WITH NAME</c> renames, and a role refuses dbo or itself as a
    /// member (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void UserAndRoleOptions()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u1 without login with default_schema = nosuch; create role r1");
        AreEqual("nosuch", sim.ExecuteScalar("select default_schema_name from sys.database_principals where name = 'u1'"));
        _ = sim.ExecuteNonQuery("alter user u1 with name = u2, default_schema = dbo; alter role r1 with name = r2");
        AreEqual("u2:dbo|r2", sim.ExecuteScalar("select concat((select name + ':' + default_schema_name from sys.database_principals where name = 'u2'), '|', (select name from sys.database_principals where name = 'r2'))"));
        sim.AssertSqlError("alter user nosuch with default_schema = dbo", 15151, "Cannot alter the user 'nosuch', because it does not exist or you do not have permission.");
        sim.AssertSqlError("alter role r2 add member dbo", 15405, "Cannot use the special principal 'dbo'.");
        sim.AssertSqlError("alter role r2 add member r2", 15413, "Cannot make a role a member of itself.");
        sim.AssertSqlError("create role r3; alter role r3 with name = r2", 15023, "User, group, or role 'r2' already exists in the current database.");
    }

    // ---- Statement validation, authority and the recorded grantor (probed 2026-10-04 against SQL Server 2025) ----

    private static Simulation Granting()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int)",
            "create procedure dbo.p as select 1 x",
            "create user u1 without login; create user u2 without login; create role r1; alter role r1 add member u1");
        return sim;
    }

    private static string Rows(Simulation sim, string grantee) => (string)sim.ExecuteScalar(
        $"select isnull(string_agg(concat(permission_name, ':', state_desc, ':', user_name(grantor_principal_id)), ',') within group (order by permission_name), '') from sys.database_permissions where grantee_principal_id = user_id('{grantee}') and class <> 0")!;

    [TestMethod]
    public void PermissionNames_AreCheckedAgainstTheClass()
    {
        var sim = Granting();
        var ex = sim.AssertSqlError("grant create table on schema::dbo to u1", 102);
        AreEqual(0, ex.Errors[0].LineNumber);
        Contains("'CREATE TABLE'", ex.Message);
        _ = sim.AssertSqlError("grant create sequence to u1", 102);
        _ = sim.AssertSqlError("grant connect on dbo.t to u1", 102);
        _ = sim.AssertSqlError("grant receive on dbo.t to u1", 4606);
        _ = sim.ExecuteNonQuery("grant alter any user to u1");
        AreEqual("ALTER ANY USER|ALUS", sim.ExecuteScalar("select permission_name + '|' + rtrim(type) from sys.database_permissions where grantee_principal_id = user_id('u1') and permission_name like 'ALTER%'"));
    }

    [TestMethod]
    public void Grantees_AllResolveBeforeAnyRowChanges()
    {
        var sim = Granting();
        _ = sim.AssertSqlError("grant select on dbo.t to u1, nosuch", 15151);
        AreEqual("", Rows(sim, "u1"));
        _ = sim.AssertSqlError("grant select on dbo.t to db_datareader", 4617);
        _ = sim.ExecuteNonQuery("grant select on dbo.t to public");
    }

    [TestMethod]
    public void All_ExpandsAndWarns()
    {
        var sim = Granting();
        _ = sim.ExecuteNonQuery("grant all on dbo.t to u1");
        AreEqual("DELETE:GRANT:dbo,INSERT:GRANT:dbo,REFERENCES:GRANT:dbo,SELECT:GRANT:dbo,UPDATE:GRANT:dbo", Rows(sim, "u1"));
        _ = sim.ExecuteNonQuery("grant all on dbo.p to u2");
        AreEqual("EXECUTE:GRANT:dbo", Rows(sim, "u2"));
    }

    [TestMethod]
    public void Securables_AreNamedByTheirClass()
    {
        var sim = Granting();
        AreEqual("Cannot find the schema 'nosuch', because it does not exist or you do not have permission.", sim.AssertSqlError("grant select on schema::nosuch to u1", 15151).Message);
        AreEqual("Cannot find the role 'nosuch', because it does not exist or you do not have permission.", sim.AssertSqlError("grant alter on role::nosuch to u1", 15151).Message);
        AreEqual("Cannot find the database 'nosuch', because it does not exist or you do not have permission.", sim.AssertSqlError("grant select on database::nosuch to u1", 15151).Message);
        AreEqual("Cannot find the object 'db_owner', because it does not exist or you do not have permission.", sim.AssertSqlError("grant control on role::db_owner to u1", 15151).Message);
        AreEqual("Cannot find the object 'int', because it does not exist or you do not have permission.", sim.AssertSqlError("grant references on type::int to u1", 15151).Message);
        _ = sim.AssertSqlError("grant select on information_schema.tables to u1", 4629);
        _ = sim.AssertSqlError("grant select on master.dbo.spt_values to u1", 4610);
    }

    [TestMethod]
    public void TheGrantorIsTheSecurablesOwner_UnlessAGrantOptionAnswers()
    {
        var sim = Granting();
        sim.ExecuteBatches("create user u3 without login", "grant select on dbo.t to u1 with grant option", "grant control on dbo.t to u3");
        _ = sim.ExecuteNonQuery("execute as user = 'u1'; grant select on dbo.t to u2");
        AreEqual("SELECT:GRANT:u1", Rows(sim, "u2"));
        _ = sim.ExecuteNonQuery("execute as user = 'u3'; grant insert on dbo.t to u2");
        AreEqual("INSERT:GRANT:dbo,SELECT:GRANT:u1", Rows(sim, "u2"));
        _ = sim.ExecuteNonQuery("grant impersonate on user::u2 to u1");
        AreEqual("u2", sim.ExecuteScalar("select user_name(grantor_principal_id) from sys.database_permissions where class = 4 and grantee_principal_id = user_id('u1')"));
        // A grant option doesn't carry DENY, and dbo's REVOKE leaves what u1 granted.
        _ = sim.AssertSqlError("execute as user = 'u1'; deny select on dbo.t to u2", 15151);
        _ = sim.ExecuteNonQuery("revoke select on dbo.t from u2");
        AreEqual("INSERT:GRANT:dbo,SELECT:GRANT:u1", Rows(sim, "u2"));
        AreEqual("Grantor does not have GRANT permission.", sim.AssertSqlError("execute as user = 'u1'; grant create table to u2", 4613).Message);
    }

    [TestMethod]
    public void As_NamesAPrincipalTheSessionCanActAs()
    {
        var sim = Granting();
        sim.ExecuteBatches("grant select on dbo.t to r1 with grant option");
        _ = sim.ExecuteNonQuery("execute as user = 'u1'; grant select on dbo.t to u2 as r1");
        AreEqual("SELECT:GRANT:r1", Rows(sim, "u2"));
        AreEqual("Cannot find the user 'dbo', because it does not exist or you do not have permission.", sim.AssertSqlError("execute as user = 'u1'; grant select on dbo.t to u2 as dbo", 15151).Message);
        _ = sim.AssertSqlError("grant select on dbo.t to u2 as nosuch", 15151);
        AreEqual("Cannot find the object 't', because it does not exist or you do not have permission.", sim.AssertSqlError("grant select on dbo.t to u1 as u2", 15151).Message);
    }

    [TestMethod]
    public void DenyCascade_AndTheGrantOptionRule()
    {
        var sim = Granting();
        sim.ExecuteBatches("grant select on dbo.t to u1 with grant option", "execute as user = 'u1'; grant select on dbo.t to u2");
        _ = sim.AssertSqlError("deny select on dbo.t to u1", 4611);
        _ = sim.ExecuteNonQuery("deny select on dbo.t to u1 cascade");
        AreEqual("SELECT:DENY:dbo", Rows(sim, "u1"));
        AreEqual("", Rows(sim, "u2"));
        _ = sim.AssertSqlError("deny select on dbo.t from u1", 102);
    }

    [TestMethod]
    public void OwnerAsGrantee_IsTheInformational4624()
    {
        var sim = Granting();
        _ = sim.ExecuteNonQuery("alter authorization on object::dbo.t to u1");
        var messages = new List<int>();
        using var connection = sim.CreateOpenConnection();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(static error => error.Number));
        using var command = connection.CreateCommand("deny select on dbo.t to u1");
        _ = command.ExecuteNonQuery();
        AreEqual("4624", string.Join(",", messages));
        AreEqual("", Rows(sim, "u1"));
    }

    [TestMethod]
    public void DroppingAPrincipal_ThatGrantedOrStillHoldsMembers()
    {
        var sim = Granting();
        sim.ExecuteBatches("grant select on dbo.t to u1 with grant option", "execute as user = 'u1'; grant select on dbo.t to u2");
        _ = sim.AssertSqlError("drop user u1", 15284);
        _ = sim.ExecuteNonQuery("create schema x authorization r1");
        _ = sim.AssertSqlError("drop role r1", 15144);
        _ = sim.AssertSqlError("drop role db_owner", 15150);
        _ = sim.AssertSqlError("drop user dbo", 15150);
        _ = sim.AssertSqlError("drop user guest", 15539);
        _ = sim.ExecuteNonQuery("revoke select on dbo.t from u2; drop user u2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.database_permissions where grantee_principal_id not in (select principal_id from sys.database_principals)"));
    }

    [TestMethod]
    public void RenamingAFixedRole_Raises15150()
        => new Simulation().AssertSqlError("alter role db_owner with name = zz", 15150, "Cannot alter the role 'db_owner'.");

    [TestMethod]
    public void ServerRoleMemberThatDoesNotExist_Raises15151()
    {
        var sim = new Simulation();
        sim.AssertSqlError("alter server role dbcreator drop member nope", 15151, "Cannot drop the server principal 'nope', because it does not exist or you do not have permission.");
        sim.AssertSqlError("alter server role dbcreator add member nope", 15151, "Cannot add the server principal 'nope', because it does not exist or you do not have permission.");
    }

    [TestMethod]
    [Description("A principal securable's class word settles which permissions apply before the name resolves (probed 2026-10-07 against SQL Server 2025).")]
    [DataRow("grant select on user::nosuch to u1", 102, "Incorrect syntax near 'SELECT'.")]
    [DataRow("grant select on role::nosuch to u1", 102, "Incorrect syntax near 'SELECT'.")]
    [DataRow("grant control on user::nosuch to u1", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.")]
    [DataRow("deny all on user::nosuch to r1", 15151, "Cannot find the user 'nosuch', because it does not exist or you do not have permission.")]
    public void PrincipalSecurable_PermissionClassBeforeName(string sql, int number, string message)
        => new Simulation().AssertSqlError("create role r1; create user u1 without login; " + sql, number, message);

    [TestMethod]
    [Description("ALL is Msg 4623 on a securable other than the database or an object, once the securable resolves (probed 2026-10-07 against SQL Server 2025).")]
    [DataRow("grant all on schema::dbo to u1")]
    [DataRow("grant all on type::dbo.tt to u1")]
    [DataRow("grant all on role::r1 to u1")]
    [DataRow("grant all privileges on user::u1 to r1")]
    [DataRow("grant all, control on user::u1 to r1")]
    [DataRow("revoke all on user::u1 from r1")]
    public void AllPermission_OnOtherClasses_Raises4623(string sql)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create role r1; create user u1 without login; create type dbo.tt from int");
        sim.AssertSqlError(sql, 4623, "The all permission has been deprecated and is not available for this class of entity");
    }
}
