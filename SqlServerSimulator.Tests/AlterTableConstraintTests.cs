using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>ALTER TABLE ADD CONSTRAINT</c> /
/// <c>DROP CONSTRAINT</c> across all four families: PRIMARY KEY / UNIQUE /
/// FOREIGN KEY / CHECK / DEFAULT. Covers WITH CHECK / WITH NOCHECK,
/// existing-data validation, atomic multi-drop, and the catalog views.
/// Probed wording sourced from SQL Server 2025 on 2026-05-13.
/// </summary>
[TestClass]
public sealed class AlterTableConstraintTests
{
    // --- ADD CHECK ---

    [TestMethod]
    public void AddCheck_WithCleanData_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, qty int);
            insert t values (1, 5);
            alter table t add constraint ck_q check (qty > 0);
            select count(*) from t
            """));

    [TestMethod]
    public void AddCheck_WithViolatingRow_RaisesMsg547WithAlterPrefix()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, qty int not null);
            insert t values (1, -5);
            alter table t add constraint ck_q check (qty > 0)
            """, 547);
        Contains("ALTER TABLE statement conflicted with the CHECK constraint", ex.Message);
        Contains("\"ck_q\"", ex.Message);
    }

    [TestMethod]
    public void AddCheck_WithNoCheck_SkipsValidationAndMarksUntrusted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, qty int not null);
            insert t values (1, -5);
            alter table t with nocheck add constraint ck_q check (qty > 0)
            """);
        IsTrue((bool)sim.ExecuteScalar("select is_not_trusted from sys.check_constraints where name = 'ck_q'")!);
        // Existing bad row preserved; new INSERTs still enforced.
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where qty < 0"));
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (2, -10)"));
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AddCheck_Anonymous_AutoNames()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, qty int);
            alter table t add check (qty > 0)
            """);
        IsTrue((bool)sim.ExecuteScalar("select is_system_named from sys.check_constraints where parent_object_id = object_id('t')")!);
    }

    // --- ADD PRIMARY KEY ---

    [TestMethod]
    public void AddPrimaryKey_OnSimpleColumn_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null);
            insert t values (1);
            alter table t add constraint pk_t primary key (id);
            select count(*) from t
            """));

    [TestMethod]
    public void AddPrimaryKey_OnNullableColumn_RaisesMsg8111()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int null);
            alter table t add constraint pk_t primary key (id)
            """, 8111);

    [TestMethod]
    public void AddPrimaryKey_WhenAlreadyExists_RaisesMsg1779()
        => _ = new Simulation().AssertSqlError("""
            create table t (a int not null, b int not null, constraint pk_a primary key (a));
            alter table t add constraint pk_b primary key (b)
            """, 1779);

    [TestMethod]
    public void AddPrimaryKey_WithDuplicateData_RaisesMsg1505()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null);
            insert t values (1), (1);
            alter table t add constraint pk_t primary key (id)
            """, 1505);

    // --- ADD UNIQUE ---

    [TestMethod]
    public void AddUnique_WithCleanData_Succeeds()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, code varchar(10));
            insert t values (1, 'A'), (2, 'B');
            alter table t add constraint uq_code unique (code);
            select count(*) from t
            """));

    [TestMethod]
    public void AddUnique_WithDuplicateData_RaisesMsg1505()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, code varchar(10));
            insert t values (1, 'A'), (2, 'A');
            alter table t add constraint uq_code unique (code)
            """, 1505);

    [TestMethod]
    public void AddUnique_MissingColumn_RaisesMsg1911()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter table t add constraint uq_x unique (x)
            """, 1911);

    // --- ADD FOREIGN KEY ---

    [TestMethod]
    public void AddForeignKey_WithCleanData_Succeeds()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null);
            insert p values (10);
            insert c values (1, 10);
            alter table c add constraint fk_cp foreign key (p_id) references p(id);
            select count(*) from c
            """));

    [TestMethod]
    public void AddForeignKey_WithOrphan_RaisesMsg547WithAlterPrefix()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null);
            insert c values (1, 99);
            alter table c add constraint fk_cp foreign key (p_id) references p(id)
            """, 547);
        Contains("ALTER TABLE statement conflicted with the FOREIGN KEY constraint", ex.Message);
        Contains("\"fk_cp\"", ex.Message);
    }

    [TestMethod]
    public void AddForeignKey_WithNoCheck_AcceptsOrphanAndMarksUntrusted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null);
            insert c values (1, 99);
            alter table c with nocheck add constraint fk_cp foreign key (p_id) references p(id)
            """);
        IsTrue((bool)sim.ExecuteScalar("select is_not_trusted from sys.foreign_keys where name = 'fk_cp'")!);
    }

    [TestMethod]
    public void AddForeignKey_MissingChildColumn_RaisesMsg1769()
        => _ = new Simulation().AssertSqlError("""
            create table p (id int not null primary key);
            create table c (id int not null primary key);
            alter table c add constraint fk_cp foreign key (missing) references p(id)
            """, 1769);

    [TestMethod]
    public void AddForeignKey_OnDeleteCascade_PropagatesAtRuntime()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null);
            insert p values (10);
            insert c values (1, 10), (2, 10);
            alter table c add constraint fk_cp foreign key (p_id) references p(id) on delete cascade;
            delete p where id = 10
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from c"));
    }

    // --- ADD DEFAULT ---

    [TestMethod]
    public void AddDefault_FillsOmittedColumnOnInsert()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, val int null);
            alter table t add constraint df_val default (5) for val;
            insert t (id) values (1)
            """);
        AreEqual(5, sim.ExecuteScalar("select val from t where id = 1"));
    }

    [TestMethod]
    public void AddDefault_WhenColumnAlreadyHasDefault_RaisesMsg1781()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, val int default 0);
            alter table t add constraint df_val default (5) for val
            """, 1781);

    [TestMethod]
    public void AddDefault_MissingColumn_RaisesMsg1752()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter table t add constraint df_x default (0) for nonexistent
            """, 1752);

    // --- Name collisions ---

    [TestMethod]
    public void AddConstraint_DuplicateName_RaisesMsg2714()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, qty int);
            alter table t add constraint ck_q check (qty > 0);
            alter table t add constraint ck_q check (qty < 100)
            """, 2714);

    // --- DROP CONSTRAINT ---

    [TestMethod]
    public void DropConstraint_RemovesFromKeyConstraintsList()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null, code varchar(10));
            alter table t add constraint uq_code unique (code);
            alter table t drop constraint uq_code
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.key_constraints where parent_object_id = object_id('t')"));
    }

    [TestMethod]
    public void DropConstraint_NotFound_RaisesMsg3728()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter table t drop constraint missing
            """, 3728);

    [TestMethod]
    public void DropConstraint_IfExists_NotFound_SilentNoOp()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            alter table t drop constraint if exists missing;
            select count(*) from t
            """));

    [TestMethod]
    public void DropConstraint_KeyReferencedByForeignKey_RaisesMsg3725()
        => _ = new Simulation().AssertSqlError("""
            create table p (id int not null constraint pk_p primary key);
            create table c (id int not null primary key, p_id int null constraint fk_cp references p(id));
            alter table p drop constraint pk_p
            """, 3725);

    [TestMethod]
    public void DropConstraint_MultiDrop_Atomic_FirstMissingLeavesAllInPlace()
    {
        var sim = new Simulation();
        // First constraint exists, second doesn't: ck_q2 must still be present
        // after the failed multi-drop (probe-confirmed atomicity).
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, qty int);
            alter table t add constraint ck_q1 check (qty > 0);
            alter table t add constraint ck_q2 check (qty < 100)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t drop constraint ck_q1, ck_missing"));
        AreEqual("3728", ex.Data["HelpLink.EvtID"]);
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.check_constraints where parent_object_id = object_id('t')"));
    }

    [TestMethod]
    public void DropConstraint_DefaultClearsExpressionAndCatalog()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, val int);
            alter table t add constraint df_val default (5) for val;
            alter table t drop constraint df_val;
            insert t (id) values (1)
            """);
        AreEqual(DBNull.Value, sim.ExecuteScalar("select val from t where id = 1"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.default_constraints where parent_object_id = object_id('t')"));
    }

    [TestMethod]
    public void DropConstraint_ForeignKey_ClearsBothSidesOfGraph()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (id int not null primary key, p_id int null constraint fk_cp references p(id));
            alter table c drop constraint fk_cp
            """);
        // Parent can now be dropped (no incoming FK protection).
        _ = sim.ExecuteNonQuery("drop table p");
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.foreign_keys where name = 'fk_cp'"));
    }

    // --- Catalog views ---

    [TestMethod]
    public void SysCheckConstraints_ReflectsConstraintProperties()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, qty int);
            alter table t add constraint ck_q check (qty > 0)
            """);
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select name, type, type_desc, is_not_trusted, is_system_named from sys.check_constraints where parent_object_id = object_id('t')";
        using var r = cmd.ExecuteReader();
        IsTrue(r.Read());
        AreEqual("ck_q", r.GetString(0));
        AreEqual("C ", r.GetString(1));
        AreEqual("CHECK_CONSTRAINT", r.GetString(2));
        IsFalse(r.GetBoolean(3));
        IsFalse(r.GetBoolean(4));
    }

    [TestMethod]
    public void SysKeyConstraints_DistinguishesPkAndUq()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null, code varchar(10));
            alter table t add constraint pk_t primary key (id);
            alter table t add constraint uq_c unique (code)
            """);
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "select name, type, type_desc from sys.key_constraints where parent_object_id = object_id('t') order by type";
        using var r = cmd.ExecuteReader();
        IsTrue(r.Read());
        AreEqual("pk_t", r.GetString(0));
        AreEqual("PK", r.GetString(1));
        AreEqual("PRIMARY_KEY_CONSTRAINT", r.GetString(2));
        IsTrue(r.Read());
        AreEqual("uq_c", r.GetString(0));
        AreEqual("UQ", r.GetString(1));
        AreEqual("UNIQUE_CONSTRAINT", r.GetString(2));
    }

    [TestMethod]
    public void SysDefaultConstraints_PopulatedByInlineAndAlterAdd()
    {
        var sim = new Simulation();
        // Inline default — auto-named, is_system_named=true.
        // Named default via ALTER — explicit name, is_system_named=false.
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int default 7, b int);
            alter table t add constraint df_b default (9) for b
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.default_constraints where parent_object_id = object_id('t')"));
        IsTrue((bool)sim.ExecuteScalar("select is_system_named from sys.default_constraints where parent_column_id = 2")!);
        IsFalse((bool)sim.ExecuteScalar("select is_system_named from sys.default_constraints where parent_column_id = 3")!);
        AreEqual("df_b", sim.ExecuteScalar("select name from sys.default_constraints where parent_column_id = 3"));
    }

}
