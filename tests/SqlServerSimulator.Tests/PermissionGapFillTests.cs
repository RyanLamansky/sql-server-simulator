using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The DML read-completeness (A), DDL statement gates (B), scalar-UDF EXECUTE
/// in non-query contexts (C), module WITH EXECUTE AS runtime (D), and delegated-
/// grant covering (E) gap-fills. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class PermissionGapFillTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t1 (id int not null, v varchar(20) null)",
            "insert dbo.t1 values (1, 'a'), (2, 'b')",
            "create table dbo.t2 (id int not null)",
            "insert dbo.t2 values (1)",
            "create user u without login");
        return sim;
    }

    // ---- A. UPDATE / DELETE read-implies-SELECT ----

    [TestMethod]
    public void Update_WithWhere_RequiresSelect()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant update on object::dbo.t1 to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; update dbo.t1 set v = 'x' where id = 1", 229);
        Contains("SELECT permission was denied", ex.Message);
        Contains("'t1'", ex.Message);
    }

    [TestMethod]
    public void Update_NoWhere_ConstantSet_NeedsNoSelect()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant update on object::dbo.t1 to u");
        AreEqual(2, sim.ExecuteNonQuery("execute as user = 'u'; update dbo.t1 set v = 'x'"));
    }

    [TestMethod]
    public void Update_NoWhere_SetReadsTargetColumn_RequiresSelect()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant update on object::dbo.t1 to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; update dbo.t1 set v = v + 'x'", 229);
        Contains("SELECT permission was denied", ex.Message);
    }

    [TestMethod]
    public void Update_WithSelectAndUpdate_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant update on object::dbo.t1 to u; grant select on object::dbo.t1 to u");
        AreEqual(1, sim.ExecuteNonQuery("execute as user = 'u'; update dbo.t1 set v = 'x' where id = 1"));
    }

    [TestMethod]
    public void Delete_WithWhere_RequiresSelect()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant delete on object::dbo.t1 to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; delete dbo.t1 where id = 1", 229);
        Contains("SELECT permission was denied", ex.Message);
    }

    [TestMethod]
    public void Delete_NoWhere_NeedsNoSelect()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant delete on object::dbo.t1 to u");
        AreEqual(2, sim.ExecuteNonQuery("execute as user = 'u'; delete dbo.t1"));
    }

    [TestMethod]
    public void Update_JoinedFromSource_RequiresSelectOnSource()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant update on object::dbo.t1 to u; grant select on object::dbo.t1 to u");
        var ex = sim.AssertSqlError(
            "execute as user = 'u'; update t1 set v = 'j' from dbo.t1 join dbo.t2 on t1.id = t2.id", 229);
        Contains("SELECT permission was denied", ex.Message);
        Contains("'t2'", ex.Message);
    }

    // ---- B. DDL gates ----

    [TestMethod]
    public void CreateTable_NoDbPermission_Raises262State1()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; create table dbo.tt (id int)", 262);
        AreEqual((byte)1, ex.State);
        Contains("CREATE TABLE permission denied", ex.Message);
    }

    [TestMethod]
    public void CreateTable_DbPermissionButNoSchemaAlter_Raises2760()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant create table to u");
        var ex = sim.AssertSqlError("execute as user = 'u'; create table dbo.tt (id int)", 2760);
        AreEqual("The specified schema name \"dbo\" either does not exist or you do not have permission to use it.", ex.Message);
    }

    [TestMethod]
    public void CreateTable_DbPermissionAndSchemaAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant create table to u; grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; create table dbo.tt (id int)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.tt"));
    }

    // CREATE VIEW / PROCEDURE / FUNCTION / SCHEMA must be the first statement in
    // a batch, so EXECUTE AS runs on a separate command on the same connection.
    private static SimulatedSqlException CreateAsUserError(Simulation sim, string ddl)
    {
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("execute as user = 'u'").ExecuteNonQuery();
        return Throws<SimulatedSqlException>(() =>
        {
            using var cmd = conn.CreateCommand(ddl);
            _ = cmd.ExecuteNonQuery();
        });
    }

    [TestMethod]
    public void CreateView_NoPermission_Raises262State18WithAttribution()
    {
        var ex = CreateAsUserError(Seeded(), "create view dbo.vx as select 1 as a");
        AreEqual(262, ex.Number);
        AreEqual((byte)18, ex.State);
        AreEqual("vx", ex.Procedure);
        Contains("CREATE VIEW permission denied", ex.Message);
    }

    [TestMethod]
    public void CreateProcedure_NoPermission_Raises262State18()
    {
        var ex = CreateAsUserError(Seeded(), "create procedure dbo.px as select 1");
        AreEqual(262, ex.Number);
        AreEqual((byte)18, ex.State);
        AreEqual("px", ex.Procedure);
        Contains("CREATE PROCEDURE permission denied", ex.Message);
    }

    [TestMethod]
    public void CreateFunction_NoPermission_Raises262State18()
    {
        var ex = CreateAsUserError(Seeded(), "create function dbo.fx() returns int as begin return 1 end");
        AreEqual(262, ex.Number);
        AreEqual((byte)18, ex.State);
        AreEqual("fx", ex.Procedure);
        Contains("CREATE FUNCTION permission denied", ex.Message);
    }

    [TestMethod]
    public void CreateSequence_NoPermission_Raises15247()
        => Seeded().AssertSqlError("execute as user = 'u'; create sequence dbo.sx as int start with 1", 15247,
            "User does not have permission to perform this action.");

    [TestMethod]
    public void CreateRole_NoPermission_Raises15247()
        => Seeded().AssertSqlError("execute as user = 'u'; create role rx", 15247,
            "User does not have permission to perform this action.");

    [TestMethod]
    public void CreateUser_NoPermission_Raises15247()
        => Seeded().AssertSqlError("execute as user = 'u'; create user ux without login", 15247,
            "User does not have permission to perform this action.");

    [TestMethod]
    public void CreateSchema_NoPermission_Raises15247()
    {
        var ex = CreateAsUserError(Seeded(), "create schema sx");
        AreEqual(15247, ex.Number);
        AreEqual("User does not have permission to perform this action.", ex.Message);
    }

    [TestMethod]
    public void DbDdlAdmin_PassesCreateTableAndModuleGates()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("alter role db_ddladmin add member u");
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("execute as user = 'u'").ExecuteNonQuery();
        _ = conn.CreateCommand("create table dbo.tt (id int)").ExecuteNonQuery();
        _ = conn.CreateCommand("create view dbo.vv as select 1 as a").ExecuteNonQuery();
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.tt"));
    }

    [TestMethod]
    public void AlterTable_NoObjectAlter_Raises1088State13()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; alter table dbo.t1 add note nvarchar(100)", 1088);
        AreEqual((byte)13, ex.State);
        Contains("Cannot find the object \"t1\"", ex.Message);
    }

    [TestMethod]
    public void AlterTable_WithObjectAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t1 to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; alter table dbo.t1 add note nvarchar(100)");
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.t1')"));
    }

    [TestMethod]
    public void DropTable_NoSchemaAlter_Raises3701State20()
    {
        var ex = Seeded().AssertSqlError("execute as user = 'u'; drop table t1", 3701);
        AreEqual((byte)20, ex.State);
        AreEqual((byte)14, ex.Class);
        Contains("Cannot drop the table 't1'", ex.Message);
    }

    [TestMethod]
    public void DropTable_ObjectAlterInsufficient_StillDenied()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on object::dbo.t1 to u");
        _ = sim.AssertSqlError("execute as user = 'u'; drop table t1", 3701);
    }

    [TestMethod]
    public void DropTable_WithSchemaAlter_Succeeds()
    {
        var sim = Seeded();
        _ = sim.ExecuteNonQuery("grant alter on schema::dbo to u");
        _ = sim.ExecuteNonQuery("execute as user = 'u'; drop table dbo.t2");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't2'"));
    }

    [TestMethod]
    public void DropUser_Unauthorized_Raises15151()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create user victim without login");
        var ex = sim.AssertSqlError("execute as user = 'u'; drop user victim", 15151);
        Contains("Cannot drop the user 'victim'", ex.Message);
    }

    // ---- C. Scalar-UDF EXECUTE in non-query contexts ----

    private static Simulation SeededWithFunction()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create function dbo.f1() returns int as begin return 7 end");
        return sim;
    }

    [TestMethod]
    public void ScalarUdf_InSet_ExecuteDenied()
    {
        var ex = SeededWithFunction().AssertSqlError(
            "execute as user = 'u'; declare @x int; set @x = dbo.f1()", 229);
        Contains("EXECUTE permission was denied", ex.Message);
        Contains("'f1'", ex.Message);
    }

    [TestMethod]
    public void ScalarUdf_InIf_ExecuteDenied()
    {
        var ex = SeededWithFunction().AssertSqlError(
            "execute as user = 'u'; if dbo.f1() > 0 select 1", 229);
        Contains("EXECUTE permission was denied", ex.Message);
    }

    [TestMethod]
    public void ScalarUdf_InSet_WithExecuteGranted_Succeeds()
    {
        var sim = SeededWithFunction();
        _ = sim.ExecuteNonQuery("grant execute on object::dbo.f1 to u");
        AreEqual(7, sim.ExecuteScalar("execute as user = 'u'; declare @x int; set @x = dbo.f1(); select @x"));
    }

    // ---- D. Module WITH EXECUTE AS runtime ----

    [TestMethod]
    public void ScalarUdf_ExecuteAsOwner_RunsAsDbo()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create function dbo.who_owner() returns nvarchar(128) with execute as owner as begin return current_user end",
            "create function dbo.who_caller() returns nvarchar(128) as begin return current_user end");
        _ = sim.ExecuteNonQuery("grant execute on object::dbo.who_owner to u; grant execute on object::dbo.who_caller to u");
        AreEqual("dbo", sim.ExecuteScalar("execute as user = 'u'; select dbo.who_owner()"));
        AreEqual("u", sim.ExecuteScalar("execute as user = 'u'; select dbo.who_caller()"));
    }

    [TestMethod]
    public void Trigger_ExecuteAsUser_RunsBodyAsThatUser()
    {
        var sim = Seeded();
        sim.ExecuteBatches(
            "create table dbo.audit (who nvarchar(128))",
            "create user trguser without login",
            "create trigger dbo.tr on dbo.t2 with execute as 'trguser' after insert as insert dbo.audit(who) select current_user");
        _ = sim.ExecuteNonQuery("insert dbo.t2 values (9)");
        AreEqual("trguser", sim.ExecuteScalar("select who from dbo.audit"));
    }

    // ---- E. Delegated-grant covering ----

    [TestMethod]
    public void ControlWithGrantOption_DelegatesSelectOnSameObject()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create user grantee without login");
        _ = sim.ExecuteNonQuery("grant control on object::dbo.t1 to u with grant option");
        // u holds CONTROL WGO on t1 → may grant SELECT on t1 (covering).
        _ = sim.ExecuteNonQuery("execute as user = 'u'; grant select on object::dbo.t1 to grantee");
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.database_permissions where grantee_principal_id = user_id('grantee') and type = 'SL'"));
    }

    [TestMethod]
    public void WiderScopeWithGrantOption_DoesNotDelegateObjectGrant()
    {
        var sim = Seeded();
        sim.ExecuteBatches("create user grantee2 without login");
        _ = sim.ExecuteNonQuery("grant select on schema::dbo to u with grant option");
        // Schema-scope SELECT WGO does NOT authorize an object-scope grant.
        var ex = sim.AssertSqlError(
            "execute as user = 'u'; grant select on object::dbo.t1 to grantee2", 15151);
        Contains("Cannot find the object 't1'", ex.Message);
    }
}
