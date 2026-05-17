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

    [TestMethod]
    public void CreateIndex_WithOptionsClause_Accepted()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, a int);
            create index ix_a on t(a) with (fillfactor = 80, ignore_dup_key = on);
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
    // filegroup model, so both trailers parse-and-discard. Probed against
    // the Optimizely Configured Commerce v4.x starting-database script on
    // 2026-05-17 (1.6 MB, 17 K lines, all 700 `ON [PRIMARY]` occurrences).

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
}
