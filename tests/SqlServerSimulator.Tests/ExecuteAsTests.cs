using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Session-principal identity: <c>EXECUTE AS</c> / <c>REVERT</c> impersonation
/// (database users, logins, the missing-target and dbo-quirk error paths, the
/// nested-impersonation IMPERSONATE gate), the identity scalars under
/// impersonation, <c>CREATE USER … FOR LOGIN</c> / <c>WITHOUT LOGIN</c> linkage,
/// module <c>WITH EXECUTE AS</c>, and the restricted-principal Msg 916 gate.
/// Stage 1 is identity only — no permission enforcement.
/// </summary>
[TestClass]
public sealed class ExecuteAsTests
{
    // Each EXECUTE AS + observation runs in one batch so it shares the single
    // connection the ExecuteScalar helper opens (impersonation is session state).

    [TestMethod]
    public void DefaultSession_IsDboEverywhere()
        => AreEqual("dbo|dbo|dbo", new Simulation().ExecuteScalar(
            "select current_user + '|' + system_user + '|' + original_login()"));

    [TestMethod]
    public void ExecuteAsUser_WithoutLogin_SetsCurrentUserAndSidLogin()
    {
        var value = (string)new Simulation().ExecuteScalar("""
            create user u without login;
            execute as user = 'u';
            select current_user + '|' + system_user + '|' + original_login()
            """)!;
        var parts = value.Split('|');
        AreEqual("u", parts[0]);
        StartsWith("S-1-9-3-", parts[1]);
        AreEqual("dbo", parts[2]);
    }

