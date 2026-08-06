using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// <c>ALTER TABLE</c> shapes beyond the ADD / DROP / ALTER COLUMN core:
/// <c>REBUILD</c>, the <c>ADD | DROP { ROWGUIDCOL | SPARSE }</c> column
/// attributes, a multi-element <c>ADD</c> constraint list, and <c>DROP PERIOD
/// FOR SYSTEM_TIME</c> — plus the Msg 4936 determinism gate a <c>PERSISTED</c>
/// computed column takes and the Msg 128 a DEFAULT expression's column
/// reference earns. Probed against SQL Server 2025 — see
/// <c>docs/claude/alter-table.md</c>.
/// </summary>
[TestClass]
public sealed class AlterTableShapeTests
{
    // --- REBUILD ---

    [TestMethod]
    public void Rebuild_LeavesTheRowsAlone()
        => AreEqual(2, ExecuteScalar("""
            create table t (a int not null primary key, b int);
            insert t values (1, 1), (2, 2);
            alter table t rebuild;
            select count(*) from t
            """));

    [TestMethod]
    public void Rebuild_PartitionAll_Succeeds()
        => AreEqual(1, ExecuteScalar("create table t (a int); insert t values (1); alter table t rebuild partition = all; select count(*) from t"));

    [TestMethod]
    public void Rebuild_WithOptions_Succeeds()
        => AreEqual(1, ExecuteScalar("""
            create table t (a int);
            insert t values (1);
            alter table t rebuild partition = all with (data_compression = page, maxdop = 2, online = off);
            select count(*) from t
            """));

    [TestMethod]
    public void Rebuild_UnrecognizedOption_ReportsMsg155()
        => AssertSqlError(
            "create table t (a int); alter table t rebuild with (nope = on)",
            155,
            "'nope' is not a recognized ALTER TABLE option.");

    [TestMethod]
    public void Rebuild_BadCompressionLevel_IsASyntaxError()
        => AssertSqlError("create table t (a int); alter table t rebuild with (data_compression = bogus)", 102);

    [TestMethod]
    public void Rebuild_EmptyOptionList_IsASyntaxError()
        => AssertSqlError("create table t (a int); alter table t rebuild with ()", 102);

    [TestMethod]
    public void Rebuild_PartitionNumber_OnAKeyedTable_ReportsMsg7729NamingTheKeyIndex()
        => AssertSqlError(
            "create table t (a int not null, constraint pk_t primary key (a)); alter table t rebuild partition = 1",
            7729,
            "Cannot specify partition number in the alter index statement as the index 'pk_t' is not partitioned.");

    [TestMethod]
    public void Rebuild_PartitionNumber_OnAHeap_ReportsMsg7735NamingTheTable()
        => AssertSqlError(
            "create table t (a int); alter table t rebuild partition = 1",
            7735,
            "Cannot specify partition number in alter table statement to rebuild or reorganize a partition of table 't' as table is not partitioned.");

    [TestMethod]
    public void Rebuild_OnPartitions_ReportsMsg7729NamingTheTable()
        => AssertSqlError(
            "create table t (a int); alter table t rebuild partition = all with (data_compression = page on partitions (1))",
            7729,
            "Cannot specify partition number in the alter table statement as the table 't' is not partitioned.");

    [TestMethod]
    public void Rebuild_MissingTable_ReportsMsg4902()
        => AssertSqlError("alter table nope rebuild", 4902);

    // --- ALTER COLUMN ADD | DROP ROWGUIDCOL ---

    [TestMethod]
    public void AddRowGuidCol_SetsIsRowGuidCol()
        => IsTrue((bool)ExecuteScalar("""
            create table t (a int, g uniqueidentifier);
            alter table t alter column g add rowguidcol;
            select is_rowguidcol from sys.columns where object_id = object_id('t') and name = 'g'
            """)!);

