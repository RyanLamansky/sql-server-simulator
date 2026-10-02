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
            database.Schemas.EnumerateValues()
                .SelectMany(s => CatalogTables(s, batch))
                .Where(t => t.PeriodColumns is not null && !t.IsHistoryTable && !t.PeriodInheritedFromBase)
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

        // sys.change_tracking_tables: one row per tracked table of the
        // database, in object-id order. begin_version and min_valid_version
        // read the same value, since nothing is cleaned up.
        Sys("change_tracking_tables",
        [
            new("object_id", SqlType.Int32, null, false),
            new("is_track_columns_updated_on", SqlType.Bit, null, false),
            new("min_valid_version", SqlType.BigInt, null, true),
            new("begin_version", SqlType.BigInt, null, true),
            new("cleanup_version", SqlType.BigInt, null, true),
        ], static (batch, database) =>
            database.Schemas.EnumerateValues()
                .SelectMany(s => s.HeapTables.EnumerateValues())
                .Where(t => t.ChangeTracking is not null)
                .OrderBy(t => t.ObjectId)
                .Select(t => new SqlValue[]
                {
                    SqlValue.FromInt32(t.ObjectId),
                    SqlValue.FromBoolean(t.ChangeTracking!.TrackColumnsUpdated),
                    SqlValue.FromInt64(t.ChangeTracking.MinValidVersion),
                    SqlValue.FromInt64(t.ChangeTracking.MinValidVersion),
                    SqlValue.Null(SqlType.BigInt),
                }));

        // sys.external_tables / sys.filetables: PolyBase external tables and
        // FileTables aren't modeled, so each is an empty view with the
        // documented SQL Server 2025 shape. SMO's CREATE-scripting table query
        // LEFT JOINs both to detect those table flavors; the empty projection
        // resolves the join to "not one of these".
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
            new("directory_name", NVarcharSqlType.Get(256, Collation.Catalog, Coercibility.Implicit), 256, false),
            new("filename_collation_id", SqlType.Int32, null, false),
            new("filename_collation_name", SqlType.NVarchar, 129, false),
        ], static (batch, database) => []);

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

        // sys.database_recovery_status: one row per database, a never-restored
        // database's family and recovery fork one GUID and no log backup
        // (probed 2026-09-30 against SQL Server 2025). SMO's
        // database-properties preamble LEFT JOINs it by database_id for
        // DatabaseGuid / RecoveryForkGuid / HasFullBackup.
        Sys("database_recovery_status",
        [
            new("database_id", SqlType.Int32, null, false),
            new("database_guid", SqlType.UniqueIdentifier, null, true),
            new("family_guid", SqlType.UniqueIdentifier, null, true),
            new("last_log_backup_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
            new("recovery_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("first_recovery_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("fork_point_lsn", SqlType.GetDecimal(25, 0), null, true, spelledNumeric: true),
        ], static (batch, database) =>
            Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation)
                .Select(entry => new SqlValue[]
                {
                    SqlValue.FromInt32(entry.Id),
                    SqlValue.FromGuid(entry.Database.DatabaseGuid),
                    SqlValue.FromGuid(entry.Database.RecoveryForkGuid),
                    SqlValue.Null(SqlType.GetDecimal(25, 0)),
                    SqlValue.FromGuid(entry.Database.RecoveryForkGuid),
                    SqlValue.Null(SqlType.UniqueIdentifier),
                    SqlValue.Null(SqlType.GetDecimal(25, 0)),
                }));

        // sys.database_filestream_options: FILESTREAM isn't modeled, so the
        // view is empty with the documented SQL Server 2025 shape.

        Sys("change_tracking_databases",
        [
            new("database_id", SqlType.Int32, null, false),
            new("is_auto_cleanup_on", SqlType.TinyInt, null, true),
            new("retention_period", SqlType.Int32, null, true),
            new("retention_period_units", SqlType.TinyInt, null, true),
            new("retention_period_units_desc", NVarcharSqlType.Get(60, Collation.Baseline, Coercibility.Implicit), 60, true),
            new("max_cleanup_version", SqlType.BigInt, null, true),
        ], static (batch, database) =>
            // Server-wide like sys.databases: every tracking database, from any
            // database's copy of the view.
            Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation)
                .Where(entry => entry.Database.ChangeTracking is not null)
                .Select(entry => new SqlValue[]
                {
                    SqlValue.FromInt32(entry.Id),
                    SqlValue.FromByte(entry.Database.ChangeTracking!.AutoCleanup ? (byte)1 : (byte)0),
                    SqlValue.FromInt32(entry.Database.ChangeTracking.RetentionPeriod),
                    SqlValue.FromByte((byte)entry.Database.ChangeTracking.RetentionUnit),
                    SqlValue.FromNVarchar(entry.Database.ChangeTracking.RetentionUnit.ToString().ToUpperInvariant()),
                    SqlValue.Null(SqlType.BigInt),
                }));

        Sys("database_filestream_options",
        [
            new("database_id", SqlType.Int32, null, false),
            new("non_transacted_access", SqlType.TinyInt, null, false),
            new("non_transacted_access_desc", nvarchar60Catalog, 60, false),
            new("directory_name", NVarcharSqlType.Get(256, Collation.Catalog, Coercibility.Implicit), 256, true),
        ], static (batch, database) => []);

        // sys.external_data_sources: PolyBase / external data sources aren't
        // modeled, so this is an empty view with the documented SQL Server 2025
        // shape. SMO's CREATE-scripting external-table query joins it.
        Sys("external_data_sources",
        [
            new("data_source_id", SqlType.Int32, null, false),
            new("name", nvarchar128Baseline, 128, false),
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
            new("name", nvarchar128Baseline, 128, false),
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
        // uses_ansi_nulls / uses_quoted_identifier carry the module's
        // creation-time SET-option capture, null_on_null_input a scalar
        // function's RETURNS NULL ON NULL INPUT declaration,
        // execute_as_principal_id the resolved WITH EXECUTE AS principal, and
        // the inline_type / is_inlineable pair the scalar-UDF-inlining
        // analysis, is_recompiled a procedure's WITH RECOMPILE, and
        // uses_database_collation the probed rule in EnumerateSqlModules;
        // uses_native_compilation is a probe-confirmed placeholder constant.
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

        // sys.system_sql_modules: same shape as sys.sql_modules, one row per
        // system module (every system object but an extended procedure), with
        // real's flags and a NULL definition — the simulator doesn't carry
        // real's system module text. SMO reads a system procedure's Recompile
        // and a view's module flags from it.
        Sys("system_sql_modules",
        [
            new("object_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_ansi_nulls", SqlType.Bit, null, false),
            new("uses_quoted_identifier", SqlType.Bit, null, false),
            new("is_schema_bound", SqlType.Bit, null, false),
            new("uses_database_collation", SqlType.Bit, null, false),
            new("is_recompiled", SqlType.Bit, null, false),
            new("null_on_null_input", SqlType.Bit, null, false),
            new("execute_as_principal_id", SqlType.Int32, null, true),
            new("uses_native_compilation", SqlType.Bit, null, false),
            new("inline_type", SqlType.Bit, null, false),
            new("is_inlineable", SqlType.Bit, null, false),
        ], static (batch, database) => EnumerateSystemSqlModules());

        // sys.all_sql_modules is the union of sys.system_sql_modules and
        // sys.sql_modules, like sys.all_objects / sys.all_views. SMO's Script-As
        // trigger query LEFT JOINs it to read uses_native_compilation /
        // is_schema_bound off the trigger body.
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
        ], static (batch, database) => EnumerateSystemSqlModules().Concat(EnumerateSqlModules(batch, database)));
    }

    /// <summary>
    /// Wraps an identity seed / increment / high-water-mark
    /// <see cref="long"/> as a sql_variant carrying the identity column's
    /// declared type as its inner base type — the projection form
    /// <c>sys.identity_columns</c>'s seed_value / increment_value / last_value
    /// use (each a sql_variant in real SQL Server). The stored value always
    /// fits the declared numeric type (enforced at column creation).
    /// </summary>
    private static SqlValue IdentityVariant(Int128 value, SqlType columnType) =>
        SqlValue.FromVariant(IdentityState.ToSqlValue(value, columnType));

    /// <summary>
    /// Rows for <c>sys.sql_modules</c>: one per programmable module across
    /// every schema (procedure / view / DML trigger / scalar / inline /
    /// multi-statement function) plus the database-scoped DDL triggers. The
    /// definition column is <see cref="SchemaObject.DefinitionText"/> (NULL for
    /// WITH ENCRYPTION). <c>uses_ansi_nulls</c> / <c>uses_quoted_identifier</c>
    /// read the module's creation-time SET-option captures,
    /// <c>null_on_null_input</c> a scalar function's RETURNS NULL ON NULL INPUT
    /// declaration, <c>execute_as_principal_id</c> the resolved WITH EXECUTE AS
    /// principal (<see cref="SchemaObject.ExecuteAsPrincipalId"/>), and the
    /// <c>inline_type</c> / <c>is_inlineable</c> pair
    /// <see cref="ModuleInlining"/>, <c>is_recompiled</c> a procedure's
    /// <c>WITH RECOMPILE</c>; the rest are probe-confirmed placeholder
    /// constants.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSqlModules(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        var on = SqlValue.FromBoolean(true);
        var off = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);

        SqlValue[] Row(SchemaObject obj)
        {
            var (inlineType, isInlineable) = ModuleInlining.Evaluate(obj);
            return
            [
                SqlValue.FromInt32(obj.ObjectId),
                obj.DefinitionText is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(obj.DefinitionText),
                obj.UsesAnsiNulls ? on : off, // uses_ansi_nulls
                obj.UsesQuotedIdentifier ? on : off, // uses_quoted_identifier
                obj is View { IsSchemaBound: true } or UserDefinedFunction { IsSchemaBound: true } ? on : off, // is_schema_bound
                // Real reports every schema-bound module as depending on the
                // database collation, whatever it reads (probed 2026-09-26),
                // and a multi-statement function whose return table declares a
                // string column without a COLLATE (probed 2026-10-02).
                obj is View { IsSchemaBound: true } or UserDefinedFunction { IsSchemaBound: true }
                    || (obj is MultiStatementTableValuedFunction multiStatement
                        && Array.Exists(multiStatement.OutputColumns, column => column.Collation is null && SqlType.IsCollatedString(column.Type))) ? on : off, // uses_database_collation
                obj is Procedure { RecompilesEveryCall: true } ? on : off, // is_recompiled
                obj is ScalarFunction { ReturnsNullOnNullInput: true } ? on : off, // null_on_null_input
                obj.ExecuteAsPrincipalId is { } principalId ? SqlValue.FromInt32(principalId) : nullPrincipal,
                off, // uses_native_compilation
                inlineType ? on : off,
                isInlineable ? on : off,
            ];
        }

        foreach (var (_, schema) in database.Schemas)
        {
            foreach (var obj in schema.SchemaObjects().OrderBy(o => o.ObjectId))
            {
                // A CLR routine has no T-SQL body, so real SQL Server gives it
                // no sys.sql_modules row (probe-confirmed) — it appears only in
                // sys.assembly_modules.
                if (SchemaObject.IsSqlModule(obj) || obj is BindableObject)
                    yield return Row(obj);
            }
        }
        foreach (var ddlTrigger in database.DdlTriggers.EnumerateValues().OrderBy(t => t.ObjectId))
        {
            if (SchemaObject.IsSqlModule(ddlTrigger))
                yield return Row(ddlTrigger);
        }
    }

    /// <summary>
    /// Rows for <c>sys.system_sql_modules</c>: every system module ANSI_NULLS on,
    /// QUOTED_IDENTIFIER per <see cref="SystemObject.UsesQuotedIdentifier"/>, and
    /// every other flag off (probed 2026-09-30 against SQL Server 2025). The
    /// definition is NULL where real's carries the module's text.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSystemSqlModules()
    {
        var on = SqlValue.FromBoolean(true);
        var off = SqlValue.FromBoolean(false);
        var nullDefinition = SqlValue.Null(SqlType.NVarchar);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        foreach (var system in SystemObjects.Value)
        {
            if (!system.IsModule)
                continue;
            yield return
            [
                SqlValue.FromInt32(system.ObjectId),
                nullDefinition,
                on, // uses_ansi_nulls
                system.UsesQuotedIdentifier ? on : off,
                off, // is_schema_bound
                off, // uses_database_collation
                off, // is_recompiled
                off, // null_on_null_input
                nullPrincipal,
                off, // uses_native_compilation
                off, // inline_type
                off, // is_inlineable
            ];
        }
    }
}
