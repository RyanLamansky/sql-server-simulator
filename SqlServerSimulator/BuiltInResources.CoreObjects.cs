using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    private static void RegisterCoreObjects(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        // sys.schemas: (name sysname, schema_id int, principal_id int null)
        Sys("schemas",
        [
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
        ], (batch, database) =>
            database.Schemas.Values.OrderBy(s => s.SchemaId).Select(s => new SqlValue[]
            {
                SqlValue.FromSystemName(s.Name),
                SqlValue.FromInt32(s.SchemaId),
                SqlValue.Null(SqlType.Int32),
            }));

        // sys.tables: object_id / name / schema_id / type / type_desc /
        // create_date / modify_date / is_ms_shipped + temporal_type /
        // temporal_type_desc / history_table_id (temporal-table state).
        // Real SQL Server has many more columns; the shipped subset covers
        // the dominant query shapes. type is char(2) with trailing space
        // ('U ') — probe-confirmed.
        var tableType = SqlValue.FromChar(charTwo, "U ");
        var tableTypeDesc = SqlValue.FromNVarchar("USER_TABLE");
        // temporal_type: 0 = NON_TEMPORAL_TABLE, 1 = HISTORY_TABLE,
        // 2 = SYSTEM_VERSIONED_TEMPORAL_TABLE (probe-confirmed).
        var temporalTypeNone = SqlValue.FromByte(0);
        var temporalTypeHistory = SqlValue.FromByte(1);
        var temporalTypeBase = SqlValue.FromByte(2);
        var temporalDescNone = SqlValue.FromNVarchar("NON_TEMPORAL_TABLE");
        var temporalDescHistory = SqlValue.FromNVarchar("HISTORY_TABLE");
        var temporalDescBase = SqlValue.FromNVarchar("SYSTEM_VERSIONED_TEMPORAL_TABLE");
        var falseTableFlag = SqlValue.FromBoolean(false);
        var ledgerTypeNone = SqlValue.FromByte(0);
        var lockEscalationTable = SqlValue.FromString(nvarchar60Catalog, "TABLE");
        var durabilityDescSchemaAndData = SqlValue.FromString(nvarchar60Catalog, "SCHEMA_AND_DATA");
        Sys("tables",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            // No explicit table owner is modeled (ownership follows the
            // schema), so principal_id is always NULL — matching real SQL
            // Server for tables without an AUTHORIZATION override. SMO's
            // CREATE-scripting table query reads it.
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("temporal_type", SqlType.TinyInt, null, true),
            new("temporal_type_desc", nvarchar60Catalog, 60, true),
            new("history_table_id", SqlType.Int32, null, true),
            // Table-flavor flags SMO's Object-Explorer Tables node filters on.
            // None of these table kinds are modeled (memory-optimized,
            // filetable, external/PolyBase, graph node/edge, ledger), so each
            // ships as a constant 0. ledger_type is tinyint (0 = NON_LEDGER_TABLE,
            // probe-confirmed non-null on SQL Server 2025). See docs/claude/catalog-views.md.
            new("is_memory_optimized", SqlType.Bit, null, false),
            new("is_filetable", SqlType.Bit, null, false),
            new("is_external", SqlType.Bit, null, false),
            new("is_node", SqlType.Bit, null, false),
            new("is_edge", SqlType.Bit, null, false),
            // Only memory-optimized tables have a non-default durability; every
            // simulator table is disk-based, so durability is a constant 0 /
            // SCHEMA_AND_DATA. SMO's CREATE-scripting table query reads it.
            new("durability", SqlType.TinyInt, null, true),
            new("durability_desc", nvarchar60Catalog, 60, true),
            new("ledger_type", SqlType.TinyInt, null, false),
            // Ledger isn't modeled, so ledger_view_id (the object_id of the
            // ledger view over an append-only / updatable ledger table) is
            // always NULL — SMO's CREATE-scripting table query selects
            // t.ledger_view_id to detect a ledger table.
            new("ledger_view_id", SqlType.Int32, null, true),
            // uses_ansi_nulls reflects the SET ANSI_NULLS state at CREATE time;
            // every simulator table is created under ANSI_NULLS ON, so it is a
            // constant 1. is_dropped_ledger_table is 0 (ledger unmodeled); SMO's
            // CREATE-scripting table query reads both.
            new("uses_ansi_nulls", SqlType.Bit, null, true),
            new("is_dropped_ledger_table", SqlType.Bit, null, true),
            // Lock escalation isn't tunable in the simulator, so every table
            // reports the default TABLE escalation (0 / TABLE). SMO's
            // CREATE-scripting table query reads lock_escalation to emit the
            // LOCK_ESCALATION option when it differs from the default.
            new("lock_escalation", SqlType.TinyInt, null, true),
            new("lock_escalation_desc", nvarchar60Catalog, 60, true),
            // FILESTREAM isn't modeled, so filestream_data_space_id is always
            // NULL — SMO's index-scripting query LEFT JOINs sys.data_spaces on
            // it to detect a FILESTREAM filegroup / partition scheme.
            new("filestream_data_space_id", SqlType.Int32, null, true),
            // The simulator models a single implicit PRIMARY filegroup with no
            // separate LOB data space, so lob_data_space_id is a constant 0
            // (probe-confirmed non-null; 0 = no distinct LOB filegroup). SMO's
            // CREATE-scripting table query LEFT JOINs sys.data_spaces on it to
            // emit the TEXTIMAGE_ON clause; 0 suppresses it, matching the
            // single-filegroup model.
            new("lob_data_space_id", SqlType.Int32, null, false),
            // Transactional / merge replication isn't modeled, so no table is an
            // article — is_replicated is a constant 0 (nullable in real SQL
            // Server). SMO's Table property-bag query projects tbl.is_replicated
            // AS [Replicated]; without the column the whole bag query fails
            // Msg 207 and every Table property errors.
            new("is_replicated", SqlType.Bit, null, true),
            // BULK INSERT / bcp table-lock behavior isn't modeled, so
            // lock_on_bulk_load is a constant 0 (the fresh-table default,
            // probe-confirmed bit non-null on SQL Server 2025). DacFx's
            // bacpac-export reverse-engineering reads
            // CAST([st].[lock_on_bulk_load] AS bit).
            new("lock_on_bulk_load", SqlType.Bit, null, false),
            // Replication isn't modeled, so the remaining publication flags
            // are constant 0 alongside is_replicated. DacFx's table
            // reverse-engineering reads all four in one ReplInfo CASE.
            new("is_merge_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
            new("is_published", SqlType.Bit, null, false),
            // Remaining columns DacFx's SqlTable reverse-engineering reads.
            // text-in-row / large-value storage options, CDC, and Stretch
            // aren't modeled (constant 0 / false); the temporal history
            // retention pair is -1/-1 (INFINITE) on a versioned table and
            // NULL/NULL on history and non-temporal tables — all
            // probe-confirmed against SQL Server 2025.
            new("text_in_row_limit", SqlType.Int32, null, false),
            new("large_value_types_out_of_row", SqlType.Bit, null, false),
            new("is_tracked_by_cdc", SqlType.Bit, null, false),
            new("is_remote_data_archive_enabled", SqlType.Bit, null, false),
            new("history_retention_period", SqlType.Int32, null, true),
            new("history_retention_period_unit", SqlType.Int32, null, true),
        ], (batch, database) =>
            database.Schemas.Values
                .SelectMany(s => s.HeapTables.Values)
                .OrderBy(t => t.ObjectId)
                .Select(t =>
                {
                    var (tt, ttd, htid) = (t.SystemVersioning, t.IsHistoryTable) switch
                    {
                        ({ } hist, _) => (temporalTypeBase, temporalDescBase, SqlValue.FromInt32(hist.ObjectId)),
                        (_, true) => (temporalTypeHistory, temporalDescHistory, SqlValue.Null(SqlType.Int32)),
                        _ => (temporalTypeNone, temporalDescNone, SqlValue.Null(SqlType.Int32)),
                    };
                    return new SqlValue[]
                    {
                        SqlValue.FromInt32(t.ObjectId),
                        SqlValue.FromSystemName(t.Name),
                        SqlValue.FromInt32(t.SchemaId),
                        SqlValue.Null(SqlType.Int32),
                        tableType,
                        tableTypeDesc,
                        SqlValue.FromDateTime(t.CreateDate),
                        SqlValue.FromDateTime(t.ModifyDate),
                        notMsShipped,
                        tt,
                        ttd,
                        htid,
                        falseTableFlag,
                        falseTableFlag,
                        falseTableFlag,
                        falseTableFlag,
                        falseTableFlag,
                        SqlValue.FromByte(0),
                        durabilityDescSchemaAndData,
                        ledgerTypeNone,
                        SqlValue.Null(SqlType.Int32),
                        SqlValue.FromBoolean(true),
                        falseTableFlag,
                        SqlValue.FromByte(0),
                        lockEscalationTable,
                        SqlValue.Null(SqlType.Int32),
                        SqlValue.FromInt32(0),
                        falseTableFlag,
                        falseTableFlag, // lock_on_bulk_load
                        falseTableFlag, // is_merge_published
                        falseTableFlag, // is_schema_published
                        falseTableFlag, // is_published
                        SqlValue.FromInt32(0),
                        falseTableFlag, // large_value_types_out_of_row
                        falseTableFlag, // is_tracked_by_cdc
                        falseTableFlag, // is_remote_data_archive_enabled
                        t.SystemVersioning is not null ? SqlValue.FromInt32(-1) : SqlValue.Null(SqlType.Int32),
                        t.SystemVersioning is not null ? SqlValue.FromInt32(-1) : SqlValue.Null(SqlType.Int32),
                    };
                }));

        // sys.objects: every <see cref="SchemaObject"/> emits one row, plus
        // one extra row per HeapTable PK / UQ / CHECK constraint linked via
        // parent_object_id. type / type_desc come from the SchemaObject's
        // own ObjectTypeCode / ObjectTypeDescription (probe-confirmed
        // values: 'U ' / USER_TABLE, 'V ' / VIEW, 'P ' / SQL_STORED_PROCEDURE,
        // 'FN' / SQL_SCALAR_FUNCTION, 'IF' / SQL_INLINE_TABLE_VALUED_FUNCTION,
        // 'TR' / SQL_TRIGGER). Constraint codes ('PK', 'UQ', 'C ') live
        // outside the SchemaObject contract since constraints aren't first-
        // class schema objects.
        var pkType = SqlValue.FromChar(charTwo, "PK");
        var pkTypeDesc = SqlValue.FromNVarchar("PRIMARY_KEY_CONSTRAINT");
        var uqType = SqlValue.FromChar(charTwo, "UQ");
        var uqTypeDesc = SqlValue.FromNVarchar("UNIQUE_CONSTRAINT");
        var checkType = SqlValue.FromChar(charTwo, "C ");
        var checkTypeDesc = SqlValue.FromNVarchar("CHECK_CONSTRAINT");
        var zeroParent = SqlValue.FromInt32(0);
        Sys("objects",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            // No explicit object owner is modeled (ownership follows the
            // schema), so principal_id is always NULL — matching real SQL
            // Server for objects without an AUTHORIZATION override. SMO's
            // Object-Explorer function / procedure / sequence enumeration
            // reads ISNULL(o.principal_id, OBJECTPROPERTY(o.object_id,'OwnerId')).
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
        ], (batch, database) =>
            EnumerateObjects(batch, database, charTwo, pkType, pkTypeDesc, uqType, uqTypeDesc, checkType, checkTypeDesc, zeroParent, notMsShipped));

        // sys.all_objects: real SQL Server's superset of sys.objects that also
        // surfaces system objects. SMO correlates only on user-object ids, so
        // the identical user-object row set suffices (same parity contract as
        // sys.all_columns vs sys.columns).
        Sys("all_objects",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
        ], (batch, database) =>
            EnumerateObjects(batch, database, charTwo, pkType, pkTypeDesc, uqType, uqTypeDesc, checkType, checkTypeDesc, zeroParent, notMsShipped));

        // sys.synonyms: schema-scoped synonym catalog. CREATE SYNONYM isn't
        // modeled (the parser rejects it at Msg 102), so the view is always
        // empty — but SSMS's "Edit Top 200 Rows" commit probes whether the
        // edit target is a synonym via a three-part `[db].sys.synonyms` read,
        // which must resolve and return zero rows rather than Msg 208. Column
        // shape probe-confirmed against SQL Server 2025 (name sysname,
        // base_object_name nvarchar(1035), type char(2), type_desc nvarchar(60)).
        Sys("synonyms",
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
            new("base_object_name", SqlType.NVarchar, 1035, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.columns: load-bearing subset of real SQL Server's column set.
        // Probe-confirmed (2026-05-11): max_length is byte-length (4 for int,
        // 100 for nvarchar(50), 5 for char(5), 16 for uniqueidentifier, 7 for
        // datetime2(3), 9 for decimal(10,2)); -1 for *(MAX); 16 (LOB pointer)
        // for text/ntext/image. precision/scale only meaningful for numeric
        // and date/time types; 0 for everything else. collation_name set only
        // for string types.
        var systemTypeId = SqlType.TinyInt;
        var nullCollation = SqlValue.Null(SqlType.SystemName);
        // sys.columns / sys.all_columns share one shape. is_sparse ships as a
        // constant 0 (the simulator has no sparse-column storage); SMO's
        // Object-Explorer HasSparseColumn probe reads it off sys.all_columns.
        // sys.all_columns is user-object-parity with sys.columns here: real
        // SQL Server also surfaces system objects' negative-object_id columns,
        // but SMO correlates only on user tables' object_ids so the identical
        // user-column row set suffices. See docs/claude/catalog-views.md.
        HeapColumn[] ColumnsShape() =>
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("column_id", SqlType.Int32, null, false),
            new("system_type_id", systemTypeId, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", systemTypeId, null, false),
            new("scale", systemTypeId, null, false),
            new("is_nullable", SqlType.Bit, null, true),
            new("is_identity", SqlType.Bit, null, false),
            new("is_computed", SqlType.Bit, null, false),
            new("collation_name", SqlType.SystemName, 128, true),
            new("is_sparse", SqlType.Bit, null, true),
            // Probe-confirmed constants (SQL Server 2025, 2026-07-15) that SMO's
            // SSMS Object-Explorer column / index / key sub-node queries read
            // off sys.all_columns: no XML documents, column sets, dropped ledger
            // columns, or vector columns are modeled, so is_xml_document /
            // is_column_set / is_dropped_ledger_column are 0 and the vector_*
            // pair is NULL. xml_collection_id carries the bound schema
            // collection's id for a typed-xml column (0 when untyped / non-xml).
            new("is_xml_document", SqlType.Bit, null, false),
            new("xml_collection_id", SqlType.Int32, null, false),
            new("is_column_set", SqlType.Bit, null, true),
            new("is_dropped_ledger_column", SqlType.Bit, null, true),
            new("vector_dimensions", SqlType.Int32, null, true),
            new("vector_base_type_desc", SqlType.NVarchar, 20, true),
            // Ledger isn't modeled, so the ledger-view column-mapping pair is
            // always NULL — SMO's CREATE-scripting column query selects
            // ledger_view_column_type to detect a ledger-view column.
            new("ledger_view_column_type", SqlType.Int32, null, true),
            new("ledger_view_column_type_desc", nvarchar60Catalog, 60, true),
            // Probe-confirmed columns SMO's SSMS CREATE-scripting column query
            // reads off sys.all_columns. is_ansi_padded is derived from the
            // type; default_object_id points at the column's DEFAULT constraint
            // (0 when none); generated_always_type carries the temporal ROW
            // START / END marker; is_hidden reflects a HIDDEN period column.
            // Encryption (Always Encrypted), FILESTREAM, data masking, graph
            // tables, and rules aren't modeled, so those columns are NULL / 0.
            new("is_ansi_padded", SqlType.Bit, null, false),
            new("column_encryption_key_id", SqlType.Int32, null, true),
            new("default_object_id", SqlType.Int32, null, false),
            new("encryption_algorithm_name", SqlType.SystemName, 128, true),
            new("encryption_type", SqlType.Int32, null, true),
            new("generated_always_type", SqlType.TinyInt, null, true),
            new("graph_type", SqlType.Int32, null, true),
            new("is_filestream", SqlType.Bit, null, false),
            new("is_hidden", SqlType.Bit, null, true),
            new("is_masked", SqlType.Bit, null, false),
            new("is_rowguidcol", SqlType.Bit, null, false),
            new("rule_object_id", SqlType.Int32, null, false),
        ];
        IEnumerable<SqlValue[]> ColumnRows(Parser.BatchContext batch, Database database) =>
            EnumerateColumns(batch, database, defaultCollation, nullCollation);
        Sys("columns", ColumnsShape(), ColumnRows);
        Sys("all_columns", ColumnsShape(), ColumnRows);
    }

    private static IEnumerable<SqlValue[]> EnumerateColumns(
        Parser.BatchContext batch,
        Database database,
        SqlValue defaultCollation,
        SqlValue nullCollation)
    {
        _ = batch;
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var zeroInt = SqlValue.FromInt32(0);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var nullVectorBaseType = SqlValue.Null(NVarcharSqlType.Get(20, Collation.Catalog, Coercibility.Implicit));
        var nullLedgerViewColumnTypeDesc = SqlValue.Null(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit));
        // is_ansi_padded is 1 for char / varchar / nchar / nvarchar / binary /
        // varbinary (all simulator tables are created under ANSI_PADDING ON);
        // 0 for every other type, including the deprecated LOB types
        // text / ntext / image (probe-confirmed against SQL Server 2025). SMO's
        // CREATE-scripting column query reads it as [AnsiPaddingStatus].
        SqlValue AnsiPaddedFor(HeapColumn c) =>
            c.Type.SystemTypeId is 165 or 167 or 173 or 175 or 231 or 239 ? trueBit : falseBit;
        SqlValue DefaultObjectIdFor(HeapColumn c) =>
            c.DefaultConstraint is { } df ? SqlValue.FromInt32(df.ObjectId) : zeroInt;
        // xml_collection_id is the id of the schema collection a typed-xml
        // column binds (0 for untyped xml and every non-xml column — real SQL
        // Server's non-nullable-int convention for this column). The binding
        // lives on HeapColumn.XmlSchemaCollection, set by the xml(collection)
        // column DDL. View / inline-TVF output columns never carry a binding,
        // so their rows keep the 0 default below.
        SqlValue XmlCollectionIdFor(HeapColumn c) =>
            c.XmlSchemaCollection is { } coll ? SqlValue.FromInt32(coll.Id) : zeroInt;
        // The per-database default collation flows from CurrentDatabase.
        // The captured defaultCollation arg is a legacy fallback; today the
        // active database's CollationName drives the value, with per-column
        // overrides taking precedence when present.
        var dbDefaultCollation = SqlValue.FromSystemName(database.CollationName);
        _ = defaultCollation;
        SqlValue CollationFor(HeapColumn c) =>
            c.Type.Category != SqlTypeCategory.String ? nullCollation
            : c.Collation is { } overrideName ? SqlValue.FromSystemName(overrideName)
            : dbDefaultCollation;
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                var objectId = SqlValue.FromInt32(t.ObjectId);
                for (var i = 0; i < t.Columns.Length; i++)
                {
                    var col = t.Columns[i];
                    var (maxLength, precision, scale) = GetSysColumnMetadata(col);
                    yield return [
                        objectId,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(col.Type.SystemTypeId),
                        SqlValue.FromInt32(col.Type.UserTypeId),
                        SqlValue.FromInt16(maxLength),
                        SqlValue.FromByte(precision),
                        SqlValue.FromByte(scale),
                        SqlValue.FromBoolean(col.Nullable),
                        SqlValue.FromBoolean(col.Identity is not null),
                        SqlValue.FromBoolean(col.Computed is not null),
                        CollationFor(col),
                        falseBit,
                        falseBit,
                        XmlCollectionIdFor(col),
                        falseBit,
                        falseBit,
                        nullInt,
                        nullVectorBaseType,
                        nullInt,
                        nullLedgerViewColumnTypeDesc,
                        AnsiPaddedFor(col),
                        nullInt,
                        DefaultObjectIdFor(col),
                        nullSysName,
                        nullInt,
                        SqlValue.FromByte((byte)col.GeneratedAs),
                        nullInt,
                        falseBit,
                        SqlValue.FromBoolean(col.IsHidden),
                        falseBit,
                        SqlValue.FromBoolean(col.IsRowGuidCol),
                        zeroInt,
                    ];
                }
            }
            // Inline TVFs surface their output projection through sys.columns —
            // is_identity / is_computed always false (TVF output is a SELECT
            // projection, not a heap).
            foreach (var fn in schema.Functions.Values.OfType<InlineTableValuedFunction>().OrderBy(f => f.ObjectId))
            {
                var fnObjectId = SqlValue.FromInt32(fn.ObjectId);
                for (var i = 0; i < fn.OutputColumns.Length; i++)
                {
                    var col = fn.OutputColumns[i];
                    var (maxLength, precision, scale) = GetSysColumnMetadata(col);
                    yield return [
                        fnObjectId,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(col.Type.SystemTypeId),
                        SqlValue.FromInt32(col.Type.UserTypeId),
                        SqlValue.FromInt16(maxLength),
                        SqlValue.FromByte(precision),
                        SqlValue.FromByte(scale),
                        SqlValue.FromBoolean(col.Nullable),
                        falseBit,
                        falseBit,
                        CollationFor(col),
                        falseBit,
                        falseBit,
                        zeroInt,
                        falseBit,
                        falseBit,
                        nullInt,
                        nullVectorBaseType,
                        nullInt,
                        nullLedgerViewColumnTypeDesc,
                        AnsiPaddedFor(col),
                        nullInt,
                        DefaultObjectIdFor(col),
                        nullSysName,
                        nullInt,
                        SqlValue.FromByte((byte)col.GeneratedAs),
                        nullInt,
                        falseBit,
                        SqlValue.FromBoolean(col.IsHidden),
                        falseBit,
                        SqlValue.FromBoolean(col.IsRowGuidCol),
                        zeroInt,
                    ];
                }
            }
            // Views surface their output projection through sys.columns —
            // same shape as inline TVFs (is_identity / is_computed always
            // false; nullability conservatively True).
            foreach (var view in schema.Views.Values.OrderBy(v => v.ObjectId))
            {
                var viewObjectId = SqlValue.FromInt32(view.ObjectId);
                for (var i = 0; i < view.OutputColumns.Length; i++)
                {
                    var col = view.OutputColumns[i];
                    var (maxLength, precision, scale) = GetSysColumnMetadata(col);
                    yield return [
                        viewObjectId,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(col.Type.SystemTypeId),
                        SqlValue.FromInt32(col.Type.UserTypeId),
                        SqlValue.FromInt16(maxLength),
                        SqlValue.FromByte(precision),
                        SqlValue.FromByte(scale),
                        SqlValue.FromBoolean(col.Nullable),
                        falseBit,
                        falseBit,
                        CollationFor(col),
                        falseBit,
                        falseBit,
                        zeroInt,
                        falseBit,
                        falseBit,
                        nullInt,
                        nullVectorBaseType,
                        nullInt,
                        nullLedgerViewColumnTypeDesc,
                        AnsiPaddedFor(col),
                        nullInt,
                        DefaultObjectIdFor(col),
                        nullSysName,
                        nullInt,
                        SqlValue.FromByte((byte)col.GeneratedAs),
                        nullInt,
                        falseBit,
                        SqlValue.FromBoolean(col.IsHidden),
                        falseBit,
                        SqlValue.FromBoolean(col.IsRowGuidCol),
                        zeroInt,
                    ];
                }
            }
            // Table types surface their columns through sys.columns keyed by
            // type_table_object_id (probe G3). Computed columns inherit
            // is_computed=true; identity columns inherit is_identity=true.
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.ObjectId))
            {
                var typeObjectId = SqlValue.FromInt32(tt.ObjectId);
                for (var i = 0; i < tt.Columns.Length; i++)
                {
                    var col = tt.Columns[i];
                    var (maxLength, precision, scale) = GetSysColumnMetadata(col);
                    yield return [
                        typeObjectId,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(col.Type.SystemTypeId),
                        SqlValue.FromInt32(col.Type.UserTypeId),
                        SqlValue.FromInt16(maxLength),
                        SqlValue.FromByte(precision),
                        SqlValue.FromByte(scale),
                        SqlValue.FromBoolean(col.Nullable),
                        SqlValue.FromBoolean(col.Identity is not null),
                        SqlValue.FromBoolean(col.Computed is not null),
                        CollationFor(col),
                        falseBit,
                        falseBit,
                        XmlCollectionIdFor(col),
                        falseBit,
                        falseBit,
                        nullInt,
                        nullVectorBaseType,
                        nullInt,
                        nullLedgerViewColumnTypeDesc,
                        AnsiPaddedFor(col),
                        nullInt,
                        DefaultObjectIdFor(col),
                        nullSysName,
                        nullInt,
                        SqlValue.FromByte((byte)col.GeneratedAs),
                        nullInt,
                        falseBit,
                        SqlValue.FromBoolean(col.IsHidden),
                        falseBit,
                        SqlValue.FromBoolean(col.IsRowGuidCol),
                        zeroInt,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Computes the <c>sys.columns.max_length / precision / scale</c> triple
    /// for a column, matching probe-confirmed SQL Server 2025 values:
    /// <list type="bullet">
    /// <item>max_length is byte width — <c>nvarchar(50)→100</c>,
    /// <c>nchar(50)→100</c>, <c>char(5)→5</c>; <c>-1</c> for the MAX form;
    /// <c>16</c> for text/ntext/image (LOB pointer size); <c>256</c> for
    /// sysname (= nvarchar(128)).</item>
    /// <item>precision / scale are 0 for non-numeric, non-date/time types.
    /// Decimals carry their declared (p, s). Date/time fractional precision
    /// types follow <c>(time(N): 8+N, N)</c> / <c>(datetime2(N): 19+N, N)</c>
    /// / <c>(datetimeoffset(N): 26+N, N)</c>.</item>
    /// </list>
    /// </summary>
    private static (short MaxLength, byte Precision, byte Scale) GetSysColumnMetadata(HeapColumn col)
    {
        var t = col.Type;
        return t switch
        {
            _ when t == SqlType.Bit => (1, 1, 0),
            _ when t == SqlType.TinyInt => (1, 3, 0),
            _ when t == SqlType.SmallInt => (2, 5, 0),
            _ when t == SqlType.Int32 => (4, 10, 0),
            _ when t == SqlType.BigInt => (8, 19, 0),
            _ when t == SqlType.Money => (8, 19, 4),
            _ when t == SqlType.SmallMoney => (4, 10, 4),
            DecimalSqlType d => ((short)d.FixedLength, d.precision, d.scale),
            _ when t == SqlType.Float => (8, 53, 0),
            _ when t == SqlType.Real => (4, 24, 0),
            _ when t == SqlType.Date => (3, 10, 0),
            _ when t == SqlType.SmallDateTime => (4, 16, 0),
            _ when t == SqlType.DateTime => (8, 23, 3),
            DateTime2SqlType dt2 => ((short)dt2.FixedLength, (byte)(19 + dt2.precision), (byte)dt2.precision),
            TimeSqlType tm => ((short)tm.FixedLength, (byte)(8 + tm.precision), (byte)tm.precision),
            DateTimeOffsetSqlType dto => ((short)dto.FixedLength, (byte)(26 + dto.precision), (byte)dto.precision),
            _ when t == SqlType.UniqueIdentifier => (16, 0, 0),
            _ when t == SqlType.RowVersion => (8, 0, 0),
            _ when t == SqlType.Text || t == SqlType.NText || t == SqlType.Image => (16, 0, 0),
            _ when t == SqlType.SystemName => (256, 0, 0),
            CharSqlType c => (c.length, 0, 0),
            NCharSqlType nc => ((short)(nc.length * 2), 0, 0),
            BinarySqlType bn => (bn.length, 0, 0),
            VarcharSqlType vc => (vc.length == 0 ? (short)(col.MaxLength ?? 1) : vc.length, 0, 0),
            NVarcharSqlType nv => (
                nv.length switch
                {
                    -1 => -1,
                    0 => (short)((col.MaxLength ?? 1) * 2),
                    _ => (short)(nv.length * 2),
                },
                0, 0),
            VarbinarySqlType vb => (vb.length == 0 ? (short)(col.MaxLength ?? 1) : vb.length, 0, 0),
            // xml: real SQL Server reports max_length = -1 (matching the
            // nvarchar(MAX) storage shape) and no numeric precision/scale.
            XmlSqlType => (-1, 0, 0),
            // geography / geometry: same max_length = -1 reporting as xml,
            // matching the probed sys.columns shape for spatial-typed columns.
            SpatialSqlType => (-1, 0, 0),
            // hierarchyid: 892-byte max representation per the probed
            // sys.types row shape; no numeric precision/scale.
            HierarchyIdSqlType => (892, 0, 0),
            _ => throw new NotSupportedException($"No sys.columns metadata for {t}."),
        };
    }

    private static IEnumerable<SqlValue[]> EnumerateObjects(
        Parser.BatchContext batch,
        Database database,
        SqlType charTwo,
        SqlValue pkType, SqlValue pkTypeDesc,
        SqlValue uqType, SqlValue uqTypeDesc,
        SqlValue checkType, SqlValue checkTypeDesc,
        SqlValue zeroParent, SqlValue notMsShipped)
    {
        _ = batch;
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var notPublished = SqlValue.FromBoolean(false);
        var sysSchemaIdValue = SqlValue.FromInt32(Database.SysSchemaId);
        var msShipped = SqlValue.FromBoolean(true);
        foreach (var schema in database.Schemas.Values)
        {
            // Table types' internal type tables: one TYPE_TABLE ('TT') row per
            // user table type, named TT_<type>_<object_id:X8>, homed in the
            // sys schema with is_ms_shipped = 1 (all probe-confirmed against
            // SQL Server 2025; the hex suffix is the object id, byte-matching
            // real's convention). DacFx's table-type populator INNER JOINs
            // sys.objects on type_table_object_id and NREs client-side when
            // the parent row is absent.
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.ObjectId))
            {
                yield return [
                    SqlValue.FromInt32(tt.ObjectId),
                    SqlValue.FromSystemName($"TT_{tt.Name}_{tt.ObjectId:X8}"),
                    sysSchemaIdValue,
                    zeroParent,
                    nullPrincipal,
                    SqlValue.FromChar(charTwo, "TT"),
                    SqlValue.FromNVarchar("TYPE_TABLE"),
                    SqlValue.FromDateTime(tt.CreateDate),
                    SqlValue.FromDateTime(tt.ModifyDate),
                    msShipped,
                    notPublished,
                    notPublished,
                ];
            }

            // Schema-resident objects in ObjectId order. SchemaObject's
            // ObjectTypeCode / ObjectTypeDescription supply the discriminators,
            // so adding a new schema-object kind (e.g. surfacing Sequences /
            // TableTypes in sys.objects later) only requires implementing the
            // two abstract members on that type.
            foreach (var obj in schema.SchemaObjects().OrderBy(o => o.ObjectId))
            {
                var parent = obj is Trigger trigger
                    ? SqlValue.FromInt32(trigger.Parent.ObjectId)
                    : zeroParent;
                yield return [
                    SqlValue.FromInt32(obj.ObjectId),
                    SqlValue.FromSystemName(obj.Name),
                    SqlValue.FromInt32(obj.SchemaId),
                    parent,
                    nullPrincipal,
                    SqlValue.FromChar(charTwo, obj.ObjectTypeCode),
                    SqlValue.FromNVarchar(obj.ObjectTypeDescription),
                    SqlValue.FromDateTime(obj.CreateDate),
                    SqlValue.FromDateTime(obj.ModifyDate),
                    notMsShipped,
                    notPublished,
                    notPublished,
                ];

                // Constraint rows hang off HeapTable parents — emit them
                // immediately after the table they belong to so the natural
                // sys.objects ordering matches probe-confirmed real-server
                // shape (table rows interleaved with their own constraints).
                if (obj is not HeapTable t) continue;
                var schemaIdValue = SqlValue.FromInt32(t.SchemaId);
                var createDate = SqlValue.FromDateTime(t.CreateDate);
                var modifyDate = SqlValue.FromDateTime(t.ModifyDate);
                var tableObjectId = SqlValue.FromInt32(t.ObjectId);
                foreach (var key in t.KeyConstraints)
                {
                    yield return [
                        SqlValue.FromInt32(key.ObjectId),
                        SqlValue.FromSystemName(key.Name),
                        schemaIdValue,
                        tableObjectId,
                        nullPrincipal,
                        key.Kind == KeyConstraintKind.PrimaryKey ? pkType : uqType,
                        key.Kind == KeyConstraintKind.PrimaryKey ? pkTypeDesc : uqTypeDesc,
                        createDate,
                        modifyDate,
                        notMsShipped,
                        notPublished,
                        notPublished,
                    ];
                }
                foreach (var chk in t.CheckConstraints)
                {
                    yield return [
                        SqlValue.FromInt32(chk.ObjectId),
                        SqlValue.FromSystemName(chk.Name),
                        schemaIdValue,
                        tableObjectId,
                        nullPrincipal,
                        checkType,
                        checkTypeDesc,
                        createDate,
                        modifyDate,
                        notMsShipped,
                        notPublished,
                        notPublished,
                    ];
                }
                foreach (var fk in t.OutgoingForeignKeys)
                {
                    yield return [
                        SqlValue.FromInt32(fk.ObjectId),
                        SqlValue.FromSystemName(fk.Name),
                        schemaIdValue,
                        tableObjectId,
                        nullPrincipal,
                        SqlValue.FromChar(charTwo, "F "),
                        SqlValue.FromNVarchar("FOREIGN_KEY_CONSTRAINT"),
                        createDate,
                        modifyDate,
                        notMsShipped,
                        notPublished,
                        notPublished,
                    ];
                }
                // Each primary XML index owns an internal "node table"
                // (type 'IT' / INTERNAL_TABLE), named
                // xml_index_nodes_<tableObjectId>_<primaryIndexObjectId>, homed
                // in the sys schema with is_ms_shipped = 1 (probe-confirmed
                // against SQL Server 2025). DacFx's XML-index reverse-
                // engineering joins sys.objects (this row) → sys.stats (the
                // per-index statistics on this object) to build each index's
                // parent, and NREs client-side when the node table is absent.
                foreach (var xmlIndex in t.XmlIndexes)
                {
                    if (!xmlIndex.IsPrimary)
                        continue;
                    yield return [
                        SqlValue.FromInt32(xmlIndex.InternalTableObjectId),
                        SqlValue.FromSystemName($"xml_index_nodes_{t.ObjectId}_{xmlIndex.ObjectId}"),
                        sysSchemaIdValue,
                        tableObjectId,
                        nullPrincipal,
                        SqlValue.FromChar(charTwo, "IT"),
                        SqlValue.FromNVarchar("INTERNAL_TABLE"),
                        createDate,
                        modifyDate,
                        msShipped,
                        notPublished,
                        notPublished,
                    ];
                }
            }
        }
    }
}
