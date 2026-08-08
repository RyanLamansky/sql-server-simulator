using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>CREATE [UNIQUE] [CLUSTERED | NONCLUSTERED]
/// INDEX</c> + <c>DROP INDEX</c> + <c>sys.indexes</c> /
/// <c>sys.index_columns</c>. UNIQUE indexes enforce duplicate-key
/// rejection (Msg 2601); non-UNIQUE entries are catalog-only metadata.
/// Filter-aware uniqueness: rows excluded by an index's WHERE filter
/// don't participate in the uniqueness check. Probed wording sourced
/// from SQL Server 2025 on 2026-05-14.
/// </summary>
[TestClass]
public sealed class CreateIndexTests
{
    // --- CREATE INDEX — grammar coverage ---

    [TestMethod]
    public void BasicCreateIndex_PopulatesSysIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int);
            create index ix_a on t(a)
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and name = 'ix_a'"));
    }

    [TestMethod]
    public void CreateUniqueIndex_RejectsDuplicateInsert()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null);
            create unique index ix_a on t(a);
            insert t values (1, 10)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (2, 10)"));
        AreEqual("2601", ex.Data["HelpLink.EvtID"]);
        Contains("ix_a", ex.Message);
    }

    [TestMethod]
    public void CreateUniqueIndex_AllowsOneNullThenRejectsSecond()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int);
            create unique index ix_a on t(a);
            insert t values (1, null)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (2, null)"));
        AreEqual("2601", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void CreateNonClusteredIndex_GrammarAccepted()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            create nonclustered index ix_a on t(a);
            select count(*) from sys.indexes where object_id = object_id('t') and name = 'ix_a'
            """));

    [TestMethod]
    public void CreateClusteredIndex_GrammarAccepted()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table heap_t (id int, a int);
            create clustered index ix_a on heap_t(id);
            select count(*) from sys.indexes where object_id = object_id('heap_t') and name = 'ix_a'
            """));

    [TestMethod]
    public void CreateUniqueNonClusteredIndex_GrammarAccepted()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int not null);
            create unique nonclustered index ix_a on t(a);
            select is_unique from sys.indexes where object_id = object_id('t') and name = 'ix_a'
            """)!);

    [TestMethod]
    public void CreateIndex_MultiColumn_AscDesc()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int);
            create index ix_ab on t(a, b desc)
            """);
        IsFalse((bool)sim.ExecuteScalar("select is_descending_key from sys.index_columns ic join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id where i.name = 'ix_ab' and ic.key_ordinal = 1")!);
        IsTrue((bool)sim.ExecuteScalar("select is_descending_key from sys.index_columns ic join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id where i.name = 'ix_ab' and ic.key_ordinal = 2")!);
    }

    [TestMethod]
    public void CreateIndex_IncludeColumns_RecordedInSysIndexColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int);
            create index ix_inc on t(a) include (b)
            """);
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.index_columns ic
            join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id
            where i.name = 'ix_inc' and ic.is_included_column = 1
            """));
    }

    [TestMethod]
    public void CreateIndex_WithFilter_HasFilterFlagSet()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            create index ix_filter on t(a) where a is not null;
            select has_filter from sys.indexes where name = 'ix_filter'
            """)!);

    // sys.indexes.filter_definition normalization — every expected string is
    // verbatim from SQL Server 2025: columns bracketed, numerics parenthesized
    // (literal scale preserved), strings quoted (N-prefixed for nvarchar
    // literals), operators space-free, AND / IS [NOT] NULL / IN uppercase-spaced.
    [TestMethod]
    [DataRow("status = 1", "([status]=(1))")]
    [DataRow("status <> 1", "([status]<>(1))")]
    [DataRow("status >= 5 and status <= 10", "([status]>=(5) AND [status]<=(10))")]
    [DataRow("code is not null", "([code] IS NOT NULL)")]
    [DataRow("code is null", "([code] IS NULL)")]
    [DataRow("status = 1 and code is not null", "([status]=(1) AND [code] IS NOT NULL)")]
    [DataRow("name = 'abc'", "([name]='abc')")]
    [DataRow("uname = N'abc'", "([uname]=N'abc')")]
    [DataRow("x = -1", "([x]=(-1))")]
    [DataRow("status in (1, 2, 3)", "([status] IN ((1), (2), (3)))")]
    [DataRow("nm = 0.10", "([nm]=(0.10))")]
    [DataRow("status > 5", "([status]>(5))")]
    [DataRow("status < 5", "([status]<(5))")]
    public void CreateIndex_FilterDefinition_NormalizedLikeSqlServer(string filter, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            create table t (id int not null primary key, status int, code int,
                            name varchar(50), uname nvarchar(50), x int, nm decimal(10, 2));
            create unique index ix on t(id) where {filter};
            select filter_definition from sys.indexes where name = 'ix'
            """));

    // A predicate outside the renderable filtered grammar (OR — which a real
    // server rejects at CREATE, but the simulator's looser parser accepts):
    // has_filter stays set, filter_definition degrades to NULL rather than
    // emitting a non-canonical rendering.
    [TestMethod]
    public void CreateIndex_FilterDefinition_UnrenderablePredicate_IsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, status int);
            create unique index ix on t(id) where status = 1 or status = 2
            """);
        IsTrue((bool)sim.ExecuteScalar("select has_filter from sys.indexes where name = 'ix'")!);
        AreEqual(0, sim.ExecuteScalar("select count(filter_definition) from sys.indexes where name = 'ix'"));
    }

    [TestMethod]
    public void CreateIndex_WithOptionsClause_Accepted()
        // IGNORE_DUP_KEY is deliberately absent: it's the one option here with a
        // semantic, and real rejects it on a non-unique index (Msg 1916 — see
        // IgnoreDupKeyTests). Every other option is accepted and discarded.
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            create index ix_a on t(a) with (fillfactor = 80, pad_index = on, statistics_norecompute = off);
            select count(*) from sys.indexes where name = 'ix_a'
            """));

    // --- Filter-aware UNIQUE enforcement ---

    [TestMethod]
    public void FilteredUniqueIndex_AllowsDuplicatesWhenFilterExcludes()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, status int, code int);
            create unique index ix_active_code on t(code) where status = 1;
            insert t values (1, 0, 99), (2, 0, 99), (3, 1, 50);
            select count(*) from t
            """));

    [TestMethod]
    public void FilteredUniqueIndex_RejectsDuplicateWhenFilterMatches()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, status int, code int);
            create unique index ix_active_code on t(code) where status = 1;
            insert t values (1, 1, 50)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("insert t values (2, 1, 50)"));
        AreEqual("2601", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void FilteredUniqueIndex_UpdatesDontConflictAcrossFilterExcludedRows()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, status int, code int);
            create unique index ix_active_code on t(code) where status = 1;
            insert t values (1, 0, 50), (2, 1, 60);
            update t set code = 60 where id = 1;
            select count(*) from t where code = 60
            """));

    // --- Existing-data validation at CREATE ---

    [TestMethod]
    public void CreateUniqueIndex_WithExistingDuplicates_RaisesMsg1505()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, a int);
            insert t values (1, 10), (2, 10);
            create unique index ix_a on t(a)
            """, 1505);
        Contains("CREATE UNIQUE INDEX", ex.Message);
        Contains("ix_a", ex.Message);
    }

    [TestMethod]
    public void CreateUniqueIndex_WithFilter_AcceptsExistingDuplicatesOutsideFilter()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, status int, code int);
            insert t values (1, 0, 50), (2, 0, 50);
            create unique index ix_active_code on t(code) where status = 1;
            select count(*) from t where code = 50
            """));

    // --- Error paths at CREATE ---

    [TestMethod]
    public void CreateIndex_DuplicateName_RaisesMsg1913()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, a int);
            create index ix_a on t(a);
            create index ix_a on t(a)
            """, 1913);
        Contains("ix_a", ex.Message);
    }

    [TestMethod]
    public void CreateIndex_DuplicateNameMatchingPrimaryKey_RaisesMsg1913()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null constraint pk_t primary key, a int)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("create index pk_t on t(a)"));
        AreEqual("1913", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void CreateIndex_MissingTable_RaisesMsg1088()
        => _ = new Simulation().AssertSqlError("create index ix_a on missing_table(a)", 1088);

    // --- INCLUDE on a clustered index — Msg 10601 ---

    [TestMethod]
    public void CreateClusteredIndex_WithIncludeList_RaisesMsg10601()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null, a int, b int);
            create clustered index ix_a on t(a) include (b)
            """, 10601);
        AreEqual("Cannot specify included columns for a clustered index.", ex.Message);
        AreEqual(1, ex.State);
    }

    [TestMethod]
    public void CreateClusteredIndex_WithIncludeList_RaisesAheadOfMissingTable()
        => _ = new Simulation().AssertSqlError("create clustered index ix_a on missing_table(a) include (b)", 10601);

    [TestMethod]
    public void CreateClusteredIndex_WithIncludeList_RaisesAheadOfMissingColumn()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, a int);
            create clustered index ix_a on t(a) include (missing_col)
            """, 10601);

    [TestMethod]
    public void CreateClusteredIndex_WithIncludeListAndIgnoreDupKey_ReportsIncludeFirst()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, a int, b int);
            create clustered index ix_a on t(a) include (b) with (ignore_dup_key = on)
            """, 10601);

    [TestMethod]
    public void CreateUniqueClusteredIndexOnView_WithIncludeList_RaisesMsg10601()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int not null, a int)",
            "create view v with schemabinding as select id, a from dbo.t");
        _ = sim.AssertSqlError("create unique clustered index ix_v on v(id) include (a)", 10601);
    }

    [TestMethod]
    public void CreateNonClusteredIndex_WithIncludeList_StillAccepted()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null, a int, b int);
            create index ix_a on t(a) include (b);
            select count(*) from sys.index_columns ic
                join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id
                where i.name = 'ix_a' and ic.is_included_column = 1
            """));

    [TestMethod]
    public void CreateIndex_MissingColumn_RaisesMsg1911()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, a int);
            create index ix_x on t(missing_col)
            """, 1911);

    // --- DROP INDEX ---

    [TestMethod]
    public void DropIndex_RemovesFromSysIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int);
            create index ix_a on t(a);
            drop index ix_a on t
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix_a'"));
    }

    [TestMethod]
    public void DropIndex_AllowsCommaList()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int);
            create index ix_a on t(a);
            create index ix_b on t(b);
            drop index ix_a on t, ix_b on t
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where name in ('ix_a', 'ix_b')"));
    }

    [TestMethod]
    public void DropIndex_MissingIndex_RaisesMsg3701()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            drop index ix_missing on t
            """, 3701);

    [TestMethod]
    public void DropIndex_MissingTable_RaisesMsg3701()
        => _ = new Simulation().AssertSqlError("drop index ix_x on missing_table", 3701);

    [TestMethod]
    public void DropIndex_IfExists_MissingIndex_Silent()
        => _ = new Simulation().ExecuteNonQuery("""
            create table t (id int not null primary key);
            drop index if exists ix_missing on t
            """);

    [TestMethod]
    public void DropIndex_IfExists_MissingTable_Silent()
        => _ = new Simulation().ExecuteNonQuery("drop index if exists ix_x on missing_table");

    [TestMethod]
    public void DropIndex_OnPrimaryKey_RaisesMsg3723()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null constraint pk_t primary key)");
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("drop index pk_t on t"));
        AreEqual("3723", ex.Data["HelpLink.EvtID"]);
        Contains("PRIMARY KEY constraint enforcement", ex.Message);
    }

    [TestMethod]
    public void DropIndex_OnUniqueConstraint_RaisesMsg3723()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, constraint uq_a unique (a))
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("drop index uq_a on t"));
        AreEqual("3723", ex.Data["HelpLink.EvtID"]);
        Contains("UNIQUE constraint enforcement", ex.Message);
    }

    // --- sys.indexes catalog shape ---

    [TestMethod]
    public void SysIndexes_TableWithoutPk_EmitsHeapRow()
        => AreEqual("HEAP", new Simulation().ExecuteScalar("""
            create table t (id int, a int);
            select type_desc from sys.indexes where object_id = object_id('t') and index_id = 0
            """));

    [TestMethod]
    public void SysIndexes_TableWithPk_EmitsClusteredRow()
        => AreEqual("CLUSTERED", new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            select type_desc from sys.indexes where object_id = object_id('t') and index_id = 1
            """));

    [TestMethod]
    public void SysIndexes_PkRowHasIsPrimaryKey()
        => IsTrue((bool)new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            select is_primary_key from sys.indexes where object_id = object_id('t') and index_id = 1
            """)!);

    [TestMethod]
    public void SysIndexes_UniqueConstraintShowsAsUniqueConstraint()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, constraint uq_a unique (a))
            """);
        IsTrue((bool)sim.ExecuteScalar("select is_unique_constraint from sys.indexes where name = 'uq_a'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_unique from sys.indexes where name = 'uq_a'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_primary_key from sys.indexes where name = 'uq_a'")!);
    }

    [TestMethod]
    public void SysIndexes_UniqueIndexShowsAsUniqueButNotUniqueConstraint()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int);
            create unique index ix_a on t(a)
            """);
        IsTrue((bool)sim.ExecuteScalar("select is_unique from sys.indexes where name = 'ix_a'")!);
        IsFalse((bool)sim.ExecuteScalar("select is_unique_constraint from sys.indexes where name = 'ix_a'")!);
    }

    [TestMethod]
    public void SysIndexes_IndexIdAssignment_PkFirstThenUserIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int, b int);
            create index ix_a on t(a);
            create index ix_b on t(b)
            """);
        AreEqual(2, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_a'"));
        AreEqual(3, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_b'"));
    }

    [TestMethod]
    public void SysIndexes_ClusteredIndexOnHeap_TakesId1AndSuppressesHeapRow()
    {
        // A CREATE CLUSTERED INDEX on a heap (no PK) occupies index_id 1 with
        // type 1 / CLUSTERED and removes the heap row — exactly one sys.indexes
        // row. This is the Application.PaymentMethods_Archive shape that broke
        // DacFx's SqlTable query (which saw a duplicate heap row). Probe-
        // confirmed against SQL Server 2025.
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int, b int)",
            "create clustered index ix_c on t(a)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t')"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and index_id = 0"));
        AreEqual(1, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_c'"));
        AreEqual((byte)1, sim.ExecuteScalar("select type from sys.indexes where name = 'ix_c'"));
        AreEqual("CLUSTERED", sim.ExecuteScalar("select type_desc from sys.indexes where name = 'ix_c'"));
    }

    [TestMethod]
    public void SysIndexes_HeapWithNonclusteredIndexes_StartAtId2()
    {
        // On a heap the nonclustered index_ids start at 2 — index_id 1 (the
        // clustered slot) is never reused. Probe-confirmed against SQL Server 2025.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, b int);
            create index ix_a on t(a);
            create index ix_b on t(b)
            """);
        AreEqual("HEAP", sim.ExecuteScalar("select type_desc from sys.indexes where object_id = object_id('t') and index_id = 0"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and index_id = 1"));
        AreEqual(2, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_a'"));
        AreEqual(3, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_b'"));
    }

    [TestMethod]
    public void SysIndexes_ClusteredIndexAfterNonclustered_TakesId1KeepsOthers()
    {
        // The clustered index is always index_id 1 regardless of creation order;
        // pre-existing nonclustered indexes keep their 2..N ids. Probe-confirmed.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int, b int, c int);
            create index ix_a on t(a);
            create index ix_b on t(b);
            create clustered index ix_c on t(c)
            """);
        AreEqual(1, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_c'"));
        AreEqual(2, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_a'"));
        AreEqual(3, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_b'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and index_id = 0"));
    }

    [TestMethod]
    public void SysIndexes_NonclusteredPrimaryKey_StaysHeapAndPkIsNonclustered()
    {
        // PRIMARY KEY NONCLUSTERED leaves the table a heap; the PK is a
        // nonclustered index at index_id >= 2. Probe-confirmed against SQL
        // Server 2025 (PK at id 2, ix_b at id 3, heap row at 0).
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int not null primary key nonclustered, b int);
            create index ix_b on t(b)
            """);
        AreEqual("HEAP", sim.ExecuteScalar("select type_desc from sys.indexes where object_id = object_id('t') and index_id = 0"));
        AreEqual(2, sim.ExecuteScalar("select index_id from sys.indexes where object_id = object_id('t') and is_primary_key = 1"));
        AreEqual("NONCLUSTERED", sim.ExecuteScalar("select type_desc from sys.indexes where object_id = object_id('t') and is_primary_key = 1"));
        AreEqual(3, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_b'"));
        AreEqual(0, sim.ExecuteScalar("select indexproperty(object_id('t'), (select name from sys.indexes where object_id = object_id('t') and is_primary_key = 1), 'IsClustered')"));
    }

    [TestMethod]
    public void SysIndexes_UniqueClusteredConstraint_TakesId1AndSuppressesHeap()
    {
        // A UNIQUE CLUSTERED constraint occupies the clustered slot (index_id 1,
        // type 1) and suppresses the heap row. Probe-confirmed against SQL
        // Server 2025.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int not null, b int, constraint uq_a unique clustered (a));
            create index ix_b on t(b)
            """);
        AreEqual(1, sim.ExecuteScalar("select index_id from sys.indexes where name = 'uq_a'"));
        AreEqual("CLUSTERED", sim.ExecuteScalar("select type_desc from sys.indexes where name = 'uq_a'"));
        IsTrue((bool)sim.ExecuteScalar("select is_unique_constraint from sys.indexes where name = 'uq_a'")!);
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.indexes where object_id = object_id('t') and index_id = 0"));
        AreEqual(2, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_b'"));
    }

    [TestMethod]
    public void SysIndexColumns_OnlyIncludesNonHeapEntries()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int, a int);
            select count(*) from sys.index_columns where object_id = object_id('t')
            """));

    [TestMethod]
    public void SysIndexColumns_KeyOrdinalForIncludeIsZero()
        => AreEqual((byte)0, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int, b int);
            create index ix_inc on t(a) include (b);
            select key_ordinal from sys.index_columns ic
            join sys.indexes i on i.object_id = ic.object_id and i.index_id = ic.index_id
            where i.name = 'ix_inc' and ic.is_included_column = 1
            """));

    // --- Update-time UNIQUE INDEX enforcement ---

    [TestMethod]
    public void UpdateThatCausesDuplicate_RaisesMsg2601()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int);
            create unique index ix_a on t(a);
            insert t values (1, 10), (2, 20)
            """);
        var ex = Throws<DbException>(() => sim.ExecuteNonQuery("update t set a = 10 where id = 2"));
        AreEqual("2601", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Update_ShiftKey_DoesntFalseTrigger()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            create unique index ix_a on t(a);
            insert t values (1, 10), (2, 20), (3, 30);
            update t set id = id + 100;
            select count(*) from t
            """));

    // --- SSMS-emitted index-options and filegroup trailers ---
    //
    // SSMS scripts every CREATE / ALTER constraint and CREATE INDEX with the
    // full storage-tuning option set (PAD_INDEX / IGNORE_DUP_KEY / ONLINE /
    // ALLOW_ROW_LOCKS / ALLOW_PAGE_LOCKS / etc.) plus an `ON [PRIMARY]`
    // filegroup placement. The simulator has no B-tree storage and no
    // filegroup model, so both trailers parse-and-discard. Probed against a
    // generated starting-database script on 2026-05-17 (1.6 MB, 17 K lines,
    // all 700 `ON [PRIMARY]` occurrences).

    [TestMethod]
    public void CreateIndex_WithFullOptionsAndOnPrimary_Accepted()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            create nonclustered index ix_a on t(a)
              with (pad_index = off, statistics_norecompute = off, sort_in_tempdb = off,
                    drop_existing = off, online = off,
                    allow_row_locks = on, allow_page_locks = on)
              on [primary];
            select count(*) from sys.indexes where name = 'ix_a'
            """));

    [TestMethod]
    public void CreateTable_TableLevelPkClustered_WithOptionsAndOnPrimary_Accepted()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table [dbo].[ac](
                [id] [uniqueidentifier] not null,
                [name] [nvarchar](100) not null,
                constraint [pk_ac] primary key clustered ([id] asc)
                    with (pad_index = off, statistics_norecompute = off,
                          ignore_dup_key = off,
                          allow_row_locks = on, allow_page_locks = on) on [primary]
            ) on [primary];
            select count(*) from [dbo].[ac]
            """));

    [TestMethod]
    public void CreateTable_TextImageOnPrimary_Accepted()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (
                id int not null primary key,
                blob varbinary(max) null
            ) on [primary] textimage_on [primary];
            select count(*) from t
            """));

    [TestMethod]
    public void AlterTableAddConstraintUnique_WithOptionsAndOnPrimary_Accepted()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, code nvarchar(50) not null);
            alter table t add constraint ux_code unique nonclustered (code asc)
              with (pad_index = off, statistics_norecompute = off, sort_in_tempdb = off,
                    ignore_dup_key = off, online = off,
                    allow_row_locks = on, allow_page_locks = on) on [primary];
            select count(*) from sys.indexes where name = 'ux_code'
            """));

    [TestMethod]
    public void AppDictSchema_RealCreateTableAndDefault_BothAccepted()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create schema appdict;
            create table [appdict].[adminactionconfiguration](
                [id] [uniqueidentifier] not null,
                [formname] [nvarchar](256) not null,
                constraint [pk_aac] primary key clustered ([id] asc)
                    with (pad_index = off, ignore_dup_key = off,
                          allow_row_locks = on, allow_page_locks = on) on [primary]
            ) on [primary];
            alter table [appdict].[adminactionconfiguration]
              add constraint [df_aac_id] default (newsequentialid()) for [id];
            select count(*) from [appdict].[adminactionconfiguration]
            """));

    // ----- One clustered index per table (Msg 1902) -----

    [TestMethod]
    public void CreateClusteredIndex_WhenPrimaryKeyAlreadyClustered_RaisesMsg1902()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (id int not null primary key, a int);
            create clustered index ix on t (a)
            """, 1902);
        Assert.Contains("more than one clustered index", ex.Message);
    }

    [TestMethod]
    public void CreateSecondClusteredIndex_RaisesMsg1902()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, a int);
            create clustered index ix1 on t (id);
            create clustered index ix2 on t (a)
            """, 1902);

    [TestMethod]
    public void CreateNonclusteredIndex_AlongsidePrimaryKey_Succeeds()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            create index ix on t (a);
            select count(*) from t
            """));

    // ----- Deprecated two-part DROP INDEX table.index form -----

    [TestMethod]
    public void DropIndex_TwoPartForm_DropsExistingIndex()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int not null, a int);
            create index ix on t (a);
            drop index t.ix;
            select count(*) from sys.indexes where name = 'ix'
            """));

    [TestMethod]
    public void DropIndex_TwoPartForm_MissingIndex_RaisesMsg3701()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null, a int);
            drop index t.nope
            """, 3701);

    /// <summary>
    /// A unique index keys on a non-persisted computed column and enforces it —
    /// the value is evaluated per row, since the column has no storage slot.
    /// AdventureWorks' <c>AK_SalesOrderHeader_SalesOrderNumber</c> is the shape.
    /// Probed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void UniqueIndex_OnNonPersistedComputed_Enforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, b int not null, c as a + b);
            create unique index ix_t on t (c);
            insert t (id, a, b) values (1, 1, 2)
            """);
        var ex = sim.AssertSqlError("insert t (id, a, b) values (2, 2, 1)", 2601);
        Assert.Contains("unique index 'ix_t'", ex.Message);
        Assert.Contains("The duplicate key value is (3)", ex.Message);
    }

    /// <summary>A composite key mixing a stored and a computed column reports both components.</summary>
    [TestMethod]
    public void UniqueIndex_CompositeStoredAndComputedKey_Enforced()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, g int not null, a int not null, c as a + 1);
            create unique index ix_t on t (g, c);
            insert t values (1, 1, 10), (2, 2, 10)
            """);
        var ex = sim.AssertSqlError("insert t values (3, 1, 10)", 2601);
        Assert.Contains("The duplicate key value is (1, 11)", ex.Message);
    }

    /// <summary>Two NULL computed keys collide, the same NULLs-equal rule a stored key follows.</summary>
    [TestMethod]
    public void UniqueIndex_OnNonPersistedComputed_NullKeysCollide()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int null, c as a + 1);
            create unique index ix_t on t (c);
            insert t (id, a) values (1, null)
            """);
        var ex = sim.AssertSqlError("insert t (id, a) values (2, null)", 2601);
        Assert.Contains("The duplicate key value is (<NULL>)", ex.Message);
    }

    /// <summary>An UPDATE that moves the computed key onto another row's is refused; a standing key isn't.</summary>
    [TestMethod]
    public void UniqueIndex_OnNonPersistedComputed_UpdateIntoCollision_Raises2601()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, c as a + 1);
            create unique index ix_t on t (c);
            insert t values (1, 10), (2, 20)
            """);
        _ = sim.AssertSqlError("update t set a = 10 where id = 2", 2601);
        _ = sim.ExecuteNonQuery("update t set a = a where id = 2");
    }

    /// <summary>Existing duplicate data blocks the CREATE, naming the computed value.</summary>
    [TestMethod]
    public void UniqueIndex_OnNonPersistedComputed_ExistingDuplicate_Raises1505()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, c as a + 1);
            insert t values (1, 5), (2, 5)
            """);
        var ex = sim.AssertSqlError("create unique index ix_t on t (c)", 1505);
        Assert.Contains("The duplicate key value is (6)", ex.Message);
    }

    /// <summary>
    /// Real's two preconditions for any index or statistics key naming a
    /// non-persisted computed column: deterministic (Msg 2729) and precise
    /// (Msg 2799). Both gate a non-unique index and CREATE STATISTICS too, and
    /// a persisted column takes neither.
    /// </summary>
    [TestMethod]
    [DataRow("create index ix_t on t (cn)", 2729)]
    [DataRow("create unique index ix_t on t (cn)", 2729)]
    [DataRow("create statistics st_t on t (cn)", 2729)]
    [DataRow("create index ix_t on t (cf)", 2799)]
    [DataRow("create unique index ix_t on t (cf)", 2799)]
    [DataRow("create statistics st_t on t (cf)", 2799)]
    public void ComputedKeyColumn_NotIndexable_Raises(string statement, int expectedNumber)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (i int not null, f float not null, cn as getdate(), cf as f * 2)");
        _ = sim.AssertSqlError(statement, expectedNumber);
    }

    /// <summary>
    /// Imprecision reaches any <c>float</c> / <c>real</c> in the expression, not
    /// just the column's own type — a narrowing CAST over one doesn't launder it.
    /// </summary>
    [TestMethod]
    [DataRow("cast(f as int)")]
    [DataRow("cast(sqrt(i) as int)")]
    [DataRow("convert(int, convert(float, i))")]
    [DataRow("i + cast(1.5e0 as int)")]
    [DataRow("r + 1")]
    public void ComputedKeyColumn_ImpreciseThroughANarrowingCast_Raises2799(string expression)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (i int not null, f float not null, r real not null, c as {expression})");
        _ = sim.AssertSqlError("create index ix_t on t (c)", 2799);
    }

    /// <summary>
    /// A decimal-only expression is precise, and a persisted column skips the
    /// precision gate outright — its value is stored, so nothing is re-evaluated.
    /// (A persisted <em>nondeterministic</em> column can't exist in the first
    /// place: PERSISTED itself refuses one with Msg 4936.)
    /// </summary>
    [TestMethod]
    [DataRow("create table t (d decimal(10, 2) not null, c as d * 2)")]
    [DataRow("create table t (f float not null, c as f * 2 persisted)")]
    public void ComputedKeyColumn_Indexable_Succeeds(string createTable)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(createTable);
        _ = sim.ExecuteNonQuery("create index ix_t on t (c)");
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix_t'"));
    }

    /// <summary>
    /// Real refuses a filtered index whose predicate reads a computed column,
    /// <b>persisted or not</b> — deciding a row's membership means evaluating
    /// the predicate, and it won't key an index's contents on a value it
    /// re-derives. Probed against SQL Server 2025; the simulator accepting one
    /// was the over-permissive direction, and its filter evaluation read the
    /// non-persisted slot as NULL, so every such row fell outside the filter.
    /// </summary>
    [TestMethod]
    [DataRow("c as a + 1", "c > 5")]
    [DataRow("c as a + 1 persisted", "c > 5")]
    [DataRow("c as a + 1", "a > 1 and c in (2, 3)")]
    public void FilteredIndex_PredicateReadsAComputedColumn_Raises10609(string computed, string predicate)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"create table t (id int not null primary key, a int not null, {computed})");
        sim.AssertSqlError(
            $"create index ix_t on t (a) where {predicate}",
            10609,
            "Filtered index 'ix_t' cannot be created on table 'dbo.t' because the column 'c' in the filter expression is a computed column. Rewrite the filter expression so that it does not include this column.");
    }

    /// <summary>A predicate over ordinary columns is unaffected.</summary>
    [TestMethod]
    public void FilteredIndex_PredicateOverStoredColumns_Succeeds()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, c as a + 1);
            create index ix_t on t (a) where a > 5
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.indexes where name = 'ix_t'"));
    }

    /// <summary>
    /// Error order, probe-confirmed both ways: Msg 10609 outranks the duplicate
    /// index name, and the IGNORE_DUP_KEY refusal outranks Msg 10609.
    /// </summary>
    [TestMethod]
    public void FilteredIndex_ComputedColumnErrorOrder()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, c as a + 1);
            create index dup on t (id)
            """);
        _ = sim.AssertSqlError("create index dup on t (a) where c > 5", 10609);
        _ = sim.AssertSqlError("create unique index ix2 on t (a) where c > 5 with (ignore_dup_key = on)", 10618);
    }

    /// <summary>Msg 10618 names the table two-part, with its own schema.</summary>
    [TestMethod]
    public void FilteredIndex_IgnoreDupKey_NamesTheTableTwoPart()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create schema sx",
            "create table sx.t (id int not null primary key, a int not null)");
        var ex = sim.AssertSqlError("create unique index ix_t on sx.t (a) where a > 5 with (ignore_dup_key = on)", 10618);
        Assert.Contains("on table 'sx.t'", ex.Message);
    }
}