    [TestMethod]
    public void DropRowGuidCol_ClearsIt()
        => IsFalse((bool)ExecuteScalar("""
            create table t (a int, g uniqueidentifier);
            alter table t alter column g add rowguidcol;
            alter table t alter column g drop rowguidcol;
            select is_rowguidcol from sys.columns where object_id = object_id('t') and name = 'g'
            """)!);

    [TestMethod]
    public void AddRowGuidCol_WhenOneExists_ReportsMsg4925()
        => AssertSqlError(
            """
            create table t (a int, g uniqueidentifier, h uniqueidentifier);
            alter table t alter column g add rowguidcol;
            alter table t alter column h add rowguidcol
            """,
            4925,
            "ALTER TABLE ALTER COLUMN ADD ROWGUIDCOL failed because a column already exists in table 't' with ROWGUIDCOL property.");

    [TestMethod]
    public void AddRowGuidCol_OnANonGuidColumn_ReportsMsg2761()
        => AssertSqlError("create table t (a int, g uniqueidentifier); alter table t alter column a add rowguidcol", 2761);

    [TestMethod]
    public void DropRowGuidCol_WhenNoneExists_ReportsMsg4926()
        => AssertSqlError(
            "create table t (a int, g uniqueidentifier); alter table t alter column g drop rowguidcol",
            4926,
            "ALTER TABLE ALTER COLUMN DROP ROWGUIDCOL failed because a column does not exist in table 't' with ROWGUIDCOL property.");

    [TestMethod]
    public void AlterColumnAttribute_MissingColumn_ReportsMsg4924()
        => AssertSqlError("create table t (a int); alter table t alter column nosuch add rowguidcol", 4924);

    // --- ALTER COLUMN ADD | DROP SPARSE ---

    [TestMethod]
    public void AddSparse_SetsIsSparse()
        => IsTrue((bool)ExecuteScalar("""
            create table t (a int, b int);
            alter table t alter column b add sparse;
            select is_sparse from sys.columns where object_id = object_id('t') and name = 'b'
            """)!);

    [TestMethod]
    public void DropSparse_ClearsIt()
        => IsFalse((bool)ExecuteScalar("""
            create table t (a int, b int);
            alter table t alter column b add sparse;
            alter table t alter column b drop sparse;
            select is_sparse from sys.columns where object_id = object_id('t') and name = 'b'
            """)!);

    [TestMethod]
    public void AddSparse_LeavesTheDataReadable()
        => AreEqual(2, ExecuteScalar("""
            create table t (a int, b int);
            insert t values (1, 10), (2, null);
            alter table t alter column b add sparse;
            select count(*) from t
            """));

    [TestMethod]
    public void AddSparse_OnANotNullColumn_ReportsMsg1731()
        => AssertSqlError("create table t (a int, c varchar(10) not null); alter table t alter column c add sparse", 1731);

    [TestMethod]
    public void AddSparse_OnAnIdentityColumn_ReportsMsg1731()
        => AssertSqlError("create table t (a int, d int identity); alter table t alter column d add sparse", 1731);

    [TestMethod]
    public void AddSparse_OnAGeographyColumn_ReportsMsg1731()
        => AssertSqlError("create table t (a int, b geography); alter table t alter column b add sparse", 1731);

    [TestMethod]
    public void AddSparse_OnAComputedColumn_ReportsMsg4928()
        => AssertSqlError("create table t (a int, b as a + 1); alter table t alter column b add sparse", 4928);

    [TestMethod]
    public void AddSparse_OnAColumnCarryingADefault_ReportsMsg11410()
        => AssertSqlError("create table t (a int, b int default 3); alter table t alter column b add sparse", 11410);

    [TestMethod]
    public void AlterColumnAddPersisted_IsNotModeledYet()
        => Throws<NotSupportedException>(() => ExecuteScalar("create table t (a int, b as a + 1); alter table t alter column b add persisted"));

