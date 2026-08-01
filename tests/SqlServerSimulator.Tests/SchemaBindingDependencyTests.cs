using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>WITH SCHEMABINDING</c> dependency gate: Msg 3729 on a DROP / ALTER
/// of what a schema-bound module references, Msg 5074 on the column forms,
/// Msg 15336 on <c>sp_rename</c>, Msg 15348 on <c>ALTER SCHEMA TRANSFER</c>,
/// and the Msg 4512 / 4513 rules a schema-bound body's own references obey.
/// Every message here was probe-confirmed verbatim against SQL Server 2025
/// (2026-08-01).
/// </summary>
[TestClass]
public sealed class SchemaBindingDependencyTests
{
    // --- Msg 3729: DROP of a referenced object ---

    [TestMethod]
    public void DropTable_ReferencedBySchemaBoundView_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v with schemabinding as select a, b from dbo.t");
        sim.AssertSqlError("drop table dbo.t", 3729,
            "Cannot DROP TABLE 'dbo.t' because it is being referenced by object 'v'.");
    }

    /// <summary>A schema-bound scalar function's table reference blocks the same way a view's does.</summary>
    [TestMethod]
    public void DropTable_ReferencedBySchemaBoundFunction_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create function dbo.f() returns int with schemabinding as begin return (select count(*) from dbo.t) end");
        sim.AssertSqlError("drop table dbo.t", 3729,
            "Cannot DROP TABLE 'dbo.t' because it is being referenced by object 'f'.");
    }

    [TestMethod]
    public void DropView_ReferencedBySchemaBoundView_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v1 with schemabinding as select a from dbo.t",
            "create view dbo.v2 with schemabinding as select a from dbo.v1");
        sim.AssertSqlError("drop view dbo.v1", 3729,
            "Cannot DROP VIEW 'dbo.v1' because it is being referenced by object 'v2'.");
    }

    [TestMethod]
    public void DropFunction_ReferencedBySchemaBoundFunction_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.leaf(@x int) returns int with schemabinding as begin return @x + 1 end",
            "create function dbo.caller(@x int) returns int with schemabinding as begin return dbo.leaf(@x) end");
        sim.AssertSqlError("drop function dbo.leaf", 3729,
            "Cannot DROP FUNCTION 'dbo.leaf' because it is being referenced by object 'caller'.");
    }

    /// <summary>A TVF reached from a FROM clause is a dependency like any other.</summary>
    [TestMethod]
    public void DropInlineTvf_ReferencedBySchemaBoundView_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create function dbo.tvf() returns table with schemabinding as return (select a from dbo.t)",
            "create view dbo.v with schemabinding as select a from dbo.tvf()");
        sim.AssertSqlError("drop function dbo.tvf", 3729,
            "Cannot DROP FUNCTION 'dbo.tvf' because it is being referenced by object 'v'.");
    }

    /// <summary>
    /// The message echoes the target <b>as the statement spelled it</b>, so an
    /// unqualified DROP reports the bare leaf while the two-part form reports
    /// both segments (probe-confirmed against SQL Server 2025 for DROP TABLE
    /// and for the ALTER leg below).
    /// </summary>
    [TestMethod]
    public void DropTable_Unqualified_EchoesTheNameAsWritten()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as select a from dbo.t");
        sim.AssertSqlError("drop table t", 3729,
            "Cannot DROP TABLE 't' because it is being referenced by object 'v'.");
    }

    [TestMethod]
    public void AlterFunction_Unqualified_EchoesTheNameAsWritten()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.leaf(@x int) returns int with schemabinding as begin return @x + 1 end",
            "create function dbo.caller(@x int) returns int with schemabinding as begin return dbo.leaf(@x) end");
        sim.AssertSqlError("alter function leaf(@x int) returns int with schemabinding as begin return @x + 2 end", 3729,
            "Cannot ALTER 'leaf' because it is being referenced by object 'caller'.");
    }

    /// <summary>A cross-schema reference resolves through the written qualifier.</summary>
    [TestMethod]
    public void DropTable_ReferencedAcrossSchemas_EchoesTheWrittenQualifier()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create schema s",
            "create table s.t (a int not null)",
            "create view dbo.v with schemabinding as select a from s.t");
        sim.AssertSqlError("drop table s.t", 3729,
            "Cannot DROP TABLE 's.t' because it is being referenced by object 'v'.");
    }

    /// <summary>
    /// The FK gate runs first: a table that is both an FK parent and a
    /// schema-bound view's base reports Msg 3726, not 3729.
    /// </summary>
    [TestMethod]
    public void DropTable_ForeignKeyParentAndSchemaBound_ReportsForeignKeyFirst()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null primary key)",
            "create table dbo.c (a int not null references dbo.t(a))",
            "create view dbo.v with schemabinding as select a from dbo.t");
        _ = sim.AssertSqlError("drop table dbo.t", 3726);
    }

    /// <summary>Real names one blocker even when several qualify, and picks the oldest.</summary>
    [TestMethod]
    public void DropTable_TwoDependents_NamesTheOldest()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.first_v with schemabinding as select a from dbo.t",
            "create view dbo.second_v with schemabinding as select a from dbo.t");
        sim.AssertSqlError("drop table dbo.t", 3729,
            "Cannot DROP TABLE 'dbo.t' because it is being referenced by object 'first_v'.");
    }

    /// <summary>The gate lifts with the last dependent — the record is the body, not a registration.</summary>
    [TestMethod]
    public void DropTable_AfterDroppingTheDependentView_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as select a from dbo.t",
            "drop view dbo.v",
            "drop table dbo.t");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    /// <summary>Dropping the binding (ALTER VIEW without SCHEMABINDING) releases the base table.</summary>
    [TestMethod]
    public void DropTable_AfterAlteringTheViewToNonSchemaBound_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as select a from dbo.t",
            "alter view dbo.v as select a from dbo.t",
            "drop table dbo.t");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    // --- Msg 3729: ALTER of a referenced module ---

    /// <summary>
    /// The ALTER form drops the object kind from the message, carries state 3
    /// rather than 1, and attributes the error to the module being altered.
    /// </summary>
    [TestMethod]
    public void AlterView_ReferencedBySchemaBoundView_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v1 with schemabinding as select a from dbo.t",
            "create view dbo.v2 with schemabinding as select a from dbo.v1");
        var ex = sim.AssertSqlError("alter view dbo.v1 with schemabinding as select a from dbo.t where a > 0", 3729);
        AreEqual("Cannot ALTER 'dbo.v1' because it is being referenced by object 'v2'.", ex.Message);
        AreEqual(3, ex.State);
        AreEqual("v1", ex.Procedure);
    }

    [TestMethod]
    public void CreateOrAlterFunction_ReferencedBySchemaBoundFunction_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.leaf(@x int) returns int with schemabinding as begin return @x + 1 end",
            "create function dbo.caller(@x int) returns int with schemabinding as begin return dbo.leaf(@x) end");
        sim.AssertSqlError(
            "create or alter function dbo.leaf(@x int) returns int with schemabinding as begin return @x + 2 end", 3729,
            "Cannot ALTER 'dbo.leaf' because it is being referenced by object 'caller'.");
    }

    // --- Late binding: a non-schema-bound dependent blocks nothing ---

    [TestMethod]
    public void DropTable_ReferencedByPlainView_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v as select a from dbo.t",
            "drop table dbo.t");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 't'"));
    }

    [TestMethod]
    public void AlterView_ReferencedByPlainView_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v1 as select a, b from dbo.t",
            "create view dbo.v2 as select a from dbo.v1",
            "alter view dbo.v1 as select a from dbo.t");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.views where name = 'v1'"));
    }

    [TestMethod]
    public void DropColumn_ReferencedByPlainView_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v as select a, b from dbo.t",
            "alter table dbo.t drop column b");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.t')"));
    }

    // --- Msg 5074: the column forms ---

    [TestMethod]
    public void DropColumn_ReferencedBySchemaBoundView_Raises5074()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v with schemabinding as select a, b from dbo.t");
        var ex = sim.AssertSqlError("alter table dbo.t drop column b", 5074);
        Contains("The object 'v' is dependent on column 'b'.", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_ReferencedBySchemaBoundView_Raises5074()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v with schemabinding as select a, b from dbo.t");
        var ex = sim.AssertSqlError("alter table dbo.t alter column a bigint not null", 5074);
        Contains("The object 'v' is dependent on column 'a'.", ex.Message);
    }

    /// <summary>
    /// A widening an index waves past (varchar(50) → varchar(100), same
    /// SqlType family) still fails under a schema-bound view: the module
    /// blocker is unconditional.
    /// </summary>
    [TestMethod]
    public void AlterColumn_WideningUnderSchemaBoundView_StillRaises5074()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, s varchar(50) null)",
            "create view dbo.v with schemabinding as select a, s from dbo.t");
        var ex = sim.AssertSqlError("alter table dbo.t alter column s varchar(100) null", 5074);
        Contains("The object 'v' is dependent on column 's'.", ex.Message);
    }

    /// <summary>Column granularity: a column the body never names is free to go.</summary>
    [TestMethod]
    public void DropColumn_NotNamedByTheSchemaBoundView_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null, c int null)",
            "create view dbo.v with schemabinding as select a, b from dbo.t",
            "alter table dbo.t drop column c");
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.t')"));
    }

    /// <summary>
    /// <c>SELECT COUNT(*) FROM dbo.t</c> names no column, so every column of
    /// the referenced table stays alterable even though the table itself is
    /// pinned (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void AlterColumn_NotNamedByTheSchemaBoundFunction_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, s varchar(50) null)",
            "create function dbo.f() returns int with schemabinding as begin return (select count(*) from dbo.t) end",
            "alter table dbo.t alter column s varchar(200) null");
        AreEqual((short)200, sim.ExecuteScalar("select max_length from sys.columns where object_id = object_id('dbo.t') and name = 's'"));
    }

    /// <summary>Every dependent gets its own Msg 5074 line, oldest first.</summary>
    [TestMethod]
    public void DropColumn_TwoDependents_ListsBoth()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.first_v with schemabinding as select a from dbo.t",
            "create view dbo.second_v with schemabinding as select a from dbo.t");
        var ex = sim.AssertSqlError("alter table dbo.t drop column a", 5074);
        AreEqual("""
            The object 'first_v' is dependent on column 'a'.
            The object 'second_v' is dependent on column 'a'.
            ALTER TABLE DROP COLUMN a failed because one or more objects access this column.
            """.ReplaceLineEndings("\r\n"), ex.Message);
    }

    /// <summary>A schema-bound view precedes an index on the same column in the blocker list.</summary>
    [TestMethod]
    public void DropColumn_ViewBlockerPrecedesIndexBlocker()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, s varchar(50) null)",
            "create index ix on dbo.t (s)",
            "create view dbo.v with schemabinding as select a, s from dbo.t");
        var ex = sim.AssertSqlError("alter table dbo.t drop column s", 5074);
        IsLessThan(
            ex.Message.IndexOf("The index 'ix'", StringComparison.Ordinal),
            ex.Message.IndexOf("The object 'v'", StringComparison.Ordinal));
    }

    // --- sp_rename (Msg 15336) and ALTER SCHEMA TRANSFER (Msg 15348) ---

    [TestMethod]
    public void RenameTable_ReferencedBySchemaBoundView_Raises15336()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as select a from dbo.t");
        sim.AssertSqlError("exec sp_rename 'dbo.t', 't2'", 15336,
            "Object 'dbo.t' cannot be renamed because the object participates in enforced dependencies.");
    }

    [TestMethod]
    public void RenameColumn_ReferencedBySchemaBoundView_Raises15336()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v with schemabinding as select a from dbo.t");
        sim.AssertSqlError("exec sp_rename 'dbo.t.a', 'aa', 'COLUMN'", 15336,
            "Object 'dbo.t.a' cannot be renamed because the object participates in enforced dependencies.");
    }

    [TestMethod]
    public void RenameColumn_NotNamedBySchemaBoundView_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v with schemabinding as select a from dbo.t",
            "exec sp_rename 'dbo.t.b', 'bb', 'COLUMN'");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.t') and name = 'bb'"));
    }

    [TestMethod]
    public void TransferSchema_OfReferencedTable_Raises15348()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as select a from dbo.t",
            "create schema s");
        sim.AssertSqlError("alter schema s transfer dbo.t", 15348, "Cannot transfer a schemabound object.");
    }

    /// <summary>Only the referenced side is pinned — the schema-bound module itself moves freely.</summary>
    [TestMethod]
    public void TransferSchema_OfTheSchemaBoundViewItself_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as select a from dbo.t",
            "create schema s",
            "alter schema s transfer dbo.v");
        AreEqual("s", sim.ExecuteScalar("select schema_name(schema_id) from sys.views where name = 'v'"));
    }

    // --- Msg 4513 / 4512: what a schema-bound body may reference ---

    [TestMethod]
    public void SchemaBoundView_OverNonSchemaBoundView_Raises4513()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.plain as select a from dbo.t");
        var ex = sim.AssertSqlError("create view dbo.v with schemabinding as select a from dbo.plain", 4513);
        AreEqual("Cannot schema bind view 'dbo.v'. 'dbo.plain' is not schema bound.", ex.Message);
        AreEqual(2, ex.State);
    }

    [TestMethod]
    public void SchemaBoundFunction_CallingNonSchemaBoundFunction_Raises4513()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create function dbo.plain(@x int) returns int as begin return @x + 1 end");
        sim.AssertSqlError(
            "create function dbo.f(@x int) returns int with schemabinding as begin return dbo.plain(@x) end", 4513,
            "Cannot schema bind function 'dbo.f'. 'dbo.plain' is not schema bound.");
    }

    [TestMethod]
    public void SchemaBoundView_OverOnePartName_Raises4512()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int not null)");
        var ex = sim.AssertSqlError("create view dbo.v with schemabinding as select a from t", 4512);
        AreEqual(
            "Cannot schema bind view 'dbo.v' because name 't' is invalid for schema binding. Names must be in two-part format and an object cannot reference itself.",
            ex.Message);
        AreEqual(3, ex.State);
    }

    [TestMethod]
    public void SchemaBoundFunction_OverThreePartName_Raises4512()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int not null)");
        sim.AssertSqlError(
            "create function dbo.f() returns int with schemabinding as begin return (select count(*) from simulated.dbo.t) end",
            4512,
            "Cannot schema bind function 'dbo.f' because name 'simulated.dbo.t' is invalid for schema binding. Names must be in two-part format and an object cannot reference itself.");
    }

    /// <summary>
    /// A derived table in FROM position carries no name to shape-check, and
    /// real accepts it inside a schema-bound body (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void SchemaBoundView_OverDerivedTable_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as select a from (select a from dbo.t) d");
        IsTrue(sim.ExecuteScalar<bool>("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.v')"));
    }

    /// <summary>
    /// A CTE reference is one-part by grammar, so the Msg 4512 shape check has
    /// to know the body's own <c>WITH</c> prefix declared it — even when the
    /// default schema holds a table of that very name, which real reads as the
    /// CTE (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void SchemaBoundView_OverCte_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "create view dbo.v with schemabinding as with c as (select a from dbo.t) select a from c",
            "create view dbo.shadow with schemabinding as with t as (select 1 as a) select a from t");
        IsTrue(sim.ExecuteScalar<bool>("select is_schema_bound from sys.sql_modules where object_id = object_id('dbo.v')"));
        AreEqual(1, sim.ExecuteScalar("select a from dbo.shadow"));
    }

    /// <summary>
    /// The prefix doesn't exempt the body's real references: a one-part table
    /// name inside a CTE definition still trips Msg 4512.
    /// </summary>
    [TestMethod]
    public void SchemaBoundView_CteBodyOverOnePartName_Raises4512()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table dbo.t (a int not null)");
        sim.AssertSqlError(
            "create view dbo.v with schemabinding as with c as (select a from t) select a from c", 4512,
            "Cannot schema bind view 'dbo.v' because name 't' is invalid for schema binding. Names must be in two-part format and an object cannot reference itself.");
    }

    /// <summary>
    /// A table reached only from inside a CTE definition is still a dependency
    /// — the gate runs on both directions of the same reference set.
    /// </summary>
    [TestMethod]
    public void DropTable_ReferencedInsideCteDefinition_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int null)",
            "create view dbo.v with schemabinding as with c as (select a from dbo.t) select a from c");
        sim.AssertSqlError("drop table dbo.t", 3729,
            "Cannot DROP TABLE 'dbo.t' because it is being referenced by object 'v'.");
        _ = sim.AssertSqlError("alter table dbo.t drop column a", 5074);
        _ = sim.ExecuteNonQuery("alter table dbo.t drop column b");
    }

    /// <summary>
    /// A built-in TVF in FROM position is a one-part name that isn't an
    /// object reference — real accepts it inside a schema-bound body
    /// (probe-confirmed), so the Msg 4512 shape check has to let it through.
    /// </summary>
    [TestMethod]
    public void SchemaBoundFunction_OverBuiltInTableValuedFunction_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create function dbo.f() returns int with schemabinding as begin return (select count(*) from string_split('a,b', ',')) end");
        AreEqual(2, sim.ExecuteScalar("select dbo.f()"));
    }

    // --- Composition with the indexed-view machinery ---

    /// <summary>
    /// An indexed view is schema bound by requirement, so its base table
    /// picks up the same gate — separately from the
    /// <c>DependentIndexedViews</c> wiring that re-validates uniqueness.
    /// </summary>
    [TestMethod]
    public void DropTable_UnderAnIndexedView_Raises3729()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null, b int not null)",
            "create view dbo.v with schemabinding as select a, b from dbo.t",
            "create unique clustered index ixv on dbo.v (a)");
        sim.AssertSqlError("drop table dbo.t", 3729,
            "Cannot DROP TABLE 'dbo.t' because it is being referenced by object 'v'.");
    }

    /// <summary>TRUNCATE isn't gated — only the base table's shape is pinned, not its rows.</summary>
    [TestMethod]
    public void TruncateTable_UnderSchemaBoundView_Succeeds()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int not null)",
            "insert dbo.t values (1)",
            "create view dbo.v with schemabinding as select a from dbo.t",
            "truncate table dbo.t");
        AreEqual(0, sim.ExecuteScalar("select count(*) from dbo.v"));
    }
}
