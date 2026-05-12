using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>DECLARE @t TABLE (...)</c> and DML against table variables.
/// Behavior probed against SQL Server 2025 (2026-05-12). Covers v1 scope:
/// columns + DEFAULTs + NOT NULL + inline anonymous PRIMARY KEY + table-level
/// anonymous PRIMARY KEY, plus the routing / scope / non-transactional /
/// rejection-paths around it.
/// </summary>
[TestClass]
public sealed class TableVariableTests
{
    public TestContext TestContext { get; set; } = null!;

    // ---- basic DECLARE + DML ----

    [TestMethod]
    public void Basic_Insert_Select_RoundTrips()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int); insert @t values (1), (2); select * from @t order by id");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Insert_INTO_Optional()
        => AreEqual(5, new Simulation().ExecuteScalar(
            "declare @t table (id int); insert into @t values (5); select id from @t"));

    [TestMethod]
    public void Insert_From_Select()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "declare @t table (id int); insert @t select 1 union select 2; select count(*) from @t"));

    [TestMethod]
    public void Update_TableVariable()
        => AreEqual(99, new Simulation().ExecuteScalar(
            "declare @t table (id int, v int); insert @t values (1, 10); update @t set v = 99; select v from @t"));

    [TestMethod]
    public void Delete_From_TableVariable()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "declare @t table (id int); insert @t values (1), (2); delete from @t where id = 2; select count(*) from @t"));

    [TestMethod]
    public void Delete_TableVariable_NoFrom()
        => AreEqual(0, new Simulation().ExecuteScalar(
            "declare @t table (id int); insert @t values (1); delete @t; select count(*) from @t"));

    [TestMethod]
    public void MultiColumn_Schema()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int, name varchar(20)); insert @t values (1, 'a'), (2, 'b'); select * from @t order by id");
        IsTrue(reader.Read());
        AreEqual("a", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual("b", reader.GetString(1));
    }

    // ---- constraints ----

    [TestMethod]
    public void InlinePk_Violation_RaisesMsg2627()
        => new Simulation().AssertSqlError(
            "declare @t table (id int primary key); insert @t values (1); insert @t values (1)",
            2627);

    [TestMethod]
    public void TableLevelPk_Violation_RaisesMsg2627()
        => new Simulation().AssertSqlError(
            "declare @t table (id int, primary key (id)); insert @t values (1); insert @t values (1)",
            2627);

    [TestMethod]
    public void NotNull_Violation_RaisesMsg515()
        => new Simulation().AssertSqlError(
            "declare @t table (id int not null); insert @t values (null)",
            515);

    [TestMethod]
    public void InlinePk_OnNullableColumn_RaisesMsg8111()
        => new Simulation().AssertSqlError(
            "declare @t table (id int null primary key)",
            8111);

    [TestMethod]
    public void Defaults_AppliedOnOmittedColumns()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int, name varchar(10) default 'x', age int default 0); insert @t (id) values (1); select * from @t");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("x", reader.GetString(1));
        AreEqual(0, reader.GetInt32(2));
    }

    [TestMethod]
    public void InlinePk_PromotesToNotNull_NullInsertFails()
        => new Simulation().AssertSqlError(
            "declare @t table (id int primary key); insert @t values (null)",
            515);

    [TestMethod]
    public void MultiplePk_RaisesMsg8110()
        => new Simulation().AssertSqlError(
            "declare @t table (id int, v int, primary key (id), primary key (v))",
            8110);

    // ---- rejection paths ----

    [TestMethod]
    public void NamedConstraint_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int constraint pk1 primary key)",
            102);

    [TestMethod]
    public void NamedTableLevelConstraint_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int, constraint pk1 primary key (id))",
            102);

    [TestMethod]
    public void MultiVariableDeclare_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t1 table (id int), @t2 table (id int)",
            102);

    [TestMethod]
    public void MixedDeclare_ScalarThenTable_Rejected()
        => Throws<Exception>(() => new Simulation().ExecuteNonQuery(
            "declare @x int = 5, @t table (id int)"));

    [TestMethod]
    public void ReDeclare_SameName_RaisesMsg134()
        => new Simulation().AssertSqlError(
            "declare @t table (id int); declare @t table (v int)",
            134);

    [TestMethod]
    public void ReDeclare_ScalarThenTable_RaisesMsg134()
        => new Simulation().AssertSqlError(
            "declare @t int; declare @t table (id int)",
            134);

    [TestMethod]
    public void ReDeclare_TableThenScalar_RaisesMsg134()
        => new Simulation().AssertSqlError(
            "declare @t table (id int); declare @t int",
            134);

    [TestMethod]
    public void Identity_NotSupported_v1()
        => Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "declare @t table (id int identity, v int)"));

    [TestMethod]
    public void Unique_NotSupported_v1()
        => Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "declare @t table (id int unique)"));

    [TestMethod]
    public void Check_NotSupported_v1()
        => Throws<NotSupportedException>(() => new Simulation().ExecuteNonQuery(
            "declare @t table (id int check (id > 0))"));

    // ---- routing ----

    [TestMethod]
    public void Missing_TableVariable_RaisesMsg1087()
        => new Simulation().AssertSqlError("select * from @t", 1087);

    [TestMethod]
    public void Missing_TableVariable_Insert_RaisesMsg1087()
        => new Simulation().AssertSqlError("insert @t values (1)", 1087);

    [TestMethod]
    public void TwoPartName_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int); select * from dbo.@t",
            102);

    [TestMethod]
    public void Alter_TableVariable_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int); alter table @t add v int",
            102);

    [TestMethod]
    public void Drop_TableVariable_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int); drop table @t",
            102);

    [TestMethod]
    public void Truncate_TableVariable_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int); truncate table @t",
            102);

    [TestMethod]
    public void SelectInto_TableVariable_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int); select 1 as id into @t",
            102);

    // ---- non-transactional ----

    [TestMethod]
    public void Insert_InRollback_SurvivesRollback()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "declare @t table (id int); begin tran; insert @t values (1); rollback; select count(*) from @t"));

    [TestMethod]
    public void Update_InRollback_SurvivesRollback()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int, v int); insert @t values (1, 10); begin tran; update @t set v = 99 where id = 1; rollback; select v from @t");
        IsTrue(reader.Read());
        AreEqual(99, reader.GetInt32(0));
    }

    [TestMethod]
    public void Insert_InTry_VisibleInCatch_AfterError()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "declare @t table (id int); begin try insert @t values (1); raiserror('boom', 16, 1); end try begin catch select count(*) from @t end catch"));

    // ---- usage in queries ----

    [TestMethod]
    public void Join_With_TableVariable()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int); insert @t values (1), (2); declare @u table (id int, n varchar(5)); insert @u values (1, 'a'), (2, 'b'); select t.id, u.n from @t t join @u u on t.id = u.id order by t.id");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("a", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
    }

    [TestMethod]
    public void TableVariable_InDerivedTable()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "declare @t table (id int); insert @t values (1), (2); select count(*) from (select id from @t) x"));

    [TestMethod]
    public void TableVariable_InCte()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "declare @t table (id int); insert @t values (1), (2); with c as (select id from @t) select count(*) from c"));

    [TestMethod]
    public void TableVariable_WithAlias()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int); insert @t values (5); select x.id from @t x");
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
    }

    // ---- expression context (scalar var resolution) ----

    /// <summary>
    /// Real SQL Server treats @t in expression position as a scalar-variable
    /// lookup that fails (since @t is registered as a table variable, not a
    /// scalar). Probe-confirmed Msg 137 wording.
    /// </summary>
    [TestMethod]
    public void TableVariable_InExpressionContext_RaisesMsg137()
        => _ = new Simulation().AssertSqlError(
            "declare @t table (id int); declare @x int = @t",
            137);

    // ---- skip-mode interaction (un-taken IF DECLARE) ----
    // Note: the simulator's eager name resolution diverges from real SQL
    // Server's deferred-binding behavior for un-taken branches. Per CLAUDE.md
    // this is a documented general fidelity gap, not specific to table vars.

    // ---- OUTPUT INTO @t ----

    [TestMethod]
    public void Output_Into_TableVariable_InsertCapturesIdentity()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int identity primary key, name varchar(20));
            declare @out table (id int, name varchar(20));
            insert t output inserted.id, inserted.name into @out values ('a'), ('b');
            select id, name from @out order by id
            """);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("a", reader.GetString(1));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual("b", reader.GetString(1));
    }

    [TestMethod]
    public void Output_Into_TableVariable_SuppressesResultSet()
    {
        // Probe-confirmed: OUTPUT INTO target directs rows to the target
        // only — no result set surfaces to the client. Reading the
        // statement should return zero result rows.
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int identity primary key, v int);
            declare @out table (id int);
            insert t output inserted.id into @out values (42)
            """);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Output_Into_TableVariable_ExplicitColumnList()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int identity primary key, name varchar(20));
            declare @out table (insid int, insname varchar(20));
            insert t output inserted.id, inserted.name into @out (insid, insname) values ('x');
            select insid, insname from @out
            """);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual("x", reader.GetString(1));
    }

    [TestMethod]
    public void Output_Into_TableVariable_ColumnMismatch_RaisesMsg213()
        => new Simulation().AssertSqlError("""
            create table t (id int identity primary key, name varchar(20));
            declare @out table (id int);
            insert t output inserted.id, inserted.name into @out values ('x')
            """, 213);

    [TestMethod]
    public void Output_Update_Into_TableVariable_CapturesBeforeAndAfter()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int, v int);
            insert t values (1, 10), (2, 20);
            declare @out table (id int, oldv int, newv int);
            update t set v = v * 10 output inserted.id, deleted.v, inserted.v into @out;
            select id, oldv, newv from @out order by id
            """);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        AreEqual(10, reader.GetInt32(1));
        AreEqual(100, reader.GetInt32(2));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
        AreEqual(20, reader.GetInt32(1));
        AreEqual(200, reader.GetInt32(2));
    }

    [TestMethod]
    public void Output_Delete_Into_TableVariable_CapturesDeletedRows()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int);
            insert t values (1), (2);
            declare @out table (id int);
            delete t output deleted.id into @out;
            select id from @out order by id
            """);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
    }

    [TestMethod]
    public void Output_Into_MissingTableVariable_RaisesMsg1087()
        => new Simulation().AssertSqlError("""
            create table t (id int identity primary key, v int);
            insert t output inserted.id into @undeclared values (42)
            """, 1087);
}
