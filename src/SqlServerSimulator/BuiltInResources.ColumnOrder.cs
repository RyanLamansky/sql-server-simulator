using System.Collections.Frozen;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    /// <summary>
    /// The order SQL Server 2025 lists each of these catalog views' columns in
    /// — what <c>SELECT *</c>, <c>sys.all_columns.column_id</c> and
    /// <c>COLUMNPROPERTY(…, 'ColumnId')</c> show — restricted to the columns
    /// the simulator declares (probed 2026-09-25). A view's declaration and
    /// row generator keep the order they were written in, and
    /// <see cref="ApplyRealColumnOrder"/> presents them in this one.
    /// </summary>
    private static readonly FrozenDictionary<string, string[]> RealColumnOrder = new Dictionary<string, string[]>(BuiltInToken.Comparer)
    {
        ["sys.all_columns"] =
        [
            "object_id", "name", "column_id", "system_type_id", "user_type_id", "max_length",
            "precision", "scale", "collation_name", "is_nullable", "is_ansi_padded", "is_rowguidcol",
            "is_identity", "is_computed", "is_filestream", "is_replicated", "is_non_sql_subscribed", "is_merge_published",
            "is_dts_replicated", "is_xml_document", "xml_collection_id", "default_object_id", "rule_object_id", "is_sparse",
            "is_column_set", "generated_always_type", "generated_always_type_desc", "encryption_type", "encryption_type_desc", "encryption_algorithm_name",
            "column_encryption_key_id", "column_encryption_key_database_name", "is_hidden", "is_masked", "graph_type", "graph_type_desc",
            "is_data_deletion_filter_column", "ledger_view_column_type", "ledger_view_column_type_desc", "is_dropped_ledger_column", "vector_dimensions", "vector_base_type",
            "vector_base_type_desc",
        ],
        ["sys.all_objects"] =
        [
            "name", "object_id", "principal_id", "schema_id", "parent_object_id", "type",
            "type_desc", "create_date", "modify_date", "is_ms_shipped", "is_published", "is_schema_published",
        ],
        ["sys.all_views"] =
        [
            "name", "object_id", "principal_id", "schema_id", "type", "type_desc",
            "create_date", "modify_date", "is_ms_shipped", "has_opaque_metadata", "with_check_option", "is_date_correlation_view",
            "ledger_view_type", "is_dropped_ledger_view",
        ],
        ["sys.columns"] =
        [
            "object_id", "name", "column_id", "system_type_id", "user_type_id", "max_length",
            "precision", "scale", "collation_name", "is_nullable", "is_ansi_padded", "is_rowguidcol",
            "is_identity", "is_computed", "is_filestream", "is_replicated", "is_non_sql_subscribed", "is_merge_published",
            "is_dts_replicated", "is_xml_document", "xml_collection_id", "default_object_id", "rule_object_id", "is_sparse",
            "is_column_set", "generated_always_type", "generated_always_type_desc", "encryption_type", "encryption_type_desc", "encryption_algorithm_name",
            "column_encryption_key_id", "column_encryption_key_database_name", "is_hidden", "is_masked", "graph_type", "graph_type_desc",
            "is_data_deletion_filter_column", "ledger_view_column_type", "ledger_view_column_type_desc", "is_dropped_ledger_column", "vector_dimensions", "vector_base_type",
            "vector_base_type_desc",
        ],
        ["sys.dm_os_waiting_tasks"] =
        [
            "session_id", "wait_type", "blocking_session_id", "resource_description",
        ],
        ["sys.fulltext_indexes"] =
        [
            "object_id", "unique_index_id", "fulltext_catalog_id", "is_enabled", "change_tracking_state", "change_tracking_state_desc",
            "has_crawl_completed", "crawl_type", "crawl_type_desc", "crawl_start_date", "crawl_end_date", "stoplist_id",
            "property_list_id", "data_space_id",
        ],
        ["sys.identity_columns"] =
        [
            "object_id", "name", "column_id", "is_identity", "seed_value", "increment_value",
            "last_value", "is_not_for_replication",
        ],
        ["sys.objects"] =
        [
            "name", "object_id", "principal_id", "schema_id", "parent_object_id", "type",
            "type_desc", "create_date", "modify_date", "is_ms_shipped", "is_published", "is_schema_published",
        ],
        ["sys.procedures"] =
        [
            "name", "object_id", "schema_id", "type", "type_desc", "create_date",
            "modify_date", "is_ms_shipped", "is_auto_executed",
        ],
        ["sys.sequences"] =
        [
            "name", "object_id", "principal_id", "schema_id", "create_date", "modify_date",
            "start_value", "increment", "minimum_value", "maximum_value", "is_cycling", "is_cached",
            "cache_size", "system_type_id", "user_type_id", "precision", "scale", "current_value",
            "is_exhausted", "last_used_value",
        ],
        ["sys.system_objects"] =
        [
            "name", "object_id", "principal_id", "schema_id", "parent_object_id", "type",
            "type_desc", "create_date", "modify_date", "is_ms_shipped",
        ],
        ["sys.table_types"] =
        [
            "name", "system_type_id", "user_type_id", "schema_id", "principal_id", "max_length",
            "precision", "scale", "collation_name", "is_nullable", "is_user_defined", "is_assembly_type",
            "is_table_type", "type_table_object_id", "is_memory_optimized",
        ],
        ["sys.tables"] =
        [
            "name", "object_id", "principal_id", "schema_id", "type", "type_desc",
            "create_date", "modify_date", "is_ms_shipped", "is_published", "is_schema_published", "lob_data_space_id",
            "filestream_data_space_id", "max_column_id_used", "lock_on_bulk_load", "uses_ansi_nulls", "is_replicated", "is_merge_published",
            "text_in_row_limit", "large_value_types_out_of_row", "is_tracked_by_cdc", "lock_escalation", "lock_escalation_desc", "is_filetable",
            "is_memory_optimized", "durability", "durability_desc", "temporal_type", "temporal_type_desc", "history_table_id",
            "is_remote_data_archive_enabled", "is_external", "history_retention_period", "history_retention_period_unit", "history_retention_period_unit_desc", "is_node",
            "is_edge", "ledger_type", "ledger_view_id", "is_dropped_ledger_table",
        ],
        ["sys.triggers"] =
        [
            "name", "object_id", "parent_class", "parent_class_desc", "parent_id", "type",
            "type_desc", "create_date", "modify_date", "is_ms_shipped", "is_disabled", "is_not_for_replication",
            "is_instead_of_trigger",
        ],
        ["sys.types"] =
        [
            "name", "system_type_id", "user_type_id", "schema_id", "principal_id", "max_length",
            "precision", "scale", "collation_name", "is_nullable", "is_user_defined", "is_assembly_type",
            "default_object_id", "rule_object_id", "is_table_type",
        ],
        ["sys.views"] =
        [
            "name", "object_id", "principal_id", "schema_id", "type", "type_desc",
            "create_date", "modify_date", "is_ms_shipped", "has_opaque_metadata", "with_check_option", "is_date_correlation_view",
            "ledger_view_type", "is_dropped_ledger_view",
        ],
        ["sys.xml_indexes"] =
        [
            "object_id", "name", "index_id", "type", "type_desc", "is_unique",
            "data_space_id", "ignore_dup_key", "is_primary_key", "is_unique_constraint", "fill_factor", "is_padded",
            "is_disabled", "is_hypothetical", "is_ignored_in_optimization", "allow_row_locks", "allow_page_locks", "using_xml_index_id",
            "secondary_type", "secondary_type_desc", "has_filter", "filter_definition", "xml_index_type", "xml_index_type_description",
            "path_id", "auto_created",
        ],
    }.ToFrozenDictionary(BuiltInToken.Comparer);

    /// <summary>
    /// Replaces each view <see cref="RealColumnOrder"/> lists with one
    /// presenting its columns in real's order. Runs before anything records a
    /// column ordinal against the registry.
    /// </summary>
    private static void ApplyRealColumnOrder(Dictionary<string, CatalogView> views)
    {
        var replaced = new Dictionary<CatalogView, CatalogView>(ReferenceEqualityComparer.Instance);
        foreach (var (key, order) in RealColumnOrder)
        {
            var view = views[key];
            if (!replaced.ContainsKey(view))
                replaced[view] = view.InColumnOrder(order);
        }
        foreach (var key in views.Keys.ToArray())
        {
            if (replaced.TryGetValue(views[key], out var presented))
                views[key] = presented;
        }
    }
}
