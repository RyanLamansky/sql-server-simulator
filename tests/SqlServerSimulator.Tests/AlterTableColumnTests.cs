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
    public void AddColumn_ColumnKeyword_RejectedMsg156()
    {
        // `ALTER TABLE … ADD` takes no COLUMN keyword (unlike DROP COLUMN /
        // ALTER COLUMN) — real SQL Server 2025 rejects it with Msg 156 near
        // COLUMN.
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            alter table t add column note nvarchar(50)
            """, 156);
        Assert.Contains("column", ex.Message);
    }

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

    // --- ALTER COLUMN — type changes ---

    [TestMethod]
    public void AlterColumn_WidenVarchar_PreservesData()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v varchar(10));
            insert t values (1, 'hello');
            alter table t alter column v varchar(50)
            """);
        AreEqual("hello", sim.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void AlterColumn_WidenInt_PreservesData()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v int);
            insert t values (1, 42);
            alter table t alter column v bigint
            """);
        AreEqual(42L, sim.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void AlterColumn_VarcharToNvarchar_PreservesText()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(20));
            insert t values (1, 'hello');
            alter table t alter column v nvarchar(20)
            """);
        AreEqual("hello", sim.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void AlterColumn_NarrowVarcharFitting_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(50));
            insert t values (1, 'hi');
            alter table t alter column v varchar(10)
            """);
        AreEqual("hi", sim.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void AlterColumn_NarrowVarcharOverflow_Raises2628()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(50));
            insert t values (1, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v varchar(10)"));
        AreEqual("2628", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AlterColumn_IntToTinyint_FittingValues_Succeeds()
        => AreEqual((byte)5, new Simulation().ExecuteScalar("""
            create table t (v int);
            insert t values (5);
            alter table t alter column v tinyint;
            select v from t
            """));

    [TestMethod]
    public void AlterColumn_IntToTinyint_Overflow_Raises220()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v int);
            insert t values (5), (500)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v tinyint"));
        AreEqual("220", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("tinyint", ex.Message);
        Assert.Contains("500", ex.Message);
    }

    [TestMethod]
    // Same source-type-keyed family as CAST and column assignment
    // (probe-confirmed 2026-07-31): a bigint source keeps the generic
    // Msg 8115, a float source reports the value-bearing Msg 232.
    public void AlterColumn_BigintToInt_Overflow_Raises8115()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v bigint);
            insert t values (3000000000)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v int"));
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("data type int", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_FloatToTinyint_Overflow_Raises232WithValue()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v float);
            insert t values (300)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v tinyint"));
        AreEqual("232", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("value = 300.000000", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_VarcharToInt_BadData_Raises245()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(20));
            insert t values ('hello'), ('123')
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v int"));
        AreEqual("245", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AlterColumn_VarcharToInt_GoodData_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(20));
            insert t values ('123'), ('456');
            alter table t alter column v int
            """);
        AreEqual(579, sim.ExecuteScalar("select sum(v) from t"));
    }

    [TestMethod]
    public void AlterColumn_VarcharToDate_BadData_Raises241()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(20));
            insert t values ('not-a-date')
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v date"));
        AreEqual("241", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AlterColumn_DecimalPrecisionNarrow_Fitting_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v decimal(10,2));
            insert t values (99.99);
            alter table t alter column v decimal(5,2)
            """);
        AreEqual(99.99m, sim.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void AlterColumn_DecimalPrecisionNarrow_Overflow_Raises8115()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v decimal(10,2));
            insert t values (999.99)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v decimal(4,2)"));
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
    }

    // --- ALTER COLUMN — nullability ---

    [TestMethod]
    public void AlterColumn_NullToNotNull_WithNullData_Raises515()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(10) null);
            insert t values (1, null), (2, 'x')
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v varchar(10) not null"));
        AreEqual("515", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("v", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_NullToNotNull_NoNullData_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(10) null);
            insert t values (1, 'a'), (2, 'b');
            alter table t alter column v varchar(10) not null
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from t where v is not null"));
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t (id) values (3)"));
        AreEqual("515", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AlterColumn_NotNullToNull_AlwaysSucceeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(10) not null);
            insert t values (1, 'a');
            alter table t alter column v varchar(10) null;
            insert t (id) values (2)
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from t where v is null"));
    }

    [TestMethod]
    public void AlterColumn_NoNullabilityKeyword_PreservesExistingNullability()
    {
        var sim = new Simulation();
        // Probe-confirmed: omitting NULL/NOT NULL keeps the column's existing
        // nullability. The simulator implements this by carrying over
        // existingCol.Nullable when the parser produces null.
        _ = sim.ExecuteNonQuery("""
            create table t (v int not null);
            alter table t alter column v bigint;
            insert t values (1)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (null)"));
        AreEqual("515", ex.Data["HelpLink.EvtID"]);
    }

    // --- ALTER COLUMN — blockers (Msg 5074) ---

    [TestMethod]
    public void AlterColumn_UnderPrimaryKey_Raises5074()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column id bigint not null"));
        AreEqual("5074", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("object", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_UnderOutgoingForeignKey_Raises5074()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key);
            create table c (cid int primary key, pid int, constraint fk_c foreign key (pid) references p(id))
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table c alter column pid bigint"));
        AreEqual("5074", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("fk_c", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_UnderIncomingForeignKey_Raises5074()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table p (id int not null primary key, v varchar(10));
            create table c (cid int primary key, pid int, foreign key (pid) references p(id))
            """);
        // p.v isn't referenced; alter should succeed.
        _ = sim.ExecuteNonQuery("alter table p alter column v varchar(50)");
        // p.id IS referenced — PK blocks first; both PK and incoming FK
        // dependencies surface in the multi-blocker enumeration.
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table p alter column id bigint not null"));
        AreEqual("5074", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AlterColumn_UnderNonUniqueIndex_TypeChange_Raises5074()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v varchar(50));
            create index ix_v on t(v)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v nvarchar(50)"));
        AreEqual("5074", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("ix_v", ex.Message);
        Assert.Contains("index", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_UnderIndex_LengthWidening_Succeeds()
    {
        // Probe-confirmed: length widening within same SqlType family
        // (varchar(50) → varchar(100)) is allowed under an index.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int primary key, v varchar(50));
            create index ix_v on t(v);
            alter table t alter column v varchar(100)
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix_v'"));
    }

    [TestMethod]
    public void AlterColumn_UnderComputedDependency_Raises5074()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b as a * 2)");
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column a bigint"));
        AreEqual("5074", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("column 'b'", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_ComputedColumnItself_Raises4928()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b as a * 2)");
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column b bigint"));
        AreEqual("4928", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("COMPUTED", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_RowVersion_Raises4928()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, v rowversion)");
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column v bigint"));
        AreEqual("4928", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("timestamp", ex.Message);
    }

    // --- ALTER COLUMN — grammar / preservation ---

    [TestMethod]
    public void AlterColumn_MissingColumn_Raises4924()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int)");
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("alter table t alter column missing bigint"));
        AreEqual("4924", ex.Data["HelpLink.EvtID"]);
        Assert.Contains("missing", ex.Message);
    }

    [TestMethod]
    public void AlterColumn_UnderCheck_AllowsTypeChange()
    {
        // Probe-confirmed: CHECK constraints don't block ALTER COLUMN.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(10), check (len(v) > 0));
            insert t values (1, 'x');
            alter table t alter column v varchar(20)
            """);
        AreEqual("x", sim.ExecuteScalar("select v from t"));
        // CHECK constraint still enforced after the type change.
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (2, '')"));
        AreEqual("547", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void AlterColumn_PreservesDefault()
    {
        // Inline named DEFAULT isn't supported in CREATE TABLE; add via
        // ALTER ADD CONSTRAINT instead.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(10));
            alter table t add constraint df_v default ('d') for v;
            alter table t alter column v varchar(50);
            insert t (id) values (1)
            """);
        AreEqual("d", sim.ExecuteScalar("select v from t"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.default_constraints where name = 'df_v'"));
    }

    [TestMethod]
    public void AlterColumn_PreservesIdentity_WidensIntToBigInt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int identity not null, v varchar(20));
            insert t (v) values ('a'), ('b');
            alter table t alter column id bigint not null
            """);
        // Identity advances post-alter (high-water mark survived the column
        // instance swap).
        _ = sim.ExecuteNonQuery("insert t (v) values ('c')");
        AreEqual(3L, sim.ExecuteScalar("select id from t where v = 'c'"));
    }

    [TestMethod]
    public void AlterColumn_IdentityToVarchar_RaisesMsg2749()
        => new Simulation().AssertSqlError(
            "create table t (id int identity, v varchar(20)); alter table t alter column id varchar(20)",
            2749,
            "Identity column 'id' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, unencrypted, and constrained to be nonnullable.");

    [TestMethod]
    public void AlterColumn_CollateClause_ParseAccepted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(20));
            insert t values ('hello');
            alter table t alter column v varchar(50) collate Latin1_General_BIN
            """);
        AreEqual("hello", sim.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void AlterColumn_NoOp_Succeeds()
    {
        // Same type, same nullability — should be a no-op pass-through.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v int not null);
            insert t values (42);
            alter table t alter column v int not null
            """);
        AreEqual(42, sim.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void AlterColumn_EmptyTable_TypeChangeSucceeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(10));
            alter table t alter column v int;
            insert t values (42)
            """);
        AreEqual(42, sim.ExecuteScalar("select v from t"));
    }

    // === Stable column ids (sys.columns.column_id / sys.tables.max_column_id_used) ===

    private static string ColumnIds(Simulation sim) =>
        (string)sim.ExecuteScalar("""
            select string_agg(cast(name as varchar(20)) + '=' + cast(column_id as varchar), ',')
            from sys.columns where object_id = object_id('w_t')
            """)!;

    /// <summary>
    /// Dropping a column leaves a permanent hole: the survivors keep their
    /// ids and the watermark doesn't shrink (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void DropColumn_LeavesAHoleInColumnIds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table w_t (c1 int, c2 int, c3 int); alter table w_t drop column c2");
        AreEqual("c1=1,c3=3", ColumnIds(sim));
        AreEqual(3, sim.ExecuteScalar("select max_column_id_used from sys.tables where object_id = object_id('w_t')"));
    }

    /// <summary>An added column takes watermark + 1 rather than filling a hole.</summary>
    [TestMethod]
    public void AddColumn_TakesTheNextIdPastTheWatermark()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table w_t (c1 int, c2 int, c3 int);
            alter table w_t drop column c2;
            alter table w_t add c4 int null
            """);
        AreEqual("c1=1,c3=3,c4=4", ColumnIds(sim));
        AreEqual(4, sim.ExecuteScalar("select max_column_id_used from sys.tables where object_id = object_id('w_t')"));
    }

    /// <summary>
    /// The watermark never resets, so ids keep climbing past every dropped
    /// column even when the live column count falls.
    /// </summary>
    [TestMethod]
    public void Watermark_NeverResets_AcrossRepeatedDropAndAdd()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table w_t (c1 int, c2 int, c3 int);
            alter table w_t drop column c2;
            alter table w_t add c4 int null;
            alter table w_t drop column c3, c4;
            alter table w_t add c9 int null
            """);
        AreEqual("c1=1,c9=5", ColumnIds(sim));
        AreEqual(5, sim.ExecuteScalar("select max_column_id_used from sys.tables where object_id = object_id('w_t')"));
    }

    /// <summary>A type change replaces the column but not its catalog identity.</summary>
    [TestMethod]
    public void AlterColumn_PreservesColumnId()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table w_t (c1 int, c2 int, c3 int);
            alter table w_t drop column c2;
            alter table w_t alter column c1 bigint
            """);
        AreEqual("c1=1,c3=3", ColumnIds(sim));
    }

    [TestMethod]
    public void RenameColumn_PreservesColumnId()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table w_t (c1 int, c2 int, c3 int); alter table w_t drop column c2");
        _ = sim.ExecuteNonQuery("exec sp_rename 'w_t.c3', 'renamed', 'column'");
        AreEqual("c1=1,renamed=3", ColumnIds(sim));
    }

    /// <summary>A fresh table's watermark is simply its column count.</summary>
    [TestMethod]
    public void FreshTable_WatermarkIsTheColumnCount()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table w_t (c1 int, c2 int, c3 int);
            select max_column_id_used from sys.tables where object_id = object_id('w_t')
            """));

    // === PRIMARY KEY / UNIQUE column direction (sys.index_columns.is_descending_key) ===

    /// <summary>
    /// Direction has no runtime effect — rows are stored unordered either way
    /// — so the assertion is the catalog flag a schema-diff tool reads.
    /// </summary>
    private static string KeyDirections(Simulation sim, string table) =>
        (string)sim.ExecuteScalar($"""
            select string_agg(cast(c.name as varchar(20)) + '=' + cast(ic.is_descending_key as varchar), ',')
            from sys.index_columns ic
            join sys.columns c on c.object_id = ic.object_id and c.column_id = ic.column_id
            where ic.object_id = object_id('{table}')
            """)!;

    [TestMethod]
    public void TableLevelPrimaryKey_RecordsColumnDirection()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int not null, b int not null, constraint pk_t primary key (a desc, b asc))");
        AreEqual("a=1,b=0", KeyDirections(sim, "t"));
    }

    [TestMethod]
    public void TableLevelUnique_RecordsColumnDirection()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int not null, b int not null, constraint uq_t unique (a desc, b))");
        AreEqual("a=1,b=0", KeyDirections(sim, "t"));
    }

    [TestMethod]
    public void AlterTableAddConstraint_RecordsColumnDirection()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int not null, b int not null);
            alter table t add constraint pk_t primary key (b desc, a desc)
            """);
        AreEqual("b=1,a=1", KeyDirections(sim, "t"));
    }

    [TestMethod]
    public void KeyWithoutDirection_ReportsAscending()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int not null, b int not null, constraint pk_t primary key (a, b))");
        AreEqual("a=0,b=0", KeyDirections(sim, "t"));
    }

    /// <summary>
    /// The inline column-level form takes no direction at all — real rejects
    /// it where the table-level list accepts it.
    /// </summary>
    [TestMethod]
    public void InlinePrimaryKey_RejectsDirection()
        => new Simulation().AssertSqlError(
            "create table t (a int not null primary key desc, b int)",
            156,
            "Incorrect syntax near the keyword 'desc'.");
}
