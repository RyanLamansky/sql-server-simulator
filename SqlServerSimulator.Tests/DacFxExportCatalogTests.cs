using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Resolution + value tests for the catalog surface added to unblock DacFx's
/// bacpac export (<c>sqlpackage /Action:Export</c>, the same reverse-engineering
/// engine SSMS's Export wizard drives): 38 accepted-but-empty unmodeled-feature
/// views (Service Broker, partitioning, encryption / key management, Always
/// Encrypted, Row-Level Security, server audits, external languages / models,
/// graph edge constraints, …), the projected <c>sys.stats_columns</c>, and the
/// <c>sys.tables.lock_on_bulk_load</c> column. Shapes / row decisions probe-
/// confirmed against SQL Server 2025 WideWorldImporters (2026-07-16).
/// </summary>
[TestClass]
public sealed class DacFxExportCatalogTests
{
    /// <summary>
    /// sys.tables.lock_on_bulk_load ships as a constant 0 bit (the fresh-table
    /// default). DacFx reads CAST([st].[lock_on_bulk_load] AS bit).
    /// </summary>
    [TestMethod]
    public void LockOnBulkLoad_IsFalse()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (id int primary key)");
        IsFalse((bool)sim.ExecuteScalar("select lock_on_bulk_load from sys.tables where name = 't'")!);
        IsFalse((bool)sim.ExecuteScalar("select cast([lock_on_bulk_load] as bit) from sys.tables where name = 't'")!);
    }

    /// <summary>
    /// Every DacFx-referenced unmodeled-feature view resolves and returns zero
    /// rows — the accepted-but-empty pattern. Covers the Service Broker family
    /// (which real SQL Server seeds with system rows, deliberately omitted since
    /// DacFx exports only user objects), partitioning, encryption / key mgmt,
    /// Always Encrypted, RLS, audits, external languages / libraries / models,
    /// graph edge constraints, and the remaining index / search / assembly views.
    /// </summary>
    [TestMethod]
    public void UnmodeledFeatureViews_ResolveEmpty()
    {
        var sim = new Simulation();
        foreach (var view in new[]
        {
            // Service Broker
            "services", "service_queues", "service_contracts", "service_contract_usages",
            "service_contract_message_usages", "service_message_types", "routes",
            "conversation_priorities", "remote_service_bindings", "event_notifications",
            // Partitioning
            "partition_functions", "partition_schemes", "partition_range_values",
            // Encryption / key management / Always Encrypted
            "symmetric_keys", "cryptographic_providers", "crypt_properties", "key_encryptions",
            "column_master_keys", "column_encryption_key_values",
            "database_credentials", "database_scoped_credentials",
            // Row-Level Security / audits
            "security_policies", "security_predicates", "server_audits", "server_file_audits",
            // External languages / libraries / models + their per-platform file rows
            "external_languages", "external_libraries", "external_models",
            "external_library_files", "external_language_files",
            // Graph edge constraints / events / assemblies
            "edge_constraints", "edge_constraint_clauses", "events", "assembly_files",
            // Index / search / numbered procs
            "json_index_paths", "selective_xml_index_namespaces", "vector_indexes",
            "registered_search_properties", "numbered_procedure_parameters", "function_order_columns",
        })
        {
            AreEqual(0, sim.ExecuteScalar($"select count(*) from sys.{view}"), view);
        }
    }

    /// <summary>
    /// A representative column-shape check: selecting specific columns from the
    /// empty views resolves (no Msg 207) and returns an empty set — including
    /// the sql_variant-substituted (partition_range_values.value,
    /// symmetric_keys.cryptographic_provider_algid), nvarchar(max)-substituted
    /// (security_predicates.predicate_definition, external_models.parameters),
    /// datetime2 (external_models.create_time), and varbinary(max)
    /// (assembly_files.content) columns.
    /// </summary>
    [TestMethod]
    public void EmptyViews_ColumnShapesResolve()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.partition_range_values where value is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.symmetric_keys where cryptographic_provider_algid is null and key_thumbprint is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.security_predicates where predicate_definition is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.external_models where parameters is null and create_time is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.assembly_files where content is null and sha2_256 is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.crypt_properties where crypt_property is not null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.service_queues where activation_procedure is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.server_file_audits where log_file_path is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.external_library_files where content is null and platform_desc is null"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.external_language_files where content is null and file_name is null and environment_variables is null"));
    }

    /// <summary>
    /// sys.types.rule_object_id ships as a constant 0 (rules aren't modeled;
    /// probe-confirmed real reports 0 for an unbound type). DacFx's UDDT
    /// reverse-engineering query INNER JOINs sys.objects ON [st].[rule_object_id],
    /// so an alias type resolves to no legacy-rule row.
    /// </summary>
    [TestMethod]
    public void Types_RuleObjectId_IsZero()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar("select rule_object_id from sys.types where name = 'int'"));
        _ = sim.ExecuteNonQuery("create type dbo.Phone from nvarchar(20) null");
        AreEqual(0, sim.ExecuteScalar("select rule_object_id from sys.types where name = 'Phone'"));
        // The DacFx join over rule_object_id yields no UDDT-with-rule rows.
        AreEqual(0, sim.ExecuteScalar(
            "select count(*) from sys.types st " +
            "inner join sys.objects lo on lo.object_id = st.rule_object_id " +
            "where st.user_type_id > 256"));
    }

    /// <summary>
    /// DacFx's symmetric-key export joins sys.symmetric_keys to
    /// sys.cryptographic_providers on the provider guid; the LEFT JOIN over two
    /// empty views resolves and yields no rows.
    /// </summary>
    [TestMethod]
    public void SymmetricKeys_LeftJoinCryptographicProviders_Resolves()
    {
        var sim = new Simulation();
        AreEqual(0, sim.ExecuteScalar(
            "select count(*) from sys.symmetric_keys sk " +
            "left join sys.cryptographic_providers cp on cp.guid = sk.cryptographic_provider_guid"));
    }

    /// <summary>
    /// sys.stats_columns projects one row per KEY column of each index-backed
    /// statistic (stats_id = index_id), excluding INCLUDE columns. A table with
    /// a PK (a), a composite nonclustered index on (b, c), and a filtered/
    /// include index on (d) INCLUDE (b) yields: PK stat → column a; (b,c) stat →
    /// columns b, c; (d) stat → column d only (the included b is omitted).
    /// </summary>
    [TestMethod]
    public void StatsColumns_ProjectsKeyColumnsExcludingIncludes()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int primary key, b int, c int, d int)",
            "create index ix_bc on dbo.t (b, c)",
            "create index ix_d on dbo.t (d) include (b)");

        // stats_id mirrors sys.indexes.index_id: 1 = PK, then ix_bc / ix_d in
        // object-id (declaration) order at 2 / 3.
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.stats_columns where object_id = object_id('dbo.t') and stats_id = 1"));
        AreEqual(2, sim.ExecuteScalar("select count(*) from sys.stats_columns where object_id = object_id('dbo.t') and stats_id = 2"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.stats_columns where object_id = object_id('dbo.t') and stats_id = 3"));

        // PK stat covers column a (column_id 1), stats_column_id 1.
        AreEqual(1, sim.ExecuteScalar("select column_id from sys.stats_columns where object_id = object_id('dbo.t') and stats_id = 1 and stats_column_id = 1"));

        // (b, c) stat: stats_column_id 1 → b (column_id 2), 2 → c (column_id 3).
        AreEqual(2, sim.ExecuteScalar("select column_id from sys.stats_columns where object_id = object_id('dbo.t') and stats_id = 2 and stats_column_id = 1"));
        AreEqual(3, sim.ExecuteScalar("select column_id from sys.stats_columns where object_id = object_id('dbo.t') and stats_id = 2 and stats_column_id = 2"));

        // ix_d stat: only the key column d (column_id 4); the INCLUDE(b) is omitted.
        AreEqual(4, sim.ExecuteScalar("select column_id from sys.stats_columns where object_id = object_id('dbo.t') and stats_id = 3 and stats_column_id = 1"));
    }

    /// <summary>
    /// Cross-view parity: every sys.stats_columns row matches a sys.index_columns
    /// KEY row (is_included_column = 0) on (object_id, stats_id = index_id,
    /// stats_column_id = index_column_id, column_id), and the counts agree —
    /// exactly the relationship probed against live WWI.
    /// </summary>
    [TestMethod]
    public void StatsColumns_MatchesIndexColumnsKeyRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.t (a int primary key, b int, c int, d int)",
            "create unique index ux_bc on dbo.t (b, c)",
            "create index ix_d on dbo.t (d) include (a, b)");

        var statsColumnsCount = sim.ExecuteScalar<int>("select count(*) from sys.stats_columns where object_id = object_id('dbo.t')");
        var indexKeyCount = sim.ExecuteScalar<int>("select count(*) from sys.index_columns where object_id = object_id('dbo.t') and is_included_column = 0");
        AreEqual(indexKeyCount, statsColumnsCount);

        var joined = sim.ExecuteScalar<int>(
            "select count(*) from sys.stats_columns sc " +
            "join sys.index_columns ic on ic.object_id = sc.object_id and ic.index_id = sc.stats_id " +
            "and ic.index_column_id = sc.stats_column_id and ic.column_id = sc.column_id " +
            "where ic.is_included_column = 0");
        AreEqual(statsColumnsCount, joined);
    }

    /// <summary>
    /// A table with no clustered index carries a heap (index_id 0, no statistic),
    /// so its lone nonclustered index takes index_id / stats_id 2 — never the
    /// clustered slot's id 1 (probe-confirmed against SQL Server 2025). sys.stats_columns
    /// skips the heap exactly like sys.stats.
    /// </summary>
    [TestMethod]
    public void StatsColumns_HeapTable_SkipsHeapAndStartsAtIndexId2()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.h (a int, b int)",
            "create index ix_a on dbo.h (a)");
        // No stats_id 0 (heap has no statistic) and no stats_id 1 (the clustered
        // slot is empty on a heap); the nonclustered index is stats_id 2.
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.stats_columns where object_id = object_id('dbo.h') and stats_id in (0, 1)"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.stats_columns where object_id = object_id('dbo.h') and stats_id = 2"));
        AreEqual(1, sim.ExecuteScalar("select column_id from sys.stats_columns where object_id = object_id('dbo.h') and stats_id = 2 and stats_column_id = 1"));
    }

    /// <summary>
    /// A temporal-history-shaped table (no PK, one explicit CLUSTERED INDEX)
    /// projects exactly one sys.indexes row — the clustered index at index_id 1,
    /// no phantom heap row. Reproduces the Application.PaymentMethods_Archive
    /// case: DacFx's SqlTable query LEFT JOINs (SELECT * FROM sys.indexes WHERE
    /// ISNULL(index_id, 0) &lt; 2), and a duplicate heap+clustered pair produced
    /// two join rows per history table → "unresolved reference to Table [X_Archive]".
    /// Probe-confirmed against SQL Server 2025 WWI (one row: 1 / CLUSTERED).
    /// </summary>
    [TestMethod]
    public void SysIndexes_HistoryTableWithClusteredIndex_SingleRowForDacFxJoin()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.PaymentMethods_Archive (PaymentMethodID int not null, PaymentMethodName nvarchar(50) not null, ValidFrom datetime2 not null, ValidTo datetime2 not null)",
            "create clustered index ix_PaymentMethods_Archive on dbo.PaymentMethods_Archive (ValidTo, ValidFrom)");
        // The DacFx SqlTable-element join shape: exactly one row where index_id < 2.
        AreEqual(1, sim.ExecuteScalar("""
            select count(*) from sys.indexes
            where object_id = object_id('dbo.PaymentMethods_Archive') and isnull(index_id, 0) < 2
            """));
        AreEqual(1, sim.ExecuteScalar("select index_id from sys.indexes where name = 'ix_PaymentMethods_Archive'"));
        AreEqual((byte)1, sim.ExecuteScalar("select type from sys.indexes where name = 'ix_PaymentMethods_Archive'"));
        AreEqual("CLUSTERED", sim.ExecuteScalar("select type_desc from sys.indexes where name = 'ix_PaymentMethods_Archive'"));
    }

    /// <summary>
    /// sys.key_constraints.unique_index_id points each PK / UNIQUE constraint at
    /// ITS own backing index (not a shared id). Reproduces the
    /// Warehouse.StockItemStockGroups case: DacFx's UQ query INNER JOINs
    /// sys.key_constraints kc ON i.index_id = kc.unique_index_id, and both UQs
    /// reporting the PK's id 1 bound them to the PK index → the UQ elements were
    /// lost. Probe-confirmed against SQL Server 2025 WWI (PK → 1, UQs → 2, 3).
    /// </summary>
    [TestMethod]
    public void KeyConstraints_UniqueIndexId_BindsEachConstraintToItsOwnIndex()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.StockItemStockGroups (
                StockItemStockGroupID int not null,
                StockItemID int not null,
                StockGroupID int not null,
                constraint PK_StockItemStockGroups primary key clustered (StockItemStockGroupID),
                constraint UQ_StockItemStockGroups_StockItemID unique nonclustered (StockItemID, StockGroupID),
                constraint UQ_StockItemStockGroups_StockGroupID unique nonclustered (StockGroupID, StockItemID))
            """);
        // The DacFx UQ join: each constraint resolves to the index of its own name.
        AreEqual(3, sim.ExecuteScalar("""
            select count(*) from sys.key_constraints kc
            join sys.indexes i on i.object_id = kc.parent_object_id and i.index_id = kc.unique_index_id
            where kc.parent_object_id = object_id('dbo.StockItemStockGroups') and i.name = kc.name
            """));
        AreEqual(1, sim.ExecuteScalar("select unique_index_id from sys.key_constraints where name = 'PK_StockItemStockGroups'"));
        AreEqual(2, sim.ExecuteScalar("select unique_index_id from sys.key_constraints where name = 'UQ_StockItemStockGroups_StockItemID'"));
        AreEqual(3, sim.ExecuteScalar("select unique_index_id from sys.key_constraints where name = 'UQ_StockItemStockGroups_StockGroupID'"));
        // The UQ backing indexes report is_unique_constraint = 1 (not the PK).
        IsTrue((bool)sim.ExecuteScalar("select is_unique_constraint from sys.indexes where object_id = object_id('dbo.StockItemStockGroups') and index_id = 2")!);
        IsTrue((bool)sim.ExecuteScalar("select is_unique_constraint from sys.indexes where object_id = object_id('dbo.StockItemStockGroups') and index_id = 3")!);
    }

    /// <summary>
    /// DacFx's SqlUserDefinedDataType scripting query reads
    /// sys.types.collation_name / principal_id / default_object_id and the
    /// base-type self-join. Probe-confirmed: character-family types (and
    /// alias types over them) report the database collation; every other
    /// type reports NULL; principal_id NULL; default_object_id 0.
    /// </summary>
    [TestMethod]
    public void SysTypes_CollationPrincipalDefault_MatchDacFxUddtQuery()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create type dbo.PhoneNumber from varchar(20) not null",
            "create type dbo.Amount from decimal(18, 2)");
        AreEqual("SQL_Latin1_General_CP1_CI_AS", sim.ExecuteScalar("select collation_name from sys.types where name = 'PhoneNumber'"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select collation_name from sys.types where name = 'Amount'"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select principal_id from sys.types where name = 'PhoneNumber'"));
        AreEqual(0, sim.ExecuteScalar("select default_object_id from sys.types where name = 'PhoneNumber'"));
        AreEqual("SQL_Latin1_General_CP1_CI_AS", sim.ExecuteScalar("select collation_name from sys.types where name = 'nvarchar'"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select collation_name from sys.types where name = 'int'"));

        // The exact DacFx UDDT query shape (joins, CASE length halving, cdc filter).
        AreEqual("PhoneNumber", sim.ExecuteScalar(
            "SELECT TOP 1 [st].[name] FROM [sys].[types] [st] WITH (NOLOCK) " +
            "LEFT JOIN [sys].[database_principals] [dp] WITH (NOLOCK) ON [dp].[principal_id] = [st].[principal_id] " +
            "LEFT JOIN [sys].[types] [bt] WITH (NOLOCK) ON [st].[system_type_id] = [bt].[system_type_id] AND [bt].[system_type_id] = [bt].[user_type_id] " +
            "WHERE [st].[is_user_defined] = 1 AND [st].[is_assembly_type] = 0 AND [st].[is_table_type] = 0 " +
            "AND SCHEMA_NAME([st].[schema_id]) <> N'cdc' ORDER BY [st].[name] DESC"));
    }

    /// <summary>
    /// DacFx's table-type populator INNER JOINs sys.objects on
    /// type_table_object_id (parent rows) while the column populator keys off
    /// sys.columns — an absent TYPE_TABLE object row NREs DacFx client-side.
    /// Probe-confirmed shape: name TT_&lt;type&gt;_&lt;object_id:X8&gt;, type 'TT',
    /// homed in sys (schema_id 4), is_ms_shipped 1.
    /// </summary>
    [TestMethod]
    public void SysObjects_TableType_SurfacesTypeTableRow()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type dbo.IdList as table (id int not null)");
        AreEqual(1, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM [sys].[table_types] [tt] WITH (NOLOCK) " +
            "INNER JOIN [sys].[objects] as [o] WITH (NOLOCK) ON [tt].[type_table_object_id] = [o].[object_id] " +
            "WHERE [tt].[is_user_defined] = 1"));
        AreEqual("TT", sim.ExecuteScalar(
            "select rtrim(o.type) from sys.table_types tt join sys.objects o on tt.type_table_object_id = o.object_id"));
        AreEqual(4, sim.ExecuteScalar(
            "select o.schema_id from sys.table_types tt join sys.objects o on tt.type_table_object_id = o.object_id"));
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.objects where type = 'TT' and name like 'TT[_]IdList[_]%' and is_ms_shipped = 1"));
    }

    /// <summary>
    /// DacFx's assembly-type scripting query joins sys.assembly_types.assembly_class;
    /// the three CLR system types carry their probe-confirmed class names but are
    /// is_user_defined = 0, so the query's filter returns zero rows.
    /// </summary>
    [TestMethod]
    public void AssemblyTypes_AssemblyClass_PresentAndFilteredOut()
    {
        var sim = new Simulation();
        AreEqual("Microsoft.SqlServer.Types.SqlHierarchyId",
            sim.ExecuteScalar("select assembly_class from sys.assembly_types where name = 'hierarchyid'"));
        AreEqual(0, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM [sys].[types] [st] WITH (NOLOCK) " +
            "LEFT JOIN [sys].[assembly_types] [at] WITH (NOLOCK) ON [at].[user_type_id] = [st].[user_type_id] " +
            "WHERE [st].[is_user_defined] = 1 AND [st].[is_assembly_type] = 1"));
    }
}