    [TestMethod]
    public void ExecuteAsUser_ReflectsInUserIdAndDatabasePrincipalId()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create user u without login;
            execute as user = 'u';
            select case when user_id() = database_principal_id() and user_id() = user_id('u') then 1 else 0 end
            """));

    [TestMethod]
    public void Revert_PopsOneFrame()
        => AreEqual("dbo", new Simulation().ExecuteScalar("""
            create user u without login;
            execute as user = 'u';
            revert;
            select current_user
            """));

    [TestMethod]
    public void StrayRevert_AtBase_IsNoOp()
        => AreEqual("dbo", new Simulation().ExecuteScalar("revert; select current_user"));

    [TestMethod]
    public void NestedImpersonation_RevertsOneLevelAtATime()
        => AreEqual("a|b|a|dbo", new Simulation().ExecuteScalar("""
            create user a without login;
            create user b without login;
            grant impersonate on user::b to a;
            declare @out nvarchar(200) = '';
            execute as user = 'a';
            set @out = current_user;
            execute as user = 'b';
            set @out = @out + '|' + current_user;
            revert;
            set @out = @out + '|' + current_user;
            revert;
            set @out = @out + '|' + current_user;
            select @out
            """));

    [TestMethod]
    public void ExecuteAsUser_Missing_Raises15517()
        => new Simulation().AssertSqlError(
            "execute as user = 'ghost'; select 1", 15517,
            "Cannot execute as the database principal because the principal \"ghost\" does not exist, this type of principal cannot be impersonated, or you do not have permission.");

    [TestMethod]
    public void ExecuteAsUser_Dbo_AlwaysRaises15517()
        => new Simulation().AssertSqlError("execute as user = 'dbo'; select 1", 15517);

    [TestMethod]
    public void NestedImpersonation_ByNonDbo_WithoutImpersonateGrant_Raises15517()
        => new Simulation().AssertSqlError("""
            create user a without login;
            create user b without login;
            execute as user = 'a';
            execute as user = 'b';
            select 1
            """, 15517);

    [TestMethod]
    public void ExecuteAsLogin_Missing_Raises15406()
        => new Simulation().AssertSqlError(
            "execute as login = 'ghostlogin'; select 1", 15406,
            "Cannot execute as the server principal because the principal \"ghostlogin\" does not exist, this type of principal cannot be impersonated, or you do not have permission.");

    [TestMethod]
    public void ExecuteAsLogin_MapsToDatabaseUser()
        => AreEqual("u|app|dbo", new Simulation().ExecuteScalar("""
            create login app with password = 'S3cret!Pass';
            create user u for login app;
            execute as login = 'app';
            select current_user + '|' + system_user + '|' + original_login()
            """));

    [TestMethod]
    public void Use_UnderImpersonation_Raises916()
        => new Simulation().AssertSqlError("""
            create user u without login;
            execute as user = 'u';
            use master
            """, 916);

    [TestMethod]
    public void Use_AsDbo_StillSwitches()
        => AreEqual("master", new Simulation().ExecuteScalar("use master; select db_name()"));

    // ---- CREATE USER source clauses ----

    [TestMethod]
    public void WithoutLoginSid_IsDeterministicAcrossSimulations()
    {
        static string SidOf(Simulation sim) => (string)sim.ExecuteScalar("""
            create user u without login;
            execute as user = 'u';
            select system_user
            """)!;
        AreEqual(SidOf(new Simulation()), SidOf(new Simulation()));
    }

    [TestMethod]
    public void CreateUser_BareAndForLogin_BothRegisterPrincipal()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            create login l with password = 'P@ss1word';
            create user bare;
            create user linked for login l;
            select case when user_id('bare') is not null then 1 else 0 end
                 + case when user_id('linked') is not null then 1 else 0 end
            """));

    // ---- Module WITH EXECUTE AS ----

    [TestMethod]
    public void Procedure_ExecuteAsOwner_RunsAsDbo()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create procedure dbo.p_owner with execute as owner as select current_user");
        _ = sim.ExecuteNonQuery("grant execute on object::dbo.p_owner to u");
        AreEqual("dbo", sim.ExecuteScalar("execute as user = 'u'; exec dbo.p_owner"));
    }

    [TestMethod]
    public void Procedure_ExecuteAsSelf_RunsAsDbo()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p_self with execute as self as select current_user");
        AreEqual("dbo", sim.ExecuteScalar("exec dbo.p_self"));
    }

    [TestMethod]
    public void Procedure_ExecuteAsNamedUser_RunsAsThatUser()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create procedure dbo.p_user with execute as 'u' as select current_user");
        AreEqual("u", sim.ExecuteScalar("exec dbo.p_user"));
    }

    [TestMethod]
    public void Procedure_ExecuteAsCaller_RunsAsCaller()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create procedure dbo.p_caller with execute as caller as select current_user");
        _ = sim.ExecuteNonQuery("grant execute on object::dbo.p_caller to u");
        AreEqual("u", sim.ExecuteScalar("execute as user = 'u'; exec dbo.p_caller"));
    }

    [TestMethod]
    public void Procedure_FrameReverts_OnExit()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create procedure dbo.p_owner with execute as owner as select current_user");
        // After the proc's OWNER frame pops, the outer session is dbo again.
        AreEqual("dbo", sim.ExecuteScalar("exec dbo.p_owner; select current_user"));
    }

    [TestMethod]
    public void Procedure_ExecuteAsMissingUser_Raises15517AtExec()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p_ghost with execute as 'ghost' as select 1");
        _ = sim.AssertSqlError("exec dbo.p_ghost", 15517);
    }

    // ----- sys.sql_modules.execute_as_principal_id -----
    //
    // Real records the clause's resolved principal at CREATE: -2 for OWNER
    // (the owner is resolved per execution, so no id is pinned), the creating
    // session's principal for SELF, the named user's id for a user, and NULL
    // for CALLER and for no clause at all. Probe-confirmed across procedures,
    // scalar functions and triggers.

    private static object? ExecuteAsPrincipalId(Simulation simulation, string objectName)
        => simulation.ExecuteScalar($"select execute_as_principal_id from sys.sql_modules where object_id = object_id('{objectName}')");

    [TestMethod]
    public void SqlModules_ExecuteAsPrincipalId_NoClauseAndCaller_AreNull()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p_none as select 1",
            "create procedure dbo.p_caller with execute as caller as select 1");
        AreEqual(DBNull.Value, ExecuteAsPrincipalId(sim, "dbo.p_none"));
        AreEqual(DBNull.Value, ExecuteAsPrincipalId(sim, "dbo.p_caller"));
    }

    [TestMethod]
    public void SqlModules_ExecuteAsPrincipalId_Owner_IsMinusTwo()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p_owner with execute as owner as select 1");
        AreEqual(-2, ExecuteAsPrincipalId(sim, "dbo.p_owner"));
    }

    [TestMethod]
    public void SqlModules_ExecuteAsPrincipalId_Self_IsTheCreatingPrincipal()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p_self with execute as self as select 1");
        AreEqual(1, ExecuteAsPrincipalId(sim, "dbo.p_self"));
    }

    [TestMethod]
    public void SqlModules_ExecuteAsPrincipalId_NamedUser_IsThatUsersId()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user u without login",
            "create procedure dbo.p_named with execute as 'u' as select 1");
        AreEqual(sim.ExecuteScalar("select principal_id from sys.database_principals where name = 'u'"),
            ExecuteAsPrincipalId(sim, "dbo.p_named"));
    }

    [TestMethod]
    public void SqlModules_ExecuteAsPrincipalId_ScalarFunction_ResolvesTheSameWay()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.f_owner() returns int with execute as owner as begin return 1 end",
            "create function dbo.f_self() returns int with execute as self as begin return 1 end",
            "create function dbo.f_caller() returns int with execute as caller as begin return 1 end");
        AreEqual(-2, ExecuteAsPrincipalId(sim, "dbo.f_owner"));
        AreEqual(1, ExecuteAsPrincipalId(sim, "dbo.f_self"));
        AreEqual(DBNull.Value, ExecuteAsPrincipalId(sim, "dbo.f_caller"));
    }

    [TestMethod]
    public void SqlModules_ExecuteAsPrincipalId_Trigger_ResolvesTheSameWay()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (id int)",
            "create trigger dbo.tr_owner on dbo.t with execute as owner after insert as select 1",
            "create trigger dbo.tr_plain on dbo.t after update as select 1");
        AreEqual(-2, ExecuteAsPrincipalId(sim, "dbo.tr_owner"));
        AreEqual(DBNull.Value, ExecuteAsPrincipalId(sim, "dbo.tr_plain"));
    }
}