    [TestMethod]
    public void AlterColumnAddMasked_IsNotModeledYet()
        => Throws<NotSupportedException>(() => ExecuteScalar("create table t (a int, n varchar(50)); alter table t alter column n add masked with (function = 'default()')"));

    // --- multi-element ADD ---

    [TestMethod]
    public void MultiConstraintAdd_CreatesEveryElement()
        => AreEqual(3, ExecuteScalar("""
            create table t (a int not null, b int, c int);
            alter table t add constraint pk1 primary key (a), constraint ck1 check (b > 0), check (c > 0);
            select count(*) from sys.objects where parent_object_id = object_id('t')
            """));

    [TestMethod]
    public void MultiConstraintAdd_TakesAKeyAndAForeignKey()
        => AreEqual(3, ExecuteScalar("""
            create table t (a int not null, b int not null);
            alter table t add constraint pk6 primary key (a), constraint uq6 unique (b), constraint fk6 foreign key (b) references t(a);
            select count(*) from sys.objects where parent_object_id = object_id('t')
            """));

    [TestMethod]
    public void MultiConstraintAdd_ABinderErrorRollsTheEarlierElementBack()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int); insert t values (1, 1)");
        _ = simulation.AssertSqlError("alter table t add constraint pk2 primary key (a), constraint ckbad check (nosuch > 0)", 207);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.objects where parent_object_id = object_id('t')"));
    }

    [TestMethod]
    public void MultiConstraintAdd_AViolatedCheckRollsTheEarlierElementBack()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int); insert t values (1, -1)");
        _ = simulation.AssertSqlError("alter table t add constraint pk3 primary key (a), constraint ck3 check (b > 0)", 547);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.objects where parent_object_id = object_id('t')"));
    }

    [TestMethod]
    public void MultiConstraintAdd_ADefaultElementIsRolledBackToo()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int not null, b int); insert t values (1, 1)");
        _ = simulation.AssertSqlError("alter table t add constraint df1 default 7 for b, constraint ckbad check (nosuch > 0)", 207);
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.default_constraints where parent_object_id = object_id('t')"));
    }

    // --- DROP PERIOD FOR SYSTEM_TIME ---

    private const string Versioned = """
        create table dbo.tt (a int primary key, b int,
          vf datetime2 generated always as row start not null,
          vt datetime2 generated always as row end not null,
          period for system_time (vf, vt))
        with (system_versioning = on (history_table = dbo.tt_hist))
        """;

    [TestMethod]
    public void DropPeriod_WhileVersioned_ReportsMsg13592()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Versioned);
        _ = simulation.AssertSqlError("alter table dbo.tt drop period for system_time", 13592);
    }

    [TestMethod]
    public void DropPeriod_AfterVersioningOff_EmptiesSysPeriods()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Versioned);
        _ = simulation.ExecuteNonQuery("alter table dbo.tt set (system_versioning = off)");
        _ = simulation.ExecuteNonQuery("alter table dbo.tt drop period for system_time");
        AreEqual(0, simulation.ExecuteScalar("select count(*) from sys.periods where object_id = object_id('dbo.tt')"));
        AreEqual((byte)0, simulation.ExecuteScalar("select temporal_type from sys.tables where object_id = object_id('dbo.tt')"));
    }

    [TestMethod]
    public void DropPeriod_LeavesThePeriodColumnsAsOrdinaryColumns()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Versioned);
        _ = simulation.ExecuteNonQuery("alter table dbo.tt set (system_versioning = off)");
        _ = simulation.ExecuteNonQuery("alter table dbo.tt drop period for system_time");
        AreEqual(0, simulation.ExecuteScalar("""
            select count(*) from sys.columns
            where object_id = object_id('dbo.tt') and generated_always_type <> 0
            """));
        AreEqual(4, simulation.ExecuteScalar("select count(*) from sys.columns where object_id = object_id('dbo.tt')"));
    }

    [TestMethod]
    public void DropPeriod_Twice_ReportsMsg13593()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Versioned);
        _ = simulation.ExecuteNonQuery("alter table dbo.tt set (system_versioning = off)");
        _ = simulation.ExecuteNonQuery("alter table dbo.tt drop period for system_time");
        _ = simulation.AssertSqlError("alter table dbo.tt drop period for system_time", 13593);
    }

    [TestMethod]
    public void DropPeriod_OnATableThatNeverHadOne_ReportsMsg13593()
        => AssertSqlError("create table t (a int); alter table t drop period for system_time", 13593);

    // --- Msg 4936: a PERSISTED computed column must be deterministic ---

    [TestMethod]
    public void PersistedComputedColumn_Nondeterministic_ReportsMsg4936AtCreate()
        => AssertSqlError(
            "create table t (a int, e as cast(getdate() as date) persisted)",
            4936,
            "Computed column 'e' in table 't' cannot be persisted because the column is non-deterministic.");

    [TestMethod]
    public void PersistedComputedColumn_Newid_ReportsMsg4936()
        => AssertSqlError("create table t (a int); alter table t add e as newid() persisted", 4936);

    [TestMethod]
    public void PersistedComputedColumn_Deterministic_IsAccepted()
        => AreEqual(1, ExecuteScalar("""
            create table t (a int, e as a * 2 persisted);
            insert t (a) values (5);
            select count(*) from t where e = 10
            """));

    [TestMethod]
    public void PersistedComputedColumn_ConvertWithADeterministicStyle_IsAccepted()
        => AreEqual(1, ExecuteScalar("""
            create table t (a datetime, e as convert(varchar(20), a, 112) persisted);
            insert t (a) values ('2026-08-06');
            select count(*) from t
            """));

    [TestMethod]
    public void PersistedComputedColumn_ConvertWithANondeterministicStyle_ReportsMsg4936()
        => AssertSqlError("create table t (a datetime, e as convert(varchar(20), a, 0) persisted)", 4936);

    [TestMethod]
    public void NonPersistedComputedColumn_MayBeNondeterministic()
        => AreEqual(1, ExecuteScalar("create table t (a int, e as cast(getdate() as date)); insert t (a) values (1); select count(*) from t"));

    // --- Msg 128: a DEFAULT expression has no column scope ---

    [TestMethod]
    public void DefaultExpression_NamingAColumn_ReportsMsg128()
        => AssertSqlError(
            "create table t (v int); insert t values (1); alter table t add w int not null default (v)",
            128,
            "The name \"v\" is not permitted in this context. Valid expressions are constants, constant expressions, and (in some contexts) variables. Column names are not permitted.");

    [TestMethod]
    public void DefaultExpression_OverAnEmptyTable_ReportsMsg128Too()
        => AssertSqlError("create table t (v int); alter table t add w int not null default (v)", 128);

    [TestMethod]
    public void DefaultExpression_NamingNothing_ReportsMsg128()
        => AssertSqlError("create table t (v int); alter table t add w int default (nosuchcol)", 128);

    [TestMethod]
    public void DefaultExpression_InlineAtCreate_ReportsMsg128()
        => AssertSqlError("create table t (v int, w int default (v))", 128);

    [TestMethod]
    public void DefaultExpression_InTheNamedConstraintForm_ReportsMsg128()
        => AssertSqlError("create table t (v int, w int); alter table t add constraint df1 default (v) for w", 128);

    [TestMethod]
    public void DefaultExpression_Subquery_ReportsMsg1046()
        => AssertSqlError("create table t (v int); alter table t add w int default ((select 1))", 1046);

    [TestMethod]
    public void DefaultExpression_AConstantExpressionIsStillFine()
        => AreEqual(7, ExecuteScalar("create table t (v int, w int default (3 + 4)); insert t (v) values (1); select w from t"));
}
