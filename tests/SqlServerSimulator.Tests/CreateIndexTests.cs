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
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("insert t values (2, 10)"));
        AreEqual(2601, ex.Number);
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
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("insert t values (2, null)"));
        AreEqual(2601, ex.Number);
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
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("insert t values (2, 1, 50)"));
        AreEqual(2601, ex.Number);
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
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("create index pk_t on t(a)"));
        AreEqual(1913, ex.Number);
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
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("drop index pk_t on t"));
        AreEqual(3723, ex.Number);
        Contains("PRIMARY KEY constraint enforcement", ex.Message);
    }

    [TestMethod]
    public void DropIndex_OnUniqueConstraint_RaisesMsg3723()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int not null, constraint uq_a unique (a))
            """);
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("drop index uq_a on t"));
        AreEqual(3723, ex.Number);
        Contains("UNIQUE KEY constraint enforcement", ex.Message);
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
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteNonQuery("update t set a = 10 where id = 2"));
        AreEqual(2601, ex.Number);
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
        => AreEqual(0, new Simulation().WithSchemas("appdict").ExecuteScalar("""
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
            "Filtered index 'ix_t' cannot be created on table 't' because the column 'c' in the filter expression is a computed column. Rewrite the filter expression so that it does not include this column.");
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

    // ---- the filtered-index predicate grammar (probed 2026-09-24 against SQL Server 2025) ----

    private const string FilterTable = "create table fx (a int, s varchar(10), b int, d date);";

    [TestMethod]
    [DataRow("s like 'a%'", "like")]
    [DataRow("a = 1 or a = 2", "or")]
    [DataRow("not a = 1", "not")]
    [DataRow("a between 1 and 5", "between")]
    [DataRow("exists (select 1)", "exists")]
    public void FilteredIndex_RefusedConnective_RaisesMsg156(string predicate, string keyword)
        => new Simulation().AssertSqlError($"{FilterTable} create index ix on fx(a) where {predicate}", 156, $"Incorrect syntax near the keyword '{keyword}'.");

    [TestMethod]
    public void FilteredIndex_NotIn_RaisesMsg102NearNot()
        => new Simulation().AssertSqlError($"{FilterTable} create index ix on fx(a) where a not in (1, 2)", 102, "Incorrect syntax near 'NOT'.");

    [TestMethod]
    [DataRow("a = b")]
    [DataRow("1 = a")]
    [DataRow("a + 1 = 2")]
    [DataRow("a = abs(1)")]
    [DataRow("a = @@spid")]
    public void FilteredIndex_NonConstantComparison_RaisesMsg10735(string predicate)
        => new Simulation().AssertSqlError($"{FilterTable} create index ix on fx(a) where {predicate}", 10735, "Incorrect WHERE clause for filtered index 'ix' on table 'fx'.");

    [TestMethod]
    [DataRow("a = 1 and b > 2", "([a]=(1) AND [b]>(2))")]
    [DataRow("a in (1, 2)", "([a] IN ((1), (2)))")]
    [DataRow("a is null and s = 'x'", "([a] IS NULL AND [s]='x')")]
    [DataRow("a > 1 and (b = 2)", "([a]>(1) AND [b]=(2))")]
    [DataRow("a <> 1", "([a]<>(1))")]
    [DataRow("a = -1", "([a]=(-1))")]
    [DataRow("d > '2020-01-01'", "([d]>'2020-01-01')")]
    [DataRow("a = 5000000000", "([a]=(5000000000.))")]
    [DataRow("a = 10.", "([a]=(10.))")]
    [DataRow("a = cast(10 as int)", "([a]=CONVERT([int],(10)))")]
    [DataRow("a = 0x0A", "([a]=0x0A)")]
    [DataRow("a = $5", "([a]=($5.0000))")]
    [DataRow("a = .5", "([a]=(0.5))")]
    [DataRow("a in (10)", "([a]=(10))")]
    [DataRow("a !< 10", "([a]>=(10))")]
    public void FilteredIndex_AcceptedShape_StoresItsDefinition(string predicate, string definition)
        => AreEqual(definition, new Simulation().ExecuteScalar($"{FilterTable} create index ix on fx(a) where {predicate}; select filter_definition from sys.indexes where name = 'ix'"));

    /// <summary>
    /// An index declared inline in <c>CREATE TABLE</c> takes the standalone
    /// grammar — <c>UNIQUE</c>, clustering, <c>INCLUDE</c> (table level only),
    /// a filter, <c>WITH</c> and <c>ON</c> — and enforces what it declares
    /// (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void InlineIndex_TakesTheStandaloneGrammar()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, v int, w int, index ix unique nonclustered (v) include (w) where v > 0 with (fillfactor = 90) on [primary])");
        AreEqual("ix|1|1|([v]>(0))|1", sim.ExecuteScalar("""
            select concat_ws('|', i.name, i.is_unique, i.has_filter, i.filter_definition,
                (select count(*) from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id and ic.is_included_column = 1))
            from sys.indexes i where i.object_id = object_id('t') and i.name = 'ix'
            """));
        _ = sim.ExecuteNonQuery("insert t values (1, -1, 1), (2, -1, 2)");
        _ = sim.AssertSqlError("insert t values (3, 1, 1), (4, 1, 2)", 2601);
    }

    [TestMethod]
    public void ColumnLevelInlineIndex_TakesUnique()
        => AreEqual("ix|1|0", new Simulation().ExecuteScalar("""
            create table t (id int, v int index ix unique clustered with (fillfactor = 80) on [primary]);
            select concat_ws('|', name, is_unique, (select count(*) from sys.key_constraints where parent_object_id = object_id('t'))) from sys.indexes where object_id = object_id('t') and name is not null
            """));

    [TestMethod]
    [DataRow("create table t (id int, v int, index ix clustered (v) include (id))", 10601, "Cannot specify included columns for a clustered index.")]
    [DataRow("create table t (id int, v int, index ix (v) with (ignore_dup_key = on))", 1916, "CREATE INDEX options nonunique and ignore_dup_key are mutually exclusive.")]
    [DataRow("create table t (id int, v int, index ix (v, V))", 1909, "Cannot use duplicate column names in index. Column name 'V' listed more than once.")]
    [DataRow("create table t (id int, w int, v int index ix include (w))", 102, "Incorrect syntax near 'include'.")]
    [DataRow("create table t (id int primary key) with (data_compression = page on partitions (1))", 7729, "Cannot specify partition number in the create index statement as the index '' is not partitioned.")]
    public void InlineIndex_Refusals(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);

    [TestMethod]
    [DataRow("create index ix on t (id) include (id)", "id", 2)]
    [DataRow("create index ix on t (id, ID)", "ID", 1)]
    [DataRow("create index ix on t (id) include (v, v)", "v", 2)]
    public void DuplicateIndexColumn_RaisesMsg1909(string create, string column, int state)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, v int)");
        var error = sim.AssertSqlError(create, 1909);
        AreEqual($"Cannot use duplicate column names in index. Column name '{column}' listed more than once.", error.Errors[0].Message);
        AreEqual((byte)state, error.Errors[0].State);
    }

    [TestMethod]
    [DataRow("with (data_compression = page)")]
    [DataRow("on [primary] with (data_compression = row)")]
    [DataRow("with (data_compression = none, xml_compression = off)")]
    public void TableStorageOptions_AreAccepted(string options)
        => AreEqual(1, new Simulation().ExecuteScalar($"create table t (id int primary key, x xml) {options}; insert t values (1, null); select count(*) from t"));

    /// <summary>
    /// A table variable and a table type take inline indexes, enforcing a
    /// unique one per variable (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void InlineIndex_OnATableVariable_Enforces()
        => new Simulation().AssertSqlError(
            "declare @t table (id int, v int, index ix unique (v) include (id) where v > 0); insert @t values (1, 1), (2, 1)",
            2601,
            "Cannot insert duplicate key row in object 'dbo.@t' with unique index 'ix'. The duplicate key value is (1).");

    [TestMethod]
    public void InlineIndex_OnATableType_HoldsPerInstance()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create type dbo.tt as table (id int primary key, v int, index ix unique (v))");
        AreEqual(2, sim.ExecuteScalar("declare @a dbo.tt, @b dbo.tt; insert @a values (1, 1); insert @b values (1, 1); select (select count(*) from @a) + (select count(*) from @b)"));
        _ = sim.AssertSqlError("declare @a dbo.tt; insert @a values (1, 1), (2, 1)", 2601);
    }

    /// <summary>
    /// <c>DROP_EXISTING = ON</c> replaces the named index, keeping its index_id;
    /// a constraint's index may be recreated only as the index it enforces.
    /// Probed 2026-09-26 against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("create index ix on t (a, b) with (drop_existing = on)", "pk:1:1,ix:2:0:a|b,ux:3:1:b")]
    [DataRow("create unique index ix on t (b) with (drop_existing = on)", "pk:1:1,ix:2:1:b,ux:3:1:b")]
    [DataRow("create index ux on t (b) with (drop_existing = on)", "pk:1:1,ix:2:0:a,ux:3:0:b")]
    [DataRow("create unique clustered index pk on t (id) with (drop_existing = on, fillfactor = 70)", "pk:1:1,ix:2:0:a,ux:3:1:b")]
    public void DropExisting_ReplacesInPlace(string ddl, string expected)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int not null constraint pk primary key, a int, b int); create index ix on t (a); create unique index ux on t (b)");
        _ = simulation.ExecuteNonQuery(ddl);
        AreEqual(expected, simulation.ExecuteScalar("""
            select string_agg(concat(i.name, ':', i.index_id, ':', cast(i.is_unique as int), case when i.name = 'pk' then '' else ':' + c.cols end), ',') within group (order by i.index_id)
            from sys.indexes i
            cross apply (select string_agg(col_name(ic.object_id, ic.column_id), '|') within group (order by ic.key_ordinal) cols
                from sys.index_columns ic where ic.object_id = i.object_id and ic.index_id = i.index_id) c
            where i.object_id = object_id('t')
            """));
    }

    [TestMethod]
    [DataRow("create index nope on t (a) with (drop_existing = on)", 7999, 9)]
    [DataRow("create index ix on t (a) with (drop_existing = off)", 1913, 1)]
    [DataRow("create clustered index ix on t (a) with (drop_existing = on)", 1902, 3)]
    [DataRow("create unique clustered index pk on t (id, a) with (drop_existing = on)", 1907, 2)]
    [DataRow("create unique nonclustered index pk on t (id) with (drop_existing = on)", 1925, 2)]
    public void DropExisting_RefusesAsRealDoes(string ddl, int number, int state)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (id int not null constraint pk primary key, a int, b int); create index ix on t (a)");
        AreEqual((byte)state, simulation.AssertSqlError(ddl, number).State);
    }

    /// <summary>
    /// A declaration's keys and inline indexes take object — so index — ids in
    /// one sequence: the clustered one first, then the rest in reverse
    /// declaration order (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create table t (a int unique, b int, index ix (b))", "ix:2,UQ:3")]
    [DataRow("create table t (b int, index ix (b), a int unique)", "UQ:2,ix:3")]
    [DataRow("create table t (k int primary key nonclustered, u int unique, v int, index ix (v))", "ix:2,UQ:3,PK:4")]
    [DataRow("create table t (a int unique, b int unique, c int, index i1 (c), d int, index i2 (d))", "i2:2,i1:3,UQ:4,UQ:5")]
    [DataRow("create table t (a int unique, b int index ib)", "ib:2,UQ:3")]
    public void InlineIndexes_InterleaveWithKeysInIdOrder(string ddl, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"""
            {ddl};
            select string_agg(concat(left(name, 2) + case when name like '%[_][_]%' then '' else substring(name, 3, 10) end, ':', index_id), ',') within group (order by index_id)
            from sys.indexes where object_id = object_id('t') and index_id > 0
            """));

    /// <summary>
    /// <c>STATISTICS_ONLY</c>, any value and either option form, makes the
    /// index hypothetical: listed with its statistic, never built, its
    /// uniqueness unenforced (probed 2026-10-02).
    /// </summary>
    [TestMethod]
    public void StatisticsOnly_MakesAHypotheticalIndex()
        => AreEqual("ixh:2:1:0|ixu:3:1:0|2", new Simulation().ExecuteScalar("""
            create table t (id int, v int);
            create index ixh on t (id) with statistics_only = -1;
            create unique index ixu on t (v) with (statistics_only = 0);
            insert t values (1, 1), (2, 1);
            select string_agg(concat(name, ':', index_id, ':', cast(is_hypothetical as int), ':', data_space_id), '|') within group (order by index_id)
                + '|' + cast((select count(*) from sys.stats where object_id = object_id('t')) as varchar)
            from sys.indexes where object_id = object_id('t') and index_id > 0
            """));

    [TestMethod]
    public void HypotheticalIndex_IsNoIndexHintTarget()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int);
            create index ixh on t (id) with statistics_only = -1;
            select * from t with (index (ixh))
            """, 308);

    [TestMethod]
    public void HypotheticalIndex_HelpIndexAndProperty()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int); create unique index ixh on t (id) with statistics_only = -1, fillfactor = 50");
        AreEqual(1, sim.ExecuteScalar("select indexproperty(object_id('t'), 'ixh', 'IsHypothetical')"));
        using var reader = sim.ExecuteReader("exec sp_helpindex 't'");
        IsTrue(reader.Read());
        AreEqual("nonclustered, unique, hypothetical", reader.GetString(1));
    }

    [TestMethod]
    public void HypotheticalIndex_DropsAndTakesItsName()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int);
            create index ixh on t (id) with statistics_only = -1;
            alter index ixh on t rebuild;
            drop index ixh on t;
            select count(*) from sys.indexes where object_id = object_id('t') and index_id > 0
            """));

    private const string OptionTable = """
        create table t (id int not null, a int null, b varchar(20) null);
        insert t values (3, 30, 'c'), (1, 10, 'a'), (2, 20, 'b');
        """;

    /// <summary>
    /// The index-option values and combinations real refuses (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create index ix on t (a) with (fillfactor = '50')", 102)]
    [DataRow("create index ix on t (a) with (fillfactor = 50.5)", 1080)]
    [DataRow("create index ix on t (a) with (pad_index = 1)", 153)]
    [DataRow("create index ix on t (a) with (maxdop = -1)", 304)]
    [DataRow("create index ix on t (a) with (data_compression = columnstore)", 10798)]
    [DataRow("create index ix on t (a) with (data_compression = xpress)", 102)]
    [DataRow("create index ix on t (a) with (data_compression = row, data_compression = page)", 7711)]
    [DataRow("create index ix on t (a) with (data_compression = row on partitions (1))", 7729)]
    [DataRow("create index ix on t (a) with (statistics_incremental = on)", 9108)]
    [DataRow("create index ix on t (a) with (online = off (wait_at_low_priority (max_duration = 1, abort_after_wait = self)))", 155)]
    [DataRow("create index ix on t (a) with (drop_existing = on)", 7999)]
    [DataRow("create index ix on t (a) with allow_dup_row", 1070)]
    [DataRow("create unique index ix on t (id) with online = on", 156)]
    [DataRow("create index ix on t (a) filestream_on [PRIMARY]", 1716)]
    public void IndexOptions_Refusals(string statement, int number)
        => _ = new Simulation().AssertSqlError(OptionTable + statement, number);

    [TestMethod]
    public void IndexOptions_LegacyBareForms_AreTaken()
        => AreEqual(70, new Simulation().ExecuteScalar(OptionTable + """
            create index ix on t (a) with pad_index, fillfactor = 70, sort_in_tempdb, statistics_norecompute;
            select cast(fill_factor as int) from sys.indexes where object_id = object_id('t') and name = 'ix'
            """));

    /// <summary>
    /// <c>DATA_COMPRESSION</c> and <c>XML_COMPRESSION</c> are recorded on the
    /// index's partitions, from CREATE INDEX, the CREATE TABLE options and a
    /// rebuild alike.
    /// </summary>
    [TestMethod]
    public void DataCompression_ReachesSysPartitions()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int) with (data_compression = page);
            create index ix on t (a) with (data_compression = row);
            """);
        AreEqual("PAGE", sim.ExecuteScalar("select data_compression_desc from sys.partitions where object_id = object_id('t') and index_id = 1"));
        AreEqual("ROW", sim.ExecuteScalar("select data_compression_desc from sys.partitions where object_id = object_id('t') and index_id = 2"));
        _ = sim.ExecuteNonQuery("alter index ix on t rebuild with (data_compression = none)");
        AreEqual("NONE", sim.ExecuteScalar("select data_compression_desc from sys.partitions where object_id = object_id('t') and index_id = 2"));
    }

    [TestMethod]
    [DataRow("create table k (x char(1701)); create index ix on k (x)", 1944)]
    [DataRow("create table k (x char(901)); create clustered index ix on k (x)", 1944)]
    [DataRow("create table k (x varchar(1000), y varchar(1000)); create clustered index ix on k (x, y); insert k values (replicate('a', 500), replicate('b', 500))", 1946)]
    [DataRow("create table k (x varchar(1000), y varchar(1000)); create index ix on k (x, y); insert k values (replicate('a', 1000), replicate('b', 1000))", 1946)]
    public void KeyLength_Limits(string statement, int number)
        => _ = new Simulation().AssertSqlError(statement, number);

    [TestMethod]
    public void KeyLength_AWideVariableKey_WarnsAndAdmitsShortEntries()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table k (x varchar(1000), y varchar(1000));
            create index ix on k (x, y);
            insert k values ('a', 'b');
            select count(*) from k
            """));

    [TestMethod]
    public void KeyColumns_AtMost32()
    {
        var columns = string.Join(", ", Enumerable.Range(1, 33).Select(static i => $"c{i}"));
        var declared = string.Join(", ", Enumerable.Range(1, 33).Select(static i => $"c{i} int"));
        new Simulation().AssertSqlError(
            $"create table k ({declared}); create index ix on dbo.k ({columns})",
            1904,
            "The index 'ix' on table 'dbo.k' has 33 columns in the key list. The maximum limit for index key column list is 32.");
    }

    [TestMethod]
    [DataRow("create synonym sy for t; create index ix on dbo.sy (a)", 1914)]
    [DataRow("create statistics st on t (a); create index st on t (b)", 1913)]
    [DataRow("create table k (x xml); create index ix on k (x)", 1977)]
    [DataRow("create table k (x geography); create index ix on k (x)", 1978)]
    [DataRow("create table k (x varchar(max)); create index ix on k (x)", 1919)]
    [DataRow("create table k (y int, x text); create index ix on k (y) include (x)", 1999)]
    [DataRow("create table k (a int, c as getdate()); create index ix on k (a) include (c)", 2729)]
    public void IndexShape_Refusals(string statement, int number)
        => _ = new Simulation().AssertSqlError(OptionTable + statement, number);

    [TestMethod]
    public void ComputedKeyReadingData_IsMsg2709()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(OptionTable);
        _ = sim.ExecuteNonQuery("create function dbo.f(@a int) returns int with schemabinding as begin return (select count(*) from dbo.t where a = @a) end");
        sim.AssertSqlError(
            "create table k (a int, c as dbo.f(a)); create index ix on dbo.k (c)",
            2709,
            "Column 'c' in table 'dbo.k' cannot be used in an index or statistics or as a partition key because it does user or system data access.");
    }

    [TestMethod]
    public void UniqueIndex_ReportsTheLowestDuplicate()
        => new Simulation().AssertSqlError(
            OptionTable + "insert t values (9, 30, 'x'), (8, 10, 'y'); create unique index ix on t (a)",
            1505,
            "The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name 'dbo.t' and the index name 'ix'. The duplicate key value is (10).");

    [TestMethod]
    public void ClusteredIndex_OnANonPersistedComputedColumn_IsReadable()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table k (a int, b int, c as (b * -1));
            insert k values (1, 1), (2, 3), (3, 2);
            create clustered index cx on k (c);
            select count(*) from k
            """));

    /// <summary>
    /// DROP INDEX's options and forms (probed 2026-10-05 against SQL Server
    /// 2025): ONLINE drops only a clustered index, and a nonclustered one
    /// takes no clustered-only option.
    /// </summary>
    [TestMethod]
    [DataRow("drop index ix_a on t with (online = on)", 3745)]
    [DataRow("drop index ix_a on t with (maxdop = 1)", 3748)]
    [DataRow("drop index ix_a on t with (move to [PRIMARY])", 3748)]
    [DataRow("drop index ix_a on t with (nosuch = 1)", 155)]
    [DataRow("drop index ix_a on t, ix_b on t with (online = on)", 3744)]
    [DataRow("drop index dbo.t.ix_a.x", 166)]
    [DataRow("drop statistics dbo.t.ix_a", 3739)]
    [DataRow("drop statistics st on t", 1053)]
    public void DropIndex_Refusals(string statement, int number)
        => _ = new Simulation().AssertSqlError(
            "create table t (id int primary key, a int, b int); create index ix_a on t (a); create index ix_b on t (b); create statistics st on t (b); " + statement,
            number);

    [TestMethod]
    public void DropIndex_ClusteredWithOptions_IsTaken()
        => AreEqual(0, new Simulation().ExecuteScalar("""
            create table t (id int, a int);
            create clustered index cx on t (id);
            drop index cx on t with (online = off, maxdop = 1, move to [PRIMARY]);
            select count(*) from sys.indexes where object_id = object_id('t') and index_id = 1
            """));

    /// <summary>
    /// The filter refusals that name the written table and the filter's column
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create index ix on dbo.fx (a) where s = N'a'", 10611, "Filtered index 'ix' cannot be created on table 'dbo.fx' because the column 's' in the filter expression is compared with a constant of higher data type precedence or of a different collation. Converting a column to the data type of a constant is not supported for filtered indexes. To resolve this error, explicitly convert the constant to the same data type and collation as the column 's'.")]
    [DataRow("create index ix on dbo.fx (a) where a = null", 10620, "Filtered index 'ix' cannot be created on table 'dbo.fx' because the filter expression contains a comparison with a literal NULL value. Rewrite the comparison to use the IS [NOT] NULL comparison operator to test for NULL values.")]
    [DataRow("create table dbo.k (a int, h hierarchyid); create index ix on dbo.k (a) where h is not null", 10619, "Filtered index 'ix' cannot be created on table 'dbo.k' because the column 'h' in the filter expression is of a CLR data type. Rewrite the filter expression so that it does not include this column.")]
    public void FilteredIndex_Refusals_NameTheWrittenTable(string statement, int number, string message)
        => new Simulation().AssertSqlError($"{FilterTable} {statement}", number, message);

    [TestMethod]
    public void FilteredIndex_Clustered_IsASyntaxError()
        => new Simulation().AssertSqlError($"{FilterTable} create clustered index ix on fx (a) where a > 1", 102, "Incorrect syntax near 'WHERE'.");

    /// <summary>
    /// A filter's grammar takes only comparisons: a bare column is the plain
    /// syntax error at the token after it (probed 2026-10-06 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("create index ix on dbo.f (b) where (a);", 102)]
    [DataRow("create index ix on dbo.f (b) where a;", 102)]
    [DataRow("create index ix on dbo.f (b) where (a) and b = 1;", 156)]
    [DataRow("create statistics st on dbo.f (b) where (a);", 102)]
    public void FilterOfABareColumn_IsASyntaxError(string statement, int number)
        => _ = new Simulation().AssertSqlError($"create table dbo.f (a bit, b int); {statement}", number);
}
