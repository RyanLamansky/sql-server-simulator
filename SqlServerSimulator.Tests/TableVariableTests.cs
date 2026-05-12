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
    public void Identity_AutoIncrements()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int identity(1,1), name nvarchar(10)); insert @t (name) values ('a'), ('b'), ('c'); select id, name from @t order by id");
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0)); AreEqual("a", reader.GetString(1));
        IsTrue(reader.Read()); AreEqual(2, reader.GetInt32(0)); AreEqual("b", reader.GetString(1));
        IsTrue(reader.Read()); AreEqual(3, reader.GetInt32(0)); AreEqual("c", reader.GetString(1));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// Probe-confirmed: real SQL Server rejects SET IDENTITY_INSERT @t at
    /// parse with Msg 102 — there's no way to force a specific value into
    /// an identity column of a table variable.
    /// </summary>
    [TestMethod]
    public void Identity_SetIdentityInsert_RaisesMsg102()
        => new Simulation().AssertSqlError(
            "declare @t table (id int identity, v int); set identity_insert @t on",
            102);

    [TestMethod]
    public void Identity_ScopeIdentityObserves()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int identity(100,5), v int); insert @t (v) values (1); insert @t (v) values (2); select scope_identity()");
        IsTrue(reader.Read());
        AreEqual(105m, reader.GetDecimal(0));
    }

    [TestMethod]
    public void Unique_InlineViolation_RaisesMsg2627()
        => new Simulation().AssertSqlError(
            "declare @t table (id int, code nvarchar(10) unique); insert @t values (1, 'a'); insert @t values (2, 'a')",
            2627);

    [TestMethod]
    public void Unique_TableLevelViolation_RaisesMsg2627()
        => new Simulation().AssertSqlError(
            "declare @t table (id int, code nvarchar(10), unique (code)); insert @t values (1, 'a'), (2, 'a')",
            2627);

    [TestMethod]
    public void Check_InlineViolation_RaisesMsg547()
        => new Simulation().AssertSqlError(
            "declare @t table (val int check (val > 0)); insert @t values (-1)",
            547);

    [TestMethod]
    public void Check_TableLevelViolation_RaisesMsg547()
        => new Simulation().AssertSqlError(
            "declare @t table (a int, b int, check (a < b)); insert @t values (5, 2)",
            547);

    [TestMethod]
    public void Computed_NonPersistedFromTwoColumns()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (a int, b int, c as a + b); insert @t (a, b) values (1, 2), (3, 4); select a, b, c from @t order by a");
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0)); AreEqual(2, reader.GetInt32(1)); AreEqual(3, reader.GetInt32(2));
        IsTrue(reader.Read()); AreEqual(3, reader.GetInt32(0)); AreEqual(4, reader.GetInt32(1)); AreEqual(7, reader.GetInt32(2));
    }

    [TestMethod]
    public void Computed_Persisted_Works()
    {
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (a int, b int, c as a + b persisted); insert @t (a, b) values (10, 20); select c from @t");
        IsTrue(reader.Read());
        AreEqual(30, reader.GetInt32(0));
    }

    [TestMethod]
    public void RowVersion_AdvancesAcrossInserts()
    {
        // Two inserts into a @t with a rowversion column should produce
        // strictly increasing 8-byte rv values (the simulator's database-
        // scoped counter advances on every row).
        using var reader = new Simulation().ExecuteReader(
            "declare @t table (id int, rv rowversion); insert @t (id) values (1); insert @t (id) values (2); select id, rv from @t order by id");
        IsTrue(reader.Read());
        var rv1 = (byte[])reader.GetValue(1);
        IsTrue(reader.Read());
        var rv2 = (byte[])reader.GetValue(1);
        // rv2 > rv1 lexicographically (big-endian monotonic).
        var greater = false;
        for (var i = 0; i < 8; i++)
        {
            if (rv2[i] > rv1[i]) { greater = true; break; }
            if (rv2[i] < rv1[i]) break;
        }
        IsTrue(greater);
    }

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

    [TestMethod]
    public void Output_Into_TableVariable_AppliesDefaultsForUnfilled()
    {
        // Probe-confirmed: target columns not covered by the OUTPUT
        // projection receive their declared DEFAULT — not NULL.
        using var reader = new Simulation().ExecuteReader("""
            create table t (id int);
            declare @out table (id int, msg nvarchar(50) default 'defaulted');
            insert t output inserted.id into @out (id) values (1), (2);
            select id, msg from @out order by id
            """);
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0)); AreEqual("defaulted", reader.GetString(1));
        IsTrue(reader.Read()); AreEqual(2, reader.GetInt32(0)); AreEqual("defaulted", reader.GetString(1));
    }

    // ---- OUTPUT INTO regular table ----

    [TestMethod]
    public void Output_Into_RegularTable_CapturesRows()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table audit_t (id int, val int);
            create table src_t (id int identity, val int);
            insert src_t (val) output inserted.id, inserted.val into audit_t values (100), (200);
            select id, val from audit_t order by id
            """);
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0)); AreEqual(100, reader.GetInt32(1));
        IsTrue(reader.Read()); AreEqual(2, reader.GetInt32(0)); AreEqual(200, reader.GetInt32(1));
    }

    [TestMethod]
    public void Output_Into_RegularTable_AppliesDefaultsForUnfilled()
    {
        using var reader = new Simulation().ExecuteReader("""
            create table audit_t (id int, msg nvarchar(50) default 'audit_default');
            create table src_t (id int);
            insert src_t output inserted.id into audit_t (id) values (1), (2);
            select id, msg from audit_t order by id
            """);
        IsTrue(reader.Read()); AreEqual(1, reader.GetInt32(0)); AreEqual("audit_default", reader.GetString(1));
        IsTrue(reader.Read()); AreEqual(2, reader.GetInt32(0)); AreEqual("audit_default", reader.GetString(1));
    }

    [TestMethod]
    public void Output_Into_RegularTable_MissingTable_RaisesMsg208()
        => new Simulation().AssertSqlError("""
            create table t (id int);
            insert t output inserted.id into nonexistent_audit values (1)
            """, 208);

    // ---- statement-level atomicity ----

    [TestMethod]
    public void StatementAtomic_MultiRowInsert_NotNullFails_RollsBackAllRows()
    {
        // Probe-confirmed: a multi-row INSERT into @t that hits a NOT NULL
        // violation mid-batch leaves @t empty (the failed row plus any
        // earlier-in-batch row are both rolled back). Real SQL Server's
        // table-variable mutations are statement-atomic even though they
        // ignore BEGIN TRAN / ROLLBACK.
        var count = new Simulation().ExecuteScalar("""
            declare @t table (id int not null, val nvarchar(20));
            begin try
                insert @t values (1, 'a'), (null, 'b'), (3, 'c');
            end try
            begin catch
            end catch;
            select count(*) from @t
            """);
        AreEqual(0, count);
    }

    [TestMethod]
    public void StatementAtomic_MultiRowInsert_PkViolation_RollsBackAllRows()
    {
        var count = new Simulation().ExecuteScalar("""
            declare @t table (id int primary key);
            begin try
                insert @t values (1), (2), (1), (3);
            end try
            begin catch
            end catch;
            select count(*) from @t
            """);
        AreEqual(0, count);
    }

    [TestMethod]
    public void StatementAtomic_MultiRowInsert_CheckViolation_RollsBackAllRows()
    {
        var count = new Simulation().ExecuteScalar("""
            declare @t table (val int check (val > 0));
            begin try
                insert @t values (1), (2), (-1), (3);
            end try
            begin catch
            end catch;
            select count(*) from @t
            """);
        AreEqual(0, count);
    }

    [TestMethod]
    public void StatementAtomic_PriorSuccessfulInserts_Preserved()
    {
        // A failed statement rolls back its own rows but earlier successful
        // statements are preserved (verifies the per-statement scope).
        var count = new Simulation().ExecuteScalar("""
            declare @t table (id int primary key);
            insert @t values (10);
            begin try
                insert @t values (20), (30), (10);  -- 4th row violates PK
            end try
            begin catch
            end catch;
            select count(*) from @t
            """);
        AreEqual(1, count);
    }
}
