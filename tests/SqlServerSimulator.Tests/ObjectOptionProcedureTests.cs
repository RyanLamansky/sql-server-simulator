using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_changeobjectowner</c> and <c>sp_procoption</c>, probed 2026-10-15
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ObjectOptionProcedureTests
{
    /// <summary>A database <c>d</c> with users <c>u1</c> / <c>u2</c> owning schemas of their names, <c>s3</c> owned by <c>u1</c>, and a few objects.</summary>
    private static Simulation WithOwners()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "use d; create user u1 without login; create user u2 without login",
            "use d; exec('create schema u1 authorization u1'); exec('create schema u2 authorization u2'); exec('create schema s3 authorization u1')",
            "use d; create table dbo.t1 (id int primary key, c int constraint ck1 check (c > 0)); create table s3.t3 (id int); create table dbo.t4 (id int)",
            "use d; exec('create view dbo.vb with schemabinding as select id from dbo.t4'); exec('create trigger dbo.tr1 on dbo.t1 after insert as select 1')");
        return simulation;
    }

    [TestMethod]
    public void ChangeObjectOwner_MovesTheObjectAndItsChildren_WithRealsCaution()
    {
        var simulation = WithOwners();
        using var connection = simulation.CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Where(error => error.Number != 5701).Select(error => $"{error.Number}:{error.Procedure}:{error.LineNumber}"));
        _ = connection.CreateCommand("use d; exec sp_changeobjectowner 't1', 'U1'").ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "15477:sp_changeobjectowner:135" }, messages);
        AreEqual("u1.ck1,u1.t1,u1.tr1", simulation.ExecuteScalar("use d; select string_agg(concat(schema_name(schema_id), '.', name), ',') within group (order by name) from sys.objects where name in ('t1', 'ck1', 'tr1')"));
        AreEqual("dbo", simulation.ExecuteScalar("use d; declare @rc int; exec @rc = sp_changeobjectowner '[u1].[t1]', 'dbo'; select schema_name(schema_id) from sys.objects where name = 't1'"));
    }

    [TestMethod]
    public void ChangeObjectOwner_RollsBackWithTheTransaction()
        => AreEqual("u2,dbo", WithOwners().ExecuteScalar("""
            use d;
            begin tran;
            exec sp_changeobjectowner 'dbo.t1', 'u2';
            declare @inside sysname = (select schema_name(schema_id) from sys.objects where name = 't1');
            rollback;
            select concat(@inside, ',', (select schema_name(schema_id) from sys.objects where name = 't1'));
            """));

    [TestMethod]
    [DataRow("exec sp_changeobjectowner 'nosuch', 'u1'", 15001, 64, "Object 'nosuch' does not exist or is not a valid object for this operation.")]
    [DataRow("exec sp_changeobjectowner 'dbo.t4', 'u1'", 15001, 64, "Object 'dbo.t4' does not exist or is not a valid object for this operation.")]
    [DataRow("exec sp_changeobjectowner 'dbo.tr1', 'u1'", 15001, 64, "Object 'dbo.tr1' does not exist or is not a valid object for this operation.")]
    [DataRow("exec sp_changeobjectowner 'dbo.ck1', 'u1'", 15001, 64, "Object 'dbo.ck1' does not exist or is not a valid object for this operation.")]
    [DataRow("exec sp_changeobjectowner 'd.dbo.t1', 'u1'", 15001, 64, "Object 'd.dbo.t1' does not exist or is not a valid object for this operation.")]
    [DataRow("exec sp_changeobjectowner 's3.t3', 'u2'", 15001, 78, "Object 's3.t3' does not exist or is not a valid object for this operation.")]
    [DataRow("exec sp_changeobjectowner 'dbo.t1', 'nosuchowner'", 15411, 114, "Database principal or schema 'nosuchowner' does not exist in this database.")]
    [DataRow("exec sp_changeobjectowner 'dbo.t1', 's3'", 15411, 114, "Database principal or schema 's3' does not exist in this database.")]
    [DataRow("exec sp_changeobjectowner 'dbo.t1', null", 15411, 114, "Database principal or schema '(null)' does not exist in this database.")]
    [DataRow("exec sp_changeobjectowner 'dbo.t1', 'public'", 15411, 114, "Database principal or schema 'public' does not exist in this database.")]
    [DataRow("exec sp_changeobjectowner '', 'u1'", 15253, 32, "Syntax error parsing SQL identifier ''.")]
    [DataRow("exec sp_changeobjectowner null, 'u1'", 15253, 32, "Syntax error parsing SQL identifier '(null)'.")]
    [DataRow("execute as user = 'u1'; exec sp_changeobjectowner 'dbo.t1', 'u1'", 15247, 22, "User does not have permission to perform this action.")]
    public void ChangeObjectOwner_RefusesAsRealDoes(string sql, int number, int line, string message)
    {
        var error = WithOwners().AssertSqlError("use d; " + sql, number).Errors[0];
        AreEqual((message, "sp_changeobjectowner", line), (error.Message, error.Procedure, error.LineNumber));
    }

    [TestMethod]
    public void ChangeObjectOwner_ToSys_IsTheTransfersOwnRefusal()
    {
        var error = WithOwners().AssertSqlError("use d; exec sp_changeobjectowner 'dbo.t1', 'sys'", 2710).Errors[0];
        AreEqual((1, ""), (error.LineNumber, error.Procedure));
    }

    [TestMethod]
    public void ChangeObjectOwner_UnderImplicitTransactions_LeavesOneOpen()
        => AreEqual(1, WithOwners().ExecuteScalar("use d; set implicit_transactions on; exec sp_changeobjectowner 'dbo.t1', 'dbo'; select @@trancount"));

    /// <summary>A simulation with parameterless and parameterized procedures in <c>master</c>'s <c>dbo</c> schema, and one in a user database.</summary>
    private static Simulation WithProcedures()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create database d",
            "use master; exec('create procedure dbo.sp1 as select 1'); exec('create procedure dbo.sp2 @a int as select @a')",
            "use d; exec('create procedure dbo.p as select 1')");
        return simulation;
    }

    [TestMethod]
    public void ProcOption_Startup_SetsTheFlagEverySurfaceReads_AndAlterKeepsIt()
    {
        var simulation = WithProcedures();
        _ = simulation.ExecuteNonQuery("use master; exec sp_procoption 'sp1', 'Startup ', 'TRUE'");
        AreEqual("1|1|1|0", simulation.ExecuteScalar("""
            use master;
            select concat(objectproperty(object_id('sp1'), 'ExecIsStartup'), '|', is_auto_executed, '|',
                (select cast(value as int) from sys.configurations where configuration_id = 1547), '|',
                (select cast(value_in_use as int) from sys.configurations where configuration_id = 1547))
            from sys.procedures where name = 'sp1'
            """));
        AreEqual(1, simulation.ExecuteBatchesScalar("use master; exec('alter procedure dbo.sp1 @x int = 1 as select 2')", "use master; select objectproperty(object_id('sp1'), 'ExecIsStartup')"));
        simulation.ExecuteBatches("use master; exec('create or alter procedure dbo.sp1 as select 3')", "use master; exec sp_procoption 'sp1', 'startup', 'off'");
        AreEqual("0|0|0", simulation.ExecuteScalar("""
            use master;
            select concat(objectproperty(object_id('sp1'), 'ExecIsStartup'), '|', is_auto_executed, '|',
                (select cast(value as int) from sys.configurations where configuration_id = 1547))
            from sys.procedures where name = 'sp1'
            """));
    }

    [TestMethod]
    public void ProcOption_StartupFlag_SurvivesBackupAndRestore()
    {
        var simulation = WithProcedures();
        _ = simulation.ExecuteNonQuery("""
            use master;
            exec sp_procoption 'sp1', 'startup', 'on';
            backup database master to disk = N'm.bak';
            restore database m2 from disk = N'm.bak' with move N'master' to N'/m2.mdf', move N'mastlog' to N'/m2.ldf';
            """);
        AreEqual(1, simulation.ExecuteScalar("use m2; select objectproperty(object_id('dbo.sp1'), 'ExecIsStartup')"));
    }

    [TestMethod]
    [DataRow("use d; exec @rc = sp_procoption 'dbo.p', 'startup', 'on'", 15398, 73, "Only objects in the master database owned by dbo can have the startup setting changed.")]
    [DataRow("use master; exec @rc = sp_procoption 'sp2', 'startup', 'on'", 15399, 90, "Could not change startup option because this option is restricted to objects that have no parameters.")]
    [DataRow("use master; exec @rc = sp_procoption 'sp1', 'failover', 'yes'", 40510, 114, "Statement 'sp_procoption' is not supported in this version of SQL Server.")]
    [DataRow("use master; exec @rc = sp_procoption 'nosuch', 'startup', 'on'", 15165, 40, "Could not find object 'nosuch' or you do not have permission.")]
    [DataRow("use master; exec @rc = sp_procoption 'sp_who', 'startup', 'on'", 15165, 40, "Could not find object 'sp_who' or you do not have permission.")]
    [DataRow("use master; exec @rc = sp_procoption 'sp1', 'bogus', 'on'", 15600, 30, "An invalid parameter or option was specified for procedure 'sys.sp_procoption'.")]
    [DataRow("use master; exec @rc = sp_procoption 'sp1', 'startup', 'maybe'", 15600, 30, "An invalid parameter or option was specified for procedure 'sys.sp_procoption'.")]
    [DataRow("use master; exec @rc = sp_procoption 'sp1', null, 'on'", 15600, 30, "An invalid parameter or option was specified for procedure 'sys.sp_procoption'.")]
    [DataRow("use master; begin tran; exec @rc = sp_procoption 'sp1', 'startup', 'on'", 15002, 18, "The procedure 'sys.sp_procoption' cannot be executed within a transaction.")]
    public void ProcOption_RefusesAsRealDoes(string sql, int number, int line, string message)
    {
        var error = WithProcedures().AssertSqlError("declare @rc int; " + sql, number).Errors[0];
        AreEqual((message, "sp_procoption", line), (error.Message, error.Procedure, error.LineNumber));
    }

    [TestMethod]
    public void ProcOption_ForALoginOutsideSysadmin_IsMsg15165()
    {
        var simulation = WithProcedures();
        _ = simulation.ExecuteNonQuery("create login plain with password = 'Zz!Passw0rd1', check_policy = off");
        AreEqual(40, simulation.AssertSqlError("use master; execute as login = 'plain'; exec sp_procoption 'sp1', 'startup', 'off'", 15165).Errors[0].LineNumber);
    }
}
