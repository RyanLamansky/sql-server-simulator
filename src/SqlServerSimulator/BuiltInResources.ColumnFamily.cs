using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static void RegisterColumnFamily(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        // sys.periods: one row per table carrying a PERIOD FOR SYSTEM_TIME
        // declaration. History tables are excluded (they hold no PERIOD of
        // their own — the simulator copies PeriodColumns onto the history
        // sibling for the FOR SYSTEM_TIME query machinery, but real SQL Server
        // only surfaces the base table's period). The only period_type SQL
        // Server defines is SYSTEM_TIME (1 / SYSTEM_TIME_PERIOD); the period
        // name is always 'SYSTEM_TIME'. start_/end_column_id are the 1-based
        // column ordinals of the ROW START / ROW END columns.
        var systemTimeName = SqlValue.FromSystemName("SYSTEM_TIME");
        var periodTypeSystemTime = SqlValue.FromByte(1);
        var periodTypeDescSystemTime = SqlValue.FromString(nvarchar60Catalog, "SYSTEM_TIME_PERIOD");
        Sys("periods",
        [
            new("name", SqlType.SystemName, 128, true),
            new("period_type", SqlType.TinyInt, null, true),
            new("period_type_desc", nvarchar60Catalog, 60, true),
            new("object_id", SqlType.Int32, null, false),
            new("start_column_id", SqlType.Int32, null, false),
            new("end_column_id", SqlType.Int32, null, false),
        ], (batch, database) =>
            database.Schemas.Values
                .SelectMany(s => s.HeapTables.Values)
                .Where(t => t.PeriodColumns is not null && !t.IsHistoryTable)
                .OrderBy(t => t.ObjectId)
                .Select(t => new SqlValue[]
                {
                    systemTimeName,
                    periodTypeSystemTime,
                    periodTypeDescSystemTime,
                    SqlValue.FromInt32(t.ObjectId),
                    SqlValue.FromInt32(t.PeriodColumns!.Value.StartOrdinal + 1),
                    SqlValue.FromInt32(t.PeriodColumns.Value.EndOrdinal + 1),
                }));

        // sys.change_tracking_tables / sys.external_tables / sys.filetables:
        // change tracking, PolyBase external tables, and FileTables aren't
        // modeled, so each is an empty view with the documented SQL Server 2025
        // shape. SMO's CREATE-scripting table query LEFT JOINs all three to
        // detect those table flavors; the empty projection resolves the join
        // to "not one of these".
        Sys("change_tracking_tables",
        [
            new("object_id", SqlType.Int32, null, false),
            new("is_track_columns_updated_on", SqlType.Bit, null, false),
            new("min_valid_version", SqlType.BigInt, null, true),
            new("begin_version", SqlType.BigInt, null, true),
            new("cleanup_version", SqlType.BigInt, null, true),
        ], static (batch, database) => []);

        Sys("external_tables",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
            new("max_column_id_used", SqlType.Int32, null, true),
            new("uses_ansi_nulls", SqlType.Bit, null, true),
            new("data_source_id", SqlType.Int32, null, false),
            new("file_format_id", SqlType.Int32, null, true),
            new("location", SqlType.NVarchar, 4000, true),
            new("reject_type", SqlType.NVarchar, 20, true),
            new("reject_value", SqlType.Float, null, true),
            new("reject_sample_value", SqlType.Float, null, true),
            new("distribution_type", SqlType.TinyInt, null, true),
            new("distribution_desc", SqlType.NVarchar, 120, true),
            new("sharding_col_id", SqlType.Int32, null, true),
            new("remote_schema_name", SqlType.NVarchar, 128, true),
            new("remote_object_name", SqlType.NVarchar, 128, true),
            new("rejected_row_location", SqlType.NVarchar, 4000, true),
            new("table_options", SqlType.NVarchar, 1000, true),
            new("partition_type", SqlType.Int32, null, false),
            new("partition_desc", SqlType.NVarchar, 60, true),
        ], static (batch, database) => []);

        Sys("filetables",
        [
            new("object_id", SqlType.Int32, null, false),
            new("is_enabled", SqlType.Bit, null, false),
            new("directory_name", SqlType.NVarchar, 256, false),
            new("filename_collation_id", SqlType.Int32, null, false),
            new("filename_collation_name", SqlType.NVarchar, 129, false),
        ], static (batch, database) => []);

        // sys.masked_columns: Dynamic Data Masking isn't modeled, so this is
        // an empty view. Real SQL Server surfaces it as sys.columns filtered to
        // is_masked = 1 plus a masking_function column; the simulator ships the
        // load-bearing subset SMO's CREATE-scripting column query reads via a
        // correlated subquery (object_id / column_id / masking_function).
        Sys("masked_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("column_id", SqlType.Int32, null, false),
            new("is_masked", SqlType.Bit, null, true),
            new("masking_function", SqlType.NVarchar, 4000, true),
        ], static (batch, database) => []);

        // sys.computed_columns: one row per computed column. Real SQL Server
        // surfaces the full sys.columns shape plus definition / is_persisted /
        // uses_database_collation; the simulator ships the load-bearing subset
        // SMO's CREATE-scripting column query LEFT JOINs (object_id / column_id
        // / definition / is_persisted). definition carries the captured
        // parenthesized source text of the AS (…) body (HeapColumn.ComputedDefinition),
        // so DacFx / SMO re-emit a re-parseable computed-column DDL.
        // See docs/claude/catalog-views.md.
        Sys("computed_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("column_id", SqlType.Int32, null, false),
            new("is_nullable", SqlType.Bit, null, true),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_database_collation", SqlType.Bit, null, false),
            new("is_persisted", SqlType.Bit, null, false),
            new("is_computed", SqlType.Bit, null, true),
        ], EnumerateComputedColumns);

        // sys.identity_columns: one row per IDENTITY column. seed_value /
        // increment_value / last_value are first-class sql_variant, each
        // carrying the identity column's declared type as its inner base type
        // (int → int, bigint → bigint, decimal(p, s) → decimal —
        // probe-confirmed against SQL Server 2025). SMO's CREATE-scripting
        // column query reads seed_value / increment_value /
        // is_not_for_replication. last_value tracks the identity high-water
        // mark (NULL, as a NULL sql_variant, before the first insert).
        // is_identity is bit NOT NULL and always 1 — every row in this view
        // is, by definition, an identity column (probe-confirmed SQL Server
        // 2025; appended here after the load-bearing subset, name-addressable).
        // SQLAlchemy's get_columns LEFT JOINs this view and reads is_identity.
        Sys("identity_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("column_id", SqlType.Int32, null, false),
            new("seed_value", SqlType.SqlVariant, null, true),
            new("increment_value", SqlType.SqlVariant, null, true),
            new("last_value", SqlType.SqlVariant, null, true),
            new("is_not_for_replication", SqlType.Bit, null, true),
            new("is_identity", SqlType.Bit, null, false),
        ], EnumerateIdentityColumns);

        // sys.column_encryption_keys / sys.sensitivity_classifications: Always
        // Encrypted CEKs and data-classification labels aren't modeled, so both
        // are empty views with the documented SQL Server 2025 shape. SMO's
        // CREATE-scripting column query joins both (CEK by name for
        // ColumnEncryptionKeyName, classifications for sensitivity metadata).
        Sys("column_encryption_keys",
        [
            new("name", SqlType.SystemName, 128, false),
            new("column_encryption_key_id", SqlType.Int32, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
        ], static (batch, database) => []);

        Sys("sensitivity_classifications",
        [
            new("class", SqlType.Int32, null, false),
            new("class_desc", SqlType.Varchar, 16, false),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("label", SqlType.SystemName, 128, true),
            new("label_id", SqlType.SystemName, 128, true),
            new("information_type", SqlType.SystemName, 128, true),
            new("information_type_id", SqlType.SystemName, 128, true),
            new("rank", SqlType.Int32, null, true),
            new("rank_desc", SqlType.Varchar, 8, true),
        ], static (batch, database) => []);

        // sys.database_recovery_status / sys.change_tracking_databases /
        // sys.database_filestream_options: recovery-fork bookkeeping, database
        // change tracking, and FILESTREAM options aren't modeled, so all three
        // are empty views with the documented SQL Server 2025 shape. SMO's
        // database-properties preamble LEFT JOINs each by database_id; an empty
        // projection resolves each property to its ISNULL default.
        Sys("database_recovery_status",
        [
            new("database_id", SqlType.Int32, null, false),
            new("database_guid", SqlType.UniqueIdentifier, null, true),
            new("family_guid", SqlType.UniqueIdentifier, null, true),
            new("last_log_backup_lsn", SqlType.GetDecimal(25, 0), null, true),
            new("recovery_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("first_recovery_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("fork_point_lsn", SqlType.GetDecimal(25, 0), null, true),
        ], static (batch, database) => []);

        Sys("change_tracking_databases",
        [
            new("database_id", SqlType.Int32, null, false),
            new("is_auto_cleanup_on", SqlType.TinyInt, null, true),
            new("retention_period", SqlType.Int32, null, true),
            new("retention_period_units", SqlType.TinyInt, null, true),
            new("retention_period_units_desc", nvarchar60Catalog, 60, true),
            new("max_cleanup_version", SqlType.BigInt, null, true),
        ], static (batch, database) => []);

        Sys("database_filestream_options",
        [
            new("database_id", SqlType.Int32, null, false),
            new("non_transacted_access", SqlType.TinyInt, null, false),
            new("non_transacted_access_desc", nvarchar60Catalog, 60, false),
            new("directory_name", SqlType.NVarchar, 256, true),
        ], static (batch, database) => []);

        // sys.external_data_sources: PolyBase / external data sources aren't
        // modeled, so this is an empty view with the documented SQL Server 2025
        // shape. SMO's CREATE-scripting external-table query joins it.
        Sys("external_data_sources",
        [
            new("data_source_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("location", SqlType.NVarchar, 4000, false),
            new("type_desc", SqlType.NVarchar, 255, true),
            new("type", SqlType.TinyInt, null, false),
            new("resource_manager_location", SqlType.NVarchar, 4000, true),
            new("credential_id", SqlType.Int32, null, false),
            new("database_name", SqlType.NVarchar, 128, true),
            new("shard_map_name", SqlType.NVarchar, 128, true),
            new("connection_options", SqlType.NVarchar, 4000, true),
            new("pushdown", SqlType.NVarchar, 256, false),
        ], static (batch, database) => []);

        // sys.external_file_formats: PolyBase external file formats aren't
        // modeled, so this is an empty view with the documented SQL Server 2025
        // shape. SMO's CREATE-scripting external-table query joins it.
        Sys("external_file_formats",
        [
            new("file_format_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("format_type", SqlType.NVarchar, 100, false),
            new("field_terminator", SqlType.NVarchar, 10, true),
            new("string_delimiter", SqlType.NVarchar, 10, true),
            new("date_format", SqlType.NVarchar, 50, true),
            new("use_type_default", SqlType.Bit, null, true),
            new("serde_method", SqlType.NVarchar, 255, true),
            new("row_terminator", SqlType.NVarchar, 10, true),
            new("encoding", SqlType.NVarchar, 10, true),
            new("data_compression", SqlType.NVarchar, 255, true),
            new("first_row", SqlType.Int32, null, true),
            new("parser_version", SqlType.NVarchar, 32, true),
        ], static (batch, database) => []);

        // sys.sql_modules: one row per programmable module (procedure / view /
        // DML + DDL trigger / scalar / inline / multi-statement function),
        // keyed by object_id. The definition column carries the verbatim
        // CREATE-statement source captured at CREATE / ALTER time
        // (SchemaObject.DefinitionText); NULL for WITH ENCRYPTION modules.
        // The boolean flags are probe-confirmed placeholder constants
        // (uses_ansi_nulls / uses_quoted_identifier default ON, the rest OFF);
        // null_on_null_input reflects a scalar function's RETURNS NULL ON NULL
        // INPUT declaration. execute_as_principal_id is always NULL (no
        // EXECUTE AS principal modeled).
        Sys("sql_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_ansi_nulls", SqlType.Bit, null, true),
            new("uses_quoted_identifier", SqlType.Bit, null, true),
            new("is_schema_bound", SqlType.Bit, null, true),
            new("uses_database_collation", SqlType.Bit, null, true),
            new("is_recompiled", SqlType.Bit, null, true),
            new("null_on_null_input", SqlType.Bit, null, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
            new("uses_native_compilation", SqlType.Bit, null, true),
            new("inline_type", SqlType.Bit, null, true),
            new("is_inlineable", SqlType.Bit, null, true),
        ], EnumerateSqlModules);

        // sys.system_sql_modules: same shape as sys.sql_modules but scoped to
        // system objects' module definitions. The simulator ships no
        // system-defined modules with stored T-SQL, so this is always empty —
        // which is exactly what SMO's SSMS Object-Explorer trigger sub-node
        // query needs (it LEFT JOINs sys.system_sql_modules to distinguish a
        // WITH ENCRYPTION module, and user triggers never appear here).
        Sys("system_sql_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_ansi_nulls", SqlType.Bit, null, true),
            new("uses_quoted_identifier", SqlType.Bit, null, true),
            new("is_schema_bound", SqlType.Bit, null, true),
            new("uses_database_collation", SqlType.Bit, null, true),
            new("is_recompiled", SqlType.Bit, null, true),
            new("null_on_null_input", SqlType.Bit, null, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
            new("uses_native_compilation", SqlType.Bit, null, true),
            new("inline_type", SqlType.Bit, null, true),
            new("is_inlineable", SqlType.Bit, null, true),
        ], static (batch, database) => []);

        // sys.all_sql_modules shares sys.sql_modules' shape and row generator —
        // user-module parity, like sys.all_objects / sys.all_views. The
        // simulator ships no system modules with stored T-SQL, so the union of
        // system + user modules is just the user modules. SMO's Script-As
        // trigger query LEFT JOINs sys.all_sql_modules to read
        // uses_native_compilation / is_schema_bound off the trigger body.
        Sys("all_sql_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_ansi_nulls", SqlType.Bit, null, true),
            new("uses_quoted_identifier", SqlType.Bit, null, true),
            new("is_schema_bound", SqlType.Bit, null, true),
            new("uses_database_collation", SqlType.Bit, null, true),
            new("is_recompiled", SqlType.Bit, null, true),
            new("null_on_null_input", SqlType.Bit, null, true),
            new("execute_as_principal_id", SqlType.Int32, null, true),
            new("uses_native_compilation", SqlType.Bit, null, true),
            new("inline_type", SqlType.Bit, null, true),
            new("is_inlineable", SqlType.Bit, null, true),
        ], EnumerateSqlModules);
    }

    /// <summary>
    /// Rows for <c>sys.computed_columns</c>: one row per computed column
    /// across every table. <c>definition</c> is the captured parenthesized
    /// source text of the computed expression (NULL only if a column somehow
    /// lacks captured text).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateComputedColumns(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var nullDefinition = SqlValue.Null(SqlType.NVarchar);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                var objectId = SqlValue.FromInt32(t.ObjectId);
                for (var i = 0; i < t.Columns.Length; i++)
                {
                    var col = t.Columns[i];
                    if (col.Computed is null)
                        continue;
                    yield return [
                        objectId,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromBoolean(col.Nullable),
                        col.ComputedDefinition is { } def ? SqlValue.FromNVarchar(def) : nullDefinition,
                        falseBit,
                        SqlValue.FromBoolean(col.IsPersisted),
                        trueBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.identity_columns</c>: one row per IDENTITY column.
    /// seed / increment / last are sql_variant carrying the column's declared
    /// type as inner base type. last_value is the identity high-water mark,
    /// a NULL sql_variant before the first insert.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateIdentityColumns(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var nullLast = SqlValue.Null(SqlType.SqlVariant);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                var objectId = SqlValue.FromInt32(t.ObjectId);
                for (var i = 0; i < t.Columns.Length; i++)
                {
                    var col = t.Columns[i];
                    if (col.Identity is not { } identity)
                        continue;
                    yield return [
                        objectId,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        IdentityVariant(identity.Seed, col.Type),
                        IdentityVariant(identity.Increment, col.Type),
                        identity.Snapshot() is { } last ? IdentityVariant(last, col.Type) : nullLast,
                        SqlValue.FromBoolean(identity.NotForReplication),
                        trueBit,
                    ];
                }
            }
            // Table types surface their IDENTITY columns here too, keyed by
            // type_table_object_id (parity with sys.columns, which sets
            // is_identity=true for them). The template is never inserted into,
            // so last_value is always NULL — matching real SQL Server, which
            // reports seed/increment but a NULL high-water mark for a table
            // type's identity column. DacFx's table-type column populator LEFT
            // JOINs this view to read seed_value / increment_value.
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.ObjectId))
            {
                var objectId = SqlValue.FromInt32(tt.ObjectId);
                for (var i = 0; i < tt.Columns.Length; i++)
                {
                    var col = tt.Columns[i];
                    if (col.Identity is not { } identity)
                        continue;
                    yield return [
                        objectId,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        IdentityVariant(identity.Seed, col.Type),
                        IdentityVariant(identity.Increment, col.Type),
                        nullLast,
                        falseBit,
                        trueBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Wraps an identity seed / increment / high-water-mark
    /// <see cref="long"/> as a sql_variant carrying the identity column's
    /// declared type as its inner base type — the projection form
    /// <c>sys.identity_columns</c>'s seed_value / increment_value / last_value
    /// use (each a sql_variant in real SQL Server). The stored value always
    /// fits the declared numeric type (enforced at column creation).
    /// </summary>
    private static SqlValue IdentityVariant(long value, SqlType columnType) =>
        SqlValue.FromVariant(SqlValue.FromInt64(value).CoerceTo(columnType));

    /// <summary>
    /// Rows for <c>sys.sql_modules</c>: one per programmable module across
    /// every schema (procedure / view / DML trigger / scalar / inline /
    /// multi-statement function) plus the database-scoped DDL triggers. The
    /// definition column is <see cref="SchemaObject.DefinitionText"/> (NULL for
    /// WITH ENCRYPTION). Flag columns are probe-confirmed placeholder constants
    /// except <c>null_on_null_input</c>, which reads a scalar function's
    /// RETURNS NULL ON NULL INPUT declaration.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSqlModules(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var on = SqlValue.FromBoolean(true);
        var off = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);

        SqlValue[] Row(SchemaObject obj) =>
        [
            SqlValue.FromInt32(obj.ObjectId),
            obj.DefinitionText is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(obj.DefinitionText),
            on,  // uses_ansi_nulls
            on,  // uses_quoted_identifier
            obj is View { IsSchemaBound: true } ? on : off, // is_schema_bound
            off, // uses_database_collation
            off, // is_recompiled
            obj is ScalarFunction { ReturnsNullOnNullInput: true } ? on : off, // null_on_null_input
            nullPrincipal, // execute_as_principal_id
            off, // uses_native_compilation
            off, // inline_type
            off, // is_inlineable
        ];

        foreach (var schema in database.Schemas.Values)
        {
            foreach (var obj in schema.SchemaObjects().OrderBy(o => o.ObjectId))
            {
                if (obj is Procedure or View or Trigger or UserDefinedFunction)
                    yield return Row(obj);
            }
        }
        foreach (var ddlTrigger in database.DdlTriggers.Values.OrderBy(t => t.ObjectId))
            yield return Row(ddlTrigger);
    }
}
