using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>ALTER TABLE ADD [COLUMN] col TYPE [...]</c> +
/// <c>ALTER TABLE DROP COLUMN [IF EXISTS] col [, col...]</c>. Probed wording
/// sourced from SQL Server 2025 on 2026-05-14.
/// </summary>
[TestClass]
public sealed class AlterTableColumnTests
{
    // --- ADD COLUMN — basic shapes ---

    [TestMethod]
    public void AddNullableColumn_BackfillsAsNull()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, name nvarchar(50));
            insert t values (1, 'a'), (2, 'b');
            alter table t add note nvarchar(100);
            select count(*) from t where note is null
            """));

    [TestMethod]
    public void AddNullableColumn_WithDefault_DoesNotBackfill()
    {
        var sim = new Simulation();
        // SQL Server quirk: DEFAULT on a nullable ADD only applies to
        // future inserts; existing rows stay NULL even though the column
        // has a DEFAULT definition.
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key);
            insert t values (1), (2);
            alter table t add tag int default 99
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t where tag is null"));
        _ = sim.ExecuteNonQuery("insert t (id) values (3)");
        AreEqual(99, sim.ExecuteScalar("select tag from t where id = 3"));
    }

    [TestMethod]
    public void AddNotNullColumn_WithDefault_BackfillsExistingRows()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            insert t values (1), (2);
            alter table t add qty int not null default 0;
            select count(*) from t where qty = 0
            """));

    [TestMethod]
    public void AddNotNullColumn_DefaultGetUtcDate_ProducesConstantSnapshot()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            insert t values (1), (2), (3);
            alter table t add created datetime not null default getutcdate();
            select count(distinct created) from t
            """));

    [TestMethod]
    public void AddNotNullColumn_WithoutDefault_OnNonEmptyTable_RaisesMsg4901()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            insert t values (1);
            alter table t add x int not null
            """, 4901);

    [TestMethod]
    public void AddNotNullColumn_WithoutDefault_OnEmptyTable_Succeeds()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            alter table t add x int not null;
            select count(*) from t
            """));

    [TestMethod]
    public void AddIdentityColumn_BackfillsSequential()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key);
            insert t values (10), (20), (30);
            alter table t add iden int identity(100, 1)
            """);
        AreEqual(3, sim.ExecuteScalar("select count(*) from t"));
        AreEqual(3, sim.ExecuteScalar("select count(distinct iden) from t"));
        AreEqual(100, sim.ExecuteScalar("select min(iden) from t"));
        AreEqual(102, sim.ExecuteScalar("select max(iden) from t"));
    }

    [TestMethod]
    public void AddSecondIdentityColumn_RaisesMsg2744()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key identity);
            alter table t add iden2 int identity(1, 1)
            """, 2744);

    [TestMethod]
    public void AddColumn_OptionalColumnKeyword_Accepted()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            insert t values (1), (2);
            alter table t add column note nvarchar(50);
            select count(*) from t where note is null
            """));

    [TestMethod]
    public void AddMultipleColumns_CommaList_AllAdded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key);
            insert t values (1);
            alter table t add a int, b int default 99
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where a is null"));
        // Nullable with DEFAULT: existing row stays NULL.
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where b is null"));
    }

    [TestMethod]
    public void AddColumn_DuplicateName_RaisesMsg2705()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, name nvarchar(50));
            alter table t add name nvarchar(100)
            """, 2705);

    [TestMethod]
    public void AddColumn_InlineCheck_AppliedToFutureInserts()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key);
            alter table t add qty int constraint ck_qty check (qty is null or qty > 0)
            """);
        _ = sim.ExecuteNonQuery("insert t values (1, 5)");
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (2, -5)"));
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AddColumn_InlineForeignKey_Enforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (pid int not null primary key);
            create table c (id int not null primary key);
            insert p values (1);
            alter table c add p_ref int constraint fk_pref references p(pid);
            insert c values (10, 1)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert c values (20, 999)"));
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AddColumn_ComputedColumn_VisibleFromSelect()
        => AreEqual(20, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            insert t values (10);
            alter table t add doubled as (id * 2);
            select doubled from t
            """));

    [TestMethod]
    public void AddColumn_VisibleInSysColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key);
            alter table t add a int, b nvarchar(50)
            """);
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
    }

    // --- DROP COLUMN — basic shapes ---

    [TestMethod]
    public void DropColumn_RemovesFromTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int);
            insert t values (1, 10, 100);
            alter table t drop column b
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
        AreEqual(10, sim.ExecuteScalar("select a from t"));
    }

    [TestMethod]
    public void DropColumn_PreservesExistingRows()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, name nvarchar(50));
            insert t values (1, 'a'), (2, 'b');
            alter table t drop column name;
            select count(*) from t
            """));

    [TestMethod]
    public void DropColumn_DoesNotExist_RaisesMsg4924()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter table t drop column missing
            """, 4924);

    [TestMethod]
    public void DropColumn_IfExists_Missing_Silent()
        => _ = new Simulation().ExecuteNonQuery("""
            create table t (id int not null primary key);
            alter table t drop column if exists missing
            """);

    [TestMethod]
    public void DropColumn_WithDefaultConstraint_RaisesMsg5074()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, tag int default 99);
            alter table t drop column tag
            """, 5074);
        Contains("dependent on column 'tag'", ex.Message);
    }

    [TestMethod]
    public void DropColumn_WithCheckConstraint_RaisesMsg5074()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, qty int, constraint ck_q check (qty > 0));
            alter table t drop column qty
            """, 5074);
        Contains("'ck_q'", ex.Message);
    }

    [TestMethod]
    public void DropColumn_PartOfPrimaryKey_RaisesMsg5074()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, val int);
            alter table t drop column id
            """, 5074);

    [TestMethod]
    public void DropColumn_ReferencedByOutgoingFk_RaisesMsg5074()
    {
        var ex = new Simulation().AssertSqlError("""
            create table p (pid int not null primary key);
            create table c (id int not null primary key, p_ref int constraint fk_p references p(pid));
            alter table c drop column p_ref
            """, 5074);
        Contains("'fk_p'", ex.Message);
    }

    [TestMethod]
    public void DropColumn_ReferencedByIncomingFk_RaisesMsg5074()
        => _ = new Simulation().AssertSqlError("""
            create table p (pid int not null primary key);
            create table c (id int not null primary key, p_ref int references p(pid));
            alter table p drop column pid
            """, 5074);

    [TestMethod]
    public void DropColumn_WithIndexKey_RaisesMsg5074()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, a int);
            create index ix_a on t(a);
            alter table t drop column a
            """, 5074);
        Contains("The index 'ix_a'", ex.Message);
    }

    [TestMethod]
    public void DropColumn_WithIndexInclude_RaisesMsg5074()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, a int, b int);
            create index ix_inc on t(a) include (b);
            alter table t drop column b
            """, 5074);
        Contains("The index 'ix_inc'", ex.Message);
    }

    [TestMethod]
    public void DropColumn_MultiColumn_Atomic()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int default 99);
            insert t values (1, 10, 100)
            """);
        // One blocker (b has DEFAULT) → both columns stay.
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t drop column a, b"));
        AreEqual("5074", ex.Data["HelpLink.EvtID"]);
        AreEqual(3, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
    }

    [TestMethod]
    public void DropColumn_MultiColumn_AllSucceed()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int, c int);
            insert t values (1, 10, 100, 1000);
            alter table t drop column a, c
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
        AreEqual(100, sim.ExecuteScalar("select b from t"));
    }

    [TestMethod]
    public void DropColumn_IdentityColumn_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, iden int identity(1,1));
            insert t (id) values (10), (20);
            alter table t drop column iden
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('t')"));
    }

    [TestMethod]
    public void AddThenDropColumn_RoundTripCleanlyAndPreservesRows()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            insert t values (1), (2);
            alter table t add note nvarchar(100);
            alter table t drop column note;
            select count(*) from t
            """));

    // --- Storage ordinal remapping ---

    [TestMethod]
    public void DropMiddleColumn_RemainingColumnsReadable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int, c int);
            insert t values (1, 10, 100, 1000);
            alter table t drop column b
            """);
        AreEqual(10, sim.ExecuteScalar("select a from t"));
        AreEqual(1000, sim.ExecuteScalar("select c from t"));
    }

    [TestMethod]
    public void DropMiddleColumn_PrimaryKeyStillEnforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int);
            insert t values (1, 10, 100);
            alter table t drop column a
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (1, 200)"));
        AreEqual("2627", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DropMiddleColumn_IndexStillFunctions()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, mid int, b int);
            create unique index ix_b on t(b);
            insert t values (1, 10, 100);
            alter table t drop column mid
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (2, 100)"));
        AreEqual("2601", ex.Data["HelpLink.EvtID"]);
    }
}
