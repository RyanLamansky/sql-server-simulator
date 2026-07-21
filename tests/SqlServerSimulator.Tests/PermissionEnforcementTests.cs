using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Execution-time permission enforcement: the DML / EXECUTE / TRUNCATE /
/// CREATE TABLE matrix at object / schema / database scope, role closure,
/// fixed roles, DENY-beats-GRANT, ownership chaining, and the writer-side
/// GRANT / REVOKE / DENY rules. A non-dbo session is established in-batch via
/// <c>EXECUTE AS USER = 'u'</c> (the grants persist on the database, so setup
/// runs as dbo on a prior connection).
/// </summary>
[TestClass]
public sealed class PermissionEnforcementTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int not null)",
            "insert dbo.t values (1), (2)",
            "create user u without login");
        return sim;
    }

    // ---- SELECT ----

    [TestMethod]
    public void Select_Denied_WithoutGrant_Raises229()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
        Contains("SELECT permission was denied", ex.Message);
        Contains("'t'", ex.Message);
    }

    [TestMethod]
    public void Select_Granted_AtObjectScope_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void Select_Granted_AtSchemaScope_CoversObject()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on schema::dbo to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void Select_Granted_AtDatabaseScope_CoversObject()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void Select_ControlGrant_CoversSelect()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant control on object::dbo.t to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void Select_MissingObject_IsMsg208NotDenial()
    {
        var sim = Seeded();
        _ = sim.AssertSqlError("execute as user = 'u'; select id from dbo.nope", 208);
    }

    // ---- DENY beats GRANT ----

    [TestMethod]
    public void Deny_AtObject_BeatsGrant_AtDatabase()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select to u");
        _ = sim.ExecuteNonQuery("deny select on object::dbo.t to u");
        _ = sim.AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
    }

    [TestMethod]
    public void Deny_ToPublic_BindsDirectlyGrantedUser()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u");
        _ = sim.ExecuteNonQuery("deny select on object::dbo.t to public");
        _ = sim.AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
    }

    // ---- INSERT / UPDATE / DELETE ----

    [TestMethod]
    public void Insert_Denied_WithoutGrant_Raises229()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; insert dbo.t values (3)", 229);
        Contains("INSERT permission was denied", ex.Message);
    }

    [TestMethod]
    public void Insert_Granted_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant insert on object::dbo.t to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; insert dbo.t values (3)");
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.t"));
    }

    [TestMethod]
    public void Update_Denied_WithoutGrant_Raises229()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; update dbo.t set id = 9 where id = 1", 229);
        Contains("UPDATE permission was denied", ex.Message);
    }

    [TestMethod]
    public void Delete_Granted_Succeeds()
    {
        var sim = Seeded();
        // A DELETE with a WHERE clause reads the target, so SELECT is required
        // alongside DELETE (probe M1d).
        _ = sim.ExecuteNonQuery("grant delete on object::dbo.t to u; grant select on object::dbo.t to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; delete dbo.t where id = 1");
        AreEqual(1, sim.ExecuteScalar("select count(*) from dbo.t"));
    }

    // ---- EXECUTE ----

    [TestMethod]
    public void Exec_Denied_WithoutGrant_Raises229WithProcedureAttribution()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p1 as select 1",
            "create user u without login");
        var ex = sim.AssertSqlError("execute as user = 'u'; exec dbo.p1", 229);
        Contains("EXECUTE permission was denied", ex.Message);
        AreEqual("dbo.p1", ex.Procedure);
    }

    [TestMethod]
    public void Exec_Granted_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p1 as select 42",
            "create user u without login");
        _ = sim.ExecuteNonQuery("grant execute on object::dbo.p1 to u");
        AreEqual(42, sim.ExecuteScalar("execute as user = 'u'; exec dbo.p1"));
    }

    // ---- TRUNCATE / CREATE TABLE ----

    [TestMethod]
    public void Truncate_Denied_WithoutAlter_Raises1088()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; truncate table dbo.t", 1088);
        Contains("\"t\"", ex.Message);
    }

    [TestMethod]
    public void Truncate_Granted_WithAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; truncate table dbo.t");
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.t"));
    }

    [TestMethod]
    public void CreateTable_Denied_WithoutDdlAdmin_Raises262()
    {
        var sim = Seeded();
        var ex = sim.AssertSqlError("execute as user = 'u'; create table dbo.t2 (id int)", 262);
        Contains("CREATE TABLE permission denied", ex.Message);
    }

    [TestMethod]
    public void CreateTable_Allowed_ForDdlAdminMember()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_ddladmin add member u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create table dbo.t2 (id int)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.t2"));
    }

    // ---- Fixed roles ----

    [TestMethod]
    public void DbDataReader_GrantsSelectEverywhere()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_datareader add member u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void DbDataReader_DoesNotGrantInsert()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_datareader add member u");
        _ = sim.AssertSqlError("execute as user = 'u'; insert dbo.t values (3)", 229);
    }

    [TestMethod]
    public void DbDataWriter_GrantsInsert()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_datawriter add member u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; insert dbo.t values (3)");
        AreEqual(3, sim.ExecuteScalar("select count(*) from dbo.t"));
    }

    [TestMethod]
    public void DbOwner_GrantsEverything()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_owner add member u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void DbOwner_StillBoundByExplicitDeny()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_owner add member u");
        _ = sim.ExecuteNonQuery("deny select on object::dbo.t to u");
        _ = sim.AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
    }

    [TestMethod]
    public void DbDenyDataReader_BeatsDataReader()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_datareader add member u");
        _ = sim.ExecuteNonQuery("alter role db_denydatareader add member u");
        _ = sim.AssertSqlError("execute as user = 'u'; select id from dbo.t", 229);
    }

    // ---- Role closure ----

    [TestMethod]
    public void NestedRoleMembership_GrantsThroughClosure()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create role r_inner",
            "create role r_outer",
            "alter role r_outer add member u",
            "alter role r_inner add member r_outer",
            "grant select on object::dbo.t to r_inner");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    [TestMethod]
    public void PublicGrant_ReachesEveryPrincipal()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to public");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.t order by id"));
    }

    // ---- Ownership chaining ----

    [TestMethod]
    public void View_ChecksView_NotBaseTable()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create view dbo.v as select id from dbo.t");
        _ = sim.ExecuteNonQuery("grant select on object::dbo.v to u");
        _ = sim.ExecuteNonQuery("deny select on object::dbo.t to u");
        // Base-table DENY is ignored — the chain is unbroken (all dbo-owned).
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select id from dbo.v order by id"));
    }

    [TestMethod]
    public void DynamicSql_BreaksChain_ReEnablesChecks()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int not null)",
            "insert dbo.t values (1)",
            "create user u without login",
            "create procedure dbo.p_dyn as exec('select id from dbo.t')");
        _ = sim.ExecuteNonQuery("grant execute on object::dbo.p_dyn to u");
        // The proc body's static reference would chain, but EXEC('…') runs as
        // the caller — u has no SELECT on t.
        _ = sim.AssertSqlError("execute as user = 'u'; exec dbo.p_dyn", 229);
    }

    [TestMethod]
    public void ProcBody_StaticReference_ChainsUnbroken()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int not null)",
            "insert dbo.t values (7)",
            "create user u without login",
            "create procedure dbo.p_static as select id from dbo.t");
        _ = sim.ExecuteNonQuery("grant execute on object::dbo.p_static to u");
        // EXECUTE on the proc is enough; the body's SELECT on t is not checked.
        AreEqual(7, sim.ExecuteScalar("execute as user = 'u'; exec dbo.p_static"));
    }

    // ---- Writer-side rules ----

    [TestMethod]
    public void CreateUser_AutoSeedsConnectGrant()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create user u without login");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.database_permissions p join sys.database_principals pr on p.grantee_principal_id = pr.principal_id where pr.name = 'u' and p.permission_name = 'CONNECT' and p.state = 'G'"));
    }

    [TestMethod]
    public void WithGrantOption_StoresSingleWRow()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u with grant option");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.database_permissions p join sys.database_principals pr on p.grantee_principal_id = pr.principal_id where pr.name = 'u' and p.permission_name = 'SELECT'"));
        AreEqual("W", sim.ExecuteScalar(
            "select p.state from sys.database_permissions p join sys.database_principals pr on p.grantee_principal_id = pr.principal_id where pr.name = 'u' and p.permission_name = 'SELECT'"));
    }

    [TestMethod]
    public void PlainRevoke_OfGrantableWithDelegations_Raises4611()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create user u2 without login");
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u with grant option");
        // u delegates to u2, then dbo tries a plain REVOKE from u.
        _ = sim.ExecuteNonQuery("execute as user = 'u'; grant select on object::dbo.t to u2");
        _ = sim.AssertSqlError("revoke select on object::dbo.t from u", 4611);
    }

    [TestMethod]
    public void RevokeCascade_RemovesDelegatedSubtree()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create user u2 without login");
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u with grant option");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; grant select on object::dbo.t to u2");
        _ = sim.ExecuteNonQuery("revoke select on object::dbo.t from u cascade");
        // u2's delegated grant is gone.
        _ = sim.AssertSqlError("execute as user = 'u2'; select id from dbo.t", 229);
    }

    [TestMethod]
    public void TypeCode_Select_IsCanonicalSL()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u");
        AreEqual("SL  ", sim.ExecuteScalar(
            "select p.type from sys.database_permissions p join sys.database_principals pr on p.grantee_principal_id = pr.principal_id where pr.name = 'u' and p.permission_name = 'SELECT'"));
    }

    [TestMethod]
    public void SelectOnProcedure_Raises4606()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p1 as select 1",
            "create user u without login");
        _ = sim.AssertSqlError("grant select on object::dbo.p1 to u", 4606);
    }

    [TestMethod]
    public void ExecuteOnTable_Raises4606()
    {
        var sim = Seeded();
        _ = sim.AssertSqlError("grant execute on object::dbo.t to u", 4606);
    }

    [TestMethod]
    public void GrantToDbo_IsInfoChannelNoOp_NotCatchable()
    {
        var sim = Seeded();
        var messages = new List<int>();
        using var connection = sim.CreateOpenConnection();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) =>
        {
            foreach (var err in e.Errors)
                messages.Add(err.Number);
        };
        using var command = connection.CreateCommand();
        command.CommandText = "begin try grant select on object::dbo.t to dbo end try begin catch select error_number() end catch";
        using var reader = command.ExecuteReader();
        // TRY/CATCH does not see the 4624 — it surfaces on the info channel.
        Contains(4624, messages);
    }

    [TestMethod]
    public void UnknownSecurable_Raises15151ObjectVariant()
    {
        var sim = Seeded();
        var ex = sim.AssertSqlError("grant select on object::dbo.ghost to u", 15151);
        Contains("Cannot find the object", ex.Message);
    }

    // ---- Scalars ----

    [TestMethod]
    public void HasPermsByName_Dbo_SeesOne()
        => AreEqual(1, new Simulation().ExecuteScalar("select has_perms_by_name(null, 'database', 'view definition')"));

    [TestMethod]
    public void HasPermsByName_NonDbo_NoGrant_SeesZero()
        => AreEqual(0, Seeded().ExecuteScalar("execute as user = 'u'; select has_perms_by_name('dbo.t', 'object', 'select')"));

    [TestMethod]
    public void HasPermsByName_NonDbo_WithGrant_SeesOne()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant select on object::dbo.t to u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select has_perms_by_name('dbo.t', 'object', 'select')"));
    }

    [TestMethod]
    public void IsMember_TransitiveRole_SeesOne()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create role r1", "alter role r1 add member u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select is_member('r1')"));
    }

    [TestMethod]
    public void IsMember_NonMemberFixedRole_SeesZero()
        => AreEqual(0, Seeded().ExecuteScalar("execute as user = 'u'; select is_member('db_datareader')"));

    [TestMethod]
    public void IsMember_FixedRoleMember_SeesOne()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_datareader add member u");
        AreEqual(1, sim.ExecuteScalar("execute as user = 'u'; select is_member('db_datareader')"));
    }

    [TestMethod]
    public void IsMember_UnknownName_SeesNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select is_member('not_a_role')"));

    // ---- dbo bypass unchanged ----

    [TestMethod]
    public void Dbo_BypassesAllChecks()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("deny select on object::dbo.t to dbo");
        // dbo bypasses — the (info-channel-refused) DENY has no effect anyway.
        AreEqual(1, sim.ExecuteScalar("select id from dbo.t order by id"));
    }

    // ---- Fixed-role catalog projection ----

    [TestMethod]
    public void FixedRoles_ProjectInDatabasePrincipals()
        => AreEqual(9, new Simulation().ExecuteScalar(
            "select count(*) from sys.database_principals where is_fixed_role = 1 and type = 'R' and name <> 'public'"));
}
