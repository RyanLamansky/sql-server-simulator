using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the catalog surface SSMS's "Script Table as → CREATE To" reaches
/// through SMO — the barrage of <c>sys.*</c> views its column / index / trigger /
/// FK / default / extended-property scripting queries read. Covers the newly
/// modeled views (<c>sys.periods</c>, <c>sys.computed_columns</c>,
/// <c>sys.identity_columns</c>, <c>sys.trigger_events</c>, <c>sys.all_objects</c>,
/// <c>sys.filegroups</c>, <c>sys.syslanguages</c>, and the empty
/// unmodeled-feature views), plus the columns added to <c>sys.tables</c> /
/// <c>sys.columns</c>. Shapes / values probed against SQL Server 2025 (2026-07-15).
/// </summary>
[TestClass]
public sealed class SsmsScriptTableCatalogTests
{
    // === sys.periods ===

    [TestMethod]
    public void Periods_SystemVersionedTable_ProjectsOnePeriodRow_ExcludesHistory()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table emp (
                id int not null primary key,
                ValidFrom datetime2 generated always as row start not null,
                ValidTo datetime2 generated always as row end not null,
                period for system_time (ValidFrom, ValidTo)
            ) with (system_versioning = on (history_table = dbo.empHistory))
            """);
        using var reader = sim.ExecuteReader("""
            select name, period_type, period_type_desc, start_column_id, end_column_id,
                   object_id = case when object_id = object_id('emp') then 1 else 0 end
            from sys.periods
            """);
        IsTrue(reader.Read());
        AreEqual("SYSTEM_TIME", reader.GetString(0));
        AreEqual((byte)1, reader.GetByte(1));
        AreEqual("SYSTEM_TIME_PERIOD", reader.GetString(2));
        AreEqual(2, reader.GetInt32(3)); // ValidFrom is column_id 2
        AreEqual(3, reader.GetInt32(4)); // ValidTo is column_id 3
        AreEqual(1, reader.GetInt32(5)); // period row is the base table, not the history sibling
        IsFalse(reader.Read());          // history table excluded → exactly one row
    }

    [TestMethod]
    public void Periods_NonTemporalTable_ProjectsNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key);
            select count(*) from sys.periods
            """));

    // === sys.computed_columns ===

    [TestMethod]
    public void ComputedColumns_ProjectsComputedColumn_DefinitionNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int not null primary key, b as a + 1)");
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from sys.computed_columns where object_id = object_id('t')"));
        AreEqual(1, sim.ExecuteScalar<int>("select cast(is_computed as int) from sys.computed_columns where name = 'b'"));
        AreEqual(1, sim.ExecuteScalar<int>("select case when definition is null then 1 else 0 end from sys.computed_columns where name = 'b'"));
    }

    [TestMethod]
    public void ComputedColumns_PersistedFlagReflectsDeclaration()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table t (a int not null primary key, b as a + 1 persisted);
            select cast(is_persisted as int) from sys.computed_columns where name = 'b'
            """));

    // === sys.identity_columns ===

    [TestMethod]
    public void IdentityColumns_ProjectsSeedAndIncrement()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int identity(5, 3) not null primary key, x int)");
        using var reader = sim.ExecuteReader(
            "select name, seed_value, increment_value, cast(is_not_for_replication as int) from sys.identity_columns where object_id = object_id('t')");
        IsTrue(reader.Read());
        AreEqual("id", reader.GetString(0));
        AreEqual(5L, reader.GetInt64(1));
        AreEqual(3L, reader.GetInt64(2));
        AreEqual(0, reader.GetInt32(3));
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void IdentityColumns_LastValueNullBeforeFirstInsert()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table t (id int identity(1, 1) not null primary key);
            select case when last_value is null then 1 else 0 end from sys.identity_columns where object_id = object_id('t')
            """));

    // === sys.trigger_events ===

    [TestMethod]
    public void TriggerEvents_ProjectsOneRowPerDmlEvent_DenseTypeCodes()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int not null primary key)",
            "create trigger trg on t after insert, update, delete as select 1");
        using var reader = sim.ExecuteReader("""
            select type, type_desc, cast(is_trigger_event as int)
            from sys.trigger_events
            where object_id = object_id('trg')
            order by type
            """);
        (int Type, string Desc)[] expected = [(1, "INSERT"), (2, "UPDATE"), (3, "DELETE")];
        foreach (var (type, desc) in expected)
        {
            IsTrue(reader.Read());
            AreEqual(type, reader.GetInt32(0));
            AreEqual(desc, reader.GetString(1));
            AreEqual(1, reader.GetInt32(2));
        }
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void TriggerEvents_InsertOnlyTrigger_ProjectsSingleRow()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int not null primary key)",
            "create trigger trg on t after insert as select 1");
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from sys.trigger_events where object_id = object_id('trg')"));
        AreEqual(1, sim.ExecuteScalar<int>("select type from sys.trigger_events where object_id = object_id('trg')"));
    }

    // === sys.all_objects (user-object parity with sys.objects) ===

    [TestMethod]
    public void AllObjects_MatchesObjectsRowSet_ForUserTable()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        AreEqual(
            sim.ExecuteScalar<int>("select count(*) from sys.objects where object_id = object_id('t')"),
            sim.ExecuteScalar<int>("select count(*) from sys.all_objects where object_id = object_id('t')"));
    }

    // === sys.filegroups (single PRIMARY row) ===

    [TestMethod]
    public void Filegroups_ReturnsPrimaryRow()
    {
        using var reader = new Simulation().ExecuteReader(
            "select name, data_space_id, cast(is_default as int), cast(is_read_only as int), cast(is_autogrow_all_files as int) from sys.filegroups");
        IsTrue(reader.Read());
        AreEqual("PRIMARY", reader.GetString(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual(1, reader.GetInt32(2));
        AreEqual(0, reader.GetInt32(3));
        AreEqual(0, reader.GetInt32(4));
        IsFalse(reader.Read());
    }

    // === sys.syslanguages (us_english default) ===

    [TestMethod]
    public void SysLanguages_ProjectsUsEnglishDefault()
    {
        using var reader = new Simulation().ExecuteReader(
            "select langid, name, lcid from sys.syslanguages where langid = 0");
        IsTrue(reader.Read());
        AreEqual((short)0, reader.GetInt16(0));
        AreEqual("us_english", reader.GetString(1));
        AreEqual(1033, reader.GetInt32(2));
    }

    // === Empty unmodeled-feature views: resolve, project shape, zero rows ===

    [TestMethod]
    [DataRow("sys.change_tracking_tables", 5)]
    [DataRow("sys.external_tables", 29)]
    [DataRow("sys.filetables", 5)]
    [DataRow("sys.external_data_sources", 11)]
    [DataRow("sys.external_file_formats", 13)]
    [DataRow("sys.masked_columns", 5)]
    [DataRow("sys.column_encryption_keys", 4)]
    [DataRow("sys.sensitivity_classifications", 10)]
    [DataRow("sys.fulltext_stoplists", 5)]
    [DataRow("sys.registered_search_property_lists", 5)]
    [DataRow("sys.fulltext_languages", 2)]
    [DataRow("sys.assembly_modules", 6)]
    [DataRow("sys.database_recovery_status", 7)]
    [DataRow("sys.change_tracking_databases", 6)]
    [DataRow("sys.database_filestream_options", 4)]
    public void EmptyView_ResolvesWithShape_ZeroRows(string view, int columnCount)
    {
        using var reader = new Simulation().ExecuteReader($"select * from {view}");
        AreEqual(columnCount, reader.FieldCount);
        IsFalse(reader.Read());
    }

    // === sys.tables columns SMO's CREATE-scripting table query reads ===

    [TestMethod]
    public void Tables_ExposesScriptingColumns_WithProbedDefaults()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        using var reader = sim.ExecuteReader("""
            select case when principal_id is null then 1 else 0 end,
                   cast(uses_ansi_nulls as int),
                   cast(is_dropped_ledger_table as int),
                   lock_escalation, lock_escalation_desc,
                   case when ledger_view_id is null then 1 else 0 end,
                   lob_data_space_id,
                   durability, durability_desc
            from sys.tables where name = 't'
            """);
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));           // principal_id NULL
        AreEqual(1, reader.GetInt32(1));           // uses_ansi_nulls = 1
        AreEqual(0, reader.GetInt32(2));           // is_dropped_ledger_table = 0
        AreEqual((byte)0, reader.GetByte(3));      // lock_escalation = 0
        AreEqual("TABLE", reader.GetString(4));    // lock_escalation_desc
        AreEqual(1, reader.GetInt32(5));           // ledger_view_id NULL
        AreEqual(0, reader.GetInt32(6));           // lob_data_space_id = 0 (single PRIMARY filegroup)
        AreEqual((byte)0, reader.GetByte(7));      // durability = 0
        AreEqual("SCHEMA_AND_DATA", reader.GetString(8));
    }

    // Replication isn't modeled, so is_replicated is a constant 0. SMO's Table
    // property-bag projects tbl.is_replicated AS [Replicated]; a missing column
    // fails the whole bag query Msg 207 and every Table property errors.
    [TestMethod]
    public void Tables_IsReplicated_FalseForUserTable()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key);
            select count(*) from sys.tables where name = 't' and is_replicated = 0
            """));

    // The full modeled sys.tables column set resolves in one projection — the
    // SMO Table property-bag reads every column, so one missing name fails the
    // bag query and every Table property errors.
    [TestMethod]
    public void Tables_FullModeledColumnSet_Resolves()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key)");
        using var reader = sim.ExecuteReader("""
            select object_id, name, schema_id, principal_id, type, type_desc,
                   create_date, modify_date, is_ms_shipped, temporal_type,
                   temporal_type_desc, history_table_id, is_memory_optimized,
                   is_filetable, is_external, is_node, is_edge, durability,
                   durability_desc, ledger_type, ledger_view_id, uses_ansi_nulls,
                   is_dropped_ledger_table, lock_escalation, lock_escalation_desc,
                   filestream_data_space_id, lob_data_space_id, is_replicated
            from sys.tables where name = 't'
            """);
        IsTrue(reader.Read());
        AreEqual("t", reader.GetString(1));
        AreEqual(28, reader.FieldCount);
    }

    // === sys.columns columns SMO's CREATE-scripting column query reads ===

    [TestMethod]
    public void Columns_IsAnsiPadded_TrueForStringTypes_FalseForOthers()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, s nvarchar(20) null)");
        AreEqual(0, sim.ExecuteScalar<int>("select cast(is_ansi_padded as int) from sys.columns where object_id = object_id('t') and name = 'id'"));
        AreEqual(1, sim.ExecuteScalar<int>("select cast(is_ansi_padded as int) from sys.columns where object_id = object_id('t') and name = 's'"));
    }

    [TestMethod]
    public void Columns_DefaultObjectId_PointsAtDefaultConstraint()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int not null primary key, n int not null default 7)");
        // The column's default_object_id must match its DEFAULT constraint's object_id.
        AreEqual(1, sim.ExecuteScalar<int>("""
            select case when c.default_object_id = dc.object_id and c.default_object_id <> 0 then 1 else 0 end
            from sys.columns c
            join sys.default_constraints dc on dc.parent_object_id = c.object_id and dc.parent_column_id = c.column_id
            where c.object_id = object_id('t') and c.name = 'n'
            """));
        // A column without a default reports default_object_id 0.
        AreEqual(0, sim.ExecuteScalar<int>("select default_object_id from sys.columns where object_id = object_id('t') and name = 'id'"));
    }

    [TestMethod]
    public void Columns_GeneratedAlwaysType_ReflectsPeriodColumns()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table emp (
                id int not null primary key,
                ValidFrom datetime2 generated always as row start not null,
                ValidTo datetime2 generated always as row end not null,
                period for system_time (ValidFrom, ValidTo)
            ) with (system_versioning = on (history_table = dbo.empHistory))
            """);
        AreEqual(0, sim.ExecuteScalar<int>("select cast(generated_always_type as int) from sys.columns where object_id = object_id('emp') and name = 'id'"));
        AreEqual(1, sim.ExecuteScalar<int>("select cast(generated_always_type as int) from sys.columns where object_id = object_id('emp') and name = 'ValidFrom'"));
        AreEqual(2, sim.ExecuteScalar<int>("select cast(generated_always_type as int) from sys.columns where object_id = object_id('emp') and name = 'ValidTo'"));
    }

    [TestMethod]
    public void Columns_LedgerViewColumnType_AlwaysNull()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key);
            select case when ledger_view_column_type is null then 1 else 0 end
            from sys.columns where object_id = object_id('t') and name = 'id'
            """));
}
