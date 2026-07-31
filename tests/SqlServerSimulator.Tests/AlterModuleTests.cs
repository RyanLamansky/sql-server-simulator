using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>ALTER VIEW</c> / <c>ALTER FUNCTION</c> and their
/// <c>CREATE OR ALTER</c> upsert forms: replacement semantics, what the
/// replacement preserves (object_id, create_date, granted permissions,
/// attached triggers) versus resets (modify_date, indexes), and the
/// probe-confirmed error paths — <strong>Msg 208</strong> for a bare ALTER on
/// a name nothing holds and <strong>Msg 2010</strong> for a name held by an
/// incompatible object kind, which includes an ALTER FUNCTION that would
/// change the function's own kind. Probed against SQL Server 2025
/// (2026-07-31).
/// </summary>
[TestClass]
public sealed class AlterModuleTests
{
    // --- ALTER VIEW ---

    [TestMethod]
    public void AlterView_ReplacesBody()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create view dbo.v as select 1 as c",
            "alter view dbo.v as select 2 as c");
        AreEqual(2, sim.ExecuteScalar("select c from dbo.v"));
    }

    [TestMethod]
    public void AlterView_ReplacesProjectedColumns()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int, b int); insert dbo.t values (1, 2)",
            "create view dbo.v as select a from dbo.t",
            "alter view dbo.v as select a, b from dbo.t");
        AreEqual(2, sim.ExecuteScalar("select b from dbo.v"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.v')"));
    }

    [TestMethod]
    public void AlterView_PreservesObjectIdAndCreateDate_AdvancesModifyDate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create view dbo.v as select 1 as c");
        var objectId = sim.ExecuteScalar("select object_id('dbo.v')");
        sim.ExecuteBatches("alter view dbo.v as select 2 as c");
        AreEqual(objectId, sim.ExecuteScalar("select object_id('dbo.v')"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.objects where name = 'v' and modify_date >= create_date"));
    }

    /// <summary>
    /// Object-scope permissions key off object_id, which the replacement
    /// keeps — so a GRANT survives the ALTER (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void AlterView_PreservesGrantedPermissions()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user reader without login",
            "create view dbo.v as select 1 as c",
            "grant select on dbo.v to reader",
            "alter view dbo.v as select 2 as c");
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.database_permissions
            where major_id = object_id('dbo.v') and permission_name = 'SELECT' and state_desc = 'GRANT'
            """));
    }

    /// <summary>
    /// A view's INSTEAD OF triggers stay attached across the ALTER — real
    /// keeps them, and the simulator's trigger-to-parent match is by
    /// reference, so the swap carries them over explicitly.
    /// </summary>
    [TestMethod]
    public void AlterView_KeepsInsteadOfTrigger()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int)",
            "create view dbo.v as select a from dbo.t",
            "create trigger dbo.tr on dbo.v instead of insert as insert dbo.t values (99)",
            "alter view dbo.v as select a from dbo.t where a > 0");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.triggers where parent_id = object_id('dbo.v')"));
        _ = sim.ExecuteNonQuery("insert dbo.v (a) values (1)");
        AreEqual(99, sim.ExecuteScalar("select a from dbo.t"));
    }

    /// <summary>
    /// ALTER VIEW drops the view's indexes along with the schema-binding that
    /// allowed them (probe-confirmed: <c>sys.indexes</c> comes back empty and
    /// <c>is_schema_bound</c> reads 0). The base table stops re-validating the
    /// dropped unique index, so a duplicate key now inserts cleanly.
    /// </summary>
    [TestMethod]
    public void AlterView_DropsIndexesAndSchemaBinding()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.b (id int not null, val int not null); insert dbo.b values (1, 100)",
            "create view dbo.v with schemabinding as select id, val from dbo.b",
            "create unique clustered index ix_v on dbo.v(id)");
        _ = sim.AssertSqlError("insert dbo.b values (1, 200)", 2601);
        sim.ExecuteBatches("alter view dbo.v as select id, val from dbo.b");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('dbo.v')"));
        IsFalse(sim.ExecuteScalar<bool>("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.v')"));
        _ = sim.ExecuteNonQuery("insert dbo.b values (1, 200)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from dbo.v where id = 1"));
    }

    [TestMethod]
    public void AlterView_Missing_RaisesMsg208()
        => new Simulation().AssertSqlError(
            "alter view dbo.nope as select 1 as c", 208, "Invalid object name 'dbo.nope'.");

    [TestMethod]
    public void AlterView_MissingSchema_RaisesMsg208()
        => new Simulation().AssertSqlError(
            "alter view nosuch.v as select 1 as c", 208, "Invalid object name 'nosuch.v'.");

    [TestMethod]
    public void AlterView_OverTable_RaisesMsg2010()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (a int)");
        sim.AssertSqlError(
            "alter view dbo.t as select 1 as c",
            2010,
            "Cannot perform alter on 'dbo.t' because it is an incompatible object type.");
    }

    /// <summary>Msg 2010 echoes the name as written — an unqualified reference stays unqualified.</summary>
    [TestMethod]
    public void AlterView_OverTable_Msg2010_EchoesWrittenName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (a int)");
        sim.AssertSqlError(
            "alter view t as select 1 as c",
            2010,
            "Cannot perform alter on 't' because it is an incompatible object type.");
    }

    /// <summary>OBJECT_DEFINITION stores the ALTER's text under a normalized CREATE verb.</summary>
    [TestMethod]
    public void AlterView_DefinitionNormalizesVerbToCreate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create view dbo.v as select 1 as c",
            "alter view dbo.v as select 2 as c");
        AreEqual("CREATE view dbo.v as select 2 as c", sim.ExecuteScalar("select object_definition(object_id('dbo.v'))"));
    }

    // --- CREATE OR ALTER VIEW ---

    [TestMethod]
    public void CreateOrAlterView_CreatesThenReplaces()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create or alter view dbo.v as select 1 as c");
        AreEqual(1, sim.ExecuteScalar("select c from dbo.v"));
        var objectId = sim.ExecuteScalar("select object_id('dbo.v')");
        sim.ExecuteBatches("create or alter view dbo.v as select 2 as c");
        AreEqual(2, sim.ExecuteScalar("select c from dbo.v"));
        AreEqual(objectId, sim.ExecuteScalar("select object_id('dbo.v')"));
    }

    [TestMethod]
    public void CreateOrAlterView_OverTable_RaisesMsg2010()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (a int)");
        _ = sim.AssertSqlError("create or alter view dbo.t as select 1 as c", 2010);
    }

    /// <summary>The stored definition drops the OR / ALTER tokens but keeps their whitespace.</summary>
    [TestMethod]
    public void CreateOrAlterView_DefinitionCollapsesVerb()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create or alter view dbo.v as select 1 as c");
        AreEqual("create   view dbo.v as select 1 as c", sim.ExecuteScalar("select object_definition(object_id('dbo.v'))"));
    }

    // --- ALTER FUNCTION ---

    [TestMethod]
    public void AlterFunction_Scalar_ReplacesBody()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.f() returns int as begin return 1 end",
            "alter function dbo.f() returns int as begin return 2 end");
        AreEqual(2, sim.ExecuteScalar("select dbo.f()"));
    }

    [TestMethod]
    public void AlterFunction_Scalar_ReplacesSignatureAndReturnType()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.f(@x int) returns int as begin return @x end",
            "alter function dbo.f(@x int) returns varchar(20) as begin return 'v' + cast(@x as varchar(10)) end");
        AreEqual("v7", sim.ExecuteScalar("select dbo.f(7)"));
    }

    [TestMethod]
    public void AlterFunction_InlineTvf_ReplacesBody()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.f() returns table as return select 1 as c",
            "alter function dbo.f() returns table as return select 2 as c");
        AreEqual(2, sim.ExecuteScalar("select c from dbo.f()"));
    }

    [TestMethod]
    public void AlterFunction_MultiStatementTvf_ReplacesBody()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.f() returns @r table (c int) as begin insert @r values (1); return end",
            "alter function dbo.f() returns @r table (c int) as begin insert @r values (2); return end");
        AreEqual(2, sim.ExecuteScalar("select c from dbo.f()"));
    }

    [TestMethod]
    public void AlterFunction_PreservesObjectIdAndPermissions()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create user caller without login",
            "create function dbo.f() returns int as begin return 1 end",
            "grant execute on dbo.f to caller");
        var objectId = sim.ExecuteScalar("select object_id('dbo.f')");
        sim.ExecuteBatches("alter function dbo.f() returns int as begin return 2 end");
        AreEqual(objectId, sim.ExecuteScalar("select object_id('dbo.f')"));
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.database_permissions
            where major_id = object_id('dbo.f') and permission_name = 'EXECUTE' and state_desc = 'GRANT'
            """));
    }

    [TestMethod]
    public void AlterFunction_Missing_RaisesMsg208()
        => new Simulation().AssertSqlError(
            "alter function dbo.nope() returns int as begin return 1 end", 208, "Invalid object name 'dbo.nope'.");

    [TestMethod]
    public void AlterFunction_MissingSchema_RaisesMsg208()
        => new Simulation().AssertSqlError(
            "alter function nosuch.f() returns int as begin return 1 end", 208, "Invalid object name 'nosuch.f'.");

    [TestMethod]
    public void AlterFunction_OverProcedure_RaisesMsg2010()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as select 1");
        sim.AssertSqlError(
            "alter function dbo.p() returns int as begin return 1 end",
            2010,
            "Cannot perform alter on 'dbo.p' because it is an incompatible object type.");
    }

    /// <summary>
    /// A function's kind is fixed at creation: an ALTER body that writes a
    /// different one is the same Msg 2010 an unrelated object kind gets, and
    /// the stored function is left untouched.
    /// </summary>
    [TestMethod]
    [DataRow("create function dbo.f() returns int as begin return 1 end",
        "alter function dbo.f() returns table as return select 1 as c", "SQL_SCALAR_FUNCTION")]
    [DataRow("create function dbo.f() returns table as return select 1 as c",
        "alter function dbo.f() returns int as begin return 1 end", "SQL_INLINE_TABLE_VALUED_FUNCTION")]
    [DataRow("create function dbo.f() returns table as return select 1 as c",
        "alter function dbo.f() returns @r table (c int) as begin insert @r values (1); return end",
        "SQL_INLINE_TABLE_VALUED_FUNCTION")]
    [DataRow("create function dbo.f() returns @r table (c int) as begin insert @r values (1); return end",
        "alter function dbo.f() returns table as return select 1 as c", "SQL_TABLE_VALUED_FUNCTION")]
    public void AlterFunction_KindChange_RaisesMsg2010(string create, string alter, string unchangedTypeDescription)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(create);
        _ = sim.AssertSqlError(alter, 2010);
        AreEqual(unchangedTypeDescription, sim.ExecuteScalar("select type_desc from sys.objects where name = 'f'"));
    }

    // --- CREATE OR ALTER FUNCTION ---

    [TestMethod]
    public void CreateOrAlterFunction_CreatesThenReplaces()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create or alter function dbo.f() returns int as begin return 1 end");
        AreEqual(1, sim.ExecuteScalar("select dbo.f()"));
        var objectId = sim.ExecuteScalar("select object_id('dbo.f')");
        sim.ExecuteBatches("create or alter function dbo.f() returns int as begin return 2 end");
        AreEqual(2, sim.ExecuteScalar("select dbo.f()"));
        AreEqual(objectId, sim.ExecuteScalar("select object_id('dbo.f')"));
    }

    [TestMethod]
    public void CreateOrAlterFunction_InlineTvf_CreatesThenReplaces()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create or alter function dbo.f() returns table as return select 1 as c",
            "create or alter function dbo.f() returns table as return select 2 as c");
        AreEqual(2, sim.ExecuteScalar("select c from dbo.f()"));
    }

    [TestMethod]
    public void CreateOrAlterFunction_OverTable_RaisesMsg2010()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (a int)");
        _ = sim.AssertSqlError("create or alter function dbo.t() returns int as begin return 1 end", 2010);
    }

    // --- ALTER PROCEDURE's share of the same rules ---

    [TestMethod]
    public void AlterProcedure_OverTable_RaisesMsg2010()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (a int)");
        sim.AssertSqlError(
            "alter procedure dbo.t as select 1",
            2010,
            "Cannot perform alter on 'dbo.t' because it is an incompatible object type.");
    }
}
