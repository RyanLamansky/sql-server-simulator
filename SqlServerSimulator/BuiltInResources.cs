using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SqlServerSimulator;

internal static class BuiltInResources
{
    internal static readonly object?[][] SystypesRowData =
    [
        ["image", 34, 0, 34, 16, 0, 0, 0, 0, 4, 0, null, 20, false, true, 34, null, null, null, null],
        ["text", 35, 0, 35, 16, 0, 0, 0, 0, 4, 0, 872468488, 19, false, true, 35, null, null, null, "SQL_Latin1_General_CP1_CI_AS"],
        ["uniqueidentifier", 36, 0, 36, 16, 0, 0, 0, 0, 4, 0, null, 0, false, true, 37, null, 16, null, null],
        ["date", 40, 0, 40, 3, 10, 0, 0, 0, 4, 0, null, 0, false, true, 0, null, 10, 0, null],
        ["time", 41, 0, 41, 5, 16, 7, 0, 0, 4, 0, null, 0, false, true, 0, null, 16, 7, null],
        ["datetime2", 42, 0, 42, 8, 27, 7, 0, 0, 4, 0, null, 0, false, true, 0, null, 27, 7, null],
        ["datetimeoffset", 43, 0, 43, 10, 34, 7, 0, 0, 4, 0, null, 0, false, true, 0, null, 34, 7, null],
        ["tinyint", 48, 0, 48, 1, 3, 0, 0, 0, 4, 0, null, 5, false, true, 48, null, 3, 0, null],
        ["smallint", 52, 0, 52, 2, 5, 0, 0, 0, 4, 0, null, 6, false, true, 52, null, 5, 0, null],
        ["int", 56, 0, 56, 4, 10, 0, 0, 0, 4, 0, null, 7, false, true, 56, null, 10, 0, null],
        ["smalldatetime", 58, 0, 58, 4, 16, 0, 0, 0, 4, 0, null, 22, false, true, 58, null, 16, 0, null],
        ["real", 59, 0, 59, 4, 24, 0, 0, 0, 4, 0, null, 23, false, true, 59, null, 24, null, null],
        ["money", 60, 0, 60, 8, 19, 4, 0, 0, 4, 0, null, 11, false, true, 60, null, 19, 4, null],
        ["datetime", 61, 0, 61, 8, 23, 3, 0, 0, 4, 0, null, 12, false, true, 61, null, 23, 3, null],
        ["float", 62, 0, 62, 8, 53, 0, 0, 0, 4, 0, null, 8, false, true, 62, null, 53, null, null],
        ["sql_variant", 98, 0, 98, 8016, 0, 0, 0, 0, 4, 0, null, 0, false, true, 39, null, 0, null, null],
        ["ntext", 99, 0, 99, 16, 0, 0, 0, 0, 4, 0, 872468488, 0, false, true, 35, null, null, null, "SQL_Latin1_General_CP1_CI_AS"],
        ["bit", 104, 0, 104, 1, 1, 0, 0, 0, 4, 0, null, 16, false, true, 50, null, 1, null, null],
        ["decimal", 106, 0, 106, 17, 38, 38, 0, 0, 4, 0, null, 24, false, true, 55, null, 38, 38, null],
        ["numeric", 108, 0, 108, 17, 38, 38, 0, 0, 4, 0, null, 10, false, true, 63, null, 38, 38, null],
        ["smallmoney", 122, 0, 122, 4, 10, 4, 0, 0, 4, 0, null, 21, false, true, 122, null, 10, 4, null],
        ["bigint", 127, 0, 127, 8, 19, 0, 0, 0, 4, 0, null, 0, false, true, 63, null, 19, 0, null],
        ["hierarchyid", 240, 0, 128, 892, 0, 0, 0, 0, 4, 0, null, 0, false, true, 0, null, 892, null, null],
        ["geometry", 240, 0, 129, -1, 0, 0, 0, 0, 4, 0, null, 0, false, true, 0, null, -1, null, null],
        ["geography", 240, 0, 130, -1, 0, 0, 0, 0, 4, 0, null, 0, false, true, 0, null, -1, null, null],
        ["varbinary", 165, 0, 165, 8000, 0, 0, 0, 0, 4, 0, null, 4, true, true, 37, null, 8000, null, null],
        ["varchar", 167, 0, 167, 8000, 0, 0, 0, 0, 4, 0, 872468488, 2, true, true, 39, null, 8000, null, "SQL_Latin1_General_CP1_CI_AS"],
        ["binary", 173, 0, 173, 8000, 0, 0, 0, 0, 4, 0, null, 3, false, true, 45, null, 8000, null, null],
        ["char", 175, 0, 175, 8000, 0, 0, 0, 0, 4, 0, 872468488, 1, false, true, 47, null, 8000, null, "SQL_Latin1_General_CP1_CI_AS"],
        ["timestamp", 189, 1, 189, 8, 0, 0, 0, 0, 4, 0, null, 80, false, false, 45, null, 8, null, null],
        ["nvarchar", 231, 0, 231, 8000, 0, 0, 0, 0, 4, 0, 872468488, 0, true, true, 39, null, 4000, null, "SQL_Latin1_General_CP1_CI_AS"],
        ["nchar", 239, 0, 239, 8000, 0, 0, 0, 0, 4, 0, 872468488, 0, false, true, 47, null, 4000, null, "SQL_Latin1_General_CP1_CI_AS"],
        ["xml", 241, 0, 241, -1, 0, 0, 0, 0, 4, 0, null, 0, false, true, 0, null, -1, null, null],
        ["sysname", 231, 1, 256, 256, 0, 0, 0, 0, 4, 0, 872468488, 18, true, false, 39, null, 128, null, "SQL_Latin1_General_CP1_CI_AS"],
    ];

    public static readonly Lazy<Dictionary<string, HeapTable>> SystemHeapTables = new(BuildSystemHeapTables);

    private static Dictionary<string, HeapTable> BuildSystemHeapTables()
    {
        HeapColumn[] systypesColumns =
        [
            new("name", SqlType.SystemName, 128, false),
            new("xtype", SqlType.TinyInt, null, false),
            new("status", SqlType.TinyInt, null, true),
            new("xusertype", SqlType.SmallInt, null, true),
            new("length", SqlType.SmallInt, null, false),
            new("xprec", SqlType.TinyInt, null, false),
            new("xscale", SqlType.TinyInt, null, false),
            new("tdefault", SqlType.Int32, null, false),
            new("domain", SqlType.Int32, null, false),
            new("uid", SqlType.SmallInt, null, true),
            new("reserved", SqlType.SmallInt, null, true),
            new("collationid", SqlType.Int32, null, true),
            new("usertype", SqlType.SmallInt, null, true),
            new("variable", SqlType.Bit, null, false),
            new("allownulls", SqlType.Bit, null, true),
            new("type", SqlType.TinyInt, null, false),
            new("printfmt", SqlType.Varchar, 255, true),
            new("prec", SqlType.SmallInt, null, true),
            new("scale", SqlType.TinyInt, null, true),
            new("collation", SqlType.SystemName, 128, true),
        ];
        // System tables live outside any user database's id space — they're
        // process-shared and the simulator doesn't expose them via OBJECT_ID
        // (which routes through per-DB schema resolution). A small negative
        // id keeps them distinguishable in debug output.
        var systypes = new HeapTable("systypes", systypesColumns, objectId: -1);

        foreach (var row in SystypesRowData)
        {
            var values = new SqlValue[systypesColumns.Length];
            for (var i = 0; i < systypesColumns.Length; i++)
                values[i] = ObjectToSqlValue(row[i], systypesColumns[i].Type);

            _ = systypes.Heap.Insert(RowEncoder.EncodeRow(systypes.Schema, values));
        }

        return new(BuiltInToken.Comparer) { [systypes.Name] = systypes };
    }

    private static SqlValue ObjectToSqlValue(object? value, SqlType type) =>
        value is null ? SqlValue.Null(type)
        : type == SqlType.TinyInt ? SqlValue.FromByte(Convert.ToByte(value, CultureInfo.InvariantCulture))
        : type == SqlType.SmallInt ? SqlValue.FromInt16(Convert.ToInt16(value, CultureInfo.InvariantCulture))
        : type == SqlType.Int32 ? SqlValue.FromInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture))
        : type == SqlType.Bit ? SqlValue.FromBoolean((bool)value)
        : type is VarcharSqlType ? SqlValue.FromVarchar((string)value)
        : type == SqlType.SystemName ? SqlValue.FromSystemName((string)value)
        : throw new NotSupportedException($"Built-in resource materializer doesn't know how to convert {value.GetType().Name} to {type}.");

    public static readonly Lazy<Dictionary<string, CatalogView>> CatalogViews = new(BuildCatalogViews);

    /// <summary>
    /// Registers the <c>sys.&lt;view&gt;</c> and <c>INFORMATION_SCHEMA.&lt;view&gt;</c>
    /// virtual tables. Each row generator projects from live <see cref="Database"/> /
    /// <see cref="Schema"/> / <see cref="HeapTable"/> metadata at iteration
    /// time, so changes made earlier in the same batch (CREATE TABLE,
    /// CREATE SCHEMA, DROP TABLE) appear immediately in the next read. Keys
    /// are fully-qualified names (<c>"sys.tables"</c>,
    /// <c>"INFORMATION_SCHEMA.COLUMNS"</c>) so the single resolver in
    /// <see cref="Parser.BatchContext.TryResolveCatalogView"/> can serve both
    /// schemas without per-namespace dispatch. Shipped views: <c>sys.schemas</c>
    /// / <c>sys.tables</c> / <c>sys.objects</c> / <c>sys.columns</c> (load-
    /// bearing subset of real SQL Server's column set) plus
    /// <c>INFORMATION_SCHEMA.TABLES</c> / <c>.COLUMNS</c> / <c>.SCHEMATA</c>
    /// (the full ISO column shape).
    /// </summary>
    private static Dictionary<string, CatalogView> BuildCatalogViews()
    {
        var views = new Dictionary<string, CatalogView>(BuiltInToken.Comparer);
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);
        void Iso(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["INFORMATION_SCHEMA." + name] = new CatalogView(name, columns, rows);

        // Catalog-pinned types reused across catalog views — Latin1_General_CI_AS_KS_WS
        // at Implicit rank, matching what real SQL Server's catalog DDL pins
        // for _desc enum columns, permission_name, and the char(1)/char(2)
        // type/state code columns. See Collation.Catalog for empirical
        // grounding and the contained-DB-vs-non-contained-DB distinction.
        var nvarchar60Catalog = NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit);
        var nvarchar128Catalog = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);

        // numeric(25, 0) — the log-sequence-number (LSN) storage shape shared
        // by the mirroring / replica-state / master-files views. Always surfaced
        // NULL here (the simulator has no physical log), so precision matters
        // only for the column schema clients read back.
        var lsnNumeric = SqlType.GetDecimal(25, 0);

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
        var charTwo = CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit);
        var tableType = SqlValue.FromChar(charTwo, "U ");
        var tableTypeDesc = SqlValue.FromNVarchar("USER_TABLE");
        var notMsShipped = SqlValue.FromBoolean(false);
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
        Sys("tables",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
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
            new("ledger_type", SqlType.TinyInt, null, false),
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
                        ledgerTypeNone,
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
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
        ], (batch, database) =>
            EnumerateObjects(batch, database, charTwo, pkType, pkTypeDesc, uqType, uqTypeDesc, checkType, checkTypeDesc, zeroParent, notMsShipped));

        // sys.columns: load-bearing subset of real SQL Server's column set.
        // Probe-confirmed (2026-05-11): max_length is byte-length (4 for int,
        // 100 for nvarchar(50), 5 for char(5), 16 for uniqueidentifier, 7 for
        // datetime2(3), 9 for decimal(10,2)); -1 for *(MAX); 16 (LOB pointer)
        // for text/ntext/image. precision/scale only meaningful for numeric
        // and date/time types; 0 for everything else. collation_name set only
        // for string types.
        var systemTypeId = SqlType.TinyInt;
        var defaultCollation = SqlValue.FromSystemName("SQL_Latin1_General_CP1_CI_AS");
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
        ];
        IEnumerable<SqlValue[]> ColumnRows(Parser.BatchContext batch, Database database) =>
            EnumerateColumns(batch, database, defaultCollation, nullCollation);
        Sys("columns", ColumnsShape(), ColumnRows);
        Sys("all_columns", ColumnsShape(), ColumnRows);

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

        // INFORMATION_SCHEMA.TABLES: ISO-standard 4-column shape. TABLE_TYPE
        // is 'BASE TABLE' for every user table; 'VIEW' (not modeled) would be
        // the other shipped value.
        var baseTable = SqlValue.FromVarchar("BASE TABLE");
        var viewTableType = SqlValue.FromVarchar("VIEW");
        Iso("TABLES",
        [
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("TABLE_TYPE", SqlType.Varchar, 10, true),
        ], (batch, database) =>
            EnumerateInformationSchemaTables(batch, database, baseTable, viewTableType));

        // INFORMATION_SCHEMA.COLUMNS: ISO-standard 23-column shape. Tooling
        // does SELECT * here so the full column set ships even though many
        // are always NULL in the simulator (DOMAIN_*, CHARACTER_SET_SCHEMA,
        // COLLATION_CATALOG, etc.). COLUMN_DEFAULT is always NULL until
        // expression-to-SQL serialization lands (separate bundle).
        var unicodeCs = SqlValue.FromSystemName("UNICODE");
        var isoCs = SqlValue.FromSystemName("iso_1");
        var radix10 = SqlValue.FromInt16(10);
        var radix2 = SqlValue.FromInt16(2);
        Iso("COLUMNS",
        [
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("COLUMN_NAME", SqlType.SystemName, 128, true),
            new("ORDINAL_POSITION", SqlType.Int32, null, true),
            new("COLUMN_DEFAULT", SqlType.NVarchar, 4000, true),
            new("IS_NULLABLE", SqlType.Varchar, 3, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
            new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
            new("CHARACTER_OCTET_LENGTH", SqlType.Int32, null, true),
            new("NUMERIC_PRECISION", SqlType.TinyInt, null, true),
            new("NUMERIC_PRECISION_RADIX", SqlType.SmallInt, null, true),
            new("NUMERIC_SCALE", SqlType.Int32, null, true),
            new("DATETIME_PRECISION", SqlType.SmallInt, null, true),
            new("CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
            new("COLLATION_CATALOG", SqlType.SystemName, 128, true),
            new("COLLATION_SCHEMA", SqlType.SystemName, 128, true),
            new("COLLATION_NAME", SqlType.SystemName, 128, true),
            new("DOMAIN_CATALOG", SqlType.SystemName, 128, true),
            new("DOMAIN_SCHEMA", SqlType.SystemName, 128, true),
            new("DOMAIN_NAME", SqlType.SystemName, 128, true),
        ], (batch, database) =>
            EnumerateInformationSchemaColumns(batch, database, defaultCollation, unicodeCs, isoCs, radix10, radix2));

        // INFORMATION_SCHEMA.SCHEMATA: ISO-standard 6-column shape. Lists
        // only the schemas the simulator actually models — no padding for
        // role principals (db_owner / db_datareader / …) since there's no
        // principal model. SCHEMA_OWNER mirrors SCHEMA_NAME (matches real
        // SQL Server's behavior for built-in / user schemas without explicit
        // AUTHORIZATION).
        var defaultCsName = SqlValue.FromSystemName("iso_1");
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        Iso("SCHEMATA",
        [
            new("CATALOG_NAME", SqlType.SystemName, 128, true),
            new("SCHEMA_NAME", SqlType.SystemName, 128, false),
            new("SCHEMA_OWNER", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
        ], (batch, database) =>
            database.Schemas.Values.OrderBy(s => s.SchemaId).Select(s => new SqlValue[]
            {
                SqlValue.FromSystemName(database.Name),
                SqlValue.FromSystemName(s.Name),
                SqlValue.FromSystemName(s.Name),
                nullSysName,
                nullSysName,
                defaultCsName,
            }));

        // sys.parameters: one row per declared parameter + one row with
        // parameter_id=0 for the return type. The shipped column set covers
        // what real SQL Server's documented shape exposes: object_id / name /
        // parameter_id / system_type_id / user_type_id / max_length /
        // precision / scale / is_output / is_nullable. Probe-confirmed
        // ordering: return type emits first (parameter_id=0, empty name),
        // declared params follow in source order.
        Sys("parameters",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("parameter_id", SqlType.Int32, null, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("max_length", SqlType.SmallInt, null, false),
            new("precision", SqlType.TinyInt, null, false),
            new("scale", SqlType.TinyInt, null, false),
            new("is_output", SqlType.Bit, null, false),
            new("is_nullable", SqlType.Bit, null, false),
            new("is_readonly", SqlType.Bit, null, false),
        ], EnumerateParameters);

        // sys.views: per-view rows. Load-bearing subset of real SQL Server's
        // sys.views shape — object_id / name / schema_id / with_check_option /
        // is_date_correlation_view. Other documented columns (principal_id,
        // is_replicated, has_replication_filter, etc.) aren't modeled.
        Sys("views",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("with_check_option", SqlType.Bit, null, false),
            new("is_date_correlation_view", SqlType.Bit, null, false),
        ], EnumerateViews);

        // sys.procedures: per-procedure rows. Shipped column subset matches
        // the load-bearing surface — object_id / name / schema_id /
        // create_date / modify_date / is_ms_shipped. Other documented
        // columns (principal_id, is_auto_executed, is_execution_replicated,
        // etc.) aren't modeled.
        Sys("procedures",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
        ], (batch, database) =>
            EnumerateProcedures(batch, database, charTwo, notMsShipped));

        // INFORMATION_SCHEMA.ROUTINES: ISO-shape view listing both procedures
        // and functions. The simulator ships the load-bearing column subset:
        // ROUTINE_CATALOG / SCHEMA / NAME / TYPE / DATA_TYPE. For procedures
        // DATA_TYPE is NULL (procs have no scalar return type); for scalar
        // UDFs it carries the return type's family name; for inline TVFs it
        // is 'TABLE'. ROUTINE_DEFINITION carries the module source text
        // (nvarchar(4000), truncated like SQL Server). Real SQL Server ships
        // dozens of further columns (CREATED, LAST_ALTERED, etc.) not modeled.
        var procedureRoutineType = SqlValue.FromVarchar("PROCEDURE");
        var functionRoutineType = SqlValue.FromVarchar("FUNCTION");
        var tableDataType = SqlValue.FromSystemName("TABLE");
        Iso("ROUTINES",
        [
            new("ROUTINE_CATALOG", SqlType.SystemName, 128, true),
            new("ROUTINE_SCHEMA", SqlType.SystemName, 128, true),
            new("ROUTINE_NAME", SqlType.SystemName, 128, false),
            new("ROUTINE_TYPE", SqlType.Varchar, 9, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
            new("ROUTINE_DEFINITION", SqlType.NVarchar, 4000, true),
        ], (batch, database) =>
            EnumerateInformationSchemaRoutines(batch, database, procedureRoutineType, functionRoutineType, tableDataType));

        // INFORMATION_SCHEMA.PARAMETERS: ISO-shape view listing parameters
        // for procedures and functions. PARAMETER_MODE is 'IN' / 'OUT' /
        // 'INOUT'; the simulator emits 'IN' for non-output params, 'INOUT'
        // for OUTPUT-declared params (probe-confirmed: real SQL Server uses
        // INOUT for OUTPUT in procedures). CHARACTER_MAXIMUM_LENGTH is set
        // only for string types.
        var modeIn = SqlValue.FromVarchar("IN");
        var modeInOut = SqlValue.FromVarchar("INOUT");
        Iso("PARAMETERS",
        [
            new("SPECIFIC_CATALOG", SqlType.SystemName, 128, true),
            new("SPECIFIC_SCHEMA", SqlType.SystemName, 128, true),
            new("SPECIFIC_NAME", SqlType.SystemName, 128, false),
            new("ORDINAL_POSITION", SqlType.Int32, null, true),
            new("PARAMETER_MODE", SqlType.Varchar, 10, true),
            new("PARAMETER_NAME", SqlType.SystemName, 128, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
            new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
        ], (batch, database) =>
            EnumerateInformationSchemaParameters(batch, database, modeIn, modeInOut));

        // INFORMATION_SCHEMA.VIEWS: ISO-standard 6-column shape. Probe-
        // confirmed: VIEW_DEFINITION is NULL only for WITH ENCRYPTION views
        // (the simulator parses ENCRYPTION but doesn't track it — minor
        // fidelity gap, the body text always surfaces). IS_UPDATABLE is
        // probe-confirmed to always report 'NO' in real SQL Server even for
        // views that are actually updatable — matching that by hardcoding.
        var checkOptionNone = SqlValue.FromVarchar("NONE");
        var checkOptionCascade = SqlValue.FromVarchar("CASCADE");
        var isUpdatableNo = SqlValue.FromVarchar("NO");
        Iso("VIEWS",
        [
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("VIEW_DEFINITION", SqlType.NVarchar, 4000, true),
            new("CHECK_OPTION", SqlType.Varchar, 7, true),
            new("IS_UPDATABLE", SqlType.Varchar, 2, true),
        ], (batch, database) =>
            EnumerateInformationSchemaViews(batch, database, checkOptionNone, checkOptionCascade, isUpdatableNo));

        // sys.types: per-database list of system + user-defined types. Probe-
        // confirmed shipped subset: name / system_type_id / user_type_id /
        // schema_id / is_user_defined / is_table_type / is_nullable. Real SQL
        // Server has many more columns (principal_id, max_length, precision,
        // scale, collation_name, is_assembly_type, default_object_id, etc.);
        // the shipped set is what apps typically test for.
        Sys("types",
        [
            new("name", SqlType.SystemName, 128, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
            new("is_table_type", SqlType.Bit, null, false),
            new("is_nullable", SqlType.Bit, null, false),
        ], EnumerateSysTypes);

        // sys.table_types: per-database list of user-defined table types
        // only. Probe-confirmed shipped subset: name / type_table_object_id /
        // is_user_defined / schema_id / user_type_id.
        Sys("table_types",
        [
            new("name", SqlType.SystemName, 128, false),
            new("type_table_object_id", SqlType.Int32, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("user_type_id", SqlType.Int32, null, false),
        ], EnumerateSysTableTypes);

        // sys.sequences: per-database list of user-defined sequence objects.
        // Probe-confirmed shipped subset: name / object_id / schema_id /
        // start_value / increment / minimum_value / maximum_value /
        // is_cycling / is_cached / cache_size / current_value /
        // system_type_id / user_type_id / is_exhausted. cache_size is NULL
        // when no explicit CACHE n was given (real SQL Server behavior;
        // the simulator never tracks an explicit value so this is always
        // NULL). Values widen to decimal(38, 0) to match SQL Server's
        // sql_variant-typed columns in real sys.sequences, but the simulator
        // emits bigint here since all sequence state is tracked as long
        // (a minor projection-schema divergence; SqlClient surfaces these
        // as long, which matches what HiLo-style apps assert on).
        Sys("sequences",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("start_value", SqlType.BigInt, null, false),
            new("increment", SqlType.BigInt, null, false),
            new("minimum_value", SqlType.BigInt, null, false),
            new("maximum_value", SqlType.BigInt, null, false),
            new("is_cycling", SqlType.Bit, null, false),
            new("is_cached", SqlType.Bit, null, false),
            new("cache_size", SqlType.Int32, null, true),
            new("current_value", SqlType.BigInt, null, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("is_exhausted", SqlType.Bit, null, false),
        ], EnumerateSysSequences);

        // sys.triggers: per-trigger rows. Probe-confirmed shipped subset
        // (SQL Server 2025): name / object_id / parent_class /
        // parent_class_desc / parent_id / type / type_desc / create_date /
        // modify_date / is_disabled / is_instead_of_trigger /
        // is_not_for_replication. parent_class is always 1
        // (OBJECT_OR_COLUMN) for DML triggers attached to tables;
        // DDL triggers (database/server-scoped) use 0 / 100 and aren't
        // modeled. parent_id is the parent table's object_id.
        var parentClassObjectColumn = SqlValue.FromByte(1);
        var parentClassObjectColumnDesc = SqlValue.FromNVarchar("OBJECT_OR_COLUMN");
        Sys("triggers",
        [
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("parent_class", SqlType.TinyInt, null, false),
            new("parent_class_desc", nvarchar60Catalog, 60, true),
            new("parent_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_instead_of_trigger", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
        ], (batch, database) =>
            EnumerateSysTriggers(batch, database, charTwo, parentClassObjectColumn, parentClassObjectColumnDesc));

        // sys.foreign_keys: probe-confirmed 21-column shape against SQL
        // Server 2025 (2026-05-13). EF Core reads name / parent_object_id /
        // referenced_object_id / delete_referential_action /
        // update_referential_action; the simulator ships the full set so
        // catalog-introspection tooling sees an authentic shape.
        Sys("foreign_keys",
        [
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
            new("is_published", SqlType.Bit, null, true),
            new("is_schema_published", SqlType.Bit, null, true),
            new("referenced_object_id", SqlType.Int32, null, false),
            new("key_index_id", SqlType.Int32, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
            new("is_not_trusted", SqlType.Bit, null, false),
            new("delete_referential_action", SqlType.TinyInt, null, false),
            new("delete_referential_action_desc", nvarchar60Catalog, 60, true),
            new("update_referential_action", SqlType.TinyInt, null, false),
            new("update_referential_action_desc", nvarchar60Catalog, 60, true),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysForeignKeys);

        // sys.foreign_key_columns: probe-confirmed 6-column shape. One row
        // per (FK, column-pair) — composite FKs emit one row per participant
        // column with constraint_column_id starting at 1.
        Sys("foreign_key_columns",
        [
            new("constraint_object_id", SqlType.Int32, null, false),
            new("constraint_column_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("parent_column_id", SqlType.Int32, null, false),
            new("referenced_object_id", SqlType.Int32, null, false),
            new("referenced_column_id", SqlType.Int32, null, false),
        ], EnumerateSysForeignKeyColumns);

        // INFORMATION_SCHEMA.DOMAINS: ISO-standard surface. Real SQL Server
        // emits a row for every user-defined type (scalar UDTs surface their
        // base type; table types surface 'table type' as the data_type
        // literal — probe-confirmed G6). Load-bearing subset: DOMAIN_CATALOG /
        // DOMAIN_SCHEMA / DOMAIN_NAME / DATA_TYPE.
        var tableTypeDataType = SqlValue.FromNVarchar("table type");
        Iso("DOMAINS",
        [
            new("DOMAIN_CATALOG", SqlType.SystemName, 128, true),
            new("DOMAIN_SCHEMA", SqlType.SystemName, 128, true),
            new("DOMAIN_NAME", SqlType.SystemName, 128, false),
            new("DATA_TYPE", SqlType.NVarchar, 128, true),
        ], (batch, database) =>
            EnumerateInformationSchemaDomains(batch, database, tableTypeDataType));

        // sys.check_constraints: probe-confirmed 13-column shape (a subset
        // of sys.objects + the check-specific columns). Used by EF Migrations'
        // model snapshot and tooling that introspects existing CHECK rules.
        Sys("check_constraints",
        [
            new("name", SqlType.SystemName, 128, true),
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
            new("is_disabled", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
            new("is_not_trusted", SqlType.Bit, null, false),
            new("parent_column_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("uses_database_collation", SqlType.Bit, null, false),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysCheckConstraints);

        // sys.key_constraints: PK + UNIQUE rows, parallel shape to
        // sys.foreign_keys. Probe-confirmed column set.
        Sys("key_constraints",
        [
            new("name", SqlType.SystemName, 128, true),
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
            new("unique_index_id", SqlType.Int32, null, false),
            new("is_system_named", SqlType.Bit, null, false),
            new("is_enforced", SqlType.Bit, null, false),
        ], EnumerateSysKeyConstraints);

        // sys.default_constraints: per-column named DEFAULT bindings. Real
        // SQL Server emits one row per default (inline or named via ALTER).
        Sys("default_constraints",
        [
            new("name", SqlType.SystemName, 128, true),
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
            new("parent_column_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("is_system_named", SqlType.Bit, null, false),
        ], EnumerateSysDefaultConstraints);

        // sys.indexes: probe-confirmed 24-column shape against SQL Server
        // 2025 (2026-05-14). One row per (table, index) — PK / UQ
        // constraints surface alongside CREATE-INDEX rows, and a HEAP row
        // (index_id = 0, type = 0, name = NULL) appears for any table with
        // no PRIMARY KEY (matching SQL Server's "the table itself is the
        // heap" semantic). EF Migrations introspection reads name /
        // is_unique / is_primary_key / is_unique_constraint /
        // has_filter / filter_definition.
        Sys("indexes",
        [
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_unique", SqlType.Bit, null, false),
            new("data_space_id", SqlType.Int32, null, false),
            new("ignore_dup_key", SqlType.Bit, null, false),
            new("is_primary_key", SqlType.Bit, null, false),
            new("is_unique_constraint", SqlType.Bit, null, false),
            new("fill_factor", SqlType.TinyInt, null, false),
            new("is_padded", SqlType.Bit, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_hypothetical", SqlType.Bit, null, false),
            new("is_ignored_in_optimization", SqlType.Bit, null, false),
            new("allow_row_locks", SqlType.Bit, null, false),
            new("allow_page_locks", SqlType.Bit, null, false),
            new("has_filter", SqlType.Bit, null, false),
            new("filter_definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("compression_delay", SqlType.Int32, null, false),
            new("suppress_dup_key_messages", SqlType.Bit, null, false),
            new("auto_created", SqlType.Bit, null, false),
            new("optimize_for_sequential_key", SqlType.Bit, null, false),
            new("statistics_incremental", SqlType.Bit, null, true),
        ], EnumerateSysIndexes);

        // sys.data_spaces: the simulator models a single PRIMARY row-filegroup
        // (data_space_id = 1) — the same id every sys.indexes row reports for
        // data_space_id, so SMO's LEFT JOIN idx.data_space_id → dsidx resolves.
        // Probe-confirmed shape (SQL Server 2025): name / data_space_id / type
        // char(2) / type_desc / is_default / is_system. 'FG' = ROWS_FILEGROUP;
        // partition schemes ('PS' — what SMO's IsPartitioned probe compares
        // against) aren't modeled, so type is always 'FG'. See
        // docs/claude/catalog-views.md.
        var filegroupType = SqlValue.FromChar(charTwo, "FG");
        var filegroupTypeDesc = SqlValue.FromNVarchar("ROWS_FILEGROUP");
        Sys("data_spaces",
        [
            new("name", SqlType.SystemName, 128, false),
            new("data_space_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_default", SqlType.Bit, null, true),
            new("is_system", SqlType.Bit, null, true),
        ], (batch, database) =>
        {
            _ = (batch, database);
            return
            [
                [
                    SqlValue.FromSystemName("PRIMARY"),
                    SqlValue.FromInt32(1),
                    filegroupType,
                    filegroupTypeDesc,
                    SqlValue.FromBoolean(true),
                    SqlValue.FromBoolean(false),
                ],
            ];
        });

        // sys.index_columns: probe-confirmed 10-column shape. One row per
        // (index, column) pair — KEY columns get key_ordinal = 1..N and
        // index_column_id = 1..N; INCLUDE columns get key_ordinal = 0 and
        // index_column_id continuing past the key column count.
        Sys("index_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("index_column_id", SqlType.Int32, null, false),
            new("column_id", SqlType.Int32, null, false),
            new("key_ordinal", SqlType.TinyInt, null, false),
            new("partition_ordinal", SqlType.TinyInt, null, false),
            new("is_descending_key", SqlType.Bit, null, false),
            new("is_included_column", SqlType.Bit, null, false),
            new("column_store_order_ordinal", SqlType.TinyInt, null, true),
            new("data_clustering_ordinal", SqlType.TinyInt, null, true),
        ], EnumerateSysIndexColumns);

        // sys.dm_tran_locks: per-Hold rows across every schema-bound
        // SchemaLock, every HeapTable.TableDataLock, and every per-row
        // entry in HeapTable.RowLocks. GRANT entries come from
        // LockResource.Holders; WAIT entries from connection registry's
        // WaitingOnResource / WaitingForMode. Shipped column subset is
        // the most commonly read seven fields; the full real-SQL-Server
        // shape has ~18 columns most apps never touch.
        Sys("dm_tran_locks",
        [
            new("resource_type", SqlType.NVarchar, 60, false),
            new("resource_database_id", SqlType.Int32, null, false),
            new("resource_description", SqlType.NVarchar, 256, true),
            new("resource_associated_entity_id", SqlType.BigInt, null, true),
            new("request_mode", SqlType.NVarchar, 60, false),
            new("request_status", SqlType.NVarchar, 60, false),
            new("request_session_id", SqlType.Int32, null, false),
        ], LockDmvs.EnumerateDmTranLocks);

        // sys.dm_os_waiting_tasks: one row per currently-waiting
        // connection. session_id / blocking_session_id are smallint
        // matching real SQL Server; wait_type is `LCK_M_<mode>` per
        // SQL Server's convention.
        Sys("dm_os_waiting_tasks",
        [
            new("session_id", SqlType.SmallInt, null, true),
            new("wait_type", SqlType.NVarchar, 60, true),
            new("resource_description", SqlType.NVarchar, 2000, true),
            new("blocking_session_id", SqlType.SmallInt, null, true),
        ], LockDmvs.EnumerateDmOsWaitingTasks);

        // sys.dm_tran_version_store: one row per finalized HistoricalVersion
        // across every per-table chain. Pending HVs (Xmax = PendingXmax)
        // are excluded. Real SQL Server's exact column order is preserved
        // so existing diagnostic queries port unchanged.
        Sys("dm_tran_version_store",
        [
            new("transaction_sequence_num", SqlType.BigInt, null, false),
            new("version_sequence_num", SqlType.BigInt, null, false),
            new("database_id", SqlType.SmallInt, null, false),
            new("rowset_id", SqlType.BigInt, null, false),
            new("status", SqlType.TinyInt, null, false),
            new("min_length_in_bytes", SqlType.SmallInt, null, false),
            new("record_length_first_part_in_bytes", SqlType.SmallInt, null, false),
            new("record_image_first_part", VarbinarySqlType.MaxForm, null, true),
            new("record_length_second_part_in_bytes", SqlType.SmallInt, null, true),
            new("record_image_second_part", VarbinarySqlType.MaxForm, null, true),
        ], VersionStoreDmvs.EnumerateDmTranVersionStore);

        // sys.dm_tran_version_store_space_usage: aggregate sizing per
        // database. The simulator approximates pages as ceil(bytes / 8192)
        // since HV payloads aren't backed by real pages.
        Sys("dm_tran_version_store_space_usage",
        [
            new("database_id", SqlType.Int32, null, false),
            new("reserved_page_count", SqlType.BigInt, null, false),
            new("reserved_space_kb", SqlType.BigInt, null, false),
        ], VersionStoreDmvs.EnumerateDmTranVersionStoreSpaceUsage);

        // sys.dm_tran_active_snapshot_database_transactions: one row per
        // active SI tx with an allocated snapshot Xid. RCSI per-statement
        // snapshots are not tracked here (matching real SQL Server).
        Sys("dm_tran_active_snapshot_database_transactions",
        [
            new("transaction_id", SqlType.BigInt, null, false),
            new("transaction_sequence_num", SqlType.BigInt, null, false),
            new("commit_sequence_num", SqlType.BigInt, null, true),
            new("session_id", SqlType.Int32, null, false),
            new("is_snapshot", SqlType.Bit, null, false),
            new("first_snapshot_sequence_num", SqlType.BigInt, null, true),
            new("max_version_chain_traversed", SqlType.Int32, null, false),
            new("average_version_chain_traversed", SqlType.Float, null, false),
            new("elapsed_time_seconds", SqlType.BigInt, null, false),
        ], VersionStoreDmvs.EnumerateDmTranActiveSnapshotDatabaseTransactions);

        // sys.extended_properties: per-database user-defined annotations
        // attached to schemas / tables / columns / etc. via the
        // sp_addextendedproperty / sp_updateextendedproperty /
        // sp_dropextendedproperty trio. Real SQL Server's `value` column is
        // typed `sql_variant` — the simulator surfaces it as `nvarchar(MAX)`
        // since sql_variant isn't modeled; AW's 538 properties are all
        // nvarchar values so functional fidelity is preserved.
        Sys("extended_properties",
        [
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", SqlType.SystemName, 60, true),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("value", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), SqlType.MaxLengthSentinel, true),
        ], EnumerateSysExtendedProperties);

        // sys.database_principals: probe-confirmed shipped subset of columns
        // (real SQL Server's full row is ~16 cols). The simulator's principal
        // model is a thin name + id + type triple; columns we don't track
        // (authentication_type, default_schema_name, default_language_name,
        // owning_principal_id, modify_date) are emitted as NULL.
        Sys("database_principals",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("default_schema_name", SqlType.SystemName, 128, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("owning_principal_id", SqlType.Int32, null, true),
            new("sid", SqlType.Varbinary, 85, true),
            new("is_fixed_role", SqlType.Bit, null, false),
            new("authentication_type", SqlType.TinyInt, null, true),
            new("authentication_type_desc", nvarchar60Catalog, 60, true),
        ], EnumerateSysDatabasePrincipals);

        // sys.database_permissions: probe-confirmed 8-col shipped subset.
        // Real SQL Server's row carries a few additional internal columns
        // (e.g. revert_audit_flag); the simulator surfaces the user-visible
        // set only.
        var charOne = CharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit);
        Sys("database_permissions",
        [
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", nvarchar60Catalog, 60, true),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("grantee_principal_id", SqlType.Int32, null, false),
            new("grantor_principal_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("permission_name", nvarchar128Catalog, 128, true),
            new("state", charOne, 1, false),
            new("state_desc", nvarchar60Catalog, 60, true),
        ], EnumerateSysDatabasePermissions);

        // sys.database_role_members: 2-col shipped subset (real SQL Server
        // surfaces just these two — no additional internal columns).
        Sys("database_role_members",
        [
            new("role_principal_id", SqlType.Int32, null, false),
            new("member_principal_id", SqlType.Int32, null, false),
        ], EnumerateSysDatabaseRoleMembers);

        // sys.server_principals: probe-confirmed 14-col shape against SQL
        // Server 2025 (2026-07-15), projected over the per-Simulation login
        // registry (Simulation.Logins) plus two synthetic fixed rows: sa
        // (principal_id 1) and public (principal_id 2). Columns the simulator
        // doesn't track (credential_id, disabled flag) surface as their real
        // low-privilege defaults.
        Sys("server_principals",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, false),
            new("sid", SqlType.Varbinary, 85, true),
            new("type", charOne, 1, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_disabled", SqlType.Bit, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("default_database_name", SqlType.SystemName, 128, true),
            new("default_language_name", SqlType.SystemName, 128, true),
            new("credential_id", SqlType.Int32, null, true),
            new("owning_principal_id", SqlType.Int32, null, true),
            new("is_fixed_role", SqlType.Bit, null, false),
            new("tenant_id", SqlType.UniqueIdentifier, null, true),
        ], EnumerateSysServerPrincipals);

        // sys.sql_logins: probe-confirmed 14-col shape against SQL Server 2025
        // (2026-07-15). Same leading 10 columns as sys.server_principals,
        // filtered to type='S' (SQL logins) — sa plus the registry logins,
        // never the public server role. password_hash surfaces NULL: the
        // simulator deliberately doesn't expose its stored PWDCOMPARE hash,
        // matching what a low-privilege reader sees on the reference instance.
        Sys("sql_logins",
        [
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, false),
            new("sid", SqlType.Varbinary, 85, true),
            new("type", charOne, 1, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_disabled", SqlType.Bit, null, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("default_database_name", SqlType.SystemName, 128, true),
            new("default_language_name", SqlType.SystemName, 128, true),
            new("credential_id", SqlType.Int32, null, true),
            new("is_policy_checked", SqlType.Bit, null, true),
            new("is_expiration_checked", SqlType.Bit, null, true),
            new("password_hash", SqlType.Varbinary, 256, true),
        ], EnumerateSysSqlLogins);

        // sys.fulltext_catalogs: per-database full-text catalog metadata.
        // Column subset matches Microsoft Learn's documented surface for
        // SQL Server 2022+ (the reference instance doesn't have full-text
        // installed, so probe-confirmation isn't available — column shapes
        // are taken from learn.microsoft.com/sql/relational-databases/system-catalog-views/sys-fulltext-catalogs-transact-sql).
        Sys("fulltext_catalogs",
        [
            new("fulltext_catalog_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("path", SqlType.NVarchar, 260, true),
            new("is_default", SqlType.Bit, null, false),
            new("is_accent_sensitivity_on", SqlType.Bit, null, false),
            new("data_space_id", SqlType.Int32, null, true),
            new("file_id", SqlType.Int32, null, true),
            new("principal_id", SqlType.Int32, null, false),
            new("is_importing", SqlType.Bit, null, false),
        ], EnumerateSysFullTextCatalogs);

        // sys.fulltext_indexes: per-database full-text indexes. One row per
        // indexed table. Column subset from Microsoft Learn.
        Sys("fulltext_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("unique_index_id", SqlType.Int32, null, false),
            new("fulltext_catalog_id", SqlType.Int32, null, false),
            new("is_enabled", SqlType.Bit, null, false),
            new("change_tracking_state", charOne, 1, false),
            new("change_tracking_state_desc", nvarchar60Catalog, 60, true),
            new("has_crawl_completed", SqlType.Bit, null, false),
            new("crawl_type", charOne, 1, false),
            new("crawl_type_desc", nvarchar60Catalog, 60, true),
            new("crawl_start_date", SqlType.DateTime, null, true),
            new("crawl_end_date", SqlType.DateTime, null, true),
            new("stoplist_id", SqlType.Int32, null, true),
            new("data_space_id", SqlType.Int32, null, true),
            new("property_list_id", SqlType.Int32, null, true),
        ], EnumerateSysFullTextIndexes);

        // sys.fulltext_index_columns: one row per indexed column inside each
        // full-text index. column_id = 1-based storage ordinal of the
        // indexed column; type_column_id = nullable ordinal of the paired
        // doc-extension column for varbinary indexes.
        Sys("fulltext_index_columns",
        [
            new("object_id", SqlType.Int32, null, false),
            new("column_id", SqlType.Int32, null, false),
            new("type_column_id", SqlType.Int32, null, true),
            new("language_id", SqlType.Int32, null, false),
            new("statistical_semantics", SqlType.Bit, null, false),
        ], EnumerateSysFullTextIndexColumns);

        // sys.xml_schema_collections: probe-confirmed 6-col shipped subset
        // against SQL Server 2025 (2026-05-15). Real SQL Server's
        // principal_id column is nullable; the simulator's pre-seeded
        // collections leave it NULL.
        Sys("xml_schema_collections",
        [
            new("xml_collection_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("name", SqlType.SystemName, 128, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
        ], EnumerateSysXmlSchemaCollections);

        // sys.xml_indexes: probe-confirmed 9-col shipped subset (real SQL
        // Server's row is 26 cols including a long is_disabled / is_padded
        // / allow_row_locks tail of admin flags). The simulator surfaces
        // the AW-load-bearing core: identity, primary/secondary
        // discriminator, and the FOR-PATH/VALUE/PROPERTY classifier.
        Sys("xml_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("using_xml_index_id", SqlType.Int32, null, true),
            new("secondary_type", charOne, 1, true),
            new("secondary_type_desc", nvarchar60Catalog, 60, true),
            new("is_primary_key", SqlType.Bit, null, true),
        ], EnumerateSysXmlIndexes);

        // sys.spatial_indexes: probe-confirmed 23-col shape against SQL Server
        // 2025 (2026-05-15). Same shape as sys.indexes except (type, type_desc)
        // are fixed to (4, 'SPATIAL') and the four trailing spatial-specific
        // columns describe the tessellation. The simulator surfaces the
        // load-bearing identity + spatial classification subset; the
        // is_disabled / is_padded / allow_row_locks tail mirrors real values
        // (false / false / true / true) but isn't read by any application
        // path the loader cares about.
        Sys("spatial_indexes",
        [
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("is_unique", SqlType.Bit, null, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("ignore_dup_key", SqlType.Bit, null, true),
            new("is_primary_key", SqlType.Bit, null, true),
            new("is_unique_constraint", SqlType.Bit, null, true),
            new("fill_factor", SqlType.TinyInt, null, false),
            new("is_padded", SqlType.Bit, null, true),
            new("is_disabled", SqlType.Bit, null, true),
            new("is_hypothetical", SqlType.Bit, null, true),
            new("is_ignored_in_optimization", SqlType.Bit, null, true),
            new("allow_row_locks", SqlType.Bit, null, true),
            new("allow_page_locks", SqlType.Bit, null, true),
            new("spatial_index_type", SqlType.Int32, null, false),
            new("spatial_index_type_desc", nvarchar60Catalog, 60, true),
            new("tessellation_scheme", SqlType.NVarchar, 60, true),
            new("has_filter", SqlType.Bit, null, false),
            new("filter_definition", NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault), null, true),
            new("auto_created", SqlType.Bit, null, true),
        ], EnumerateSysSpatialIndexes);

        // sys.spatial_index_tessellations: probe-confirmed 16-col shape
        // against SQL Server 2025 (2026-05-15). One row per spatial index
        // carrying the per-index bounding-box + 4-level grid detail.
        Sys("spatial_index_tessellations",
        [
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("tessellation_scheme", SqlType.NVarchar, 60, true),
            new("bounding_box_xmin", SqlType.Float, null, true),
            new("bounding_box_ymin", SqlType.Float, null, true),
            new("bounding_box_xmax", SqlType.Float, null, true),
            new("bounding_box_ymax", SqlType.Float, null, true),
            new("level_1_grid", SqlType.SmallInt, null, true),
            new("level_1_grid_desc", SqlType.NVarchar, 60, true),
            new("level_2_grid", SqlType.SmallInt, null, true),
            new("level_2_grid_desc", SqlType.NVarchar, 60, true),
            new("level_3_grid", SqlType.SmallInt, null, true),
            new("level_3_grid_desc", SqlType.NVarchar, 60, true),
            new("level_4_grid", SqlType.SmallInt, null, true),
            new("level_4_grid_desc", SqlType.NVarchar, 60, true),
            new("cells_per_object", SqlType.Int32, null, true),
        ], EnumerateSysSpatialIndexTessellations);

        // sys.spatial_reference_systems: real SQL Server seeds this view with
        // ~390 rows of authoritative SRID definitions (EPSG / ESRI). The
        // simulator surfaces an empty view — the column shape matches probe
        // and the catalog is reachable, but no SRID rows pre-populate. This
        // keeps applications that reference the view's schema from breaking
        // without the byte-tonnage of the WKT-laden seed data.
        Sys("spatial_reference_systems",
        [
            new("spatial_reference_id", SqlType.Int32, null, true),
            new("authority_name", SqlType.NVarchar, 256, true),
            new("authorized_spatial_reference_id", SqlType.Int32, null, true),
            new("well_known_text", SqlType.NVarchar, 8000, true),
            new("unit_of_measure", SqlType.NVarchar, 256, true),
            new("unit_conversion_factor", SqlType.Float, null, true),
        ], EnumerateSysSpatialReferenceSystems);

        // sys.databases: the full 98-column projection SQL Server 2025 emits,
        // so SSMS's SMO Object-Explorer enumeration (which references
        // owner_sid / create_date / state_desc / recovery_model_desc /
        // containment / the is_* option flags) resolves every column. One row
        // per Database via DatabasesWithIds. Modeled columns read live Database
        // state (name / database_id / compatibility_level / collation_name /
        // snapshot-isolation trio / recovery_model / state); the remaining
        // option-flag columns carry a stock freshly-created-user-database
        // profile as constant defaults (see EnumerateSysDatabases).
        Sys("databases",
        [
            new("name", SqlType.SystemName, 128, false),
            new("database_id", SqlType.Int32, null, false),
            new("source_database_id", SqlType.Int32, null, true),
            new("owner_sid", SqlType.Varbinary, 85, true),
            new("create_date", SqlType.DateTime, null, false),
            new("compatibility_level", SqlType.TinyInt, null, false),
            new("collation_name", SqlType.SystemName, 128, true),
            new("user_access", SqlType.TinyInt, null, true),
            new("user_access_desc", nvarchar60Catalog, 60, true),
            new("is_read_only", SqlType.Bit, null, true),
            new("is_auto_close_on", SqlType.Bit, null, false),
            new("is_auto_shrink_on", SqlType.Bit, null, true),
            new("state", SqlType.TinyInt, null, true),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("is_in_standby", SqlType.Bit, null, true),
            new("is_cleanly_shutdown", SqlType.Bit, null, true),
            new("is_supplemental_logging_enabled", SqlType.Bit, null, true),
            new("snapshot_isolation_state", SqlType.TinyInt, null, true),
            new("snapshot_isolation_state_desc", nvarchar60Catalog, 60, true),
            new("is_read_committed_snapshot_on", SqlType.Bit, null, true),
            new("recovery_model", SqlType.TinyInt, null, true),
            new("recovery_model_desc", nvarchar60Catalog, 60, true),
            new("page_verify_option", SqlType.TinyInt, null, true),
            new("page_verify_option_desc", nvarchar60Catalog, 60, true),
            new("is_auto_create_stats_on", SqlType.Bit, null, true),
            new("is_auto_create_stats_incremental_on", SqlType.Bit, null, true),
            new("is_auto_update_stats_on", SqlType.Bit, null, true),
            new("is_auto_update_stats_async_on", SqlType.Bit, null, true),
            new("is_ansi_null_default_on", SqlType.Bit, null, true),
            new("is_ansi_nulls_on", SqlType.Bit, null, true),
            new("is_ansi_padding_on", SqlType.Bit, null, true),
            new("is_ansi_warnings_on", SqlType.Bit, null, true),
            new("is_arithabort_on", SqlType.Bit, null, true),
            new("is_concat_null_yields_null_on", SqlType.Bit, null, true),
            new("is_numeric_roundabort_on", SqlType.Bit, null, true),
            new("is_quoted_identifier_on", SqlType.Bit, null, true),
            new("is_recursive_triggers_on", SqlType.Bit, null, true),
            new("is_cursor_close_on_commit_on", SqlType.Bit, null, true),
            new("is_local_cursor_default", SqlType.Bit, null, true),
            new("is_fulltext_enabled", SqlType.Bit, null, true),
            new("is_trustworthy_on", SqlType.Bit, null, true),
            new("is_db_chaining_on", SqlType.Bit, null, true),
            new("is_parameterization_forced", SqlType.Bit, null, true),
            new("is_master_key_encrypted_by_server", SqlType.Bit, null, false),
            new("is_query_store_on", SqlType.Bit, null, true),
            new("is_published", SqlType.Bit, null, false),
            new("is_subscribed", SqlType.Bit, null, false),
            new("is_merge_published", SqlType.Bit, null, false),
            new("is_distributor", SqlType.Bit, null, false),
            new("is_sync_with_backup", SqlType.Bit, null, false),
            new("service_broker_guid", SqlType.UniqueIdentifier, null, false),
            new("is_broker_enabled", SqlType.Bit, null, false),
            new("log_reuse_wait", SqlType.TinyInt, null, true),
            new("log_reuse_wait_desc", nvarchar60Catalog, 60, true),
            new("is_date_correlation_on", SqlType.Bit, null, false),
            new("is_cdc_enabled", SqlType.Bit, null, false),
            new("is_encrypted", SqlType.Bit, null, true),
            new("is_honor_broker_priority_on", SqlType.Bit, null, true),
            new("replica_id", SqlType.UniqueIdentifier, null, true),
            new("group_database_id", SqlType.UniqueIdentifier, null, true),
            new("resource_pool_id", SqlType.Int32, null, true),
            new("default_language_lcid", SqlType.SmallInt, null, true),
            new("default_language_name", nvarchar128Catalog, 128, true),
            new("default_fulltext_language_lcid", SqlType.Int32, null, true),
            new("default_fulltext_language_name", nvarchar128Catalog, 128, true),
            new("is_nested_triggers_on", SqlType.Bit, null, true),
            new("is_transform_noise_words_on", SqlType.Bit, null, true),
            new("two_digit_year_cutoff", SqlType.SmallInt, null, true),
            new("containment", SqlType.TinyInt, null, true),
            new("containment_desc", nvarchar60Catalog, 60, true),
            new("target_recovery_time_in_seconds", SqlType.Int32, null, true),
            new("delayed_durability", SqlType.Int32, null, true),
            new("delayed_durability_desc", nvarchar60Catalog, 60, true),
            new("is_memory_optimized_elevate_to_snapshot_on", SqlType.Bit, null, true),
            new("is_federation_member", SqlType.Bit, null, true),
            new("is_remote_data_archive_enabled", SqlType.Bit, null, true),
            new("is_mixed_page_allocation_on", SqlType.Bit, null, true),
            new("is_temporal_history_retention_enabled", SqlType.Bit, null, true),
            new("catalog_collation_type", SqlType.Int32, null, false),
            new("catalog_collation_type_desc", nvarchar60Catalog, 60, true),
            new("physical_database_name", nvarchar128Catalog, 128, true),
            new("is_result_set_caching_on", SqlType.Bit, null, true),
            new("is_accelerated_database_recovery_on", SqlType.Bit, null, true),
            new("is_tempdb_spill_to_remote_store", SqlType.Bit, null, true),
            new("is_stale_page_detection_on", SqlType.Bit, null, true),
            new("is_memory_optimized_enabled", SqlType.Bit, null, true),
            new("is_data_retention_enabled", SqlType.Bit, null, true),
            new("is_ledger_on", SqlType.Bit, null, true),
            new("is_change_feed_enabled", SqlType.Bit, null, true),
            new("is_data_lake_replication_enabled", SqlType.Bit, null, true),
            new("is_event_stream_enabled", SqlType.Bit, null, true),
            new("data_compaction", SqlType.TinyInt, null, true),
            new("data_compaction_desc", nvarchar60Catalog, 60, true),
            new("data_lake_log_publishing", SqlType.TinyInt, null, true),
            new("data_lake_log_publishing_desc", nvarchar60Catalog, 60, true),
            new("is_vorder_enabled", SqlType.Bit, null, true),
            new("is_proactive_statistics_refresh_on", SqlType.Bit, null, true),
            new("is_optimized_locking_on", SqlType.Bit, null, true),
        ], EnumerateSysDatabases);

        // sys.fn_helpcollations() — table-valued metadata function listing the
        // collations the simulator recognizes. Real SQL Server emits ~5540
        // rows; the simulator emits the whitelist defined in Collation.Recognized
        // (currently 2). Each row carries the canonical name + a human
        // description, matching real SQL Server's column shape.
        Sys("fn_helpcollations",
        [
            new("name", SqlType.SystemName, 128, true),
            new("description", SqlType.NVarchar, 1000, true),
        ], EnumerateFnHelpCollations);

        // sys.servers: the local instance projects as row 0 (is_linked = 0);
        // each entry in <see cref="Simulation.ActiveLinkedServers"/> follows
        // with a stable monotonic server_id keyed by name-sort. Real SQL
        // Server exposes ~26 columns; the simulator surfaces the
        // load-bearing subset that BACPAC scripts + diagnostic queries
        // touch (server_id / name / product / provider / data_source /
        // is_linked). modify_date is always epoch-zero since linked-server
        // registration doesn't carry a creation timestamp.
        Sys("servers",
        [
            new("server_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("product", SqlType.NVarchar, 128, true),
            new("provider", SqlType.NVarchar, 128, true),
            new("data_source", SqlType.NVarchar, 4000, true),
            new("is_linked", SqlType.Bit, null, false),
        ], EnumerateSysServers);

        // sys.dm_os_host_info: single-row, server-scope DMV describing the
        // host operating system. SSMS selects host_platform from it on every
        // connect. The row reflects the actual .NET host process rather than a
        // canned Windows row: host_platform via OperatingSystem.Is*,
        // host_architecture via RuntimeInformation.OSArchitecture (uppercased),
        // and on Linux host_distribution / host_release parsed from
        // /etc/os-release. host_sku is 48 on Windows and NULL elsewhere
        // (matching real SQL Server on Linux); os_language_version is 1033;
        // host_service_pack_level is the empty string. Computed once into
        // DmOsHostInfoRows since host identity is fixed for the process lifetime.
        Sys("dm_os_host_info",
        [
            new("host_platform", SqlType.NVarchar, 256, false),
            new("host_distribution", SqlType.NVarchar, 256, false),
            new("host_release", SqlType.NVarchar, 256, false),
            new("host_service_pack_level", SqlType.NVarchar, 256, false),
            new("host_sku", SqlType.Int32, null, true),
            new("os_language_version", SqlType.Int32, null, false),
            new("host_architecture", SqlType.NVarchar, 256, false),
        ], (batch, database) => DmOsHostInfoRows);

        // sys.configurations: server-scoped static server-configuration
        // catalog. Real SQL Server types value / minimum / maximum /
        // value_in_use as sql_variant; the simulator surfaces them as bigint
        // (config values like 'max server memory (MB)' exceed int range),
        // following the same sql_variant-to-concrete-base substitution
        // sys.sequences uses for its sql_variant-typed value columns. The 106
        // rows are a stock instance's defaults (probe-confirmed against SQL
        // Server 2025) — configuration_id and name are stable across
        // instances, and value mirrors value_in_use on a fresh server. This is
        // static catalog data, not a live settings model: SET / sp_configure
        // changes are not reflected. SMO reads value_in_use for
        // configuration_id 16384 (Agent XPs) during SSMS's Object-Explorer
        // database-node preamble, so the row set must resolve for that folder
        // to populate. Row set is independent of the database argument.
        Sys("configurations",
        [
            new("configuration_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 35, false),
            new("value", SqlType.BigInt, null, true),
            new("minimum", SqlType.BigInt, null, true),
            new("maximum", SqlType.BigInt, null, true),
            new("value_in_use", SqlType.BigInt, null, true),
            new("description", SqlType.NVarchar, 255, false),
            new("is_dynamic", SqlType.Bit, null, false),
            new("is_advanced", SqlType.Bit, null, false),
        ], (batch, database) => ConfigurationsRows);

        // sys.database_mirroring: one row per database (join key database_id),
        // surfaced so SSMS's Object-Explorer enumeration
        // (master.sys.databases LEFT JOIN sys.database_mirroring) populates the
        // Databases folder. The simulator never mirrors a database, so every
        // mirroring_* column is NULL on every row — the exact non-mirrored
        // shape a live SQL Server 2025 returns (probe-confirmed: only
        // database_id populated). mirroring_failover_lsn / _end_of_log_lsn /
        // _replication_lsn are numeric(25, 0) on the server; surfaced NULL.
        Sys("database_mirroring",
        [
            new("database_id", SqlType.Int32, null, false),
            new("mirroring_guid", SqlType.UniqueIdentifier, null, true),
            new("mirroring_state", SqlType.TinyInt, null, true),
            new("mirroring_state_desc", nvarchar60Catalog, 60, true),
            new("mirroring_role", SqlType.TinyInt, null, true),
            new("mirroring_role_desc", nvarchar60Catalog, 60, true),
            new("mirroring_role_sequence", SqlType.Int32, null, true),
            new("mirroring_safety_level", SqlType.TinyInt, null, true),
            new("mirroring_safety_level_desc", nvarchar60Catalog, 60, true),
            new("mirroring_safety_sequence", SqlType.Int32, null, true),
            new("mirroring_partner_name", SqlType.NVarchar, 128, true),
            new("mirroring_partner_instance", SqlType.NVarchar, 128, true),
            new("mirroring_witness_name", SqlType.NVarchar, 128, true),
            new("mirroring_witness_state", SqlType.TinyInt, null, true),
            new("mirroring_witness_state_desc", nvarchar60Catalog, 60, true),
            new("mirroring_failover_lsn", lsnNumeric, null, true),
            new("mirroring_connection_timeout", SqlType.Int32, null, true),
            new("mirroring_redo_queue", SqlType.Int32, null, true),
            new("mirroring_redo_queue_type", nvarchar60Catalog, 60, true),
            new("mirroring_end_of_log_lsn", lsnNumeric, null, true),
            new("mirroring_replication_lsn", lsnNumeric, null, true),
        ], EnumerateSysDatabaseMirroring);

        // sys.availability_replicas: server-scope AlwaysOn Availability-Group
        // catalog. No AGs are configured in the simulator, so the view is
        // always empty — SSMS's enumeration does
        // `insert into #tmp select replica_id, group_id, replica_server_name
        // from master.sys.availability_replicas`, which must resolve and
        // return zero rows. Full column shape modeled so future tooling
        // selecting other columns doesn't hit Msg 207.
        Sys("availability_replicas",
        [
            new("replica_id", SqlType.UniqueIdentifier, null, true),
            new("group_id", SqlType.UniqueIdentifier, null, true),
            new("replica_metadata_id", SqlType.Int32, null, true),
            new("replica_server_name", SqlType.NVarchar, 256, true),
            new("owner_sid", SqlType.Varbinary, 85, true),
            new("endpoint_url", SqlType.NVarchar, 256, true),
            new("availability_mode", SqlType.TinyInt, null, true),
            new("availability_mode_desc", nvarchar60Catalog, 60, true),
            new("failover_mode", SqlType.TinyInt, null, true),
            new("failover_mode_desc", nvarchar60Catalog, 60, true),
            new("session_timeout", SqlType.Int32, null, true),
            new("primary_role_allow_connections", SqlType.TinyInt, null, true),
            new("primary_role_allow_connections_desc", nvarchar60Catalog, 60, true),
            new("secondary_role_allow_connections", SqlType.TinyInt, null, true),
            new("secondary_role_allow_connections_desc", nvarchar60Catalog, 60, true),
            new("create_date", SqlType.DateTime, null, true),
            new("modify_date", SqlType.DateTime, null, true),
            new("backup_priority", SqlType.Int32, null, true),
            new("read_only_routing_url", SqlType.NVarchar, 256, true),
            new("seeding_mode", SqlType.TinyInt, null, true),
            new("seeding_mode_desc", nvarchar60Catalog, 60, true),
            new("read_write_routing_url", SqlType.NVarchar, 256, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.availability_groups: server-scope AlwaysOn catalog, always empty
        // (no AGs configured). SSMS's enumeration does
        // `insert into #tmp select group_id, name from
        // master.sys.availability_groups`.
        Sys("availability_groups",
        [
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("name", SqlType.NVarchar, 128, true),
            new("resource_id", SqlType.NVarchar, 40, true),
            new("resource_group_id", SqlType.NVarchar, 40, true),
            new("failure_condition_level", SqlType.Int32, null, true),
            new("health_check_timeout", SqlType.Int32, null, true),
            new("automated_backup_preference", SqlType.TinyInt, null, true),
            new("automated_backup_preference_desc", nvarchar60Catalog, 60, true),
            new("version", SqlType.SmallInt, null, true),
            new("basic_features", SqlType.Bit, null, true),
            new("dtc_support", SqlType.Bit, null, true),
            new("db_failover", SqlType.Bit, null, true),
            new("is_distributed", SqlType.Bit, null, true),
            new("cluster_type", SqlType.TinyInt, null, true),
            new("cluster_type_desc", nvarchar60Catalog, 60, true),
            new("required_synchronized_secondaries_to_commit", SqlType.Int32, null, true),
            new("sequence_number", SqlType.BigInt, null, true),
            new("is_contained", SqlType.Bit, null, true),
            new("cluster_connection_options", SqlType.NVarchar, 4000, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.dm_hadr_database_replica_states: server-scope AlwaysOn DMV,
        // always empty (no AGs). SSMS's enumeration does
        // `insert into #tmp select group_database_id, synchronization_state,
        // is_local, group_id, database_id from
        // master.sys.dm_hadr_database_replica_states`. LSN columns are
        // numeric(25, 0).
        Sys("dm_hadr_database_replica_states",
        [
            new("database_id", SqlType.Int32, null, false),
            new("group_id", SqlType.UniqueIdentifier, null, false),
            new("replica_id", SqlType.UniqueIdentifier, null, false),
            new("group_database_id", SqlType.UniqueIdentifier, null, false),
            new("is_local", SqlType.Bit, null, true),
            new("is_primary_replica", SqlType.Bit, null, true),
            new("synchronization_state", SqlType.TinyInt, null, true),
            new("synchronization_state_desc", nvarchar60Catalog, 60, true),
            new("is_commit_participant", SqlType.Bit, null, true),
            new("synchronization_health", SqlType.TinyInt, null, true),
            new("synchronization_health_desc", nvarchar60Catalog, 60, true),
            new("database_state", SqlType.TinyInt, null, true),
            new("database_state_desc", nvarchar60Catalog, 60, true),
            new("is_suspended", SqlType.Bit, null, true),
            new("suspend_reason", SqlType.TinyInt, null, true),
            new("suspend_reason_desc", nvarchar60Catalog, 60, true),
            new("recovery_lsn", lsnNumeric, null, true),
            new("truncation_lsn", lsnNumeric, null, true),
            new("last_sent_lsn", lsnNumeric, null, true),
            new("last_sent_time", SqlType.DateTime, null, true),
            new("last_received_lsn", lsnNumeric, null, true),
            new("last_received_time", SqlType.DateTime, null, true),
            new("last_hardened_lsn", lsnNumeric, null, true),
            new("last_hardened_time", SqlType.DateTime, null, true),
            new("last_redone_lsn", lsnNumeric, null, true),
            new("last_redone_time", SqlType.DateTime, null, true),
            new("log_send_queue_size", SqlType.BigInt, null, true),
            new("log_send_rate", SqlType.BigInt, null, true),
            new("redo_queue_size", SqlType.BigInt, null, true),
            new("redo_rate", SqlType.BigInt, null, true),
            new("filestream_send_rate", SqlType.BigInt, null, true),
            new("end_of_log_lsn", lsnNumeric, null, true),
            new("last_commit_lsn", lsnNumeric, null, true),
            new("last_commit_time", SqlType.DateTime, null, true),
            new("low_water_mark_for_ghosts", SqlType.BigInt, null, true),
            new("secondary_lag_seconds", SqlType.BigInt, null, true),
            new("quorum_commit_lsn", lsnNumeric, null, true),
            new("quorum_commit_time", SqlType.DateTime, null, true),
            new("is_internal", SqlType.Bit, null, true),
        ], static (_, _) => EmptyCatalogRows);

        // sys.master_files: one data file (type 0, ROWS) + one log file
        // (type 1, LOG) per database, join key database_id. SSMS probes for
        // in-memory-OLTP filegroups via
        // `... from master.sys.master_files mf ... where mf.[type] = 2`, which
        // must return nothing — the simulator emits no type-2 (FILESTREAM /
        // memory-optimized) files. File contents are synthetic: logical name
        // `<db>_Data` / `<db>_Log`, a plausible physical path, a small page
        // count, unlimited max_size, 64 MB growth. All LSN columns numeric(25, 0),
        // surfaced NULL (no physical log).
        Sys("master_files",
        [
            new("database_id", SqlType.Int32, null, false),
            new("file_id", SqlType.Int32, null, false),
            new("file_guid", SqlType.UniqueIdentifier, null, true),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("data_space_id", SqlType.Int32, null, false),
            new("name", SqlType.NVarchar, 128, true),
            new("physical_name", SqlType.NVarchar, 260, false),
            new("state", SqlType.TinyInt, null, true),
            new("state_desc", nvarchar60Catalog, 60, true),
            new("size", SqlType.Int32, null, false),
            new("max_size", SqlType.Int32, null, false),
            new("growth", SqlType.Int32, null, false),
            new("is_media_read_only", SqlType.Bit, null, false),
            new("is_read_only", SqlType.Bit, null, false),
            new("is_sparse", SqlType.Bit, null, false),
            new("is_percent_growth", SqlType.Bit, null, false),
            new("is_name_reserved", SqlType.Bit, null, false),
            new("is_persistent_log_buffer", SqlType.Bit, null, false),
            new("create_lsn", lsnNumeric, null, true),
            new("drop_lsn", lsnNumeric, null, true),
            new("read_only_lsn", lsnNumeric, null, true),
            new("read_write_lsn", lsnNumeric, null, true),
            new("differential_base_lsn", lsnNumeric, null, true),
            new("differential_base_guid", SqlType.UniqueIdentifier, null, true),
            new("differential_base_time", SqlType.DateTime, null, true),
            new("redo_start_lsn", lsnNumeric, null, true),
            new("redo_start_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("redo_target_lsn", lsnNumeric, null, true),
            new("redo_target_fork_guid", SqlType.UniqueIdentifier, null, true),
            new("backup_lsn", lsnNumeric, null, true),
            new("credential_id", SqlType.Int32, null, true),
        ], EnumerateSysMasterFiles);

        // sys.database_query_store_options: per-database view (join key is the
        // current database context, not a database_id column). Query Store is
        // never enabled in the simulator, so a user database returns exactly
        // one OFF row and a system database (master/tempdb/model/msdb) returns
        // zero rows — the exact split a live SQL Server 2025 returns
        // (probe-confirmed 2026-07-15). SSMS's Query Store probe gates on
        // OBJECT_ID(N'[sys].[database_query_store_options]') resolving and then
        // reads actual_state. nvarchar(60) _desc columns carry the probed
        // max_length=120 bytes; actual_state_additional_info is nvarchar(4000)
        // (probed max_length=8000 bytes), surfaced as the empty string.
        Sys("database_query_store_options",
        [
            new("desired_state", SqlType.SmallInt, null, false),
            new("desired_state_desc", nvarchar60Catalog, 60, true),
            new("actual_state", SqlType.SmallInt, null, false),
            new("actual_state_desc", nvarchar60Catalog, 60, true),
            new("readonly_reason", SqlType.Int32, null, true),
            new("current_storage_size_mb", SqlType.BigInt, null, true),
            new("flush_interval_seconds", SqlType.BigInt, null, true),
            new("interval_length_minutes", SqlType.BigInt, null, true),
            new("max_storage_size_mb", SqlType.BigInt, null, true),
            new("stale_query_threshold_days", SqlType.BigInt, null, true),
            new("max_plans_per_query", SqlType.BigInt, null, true),
            new("query_capture_mode", SqlType.SmallInt, null, false),
            new("query_capture_mode_desc", nvarchar60Catalog, 60, true),
            new("capture_policy_execution_count", SqlType.Int32, null, true),
            new("capture_policy_total_compile_cpu_time_ms", SqlType.BigInt, null, true),
            new("capture_policy_total_execution_cpu_time_ms", SqlType.BigInt, null, true),
            new("capture_policy_stale_threshold_hours", SqlType.Int32, null, true),
            new("size_based_cleanup_mode", SqlType.SmallInt, null, false),
            new("size_based_cleanup_mode_desc", nvarchar60Catalog, 60, true),
            new("wait_stats_capture_mode", SqlType.SmallInt, null, false),
            new("wait_stats_capture_mode_desc", nvarchar60Catalog, 60, true),
            new("actual_state_additional_info", SqlType.NVarchar, 4000, true),
        ], EnumerateSysDatabaseQueryStoreOptions);

        // sys.query_store_runtime_stats: per-plan runtime-statistics capture.
        // The simulator never runs Query Store, so no runtime stats are ever
        // captured and the view is always empty. SSMS's Query Store probe does
        // IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats), which
        // must resolve and return zero rows. Column shape probe-confirmed
        // against SQL Server 2025 (2026-07-15): the first nine metric groups
        // carry NOT NULL last/min/max columns, the last four NULL.
        Sys("query_store_runtime_stats",
            BuildQueryStoreRuntimeStatsColumns(nvarchar60Catalog),
            static (_, _) => EmptyCatalogRows);

        return views;
    }

    /// <summary>
    /// Column shape for <c>sys.query_store_runtime_stats</c>. The nine
    /// "core" metrics (duration, cpu_time, logical/physical IO, clr_time,
    /// dop, query_max_used_memory, rowcount) expose NOT NULL last/min/max
    /// columns; the four "extended" metrics (num_physical_io_reads,
    /// log_bytes_used, tempdb_space_used, page_server_io_reads) expose them
    /// NULL — matching the probed SQL Server 2025 catalog shape. The view is
    /// always empty, so the column set only ever backs metadata reads.
    /// </summary>
    private static HeapColumn[] BuildQueryStoreRuntimeStatsColumns(NVarcharSqlType nvarchar60Catalog)
    {
        var columns = new List<HeapColumn>
        {
            new("runtime_stats_id", SqlType.BigInt, null, false),
            new("plan_id", SqlType.BigInt, null, false),
            new("runtime_stats_interval_id", SqlType.BigInt, null, false),
            new("execution_type", SqlType.TinyInt, null, false),
            new("execution_type_desc", nvarchar60Catalog, 60, true),
            new("first_execution_time", SqlType.GetDateTimeOffset(7), null, false),
            new("last_execution_time", SqlType.GetDateTimeOffset(7), null, false),
            new("count_executions", SqlType.BigInt, null, false),
        };

        void Metric(string metric, bool aggregatesNullable)
        {
            columns.Add(new("avg_" + metric, SqlType.Float, null, true));
            columns.Add(new("last_" + metric, SqlType.BigInt, null, aggregatesNullable));
            columns.Add(new("min_" + metric, SqlType.BigInt, null, aggregatesNullable));
            columns.Add(new("max_" + metric, SqlType.BigInt, null, aggregatesNullable));
            columns.Add(new("stdev_" + metric, SqlType.Float, null, true));
        }

        foreach (var metric in new[]
        {
            "duration", "cpu_time", "logical_io_reads", "logical_io_writes",
            "physical_io_reads", "clr_time", "dop", "query_max_used_memory", "rowcount",
        })
        {
            Metric(metric, aggregatesNullable: false);
        }

        foreach (var metric in new[]
        {
            "num_physical_io_reads", "log_bytes_used", "tempdb_space_used", "page_server_io_reads",
        })
        {
            Metric(metric, aggregatesNullable: true);
        }

        columns.Add(new("replica_group_id", SqlType.BigInt, null, false));
        return [.. columns];
    }

    /// <summary>
    /// Shared empty row set for the AlwaysOn Availability-Group catalog views
    /// (<c>sys.availability_replicas</c> / <c>sys.availability_groups</c> /
    /// <c>sys.dm_hadr_database_replica_states</c>). No AGs are ever configured
    /// in the simulator, so all three project zero rows; SSMS's enumeration
    /// relies only on their column shape resolving for its
    /// <c>INSERT … SELECT … FROM</c> preamble.
    /// </summary>
    private static readonly SqlValue[][] EmptyCatalogRows = [];

    /// <summary>
    /// The single row projected by <c>sys.dm_os_host_info</c>. Materialized
    /// once at first access — the host operating system, architecture, and
    /// distribution can't change during the process lifetime, so the row is
    /// shared across every read (matching how the constant catalog-view cells
    /// elsewhere are reused).
    /// </summary>
    private static readonly SqlValue[][] DmOsHostInfoRows = [BuildDmOsHostInfoRow()];

    private static SqlValue[] BuildDmOsHostInfoRow()
    {
        string platform, distribution, release;
        SqlValue sku;
        if (OperatingSystem.IsWindows())
        {
            platform = "Windows";
            distribution = "Windows";
            var version = Environment.OSVersion.Version;
            release = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}");
            sku = SqlValue.FromInt32(48);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Real SQL Server never runs on macOS; report the OS honestly
            // rather than mislabeling it 'Linux'. host_sku is NULL as on Linux.
            platform = "macOS";
            distribution = "macOS";
            release = "";
            sku = SqlValue.Null(SqlType.Int32);
        }
        else
        {
            platform = "Linux";
            var osRelease = ReadOsRelease();
            distribution = osRelease.TryGetValue("NAME", out var name) && name.Length > 0 ? name : "Linux";
            release = osRelease.TryGetValue("VERSION_ID", out var versionId) ? versionId : "";
            sku = SqlValue.Null(SqlType.Int32);
        }

        var architecture = RuntimeInformation.OSArchitecture.ToString().ToUpperInvariant();
        return
        [
            SqlValue.FromNVarchar(platform),
            SqlValue.FromNVarchar(distribution),
            SqlValue.FromNVarchar(release),
            SqlValue.FromNVarchar(""),
            sku,
            SqlValue.FromInt32(1033),
            SqlValue.FromNVarchar(architecture),
        ];
    }

    /// <summary>
    /// Parses the <c>KEY=value</c> pairs of <c>/etc/os-release</c>, stripping a
    /// single layer of surrounding double quotes from each value. Any file-
    /// access failure yields an empty map so callers fall back to defaults —
    /// this must never throw, since it runs during static initialization.
    /// </summary>
    private static Dictionary<string, string> ReadOsRelease()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                    continue;
                var key = line[..separator];
                var value = line[(separator + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                result[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>
    /// Raw stock-instance rows for <c>sys.configurations</c> (probe-confirmed
    /// against SQL Server 2025). <c>configuration_id</c> and <c>name</c> are
    /// stable across instances; <c>value</c> mirrors <c>value_in_use</c> on a
    /// fresh server. The four sql_variant columns hold integers for every
    /// option, surfaced as bigint.
    /// </summary>
    private static readonly (int Id, string Name, long Value, long Minimum, long Maximum, long ValueInUse, string Description, bool IsDynamic, bool IsAdvanced)[] ConfigurationData =
    [
        (101, "recovery interval (min)", 0, 0, 32767, 0, "Maximum recovery interval in minutes", true, true),
        (102, "allow updates", 0, 0, 1, 0, "Allow updates to system tables", true, false),
        (103, "user connections", 0, 0, 32767, 0, "Number of user connections allowed", false, true),
        (106, "locks", 0, 5000, 2147483647, 0, "Number of locks for all users", false, true),
        (107, "open objects", 0, 0, 2147483647, 0, "Number of open database objects", false, true),
        (109, "fill factor (%)", 0, 0, 100, 0, "Default fill factor percentage", false, true),
        (114, "disallow results from triggers", 0, 0, 1, 0, "Disallow returning results from triggers", true, true),
        (115, "nested triggers", 1, 0, 1, 1, "Allow triggers to be invoked within triggers", true, false),
        (116, "server trigger recursion", 1, 0, 1, 1, "Allow recursion for server level triggers", true, false),
        (117, "remote access", 1, 0, 1, 1, "Allow remote access", false, false),
        (124, "default language", 0, 0, 9999, 0, "default language", true, false),
        (400, "cross db ownership chaining", 0, 0, 1, 0, "Allow cross db ownership chaining", true, false),
        (503, "max worker threads", 0, 128, 65535, 0, "Maximum worker threads", true, true),
        (505, "network packet size (B)", 4096, 512, 32767, 4096, "Network packet size", true, true),
        (518, "show advanced options", 0, 0, 1, 0, "show advanced options", true, false),
        (542, "remote proc trans", 0, 0, 1, 0, "Create DTC transaction for remote procedures", true, false),
        (544, "c2 audit mode", 0, 0, 1, 0, "c2 audit mode", false, true),
        (1126, "default full-text language", 1033, 0, 2147483647, 1033, "default full-text language", true, true),
        (1127, "two digit year cutoff", 2049, 1753, 9999, 2049, "two digit year cutoff", true, true),
        (1505, "index create memory (KB)", 0, 704, 2147483647, 0, "Memory for index create sorts (kBytes)", true, true),
        (1517, "priority boost", 0, 0, 1, 0, "Priority boost", false, true),
        (1519, "remote login timeout (s)", 10, 0, 2147483647, 10, "remote login timeout", true, false),
        (1520, "remote query timeout (s)", 600, 0, 2147483647, 600, "remote query timeout", true, false),
        (1531, "cursor threshold", -1, -1, 2147483647, -1, "cursor threshold", true, true),
        (1532, "set working set size", 0, 0, 1, 0, "set working set size", false, true),
        (1534, "user options", 0, 0, 32767, 0, "user options", true, false),
        (1535, "affinity mask", 0, -2147483648, 2147483647, 0, "affinity mask", true, true),
        (1536, "max text repl size (B)", 65536, -1, 2147483647, 65536, "Maximum size of a text field in replication.", true, false),
        (1537, "media retention", 0, 0, 365, 0, "Tape retention period in days", true, true),
        (1538, "cost threshold for parallelism", 5, 0, 32767, 5, "cost threshold for parallelism", true, true),
        (1539, "max degree of parallelism", 8, 0, 32767, 8, "maximum degree of parallelism", true, true),
        (1540, "min memory per query (KB)", 1024, 512, 2147483647, 1024, "minimum memory per query (kBytes)", true, true),
        (1541, "query wait (s)", -1, -1, 2147483647, -1, "maximum time to wait for query memory (s)", true, true),
        (1543, "min server memory (MB)", 0, 0, 2147483647, 16, "Minimum size of server memory (MB)", true, true),
        (1544, "max server memory (MB)", 4096, 128, 2147483647, 4096, "Maximum size of server memory (MB)", true, true),
        (1545, "query governor cost limit", 0, 0, 2147483647, 0, "Maximum estimated cost allowed by query governor", true, true),
        (1546, "lightweight pooling", 0, 0, 1, 0, "User mode scheduler uses lightweight pooling", false, true),
        (1547, "scan for startup procs", 0, 0, 1, 0, "scan for startup stored procedures", false, true),
        (1549, "affinity64 mask", 0, -2147483648, 2147483647, 0, "affinity64 mask", true, true),
        (1550, "affinity I/O mask", 0, -2147483648, 2147483647, 0, "affinity I/O mask", false, true),
        (1551, "affinity64 I/O mask", 0, -2147483648, 2147483647, 0, "affinity64 I/O mask", false, true),
        (1555, "transform noise words", 0, 0, 1, 0, "Transform noise words for full-text query", true, true),
        (1556, "precompute rank", 0, 0, 1, 0, "Use precomputed rank for full-text query", true, true),
        (1557, "PH timeout (s)", 60, 1, 3600, 60, "DB connection timeout for full-text protocol handler (s)", true, true),
        (1562, "clr enabled", 0, 0, 1, 0, "CLR user code execution enabled in the server", true, false),
        (1563, "max full-text crawl range", 4, 0, 256, 4, "Maximum  crawl ranges allowed in full-text indexing", true, true),
        (1564, "ft notify bandwidth (min)", 0, 0, 32767, 0, "Number of reserved full-text notifications buffers", true, true),
        (1565, "ft notify bandwidth (max)", 100, 0, 32767, 100, "Max number of full-text notifications buffers", true, true),
        (1566, "ft crawl bandwidth (min)", 0, 0, 32767, 0, "Number of reserved full-text crawl buffers", true, true),
        (1567, "ft crawl bandwidth (max)", 100, 0, 32767, 100, "Max number of full-text crawl buffers", true, true),
        (1568, "default trace enabled", 1, 0, 1, 1, "Enable or disable the default trace", true, true),
        (1569, "blocked process threshold (s)", 0, 0, 86400, 0, "Blocked process reporting threshold", true, true),
        (1570, "in-doubt xact resolution", 0, 0, 2, 0, "Recovery policy for DTC transactions with unknown outcome", true, true),
        (1576, "remote admin connections", 0, 0, 1, 0, "Dedicated Admin Connections are allowed from remote clients", true, false),
        (1577, "common criteria compliance enabled", 0, 0, 1, 0, "Common Criteria compliance mode enabled", false, true),
        (1578, "EKM provider enabled", 0, 0, 1, 0, "Enable or disable EKM provider", true, true),
        (1579, "backup compression default", 0, 0, 1, 0, "Enable compression of backups by default", true, false),
        (1580, "filestream access level", 0, 0, 2, 0, "Sets the FILESTREAM access level", true, false),
        (1581, "optimize for ad hoc workloads", 0, 0, 1, 0, "When this option is set, plan cache size is further reduced for single-use adhoc OLTP workload.", true, true),
        (1582, "access check cache bucket count", 0, 0, 65536, 0, "Default hash bucket count for the access check result security cache", true, true),
        (1583, "access check cache quota", 0, 0, 2147483647, 0, "Default quota for the access check result security cache", true, true),
        (1584, "backup checksum default", 0, 0, 1, 0, "Enable checksum of backups by default", true, false),
        (1585, "automatic soft-NUMA disabled", 0, 0, 1, 0, "Automatic soft-NUMA is enabled by default", false, true),
        (1586, "external scripts enabled", 0, 0, 1, 0, "Allows execution of external scripts", true, false),
        (1587, "clr strict security", 1, 0, 1, 1, "CLR strict security enabled in the server", true, true),
        (1588, "column encryption enclave type", 0, 0, 2, 0, "Type of enclave used for computations on encrypted columns", false, false),
        (1589, "tempdb metadata memory-optimized", 0, 0, 1, 0, "Tempdb metadata memory-optimized is disabled by default.", false, true),
        (1591, "ADR cleaner retry timeout (min)", 15, 0, 32767, 15, "ADR cleaner retry timeout.", true, true),
        (1592, "ADR Preallocation Factor", 4, 0, 32767, 4, "ADR Preallocation Factor.", true, true),
        (1593, "version high part of SQL Server", 1114112, -2147483648, 2147483647, 1114112, "version high part of SQL Server that model database copied for", true, true),
        (1594, "version low part of SQL Server", 73072641, -2147483648, 2147483647, 73072641, "version low part of SQL Server that model database copied for", true, true),
        (1595, "Data processed daily limit in TB", 2147483647, 0, 2147483647, 2147483647, "SQL On-demand data processed daily limit in TB", true, false),
        (1596, "Data processed weekly limit in TB", 2147483647, 0, 2147483647, 2147483647, "SQL On-demand data processed weekly limit in TB", true, false),
        (1597, "Data processed monthly limit in TB", 2147483647, 0, 2147483647, 2147483647, "SQL On-demand data processed monthly limit in TB", true, false),
        (1598, "ADR Cleaner Thread Count", 1, 1, 32767, 1, "Max number of threads ADR cleaner can assign.", true, true),
        (1599, "hardware offload enabled", 0, 0, 1, 0, "Enable hardware offloading on the server", false, true),
        (1600, "hardware offload config", 0, 0, 255, 0, "Configure hardware offload accelerator", false, true),
        (1601, "hardware offload mode", 0, 0, 255, 0, "Configure hardware offload accelerator mode", false, true),
        (1602, "backup compression algorithm", 0, 0, 3, 0, "Configure default backup compression algorithm", true, false),
        (1603, "ADR cleaner lock timeout (s)", 5, 1, 32767, 5, "ADR cleaner lock timeout", true, true),
        (1606, "SLOG memory quota (%)", 75, 1, 100, 75, "SLOG memory quota percentage", true, true),
        (1609, "max RPC request params (KB)", 0, 0, 2147483647, 0, "Maximum memory for RPC request parameters (kBytes)", true, true),
        (1610, "max UCS send boxcars", 256, 256, 2048, 256, "Maximum number of UCS boxcars for sending messages.", false, true),
        (1611, "availability group commit time (ms)", 0, 0, 10, 0, "Configure availability group commit time in milliseconds for SQL Server only.", true, true),
        (1612, "tiered memory enabled", 0, 0, 1, 0, "tiered memory memory-optimized is disabled by default.", false, true),
        (1613, "max server tiered memory (MB)", 2147483647, 0, 2147483647, 2147483647, "Maximum size of server tiered memory (MB)", false, true),
        (16384, "Agent XPs", 0, 0, 1, 0, "Enable or disable Agent XPs", true, true),
        (16386, "Database Mail XPs", 0, 0, 1, 0, "Enable or disable Database Mail XPs", true, true),
        (16387, "SMO and DMO XPs", 1, 0, 1, 1, "Enable or disable SMO and DMO XPs", true, true),
        (16388, "Ole Automation Procedures", 0, 0, 1, 0, "Enable or disable Ole Automation Procedures", true, true),
        (16390, "xp_cmdshell", 0, 0, 1, 0, "Enable or disable command shell", true, true),
        (16391, "Ad Hoc Distributed Queries", 0, 0, 1, 0, "Enable or disable Ad Hoc Distributed Queries", true, true),
        (16392, "Replication XPs", 0, 0, 1, 0, "Enable or disable Replication XPs", true, true),
        (16393, "contained database authentication", 0, 0, 1, 0, "Enables contained databases and contained authentication", true, false),
        (16394, "hadoop connectivity", 0, 0, 8, 0, "Configure SQL Server to connect to external Hadoop or Microsoft Azure storage blob data sources through PolyBase", true, false),
        (16395, "polybase network encryption", 1, 0, 1, 1, "Configure SQL Server to encrypt control and data channels when using PolyBase", true, false),
        (16396, "remote data archive", 0, 0, 1, 0, "Allow the use of the REMOTE_DATA_ARCHIVE data access for databases", true, false),
        (16397, "allow polybase export", 0, 0, 1, 0, "Allows writing into an external table using PolyBase", true, false),
        (16398, "allow filesystem enumeration", 1, 0, 1, 1, "Allow enumeration of filesystem", true, true),
        (16399, "polybase enabled", 0, 0, 1, 0, "Configure SQL Server to connect to external data sources through PolyBase", true, false),
        (16400, "suppress recovery model errors", 0, 0, 1, 0, "Return warning instead of error for unsupported ALTER DATABASE SET RECOVERY command", true, true),
        (16401, "openrowset auto_create_statistics", 1, 0, 1, 1, "Enable or disable auto create statistics for openrowset sources.", true, true),
        (16402, "external rest endpoint enabled", 0, 0, 1, 0, "Enable or disable invocations of external REST endpoints", true, false),
        (16403, "external xtp dll gen util enabled", 0, 0, 1, 0, "Enable or disable using external xtp dll generation via HkDllGen.exe", true, false),
        (16404, "external AI runtimes enabled", 0, 0, 1, 0, "Enable or disable using external AI runtimes", true, false),
        (16405, "allow server scoped db credentials", 0, 0, 1, 0, "Enable or disable use of server managed identity in database scoped credentials", true, false),
    ];

    /// <summary>
    /// The 106 rows projected by <c>sys.configurations</c>, materialized once
    /// from <see cref="ConfigurationData"/> since server-configuration
    /// metadata is fixed static catalog data (matching how the other
    /// constant-row catalog views reuse a shared array). Independent of the
    /// database argument — <c>sys.configurations</c> is server-scoped.
    /// </summary>
    private static readonly SqlValue[][] ConfigurationsRows = BuildConfigurationsRows();

    private static SqlValue[][] BuildConfigurationsRows()
    {
        var rows = new SqlValue[ConfigurationData.Length][];
        for (var i = 0; i < ConfigurationData.Length; i++)
        {
            var (id, name, value, minimum, maximum, valueInUse, description, isDynamic, isAdvanced) = ConfigurationData[i];
            rows[i] =
            [
                SqlValue.FromInt32(id),
                SqlValue.FromNVarchar(name),
                SqlValue.FromInt64(value),
                SqlValue.FromInt64(minimum),
                SqlValue.FromInt64(maximum),
                SqlValue.FromInt64(valueInUse),
                SqlValue.FromNVarchar(description),
                SqlValue.FromBoolean(isDynamic),
                SqlValue.FromBoolean(isAdvanced),
            ];
        }

        return rows;
    }

    /// <summary>
    /// Rows for <c>sys.types</c>: every <see cref="SystypesRowData"/> entry
    /// (system types) followed by user-defined table types from each schema's
    /// <see cref="Schema.TableTypes"/> dict. Probe-confirmed (G1) shape:
    /// table-type rows surface <c>system_type_id = 243</c>,
    /// <c>is_user_defined = 1</c>, <c>is_table_type = 1</c>,
    /// <c>is_nullable = 0</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysTypes(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var sysSchemaId = SqlValue.FromInt32(Database.SysSchemaId);
        // System types: project from SystypesRowData using its name (col 0),
        // xtype (col 1, used as system_type_id), xusertype (col 3, used as
        // user_type_id). is_user_defined is derived from a hardcoded set:
        // sysname is user-defined; everything else system.
        foreach (var row in SystypesRowData)
        {
            var name = (string)row[0]!;
            var systemTypeId = Convert.ToByte(row[1]!, CultureInfo.InvariantCulture);
            var userTypeId = Convert.ToInt32(row[3]!, CultureInfo.InvariantCulture);
            yield return [
                SqlValue.FromSystemName(name),
                SqlValue.FromByte(systemTypeId),
                SqlValue.FromInt32(userTypeId),
                sysSchemaId,
                name == "sysname" ? trueBit : falseBit,
                falseBit,
                trueBit,
            ];
        }
        // User-defined table types: probe-confirmed system_type_id 243.
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.UserTypeId))
            {
                yield return [
                    SqlValue.FromSystemName(tt.Name),
                    SqlValue.FromByte(243),
                    SqlValue.FromInt32(tt.UserTypeId),
                    schemaId,
                    trueBit,
                    trueBit,
                    falseBit,
                ];
            }
        }
        // Scalar alias types (UDDTs): probe-confirmed against SQL Server 2025
        // — `system_type_id` is the **underlying** built-in's id (e.g. 56 for
        // an alias of int, 231 for an alias of nvarchar), `is_user_defined`
        // is true, `is_table_type` is false, and `is_nullable` reflects the
        // alias-defined NULL/NOT NULL marker from CREATE TYPE.
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var alias in schema.AliasTypes.Values.OrderBy(a => a.UserTypeId))
            {
                yield return [
                    SqlValue.FromSystemName(alias.Name),
                    SqlValue.FromByte(alias.UnderlyingType.SystemTypeId),
                    SqlValue.FromInt32(alias.UserTypeId),
                    schemaId,
                    trueBit,
                    falseBit,
                    alias.IsNullable ? trueBit : falseBit,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.extended_properties</c>. Walks every entry in
    /// <see cref="Database.ExtendedProperties"/> (per-database flat dict)
    /// and projects the 6-column shape. The <c>class_desc</c> string is
    /// derived from the class number per real SQL Server's enum (0 =
    /// DATABASE, 1 = OBJECT_OR_COLUMN, 3 = SCHEMA — the only classes the
    /// simulator currently emits; others fall through as the string form
    /// of the class number for forward compat). Value is coerced to
    /// <c>nvarchar(MAX)</c> since the simulator doesn't model
    /// <c>sql_variant</c>; for AW's all-nvarchar workload, this is a
    /// lossless surfacing.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysExtendedProperties(Parser.BatchContext batch, Database database)
    {
        foreach (var kvp in database.ExtendedProperties)
        {
            var key = kvp.Key;
            var classDesc = key.Class switch
            {
                0 => "DATABASE",
                1 => "OBJECT_OR_COLUMN",
                3 => "SCHEMA",
                7 => "INDEX",
                _ => key.Class.ToString(CultureInfo.InvariantCulture),
            };
            yield return [
                SqlValue.FromByte(key.Class),
                SqlValue.FromSystemName(classDesc),
                SqlValue.FromInt32(key.MajorId),
                SqlValue.FromInt32(key.MinorId),
                SqlValue.FromSystemName(key.Name),
                kvp.Value.IsNull ? SqlValue.Null(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)) : kvp.Value.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysTableTypes(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.UserTypeId))
            {
                yield return [
                    SqlValue.FromSystemName(tt.Name),
                    SqlValue.FromInt32(tt.ObjectId),
                    trueBit,
                    schemaId,
                    SqlValue.FromInt32(tt.UserTypeId),
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.foreign_keys</c>: every FOREIGN KEY constraint across
    /// every schema. <c>type</c> = <c>F </c> (probe-confirmed two-char
    /// padding); <c>type_desc</c> = <c>FOREIGN_KEY_CONSTRAINT</c>.
    /// <c>delete_referential_action</c> / <c>update_referential_action</c>
    /// use the integer codes 0=NO_ACTION, 1=CASCADE, 2=SET_NULL, 3=SET_DEFAULT.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysForeignKeys(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var fkType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "F ");
        var fkTypeDesc = SqlValue.FromNVarchar("FOREIGN_KEY_CONSTRAINT");
        // key_index_id is the index id on the referenced table that satisfies
        // the FK — the simulator doesn't model indexes so report 1 (the
        // referenced PK / UQ ordinal in real SQL Server typically lands at 1
        // because PK gets a clustered index id of 1).
        var keyIndexId = SqlValue.FromInt32(1);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    yield return [
                        SqlValue.FromSystemName(fk.Name),
                        SqlValue.FromInt32(fk.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        fkType,
                        fkTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(fk.ReferencedTable.ObjectId),
                        keyIndexId,
                        fk.IsDisabled ? trueBit : falseBit,
                        falseBit,
                        fk.IsNotTrusted ? trueBit : falseBit,
                        SqlValue.FromByte((byte)fk.DeleteAction),
                        SqlValue.FromNVarchar(ReferentialActionDescription(fk.DeleteAction)),
                        SqlValue.FromByte((byte)fk.UpdateAction),
                        SqlValue.FromNVarchar(ReferentialActionDescription(fk.UpdateAction)),
                        fk.IsSystemNamed ? trueBit : falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.foreign_key_columns</c>: one per (FK, column-pair).
    /// <c>parent_column_id</c> and <c>referenced_column_id</c> are 1-based
    /// (probe-confirmed) — matches the <c>sys.columns.column_id</c> convention.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysForeignKeyColumns(Parser.BatchContext batch, Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var fk in table.OutgoingForeignKeys.OrderBy(f => f.ObjectId))
                {
                    for (var i = 0; i < fk.ChildColumnOrdinals.Length; i++)
                    {
                        yield return [
                            SqlValue.FromInt32(fk.ObjectId),
                            SqlValue.FromInt32(i + 1),
                            SqlValue.FromInt32(fk.ChildTable.ObjectId),
                            SqlValue.FromInt32(fk.ChildColumnOrdinals[i] + 1),
                            SqlValue.FromInt32(fk.ReferencedTable.ObjectId),
                            SqlValue.FromInt32(fk.ReferencedColumnOrdinals[i] + 1),
                        ];
                    }
                }
            }
        }
    }

    private static string ReferentialActionDescription(ReferentialAction action) => action switch
    {
        ReferentialAction.NoAction => "NO_ACTION",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET_NULL",
        ReferentialAction.SetDefault => "SET_DEFAULT",
        _ => "NO_ACTION",
    };

    /// <summary>
    /// Rows for <c>sys.check_constraints</c>: one row per CHECK constraint
    /// across every table in every schema. <c>parent_column_id</c> is the
    /// 1-based column id when the CHECK is column-attached (inline); 0 for
    /// table-level. <c>definition</c> is currently null — the simulator
    /// stores the parsed predicate tree, not source text (documented quirk).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysCheckConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var ckType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "C ");
        var ckTypeDesc = SqlValue.FromNVarchar("CHECK_CONSTRAINT");
        var falseDbCollation = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var ck in table.CheckConstraints.OrderBy(c => c.ObjectId))
                {
                    var parentColumnId = 0;
                    if (ck.InlineColumn is { } inlineCol)
                    {
                        for (var i = 0; i < table.Columns.Length; i++)
                        {
                            if (database.Collation.Equals(table.Columns[i].Name, inlineCol))
                            {
                                parentColumnId = i + 1;
                                break;
                            }
                        }
                    }
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    yield return [
                        SqlValue.FromSystemName(ck.Name),
                        SqlValue.FromInt32(ck.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        ckType,
                        ckTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        ck.IsDisabled ? trueBit : falseBit,
                        falseBit,
                        ck.IsNotTrusted ? trueBit : falseBit,
                        SqlValue.FromInt32(parentColumnId),
                        ck.Definition is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(ck.Definition),
                        falseDbCollation,
                        ck.IsSystemNamed ? trueBit : falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.key_constraints</c>: PK + UNIQUE constraints across
    /// every table. <c>type</c> = <c>PK</c> / <c>UQ</c>;
    /// <c>type_desc</c> = <c>PRIMARY_KEY_CONSTRAINT</c> / <c>UNIQUE_CONSTRAINT</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysKeyConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var charTwo = CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit);
        var pkType = SqlValue.FromChar(charTwo, "PK");
        var uqType = SqlValue.FromChar(charTwo, "UQ");
        var pkTypeDesc = SqlValue.FromNVarchar("PRIMARY_KEY_CONSTRAINT");
        var uqTypeDesc = SqlValue.FromNVarchar("UNIQUE_CONSTRAINT");
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                foreach (var key in table.KeyConstraints.OrderBy(k => k.ObjectId))
                {
                    var isPk = key.Kind == KeyConstraintKind.PrimaryKey;
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    // PK gets a system-named flag iff the name starts with
                    // "PK__"; UQ similarly. The simulator tracks is_system_named
                    // on FK / CHECK explicitly; for KeyConstraint we infer from
                    // the auto-name prefix since the existing storage doesn't
                    // carry the flag.
                    var systemNamed = key.Name.StartsWith(isPk ? "PK__" : "UQ__", StringComparison.Ordinal);
                    yield return [
                        SqlValue.FromSystemName(key.Name),
                        SqlValue.FromInt32(key.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        isPk ? pkType : uqType,
                        isPk ? pkTypeDesc : uqTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(1),
                        systemNamed ? trueBit : falseBit,
                        trueBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.default_constraints</c>: one row per named DEFAULT
    /// binding. Inline DEFAULT at CREATE TABLE and ALTER TABLE ADD DEFAULT
    /// both populate; inline-without-CONSTRAINT-name auto-generates with
    /// <see cref="DefaultConstraint.IsSystemNamed"/> = true.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDefaultConstraints(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPrincipal = SqlValue.Null(SqlType.Int32);
        var dfType = SqlValue.FromChar(CharSqlType.Get(2, Collation.Catalog, Coercibility.Implicit), "D ");
        var dfTypeDesc = SqlValue.FromNVarchar("DEFAULT_CONSTRAINT");
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var table in schema.HeapTables.Values)
            {
                for (var i = 0; i < table.Columns.Length; i++)
                {
                    var col = table.Columns[i];
                    if (col.DefaultConstraint is not { } df)
                        continue;
                    var createDate = SqlValue.FromDateTime(table.CreateDate);
                    yield return [
                        SqlValue.FromSystemName(df.Name),
                        SqlValue.FromInt32(df.ObjectId),
                        nullPrincipal,
                        schemaId,
                        SqlValue.FromInt32(table.ObjectId),
                        dfType,
                        dfTypeDesc,
                        createDate,
                        createDate,
                        falseBit,
                        falseBit,
                        falseBit,
                        SqlValue.FromInt32(i + 1),
                        df.Definition is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(df.Definition),
                        df.IsSystemNamed ? trueBit : falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.indexes</c>: one row per (table, index) — PK / UQ
    /// from <see cref="HeapTable.KeyConstraints"/>, plus
    /// <c>CREATE INDEX</c>-declared entries from <see cref="HeapTable.Indexes"/>.
    /// PK gets index_id = 1 with type_desc = CLUSTERED; tables without a
    /// PK emit a HEAP row (index_id = 0, name = NULL). Remaining UQ /
    /// user indexes get index_id starting at 2 in <c>ObjectId</c> order
    /// (the simulator allocates object ids monotonically, so this matches
    /// declaration order).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysIndexes(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        var zeroInt = SqlValue.FromInt32(0);
        var nullName = SqlValue.Null(SqlType.SystemName);
        var nullFilter = SqlValue.Null(SqlType.NVarchar);
        var heapDesc = SqlValue.FromNVarchar("HEAP");
        var clusteredDesc = SqlValue.FromNVarchar("CLUSTERED");
        var nonClusteredDesc = SqlValue.FromNVarchar("NONCLUSTERED");
        var primaryDataSpace = SqlValue.FromInt32(1);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                var tableObjectId = SqlValue.FromInt32(table.ObjectId);
                KeyConstraint? primaryKey = null;
                foreach (var k in table.KeyConstraints)
                {
                    if (k.Kind == KeyConstraintKind.PrimaryKey)
                    {
                        primaryKey = k;
                        break;
                    }
                }
                var hasPk = primaryKey is not null;
                var nextIndexId = hasPk ? 2 : 1;
                yield return BuildIndexRow(
                    name: hasPk ? SqlValue.FromSystemName(primaryKey!.Name) : nullName,
                    objectId: tableObjectId,
                    indexId: SqlValue.FromInt32(hasPk ? 1 : 0),
                    type: hasPk ? SqlValue.FromByte(1) : zeroByte,
                    typeDesc: hasPk ? clusteredDesc : heapDesc,
                    isUnique: hasPk ? trueBit : falseBit,
                    dataSpaceId: primaryDataSpace,
                    isPrimaryKey: hasPk ? trueBit : falseBit,
                    isUniqueConstraint: falseBit,
                    hasFilter: falseBit,
                    filterDefinition: nullFilter,
                    falseBit, trueBit, zeroByte);

                var others = new List<(int ObjectId, KeyConstraint? Key, Storage.Index? Index)>();
                foreach (var k in table.KeyConstraints)
                {
                    if (!ReferenceEquals(k, primaryKey))
                        others.Add((k.ObjectId, k, null));
                }
                foreach (var ix in table.Indexes)
                    others.Add((ix.ObjectId, null, ix));
                others.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

                foreach (var (_, key, index) in others)
                {
                    if (key is not null)
                    {
                        yield return BuildIndexRow(
                            name: SqlValue.FromSystemName(key.Name),
                            objectId: tableObjectId,
                            indexId: SqlValue.FromInt32(nextIndexId++),
                            type: SqlValue.FromByte(2),
                            typeDesc: nonClusteredDesc,
                            isUnique: trueBit,
                            dataSpaceId: primaryDataSpace,
                            isPrimaryKey: falseBit,
                            isUniqueConstraint: trueBit,
                            hasFilter: falseBit,
                            filterDefinition: nullFilter,
                            falseBit, trueBit, zeroByte);
                    }
                    else
                    {
                        var hasFilter = index!.Filter is not null;
                        yield return BuildIndexRow(
                            name: SqlValue.FromSystemName(index.Name),
                            objectId: tableObjectId,
                            indexId: SqlValue.FromInt32(nextIndexId++),
                            type: SqlValue.FromByte(2),
                            typeDesc: nonClusteredDesc,
                            isUnique: index.IsUnique ? trueBit : falseBit,
                            dataSpaceId: primaryDataSpace,
                            isPrimaryKey: falseBit,
                            isUniqueConstraint: falseBit,
                            hasFilter: hasFilter ? trueBit : falseBit,
                            filterDefinition: index.FilterDefinition is { } def ? SqlValue.FromNVarchar(def) : nullFilter,
                            falseBit, trueBit, zeroByte);
                    }
                }
            }
        }

        SqlValue[] BuildIndexRow(
            SqlValue name, SqlValue objectId, SqlValue indexId, SqlValue type, SqlValue typeDesc,
            SqlValue isUnique, SqlValue dataSpaceId, SqlValue isPrimaryKey, SqlValue isUniqueConstraint,
            SqlValue hasFilter, SqlValue filterDefinition, SqlValue isPadded, SqlValue allowLocks, SqlValue fillFactor) =>
            [
                name,
                objectId,
                indexId,
                type,
                typeDesc,
                isUnique,
                dataSpaceId,
                falseBit, // ignore_dup_key
                isPrimaryKey,
                isUniqueConstraint,
                fillFactor,
                isPadded,
                falseBit, // is_disabled
                falseBit, // is_hypothetical
                falseBit, // is_ignored_in_optimization
                allowLocks, // allow_row_locks
                allowLocks, // allow_page_locks
                hasFilter,
                filterDefinition,
                zeroInt, // compression_delay
                falseBit, // suppress_dup_key_messages
                falseBit, // auto_created
                falseBit, // optimize_for_sequential_key
                falseBit, // statistics_incremental
            ];
    }

    /// <summary>
    /// Rows for <c>sys.index_columns</c>: one row per (index, column) for
    /// every index reported by <see cref="EnumerateSysIndexes"/>. KEY
    /// columns get key_ordinal = 1..N and index_column_id = 1..N; INCLUDE
    /// columns get key_ordinal = 0 and index_column_id continuing past
    /// the key column count. HEAP rows (index_id = 0) don't appear here —
    /// real SQL Server's catalog omits them and the simulator matches.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysIndexColumns(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var zeroByte = SqlValue.FromByte(0);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                var tableObjectId = SqlValue.FromInt32(table.ObjectId);
                KeyConstraint? primaryKey = null;
                foreach (var k in table.KeyConstraints)
                {
                    if (k.Kind == KeyConstraintKind.PrimaryKey)
                    {
                        primaryKey = k;
                        break;
                    }
                }
                var nextIndexId = primaryKey is null ? 1 : 1;
                if (primaryKey is not null)
                {
                    foreach (var row in EmitKeyConstraintColumns(tableObjectId, SqlValue.FromInt32(nextIndexId), primaryKey, table, falseBit, zeroByte, nullByte))
                        yield return row;
                    nextIndexId++;
                }

                var others = new List<(int ObjectId, KeyConstraint? Key, Storage.Index? Index)>();
                foreach (var k in table.KeyConstraints)
                {
                    if (!ReferenceEquals(k, primaryKey))
                        others.Add((k.ObjectId, k, null));
                }
                foreach (var ix in table.Indexes)
                    others.Add((ix.ObjectId, null, ix));
                others.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

                foreach (var (_, key, index) in others)
                {
                    var indexIdValue = SqlValue.FromInt32(nextIndexId++);
                    if (key is not null)
                    {
                        foreach (var row in EmitKeyConstraintColumns(tableObjectId, indexIdValue, key, table, falseBit, zeroByte, nullByte))
                            yield return row;
                    }
                    else
                    {
                        for (var i = 0; i < index!.KeyColumns.Length; i++)
                        {
                            var keyCol = index.KeyColumns[i];
                            yield return [
                                tableObjectId,
                                indexIdValue,
                                SqlValue.FromInt32(i + 1),
                                SqlValue.FromInt32(StorageOrdinalToColumnId(table, keyCol.StorageOrdinal)),
                                SqlValue.FromByte((byte)(i + 1)),
                                zeroByte,
                                keyCol.IsDescending ? trueBit : falseBit,
                                falseBit,
                                nullByte,
                                nullByte,
                            ];
                        }
                        for (var i = 0; i < index.IncludedColumns.Length; i++)
                        {
                            yield return [
                                tableObjectId,
                                indexIdValue,
                                SqlValue.FromInt32(index.KeyColumns.Length + i + 1),
                                SqlValue.FromInt32(StorageOrdinalToColumnId(table, index.IncludedColumns[i])),
                                zeroByte,
                                zeroByte,
                                falseBit,
                                trueBit,
                                nullByte,
                                nullByte,
                            ];
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Materializes one row per key column of a PRIMARY KEY / UNIQUE
    /// <see cref="KeyConstraint"/> for <c>sys.index_columns</c>. Shared
    /// between the PK-at-index_id-1 emission and the non-PK UQ entries
    /// in the ordered emission pass.
    /// </summary>
    private static IEnumerable<SqlValue[]> EmitKeyConstraintColumns(
        SqlValue tableObjectId, SqlValue indexIdValue, KeyConstraint constraint, HeapTable table,
        SqlValue falseBit, SqlValue zeroByte, SqlValue nullByte)
    {
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            yield return [
                tableObjectId,
                indexIdValue,
                SqlValue.FromInt32(i + 1),
                SqlValue.FromInt32(StorageOrdinalToColumnId(table, constraint.StorageOrdinals[i])),
                SqlValue.FromByte((byte)(i + 1)),
                zeroByte,
                falseBit,
                falseBit,
                nullByte,
                nullByte,
            ];
        }
    }

    /// <summary>
    /// Converts a storage ordinal back to the 1-based <c>column_id</c>
    /// reported by <c>sys.columns</c>. The simulator's column_id is the
    /// 1-based full-column ordinal (matching real SQL Server), so we walk
    /// <see cref="HeapTable.StorageOrdinals"/> looking for the full
    /// ordinal that maps to the given storage ordinal.
    /// </summary>
    private static int StorageOrdinalToColumnId(HeapTable table, int storageOrdinal)
    {
        for (var i = 0; i < table.StorageOrdinals.Length; i++)
        {
            if (table.StorageOrdinals[i] == storageOrdinal)
                return i + 1;
        }
        return storageOrdinal + 1;
    }

    /// <summary>
    /// Rows for <c>sys.triggers</c>: one row per <see cref="Trigger"/> in
    /// every schema. <c>parent_class</c> is always 1 (DML triggers attached
    /// to tables — DDL/server triggers aren't modeled);
    /// <c>is_not_for_replication</c> is always 0 (the simulator
    /// parse-and-ignores the WITH clause). Probe-confirmed columns; modify
    /// date mirrors create date because <c>ALTER TRIGGER</c> replaces the
    /// instance wholesale.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysTriggers(
        Parser.BatchContext batch,
        Database database,
        SqlType charTwo,
        SqlValue parentClassObjectColumn,
        SqlValue parentClassObjectColumnDesc)
    {
        _ = batch;
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        // 'TR' / 'SQL_TRIGGER' — matches Trigger.ObjectTypeCode /
        // Trigger.ObjectTypeDescription, kept as local constants here to
        // avoid one SqlValue allocation per row.
        var triggerType = SqlValue.FromChar(charTwo, "TR");
        var triggerTypeDesc = SqlValue.FromNVarchar("SQL_TRIGGER");
        var parentClassDatabase = SqlValue.FromByte(0);
        var parentClassDatabaseDesc = SqlValue.FromNVarchar("DATABASE");
        var parentIdZero = SqlValue.FromInt32(0);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values.OrderBy(t => t.ObjectId))
            {
                var createDate = SqlValue.FromDateTime(trigger.CreateDate);
                yield return [
                    SqlValue.FromSystemName(trigger.Name),
                    SqlValue.FromInt32(trigger.ObjectId),
                    parentClassObjectColumn,
                    parentClassObjectColumnDesc,
                    SqlValue.FromInt32(trigger.Parent.ObjectId),
                    triggerType,
                    triggerTypeDesc,
                    createDate,
                    createDate,
                    trigger.IsDisabled ? trueBit : falseBit,
                    trigger.Timing == TriggerTiming.InsteadOf ? trueBit : falseBit,
                    falseBit,
                ];
            }
        }
        // DDL triggers: stored on Database, not per-schema. parent_class=0
        // (DATABASE), parent_class_desc='DATABASE', parent_id=0 — probe-
        // confirmed against SQL Server 2025's sys.triggers for AW's
        // [ddlDatabaseTriggerLog].
        foreach (var ddl in database.DdlTriggers.Values.OrderBy(t => t.ObjectId))
        {
            var createDate = SqlValue.FromDateTime(ddl.CreateDate);
            yield return [
                SqlValue.FromSystemName(ddl.Name),
                SqlValue.FromInt32(ddl.ObjectId),
                parentClassDatabase,
                parentClassDatabaseDesc,
                parentIdZero,
                triggerType,
                triggerTypeDesc,
                createDate,
                createDate,
                ddl.IsDisabled ? trueBit : falseBit,
                falseBit,
                falseBit,
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysDatabasePrincipals(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullSchemaName = SqlValue.Null(SqlType.SystemName);
        var nullOwningId = SqlValue.Null(SqlType.Int32);
        var nullSid = SqlValue.Null(SqlType.Varbinary);
        var nullAuthType = SqlValue.Null(SqlType.TinyInt);
        var nullAuthDesc = SqlValue.Null(SqlType.NVarchar);
        // 4-letter padding to fit char(2) — the type column is 2 bytes in
        // real SQL Server's catalog. SqlValue.FromChar pads to declared length.
        var charTwo = SqlType.GetChar(2);
        foreach (var p in database.Principals.Values.OrderBy(p => p.PrincipalId))
        {
            var createDate = SqlValue.FromDateTime(p.CreateDate);
            yield return [
                SqlValue.FromSystemName(p.Name),
                SqlValue.FromInt32(p.PrincipalId),
                SqlValue.FromChar(charTwo, p.TypeCode),
                SqlValue.FromNVarchar(p.TypeDescription),
                nullSchemaName,
                createDate,
                createDate,
                nullOwningId,
                nullSid,
                p.IsFixedRole ? trueBit : falseBit,
                nullAuthType,
                nullAuthDesc,
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysDatabasePermissions(Parser.BatchContext batch, Database database)
    {
        var charTwo = SqlType.GetChar(2);
        var charOne = SqlType.GetChar(1);
        foreach (var perm in database.Permissions)
        {
            var classDesc = perm.Class switch
            {
                0 => "DATABASE",
                1 => "OBJECT_OR_COLUMN",
                3 => "SCHEMA",
                4 => "DATABASE_PRINCIPAL",
                _ => "DATABASE",
            };
            var stateDesc = perm.State switch
            {
                "D" => "DENY",
                "G" => "GRANT",
                "R" => "REVOKE",
                "W" => "GRANT_WITH_GRANT_OPTION",
                _ => "GRANT",
            };
            yield return [
                SqlValue.FromByte(perm.Class),
                SqlValue.FromNVarchar(classDesc),
                SqlValue.FromInt32(perm.MajorId),
                SqlValue.FromInt32(perm.MinorId),
                SqlValue.FromInt32(perm.GranteePrincipalId),
                SqlValue.FromInt32(perm.GrantorPrincipalId),
                SqlValue.FromChar(charTwo, perm.TypeCode),
                SqlValue.FromNVarchar(perm.PermissionName),
                SqlValue.FromChar(charOne, perm.State),
                SqlValue.FromNVarchar(stateDesc),
            ];
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseRoleMembers(Parser.BatchContext batch, Database database)
    {
        foreach (var (roleId, memberId) in database.RoleMembers)
        {
            yield return [
                SqlValue.FromInt32(roleId),
                SqlValue.FromInt32(memberId),
            ];
        }
    }

    /// <summary>
    /// Derives a deterministic 16-byte synthetic <c>sid</c> from a login name.
    /// Real SQL logins carry a 16-byte random GUID sid; the simulator fills the
    /// four 32-bit quadrants with a per-quadrant-salted FNV-1a hash so the same
    /// name always maps to the same bytes without persisting a GUID.
    /// </summary>
    private static byte[] DeriveLoginSid(string name)
    {
        var sid = new byte[16];
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var hash = Simulation.Fnv1a32.Initial;
            hash.Mix(name);
            hash.Mix((byte)quadrant);
            var value = hash.Value;
            var offset = quadrant * 4;
            sid[offset] = (byte)value;
            sid[offset + 1] = (byte)(value >> 8);
            sid[offset + 2] = (byte)(value >> 16);
            sid[offset + 3] = (byte)(value >> 24);
        }
        return sid;
    }

    /// <summary>
    /// Projects <c>sys.server_principals</c> over the per-Simulation login
    /// registry plus the two synthetic fixed rows (<c>sa</c> = principal_id 1,
    /// <c>public</c> = principal_id 2). Rows emit in principal_id order.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysServerPrincipals(Parser.BatchContext batch, Database database)
    {
        var simulation = batch.Connection.Simulation;
        var charOne = SqlType.GetChar(1);
        var falseBit = SqlValue.FromBoolean(false);
        var sqlLogin = SqlValue.FromNVarchar("SQL_LOGIN");
        var loginType = SqlValue.FromChar(charOne, "S");
        var nullCredentialId = SqlValue.Null(SqlType.Int32);
        var nullOwningId = SqlValue.Null(SqlType.Int32);
        var master = SqlValue.FromSystemName("master");
        var usEnglish = SqlValue.FromSystemName("us_english");
        var nullTenant = SqlValue.Null(SqlType.UniqueIdentifier);
        var zeroTenant = SqlValue.FromGuid(Guid.Empty);
        var seedDate = SqlValue.FromDateTime(simulation.SeedDate);

        // sa: the fixed SQL-authentication login, principal_id 1.
        yield return [
            SqlValue.FromSystemName("sa"),
            SqlValue.FromInt32(1),
            SqlValue.FromVarbinary([0x01]),
            loginType,
            sqlLogin,
            falseBit,
            seedDate,
            seedDate,
            master,
            usEnglish,
            nullCredentialId,
            nullOwningId,
            falseBit,
            nullTenant,
        ];

        // public: the fixed server role, principal_id 2. owning_principal_id
        // points at sa (1); is_fixed_role is 0 (probe-confirmed).
        yield return [
            SqlValue.FromSystemName("public"),
            SqlValue.FromInt32(2),
            SqlValue.FromVarbinary([0x02]),
            SqlValue.FromChar(charOne, "R"),
            SqlValue.FromNVarchar("SERVER_ROLE"),
            falseBit,
            seedDate,
            seedDate,
            SqlValue.Null(SqlType.SystemName),
            SqlValue.Null(SqlType.SystemName),
            nullCredentialId,
            SqlValue.FromInt32(1),
            falseBit,
            nullTenant,
        ];

        foreach (var login in simulation.Logins.Values.OrderBy(l => l.PrincipalId))
        {
            yield return [
                SqlValue.FromSystemName(login.Name),
                SqlValue.FromInt32(login.PrincipalId),
                SqlValue.FromVarbinary(DeriveLoginSid(login.Name)),
                loginType,
                sqlLogin,
                falseBit,
                SqlValue.FromDateTime(login.CreateDate),
                SqlValue.FromDateTime(login.PasswordLastSetTime),
                master,
                usEnglish,
                nullCredentialId,
                nullOwningId,
                falseBit,
                zeroTenant,
            ];
        }
    }

    /// <summary>
    /// Projects <c>sys.sql_logins</c>: the type='S' subset of
    /// <c>sys.server_principals</c> (<c>sa</c> plus the registry logins, never
    /// the <c>public</c> server role), with the policy / expiration / hash
    /// tail. Rows emit in principal_id order.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSqlLogins(Parser.BatchContext batch, Database database)
    {
        var simulation = batch.Connection.Simulation;
        var charOne = SqlType.GetChar(1);
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var sqlLogin = SqlValue.FromNVarchar("SQL_LOGIN");
        var loginType = SqlValue.FromChar(charOne, "S");
        var nullCredentialId = SqlValue.Null(SqlType.Int32);
        var master = SqlValue.FromSystemName("master");
        var usEnglish = SqlValue.FromSystemName("us_english");
        var nullPasswordHash = SqlValue.Null(SqlType.Varbinary);
        var seedDate = SqlValue.FromDateTime(simulation.SeedDate);

        yield return [
            SqlValue.FromSystemName("sa"),
            SqlValue.FromInt32(1),
            SqlValue.FromVarbinary([0x01]),
            loginType,
            sqlLogin,
            falseBit,
            seedDate,
            seedDate,
            master,
            usEnglish,
            nullCredentialId,
            trueBit,
            falseBit,
            nullPasswordHash,
        ];

        foreach (var login in simulation.Logins.Values.OrderBy(l => l.PrincipalId))
        {
            yield return [
                SqlValue.FromSystemName(login.Name),
                SqlValue.FromInt32(login.PrincipalId),
                SqlValue.FromVarbinary(DeriveLoginSid(login.Name)),
                loginType,
                sqlLogin,
                falseBit,
                SqlValue.FromDateTime(login.CreateDate),
                SqlValue.FromDateTime(login.PasswordLastSetTime),
                master,
                usEnglish,
                nullCredentialId,
                trueBit,
                falseBit,
                nullPasswordHash,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.fulltext_catalogs</c>. One row per
    /// <see cref="FullTextCatalog"/> in <see cref="Database.FullTextCatalogs"/>.
    /// Filesystem-placement columns (<c>path</c>, <c>data_space_id</c>,
    /// <c>file_id</c>) surface as NULL — the simulator has no on-disk catalog
    /// storage. <c>is_importing</c> is always false (no concurrent bacpac
    /// import to observe).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysFullTextCatalogs(Parser.BatchContext batch, Database database)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var nullPath = SqlValue.Null(SqlType.NVarchar);
        var nullDataSpaceId = SqlValue.Null(SqlType.Int32);
        var nullFileId = SqlValue.Null(SqlType.Int32);
        foreach (var cat in database.FullTextCatalogs.Values.OrderBy(c => c.Id))
        {
            yield return [
                SqlValue.FromInt32(cat.Id),
                SqlValue.FromSystemName(cat.Name),
                nullPath,
                cat.IsDefault ? trueBit : falseBit,
                cat.IsAccentSensitive ? trueBit : falseBit,
                nullDataSpaceId,
                nullFileId,
                SqlValue.FromInt32(cat.PrincipalId),
                falseBit,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.fulltext_indexes</c>. One row per table that has a
    /// <see cref="HeapTable.FullTextIndex"/> populated. <c>is_enabled</c> /
    /// <c>has_crawl_completed</c> default to true (no crawl is performed
    /// but the FT index is "ready" from the catalog's POV);
    /// <c>change_tracking_state</c> = 'A' (AUTO) / 'AUTO';
    /// <c>crawl_type</c> = 'F' (FULL) / 'FULL'.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysFullTextIndexes(Parser.BatchContext batch, Database database)
    {
        var charOneType = CharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit);
        var trueBit = SqlValue.FromBoolean(true);
        var autoCode = SqlValue.FromChar(charOneType, "A");
        var autoDesc = SqlValue.FromNVarchar("AUTO");
        var fullCode = SqlValue.FromChar(charOneType, "F");
        var fullDesc = SqlValue.FromNVarchar("FULL");
        var nullDate = SqlValue.Null(SqlType.DateTime);
        var nullInt = SqlValue.Null(SqlType.Int32);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.FullTextIndex is not { } fti)
                    continue;
                yield return [
                    SqlValue.FromInt32(table.ObjectId),
                    SqlValue.FromInt32(fti.UniqueIndexId),
                    SqlValue.FromInt32(fti.CatalogId),
                    trueBit,
                    autoCode,
                    autoDesc,
                    trueBit,
                    fullCode,
                    fullDesc,
                    nullDate,
                    nullDate,
                    nullInt,
                    nullInt,
                    nullInt,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.fulltext_index_columns</c>. One row per
    /// <see cref="FullTextIndexColumn"/> across every indexed table.
    /// <c>statistical_semantics</c> always false (the simulator doesn't
    /// expose the WITH STATISTICAL_SEMANTICS option at the column level
    /// since the index parser parse-and-discards it).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysFullTextIndexColumns(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.FullTextIndex is not { } fti)
                    continue;
                foreach (var col in fti.Columns)
                {
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromInt32(col.ColumnId),
                        col.TypeColumnId is int tcid ? SqlValue.FromInt32(tcid) : SqlValue.Null(SqlType.Int32),
                        SqlValue.FromInt32(col.LanguageId),
                        falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.xml_schema_collections</c>. One row per
    /// <see cref="XmlSchemaCollection"/> across every schema. The
    /// principal_id surfaces as NULL — the simulator's CREATE XML SCHEMA
    /// COLLECTION grammar doesn't support an AUTHORIZATION clause and
    /// every collection's principal_id field is left null at construction.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysXmlSchemaCollections(Parser.BatchContext batch, Database database)
    {
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var coll in schema.XmlSchemaCollections.Values.OrderBy(c => c.Id))
            {
                yield return [
                    SqlValue.FromInt32(coll.Id),
                    SqlValue.FromInt32(coll.SchemaId),
                    coll.PrincipalId is int p ? SqlValue.FromInt32(p) : SqlValue.Null(SqlType.Int32),
                    SqlValue.FromSystemName(coll.Name),
                    SqlValue.FromDateTime(coll.CreateDate),
                    SqlValue.FromDateTime(coll.ModifyDate),
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.xml_indexes</c>. One row per
    /// <see cref="XmlIndex"/> across every table. type=3 / type_desc='XML'
    /// for both primary and secondary forms (probe-confirmed). The
    /// is_primary_key column surfaces always false — primary xml indexes
    /// aren't xml-typed PKs.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysXmlIndexes(Parser.BatchContext batch, Database database)
    {
        var charOneType = CharSqlType.Get(1, Collation.Catalog, Coercibility.Implicit);
        var typeCode = SqlValue.FromByte(3);
        var typeDesc = SqlValue.FromNVarchar("XML");
        var falseBit = SqlValue.FromBoolean(false);
        var pathCode = SqlValue.FromChar(charOneType, "P");
        var pathDesc = SqlValue.FromNVarchar("PATH");
        var valueCode = SqlValue.FromChar(charOneType, "V");
        var valueDesc = SqlValue.FromNVarchar("VALUE");
        var propertyCode = SqlValue.FromChar(charOneType, "R");
        var propertyDesc = SqlValue.FromNVarchar("PROPERTY");
        var nullChar = SqlValue.Null(charOneType);
        var nullDesc = SqlValue.Null(SqlType.NVarchar);
        var nullInt = SqlValue.Null(SqlType.Int32);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.XmlIndexes.Count == 0)
                    continue;
                // Build a quick name→objectId map so secondary indexes can
                // resolve their using_xml_index_id from the recorded
                // UsingPrimaryIndexName.
                var primaryIds = new Dictionary<string, int>(database.Collation);
                foreach (var ix in table.XmlIndexes)
                {
                    if (ix.IsPrimary)
                        primaryIds[ix.Name] = ix.ObjectId;
                }
                foreach (var ix in table.XmlIndexes)
                {
                    var usingId = ix.UsingPrimaryIndexName is { } u && primaryIds.TryGetValue(u, out var v)
                        ? SqlValue.FromInt32(v)
                        : nullInt;
                    var (secCode, secDesc) = ix.SecondaryType switch
                    {
                        XmlSecondaryIndexType.Path => (pathCode, pathDesc),
                        XmlSecondaryIndexType.Value => (valueCode, valueDesc),
                        XmlSecondaryIndexType.Property => (propertyCode, propertyDesc),
                        _ => (nullChar, nullDesc),
                    };
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromSystemName(ix.Name),
                        SqlValue.FromInt32(ix.ObjectId),
                        typeCode,
                        typeDesc,
                        usingId,
                        secCode,
                        secDesc,
                        falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.spatial_indexes</c>. One row per
    /// <see cref="SpatialIndex"/> across every table. Fixed values:
    /// type=4 / type_desc='SPATIAL', is_unique=false, data_space_id=1
    /// (the simulator's only filegroup), spatial_index_type=3 / 'GEOMETRY' or
    /// 4 / 'GEOGRAPHY' driven by <see cref="SpatialIndexKind"/>. The trailing
    /// admin flags (is_padded / allow_row_locks / etc.) mirror real-server
    /// defaults so applications reading the column shape don't see NULL where
    /// they expect a bool.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSpatialIndexes(Parser.BatchContext batch, Database database)
    {
        var typeCode = SqlValue.FromByte(4);
        var typeDesc = SqlValue.FromNVarchar("SPATIAL");
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        var oneInt = SqlValue.FromInt32(1);
        var nullDesc = SqlValue.Null(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit));
        var geometryTypeDesc = SqlValue.FromNVarchar("GEOMETRY");
        var geographyTypeDesc = SqlValue.FromNVarchar("GEOGRAPHY");
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.SpatialIndexes.Count == 0)
                    continue;
                foreach (var ix in table.SpatialIndexes)
                {
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromSystemName(ix.Name),
                        SqlValue.FromInt32(ix.IndexId),
                        typeCode,
                        typeDesc,
                        falseBit,
                        oneInt,
                        falseBit,
                        falseBit,
                        falseBit,
                        zeroByte,
                        falseBit,
                        falseBit,
                        falseBit,
                        falseBit,
                        trueBit,
                        trueBit,
                        SqlValue.FromInt32((int)ix.Kind),
                        ix.Kind == SpatialIndexKind.Geography ? geographyTypeDesc : geometryTypeDesc,
                        SqlValue.FromNVarchar(ix.TessellationScheme),
                        falseBit,
                        nullDesc,
                        falseBit,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.spatial_index_tessellations</c>. One row per
    /// spatial index across every table, carrying the bounding box +
    /// 4-level grid detail captured at CREATE time. Levels not specified
    /// in the DDL surface as NULL. The level_*_grid_desc columns mirror
    /// SQL Server's enumeration ('LOW' / 'MEDIUM' / 'HIGH' for codes 1/2/3).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSpatialIndexTessellations(Parser.BatchContext batch, Database database)
    {
        var nullDouble = SqlValue.Null(SqlType.Float);
        var nullShort = SqlValue.Null(SqlType.SmallInt);
        var nullDesc = SqlValue.Null(SqlType.NVarchar);
        var nullInt = SqlValue.Null(SqlType.Int32);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var table in schema.HeapTables.Values)
            {
                if (table.SpatialIndexes.Count == 0)
                    continue;
                foreach (var ix in table.SpatialIndexes)
                {
                    yield return [
                        SqlValue.FromInt32(table.ObjectId),
                        SqlValue.FromInt32(ix.IndexId),
                        SqlValue.FromNVarchar(ix.TessellationScheme),
                        ix.BoundingBoxXmin.HasValue ? SqlValue.FromDouble(ix.BoundingBoxXmin.Value) : nullDouble,
                        ix.BoundingBoxYmin.HasValue ? SqlValue.FromDouble(ix.BoundingBoxYmin.Value) : nullDouble,
                        ix.BoundingBoxXmax.HasValue ? SqlValue.FromDouble(ix.BoundingBoxXmax.Value) : nullDouble,
                        ix.BoundingBoxYmax.HasValue ? SqlValue.FromDouble(ix.BoundingBoxYmax.Value) : nullDouble,
                        ix.Level1Grid.HasValue ? SqlValue.FromInt16(ix.Level1Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level1Grid, nullDesc),
                        ix.Level2Grid.HasValue ? SqlValue.FromInt16(ix.Level2Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level2Grid, nullDesc),
                        ix.Level3Grid.HasValue ? SqlValue.FromInt16(ix.Level3Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level3Grid, nullDesc),
                        ix.Level4Grid.HasValue ? SqlValue.FromInt16(ix.Level4Grid.Value) : nullShort,
                        GridLevelDesc(ix.Level4Grid, nullDesc),
                        ix.CellsPerObject.HasValue ? SqlValue.FromInt32(ix.CellsPerObject.Value) : nullInt,
                    ];
                }
            }
        }
    }

    private static SqlValue GridLevelDesc(short? code, SqlValue nullDesc) =>
        code switch
        {
            1 => SqlValue.FromNVarchar("LOW"),
            2 => SqlValue.FromNVarchar("MEDIUM"),
            3 => SqlValue.FromNVarchar("HIGH"),
            _ => nullDesc,
        };

    /// <summary>
    /// Rows for <c>sys.spatial_reference_systems</c>. Real SQL Server
    /// pre-seeds this with ~390 authoritative SRID rows; the simulator
    /// surfaces an empty view (no rows yielded) so the column shape is
    /// reachable but the WKT-laden seed payload doesn't ship.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSpatialReferenceSystems(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        yield break;
    }

    /// <summary>
    /// Fixed <c>create_date</c> seed for <c>sys.databases</c> rows — the
    /// simulator doesn't track per-database creation timestamps, so every
    /// row reports this constant (matching real SQL Server's non-null,
    /// datetime-typed column).
    /// </summary>
    private static readonly DateTime SysDatabasesCreateDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// Fixed <c>service_broker_guid</c> for every <c>sys.databases</c> row.
    /// Service Broker isn't modeled; the column is non-null in real SQL
    /// Server, so a stable constant stands in.
    /// </summary>
    private static readonly Guid SysDatabasesBrokerGuid = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Rows for <c>sys.databases</c>. One row per <see cref="Database"/>
    /// hosted by the connected <see cref="Simulation"/>; matches real SQL
    /// Server's "instance-scoped catalog view" semantic. Full 98-column
    /// projection: modeled columns read live <see cref="Database"/> state
    /// (name / database_id / compatibility_level / collation_name /
    /// snapshot-isolation trio / recovery_model / physical_database_name),
    /// state is always <c>0 / ONLINE</c>, and the remaining option-flag
    /// columns carry the stock defaults a freshly created user database
    /// reports on SQL Server 2025 (user_access MULTI_USER, page_verify
    /// CHECKSUM, containment NONE, log_reuse_wait NOTHING, delayed_durability
    /// DISABLED, catalog_collation DATABASE_DEFAULT). recovery_model is FULL
    /// for the <c>model</c> template and SIMPLE elsewhere, mirroring the
    /// reference instance. Code↔desc pairs are always internally consistent.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabases(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        var trueBit = SqlValue.FromBoolean(true);
        var zeroByte = SqlValue.FromByte(0);
        var ownerSid = SqlValue.FromVarbinary([0x01]);
        var createDate = SqlValue.FromDateTime(SysDatabasesCreateDate);
        var brokerGuid = SqlValue.FromGuid(SysDatabasesBrokerGuid);
        var multiUser = SqlValue.FromNVarchar("MULTI_USER");
        var online = SqlValue.FromNVarchar("ONLINE");
        var checksum = SqlValue.FromNVarchar("CHECKSUM");
        var nothing = SqlValue.FromNVarchar("NOTHING");
        var none = SqlValue.FromNVarchar("NONE");
        var disabled = SqlValue.FromNVarchar("DISABLED");
        var databaseDefault = SqlValue.FromNVarchar("DATABASE_DEFAULT");
        var unsupported = SqlValue.FromNVarchar("UNSUPPORTED");
        var recoveryTime = SqlValue.FromInt32(60);
        var zeroInt = SqlValue.FromInt32(0);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullSmallInt = SqlValue.Null(SqlType.SmallInt);
        var nullBit = SqlValue.Null(SqlType.Bit);
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullName = SqlValue.Null(SqlType.NVarchar);

        // Ordered by database_id via DatabasesWithIds (master = 1, system
        // databases 2-4, user databases from 5) — matching real SQL Server's
        // sys.databases ordering by database_id.
        foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            var snapshotOn = db.AllowSnapshotIsolation;
            var isModel = Collation.Baseline.Equals(db.Name, "model");
            yield return [
                SqlValue.FromSystemName(db.Name),
                SqlValue.FromInt32(id),
                nullInt,
                ownerSid,
                createDate,
                SqlValue.FromByte((byte)db.CompatibilityLevel),
                SqlValue.FromSystemName(db.CollationName),
                zeroByte,
                multiUser,
                falseBit,
                falseBit,
                falseBit,
                zeroByte,
                online,
                falseBit,
                falseBit,
                falseBit,
                SqlValue.FromByte((byte)(snapshotOn ? 1 : 0)),
                SqlValue.FromNVarchar(snapshotOn ? "ON" : "OFF"),
                SqlValue.FromBoolean(db.ReadCommittedSnapshot),
                SqlValue.FromByte(isModel ? (byte)1 : (byte)3),
                SqlValue.FromNVarchar(isModel ? "FULL" : "SIMPLE"),
                SqlValue.FromByte(2),
                checksum,
                trueBit,
                falseBit,
                trueBit,
                falseBit,
                falseBit,
                trueBit,
                falseBit,
                trueBit,
                trueBit,
                trueBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                trueBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                brokerGuid,
                falseBit,
                zeroByte,
                nothing,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                nullGuid,
                nullGuid,
                nullInt,
                nullSmallInt,
                nullName,
                nullInt,
                nullName,
                nullBit,
                nullBit,
                nullSmallInt,
                zeroByte,
                none,
                recoveryTime,
                zeroInt,
                disabled,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                zeroInt,
                databaseDefault,
                SqlValue.FromNVarchar(db.Name),
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                trueBit,
                trueBit,
                falseBit,
                falseBit,
                falseBit,
                falseBit,
                zeroByte,
                unsupported,
                zeroByte,
                unsupported,
                falseBit,
                falseBit,
                falseBit,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.database_mirroring</c> — one per database (join key
    /// <c>database_id</c>, ordered via <see cref="Parser.Expressions.DbId.DatabasesWithIds"/>).
    /// The simulator never mirrors a database, so every <c>mirroring_*</c>
    /// column is NULL on every row, matching a live SQL Server 2025's
    /// non-mirrored shape. SSMS's Object-Explorer enumeration LEFT JOINs this
    /// to <c>sys.databases</c> on <c>database_id</c> and reads
    /// <c>ISNULL(mirroring_role, 0)</c> / <c>ISNULL(mirroring_state + 1, 0)</c>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseMirroring(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullTinyInt = SqlValue.Null(SqlType.TinyInt);
        var nullDesc = SqlValue.Null(NVarcharSqlType.Get(60, Collation.Catalog, Coercibility.Implicit));
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullName = SqlValue.Null(SqlType.NVarchar);
        var nullLsn = SqlValue.Null(SqlType.GetDecimal(25, 0));

        foreach (var (_, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            yield return [
                SqlValue.FromInt32(id),
                nullGuid,
                nullTinyInt,
                nullDesc,
                nullTinyInt,
                nullDesc,
                nullInt,
                nullTinyInt,
                nullDesc,
                nullInt,
                nullName,
                nullName,
                nullName,
                nullTinyInt,
                nullDesc,
                nullLsn,
                nullInt,
                nullInt,
                nullDesc,
                nullLsn,
                nullLsn,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.database_query_store_options</c> — one OFF row per user
    /// database, zero rows for a system database (master/tempdb/model/msdb per
    /// <see cref="Simulation.SystemDatabaseNames"/>). The simulator never
    /// enables Query Store, so the single row is the fixed "disabled" shape a
    /// live SQL Server 2025 returns for a Query-Store-off user database
    /// (desired/actual OFF, query_capture_mode CUSTOM). The join key is the
    /// database context (<paramref name="database"/>), so a three-part
    /// <c>master.sys.database_query_store_options</c> read returns nothing.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabaseQueryStoreOptions(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        if (Simulation.SystemDatabaseNames.Contains(database.Name))
            yield break;

        var off = SqlValue.FromNVarchar("OFF");
        yield return [
            SqlValue.FromInt16(0),                  // desired_state
            off,                                    // desired_state_desc
            SqlValue.FromInt16(0),                  // actual_state
            off,                                    // actual_state_desc
            SqlValue.FromInt32(0),                  // readonly_reason
            SqlValue.FromInt64(0),                  // current_storage_size_mb
            SqlValue.FromInt64(900),                // flush_interval_seconds
            SqlValue.FromInt64(60),                 // interval_length_minutes
            SqlValue.FromInt64(1000),               // max_storage_size_mb
            SqlValue.FromInt64(30),                 // stale_query_threshold_days
            SqlValue.FromInt64(200),                // max_plans_per_query
            SqlValue.FromInt16(4),                  // query_capture_mode
            SqlValue.FromNVarchar("CUSTOM"),        // query_capture_mode_desc
            SqlValue.FromInt32(30),                 // capture_policy_execution_count
            SqlValue.FromInt64(1000),               // capture_policy_total_compile_cpu_time_ms
            SqlValue.FromInt64(100),                // capture_policy_total_execution_cpu_time_ms
            SqlValue.FromInt32(24),                 // capture_policy_stale_threshold_hours
            SqlValue.FromInt16(0),                  // size_based_cleanup_mode
            off,                                    // size_based_cleanup_mode_desc
            SqlValue.FromInt16(0),                  // wait_stats_capture_mode
            off,                                    // wait_stats_capture_mode_desc
            SqlValue.FromNVarchar(string.Empty),    // actual_state_additional_info
        ];
    }

    /// <summary>
    /// Rows for <c>sys.master_files</c> — one data file (<c>type</c> 0, ROWS)
    /// and one log file (<c>type</c> 1, LOG) per database, join key
    /// <c>database_id</c>. The simulator emits no <c>type</c>-2
    /// (FILESTREAM / memory-optimized) files, so SSMS's in-memory-OLTP probe
    /// (<c>where mf.[type] = 2</c>) returns nothing. Contents are synthetic:
    /// logical name <c>&lt;db&gt;_Data</c> / <c>&lt;db&gt;_Log</c>, a plausible
    /// physical path, a small page count, unlimited <c>max_size</c>, 64 MB
    /// growth. All LSN columns surface NULL (no physical log).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysMasterFiles(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        var onlineState = SqlValue.FromNVarchar("ONLINE");
        var nullGuid = SqlValue.Null(SqlType.UniqueIdentifier);
        var nullTime = SqlValue.Null(SqlType.DateTime);
        var nullInt = SqlValue.Null(SqlType.Int32);
        var nullLsn = SqlValue.Null(SqlType.GetDecimal(25, 0));
        var rowsDesc = SqlValue.FromNVarchar("ROWS");
        var logDesc = SqlValue.FromNVarchar("LOG");
        var unlimited = SqlValue.FromInt32(-1);
        var growthKb = SqlValue.FromInt32(65536);

        SqlValue[] BuildFile(short id, int fileId, byte type, SqlValue typeDesc, int dataSpaceId, string logicalName, string physicalName, int sizePages) =>
        [
            SqlValue.FromInt32(id),
            SqlValue.FromInt32(fileId),
            nullGuid,
            SqlValue.FromByte(type),
            typeDesc,
            SqlValue.FromInt32(dataSpaceId),
            SqlValue.FromNVarchar(logicalName),
            SqlValue.FromNVarchar(physicalName),
            zeroByte,
            onlineState,
            SqlValue.FromInt32(sizePages),
            unlimited,
            growthKb,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            falseBit,
            nullLsn,
            nullLsn,
            nullLsn,
            nullLsn,
            nullLsn,
            nullGuid,
            nullTime,
            nullLsn,
            nullGuid,
            nullLsn,
            nullGuid,
            nullLsn,
            nullInt,
        ];

        foreach (var (db, id) in Parser.Expressions.DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            yield return BuildFile(id, 1, 0, rowsDesc, 1, db.Name + "_Data", "/var/opt/mssql/data/" + db.Name + ".mdf", 640);
            yield return BuildFile(id, 2, 1, logDesc, 0, db.Name + "_Log", "/var/opt/mssql/data/" + db.Name + "_log.ldf", 128);
        }
    }

    /// <summary>
    /// Rows for <c>sys.servers</c>. Row 0 is the local instance
    /// (<c>is_linked = 0</c>, name <c>"SIMULATED"</c>, product
    /// <c>"SQL Server"</c>); each subsequent row is one entry from
    /// <see cref="Simulation.ActiveLinkedServers"/> in name-sort order
    /// (stable across runs, distinct from real SQL Server's
    /// <c>object_id</c>-derived ordering — see the quirks list).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysServers(Parser.BatchContext batch, Database database)
    {
        _ = database;
        var notLinked = SqlValue.FromBoolean(false);
        var isLinked = SqlValue.FromBoolean(true);
        var localProduct = SqlValue.FromNVarchar("SQL Server");
        var nullProvider = SqlValue.Null(SqlType.NVarchar);
        var nullDataSource = SqlValue.Null(SqlType.NVarchar);
        yield return [
            SqlValue.FromInt32(0),
            SqlValue.FromSystemName("SIMULATED"),
            localProduct,
            nullProvider,
            nullDataSource,
            notLinked,
        ];

        var serverId = 1;
        foreach (var ls in batch.Connection.Simulation.ActiveLinkedServers.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            yield return [
                SqlValue.FromInt32(serverId++),
                SqlValue.FromSystemName(ls.Name),
                SqlValue.FromNVarchar(ls.SrvProduct),
                SqlValue.FromNVarchar(ls.Provider),
                ls.DataSource is null ? nullDataSource : SqlValue.FromNVarchar(ls.DataSource),
                isLinked,
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.fn_helpcollations()</c>. Emits one row per entry in
    /// <see cref="Collation.IsRecognized"/> — the simulator's whitelist of
    /// metadata-accepted collation names. Real SQL Server returns ~5540
    /// rows here; the simulator's shorter list is honest about which
    /// collation names round-trip through <see cref="Database.CollationName"/>
    /// / <see cref="HeapColumn.Collation"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateFnHelpCollations(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        foreach (var (entryName, entryDesc) in Collation.EnumerateRecognized().OrderBy(e => e.Name, StringComparer.Ordinal))
            yield return [SqlValue.FromSystemName(entryName), SqlValue.FromNVarchar(entryDesc)];
    }

    /// <summary>
    /// Rows for <c>sys.sequences</c>: one per registered sequence object,
    /// schema-ordered. <c>cache_size</c> is always NULL (the simulator
    /// doesn't model the batched-allocation cache; real SQL Server returns
    /// NULL when no explicit <c>CACHE n</c> was given anyway). Type-id
    /// columns derive from the declared type via <see cref="SystypesRowData"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysSequences(Parser.BatchContext batch, Database database)
    {
        var nullCache = SqlValue.Null(SqlType.Int32);
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaId = SqlValue.FromInt32(schema.SchemaId);
            foreach (var seq in schema.Sequences.Values.OrderBy(s => s.ObjectId))
            {
                var (systemTypeId, userTypeId) = SequenceTypeIds(seq.DeclaredType);
                yield return [
                    SqlValue.FromSystemName(seq.Name),
                    SqlValue.FromInt32(seq.ObjectId),
                    schemaId,
                    SqlValue.FromInt64(seq.StartValue),
                    SqlValue.FromInt64(seq.Increment),
                    SqlValue.FromInt64(seq.MinValue),
                    SqlValue.FromInt64(seq.MaxValue),
                    seq.Cycle ? trueBit : falseBit,
                    trueBit,
                    nullCache,
                    SqlValue.FromInt64(seq.CurrentValue),
                    SqlValue.FromByte(systemTypeId),
                    SqlValue.FromInt32(userTypeId),
                    seq.IsExhausted ? trueBit : falseBit,
                ];
            }
        }
    }

    /// <summary>
    /// Maps a sequence's declared scalar type to the <c>(system_type_id,
    /// user_type_id)</c> pair surfaced in <c>sys.sequences</c>. The values
    /// match SQL Server's documented system-type IDs (tinyint=48, smallint=52,
    /// int=56, bigint=127, decimal=106). System types use the same id for
    /// both columns.
    /// </summary>
    private static (byte SystemTypeId, int UserTypeId) SequenceTypeIds(SqlType type) => type switch
    {
        TinyIntSqlType => (48, 48),
        SmallIntSqlType => (52, 52),
        Int32SqlType => (56, 56),
        BigIntSqlType => (127, 127),
        DecimalSqlType => (106, 106),
        _ => (0, 0),
    };

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaDomains(Parser.BatchContext batch, Database database, SqlValue tableTypeDataType)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var tt in schema.TableTypes.Values.OrderBy(t => t.UserTypeId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(tt.Name),
                    tableTypeDataType,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.procedures</c>: one row per <see cref="Procedure"/> in
    /// every schema. The full <c>create_date</c> / <c>modify_date</c> story
    /// in real SQL Server tracks ALTER PROCEDURE separately; the simulator
    /// uses the ALTER-preserving <see cref="SchemaObject.CreateDate"/> for both
    /// columns (the original create date survives ALTER, matching the way
    /// <see cref="SchemaObject.ObjectId"/> survives — minor fidelity gap on
    /// modify_date which would shift on each ALTER in real SQL Server).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateProcedures(
        Parser.BatchContext batch,
        Database database,
        SqlType charTwo,
        SqlValue notMsShipped)
    {
        _ = batch;
        // 'P ' / 'SQL_STORED_PROCEDURE' — matches Procedure.ObjectTypeCode /
        // Procedure.ObjectTypeDescription, kept as local constants here to
        // avoid one SqlValue allocation per row.
        var procType = SqlValue.FromChar(charTwo, "P ");
        var procTypeDesc = SqlValue.FromNVarchar("SQL_STORED_PROCEDURE");
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                var createDate = SqlValue.FromDateTime(proc.CreateDate);
                yield return [
                    SqlValue.FromInt32(proc.ObjectId),
                    SqlValue.FromSystemName(proc.Name),
                    SqlValue.FromInt32(proc.Schema.SchemaId),
                    procType,
                    procTypeDesc,
                    createDate,
                    createDate,
                    notMsShipped,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.ROUTINES</c>: procedures plus functions.
    /// ROUTINE_TYPE distinguishes PROCEDURE vs FUNCTION; DATA_TYPE carries
    /// the return-type family for scalar UDFs, 'TABLE' for inline TVFs, NULL
    /// for procedures.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaRoutines(
        Parser.BatchContext batch,
        Database database,
        SqlValue procedureRoutineType,
        SqlValue functionRoutineType,
        SqlValue tableDataType)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullDataType = SqlValue.Null(SqlType.SystemName);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(proc.Name),
                    procedureRoutineType,
                    nullDataType,
                    RoutineDefinition(proc.DefinitionText),
                ];
            }
            foreach (var fn in schema.Functions.Values.OrderBy(f => f.ObjectId))
            {
                var dataType = fn is ScalarFunction scalarFn
                    ? SqlValue.FromSystemName(scalarFn.ReturnType.SqlServerName)
                    : tableDataType;
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(fn.Name),
                    functionRoutineType,
                    dataType,
                    RoutineDefinition(fn.DefinitionText),
                ];
            }
        }
    }

    /// <summary>
    /// Builds the <c>INFORMATION_SCHEMA.ROUTINES.ROUTINE_DEFINITION</c> value
    /// from a module's captured source text. The ISO column is
    /// <c>nvarchar(4000)</c>, so the definition is truncated to its first 4000
    /// characters (matching SQL Server); NULL stays NULL (encrypted modules).
    /// </summary>
    private static SqlValue RoutineDefinition(string? text) =>
        text is null
            ? SqlValue.Null(SqlType.NVarchar)
            : SqlValue.FromNVarchar(text.Length > 4000 ? text[..4000] : text);

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.PARAMETERS</c>: per-parameter entries
    /// for procedures plus functions. ORDINAL_POSITION is 1-based for
    /// declared parameters; CHARACTER_MAXIMUM_LENGTH is set only for string
    /// types. PARAMETER_MODE is 'IN' for non-OUTPUT params, 'INOUT' for
    /// OUTPUT-declared procedure params (probe-confirmed); functions have
    /// no OUTPUT semantics so all UDF params project as 'IN'.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaParameters(
        Parser.BatchContext batch,
        Database database,
        SqlValue modeIn,
        SqlValue modeInOut)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullInt = SqlValue.Null(SqlType.Int32);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                for (var i = 0; i < proc.Parameters.Length; i++)
                {
                    var param = proc.Parameters[i];
                    yield return [
                        catalog,
                        schemaName,
                        SqlValue.FromSystemName(proc.Name),
                        SqlValue.FromInt32(i + 1),
                        param.IsOutput ? modeInOut : modeIn,
                        SqlValue.FromSystemName("@" + param.Name),
                        SqlValue.FromSystemName(param.Type.SqlServerName),
                        param.Type.Category == SqlTypeCategory.String && param.DeclaredMaxLength is int len && len > 0
                            ? SqlValue.FromInt32(len)
                            : nullInt,
                    ];
                }
            }
            foreach (var fn in schema.Functions.Values.OrderBy(f => f.ObjectId))
            {
                for (var i = 0; i < fn.Parameters.Length; i++)
                {
                    var param = fn.Parameters[i];
                    yield return [
                        catalog,
                        schemaName,
                        SqlValue.FromSystemName(fn.Name),
                        SqlValue.FromInt32(i + 1),
                        modeIn,
                        SqlValue.FromSystemName("@" + param.Name),
                        SqlValue.FromSystemName(param.Type.SqlServerName),
                        nullInt,
                    ];
                }
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.views</c>: one row per <see cref="View"/> in every
    /// schema. <c>is_date_correlation_view</c> is always False (the feature
    /// isn't modeled).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateViews(Parser.BatchContext batch, Database database)
    {
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var view in schema.Views.Values.OrderBy(v => v.ObjectId))
            {
                yield return [
                    SqlValue.FromInt32(view.ObjectId),
                    SqlValue.FromSystemName(view.Name),
                    SqlValue.FromInt32(view.Schema.SchemaId),
                    SqlValue.FromBoolean(view.WithCheckOption),
                    falseBit,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>INFORMATION_SCHEMA.VIEWS</c>: per-view ISO-shape entries.
    /// VIEW_DEFINITION surfaces the stored body text (real SQL Server
    /// returns NULL for WITH ENCRYPTION views; the simulator currently
    /// always surfaces it — minor fidelity gap). CHECK_OPTION is 'CASCADE'
    /// when WITH CHECK OPTION was specified, 'NONE' otherwise. IS_UPDATABLE
    /// is hardcoded 'NO' (probe-confirmed: real SQL Server reports 'NO'
    /// even for actually-updatable views).
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaViews(
        Parser.BatchContext batch,
        Database database,
        SqlValue checkOptionNone,
        SqlValue checkOptionCascade,
        SqlValue isUpdatableNo)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var view in schema.Views.Values.OrderBy(v => v.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(view.Name),
                    SqlValue.FromNVarchar(view.BodyText),
                    view.WithCheckOption ? checkOptionCascade : checkOptionNone,
                    isUpdatableNo,
                ];
            }
        }
    }

    /// <summary>
    /// Rows for <c>sys.parameters</c>. A <see cref="ScalarFunction"/> emits a
    /// row with <c>parameter_id=0</c> for its return type (empty <c>name</c>,
    /// <c>is_output=1</c>) followed by one row per declared parameter. An
    /// <see cref="InlineTableValuedFunction"/> emits one row per declared
    /// parameter only — no return-row, because the return shape is a TABLE
    /// (the columns surface in <c>sys.columns</c> instead). Probe-confirmed
    /// against SQL Server 2025.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateParameters(Parser.BatchContext batch, Database database)
    {
        var emptyName = SqlValue.FromSystemName("");
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        foreach (var schema in database.Schemas.Values)
        {
            foreach (var proc in schema.Procedures.Values.OrderBy(p => p.ObjectId))
            {
                var procObjectId = SqlValue.FromInt32(proc.ObjectId);
                for (var i = 0; i < proc.Parameters.Length; i++)
                {
                    var param = proc.Parameters[i];
                    // TVP parameters surface system_type_id 243 (table type)
                    // and the user_type_id of the referenced TableType.
                    // is_readonly is true only for TVP params (probe-confirmed
                    // — scalar params with a future READONLY shape don't ship).
                    var isTvp = param.TableType is not null;
                    yield return [
                        procObjectId,
                        SqlValue.FromSystemName("@" + param.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(isTvp ? (byte)243 : param.Type.SystemTypeId),
                        SqlValue.FromInt32(isTvp ? param.TableType!.UserTypeId : param.Type.UserTypeId),
                        SqlValue.FromInt16(0),
                        zeroByte,
                        zeroByte,
                        SqlValue.FromBoolean(param.IsOutput),
                        trueBit,
                        SqlValue.FromBoolean(isTvp),
                    ];
                }
            }
            foreach (var fn in schema.Functions.Values.OrderBy(f => f.ObjectId))
            {
                var fnObjectId = SqlValue.FromInt32(fn.ObjectId);
                // Scalar UDFs get a synthetic parameter_id=0 return-type row;
                // inline TVFs don't (their TABLE shape lives in sys.columns).
                // max_length stays at 0 for v1 — see scalar-UDF section in
                // CLAUDE.md.
                if (fn is ScalarFunction scalarFn)
                {
                    yield return [
                        fnObjectId,
                        emptyName,
                        SqlValue.FromInt32(0),
                        SqlValue.FromByte(scalarFn.ReturnType.SystemTypeId),
                        SqlValue.FromInt32(scalarFn.ReturnType.UserTypeId),
                        SqlValue.FromInt16(0),
                        zeroByte,
                        zeroByte,
                        trueBit,
                        trueBit,
                        falseBit,
                    ];
                }
                for (var i = 0; i < fn.Parameters.Length; i++)
                {
                    var p = fn.Parameters[i];
                    yield return [
                        fnObjectId,
                        SqlValue.FromSystemName("@" + p.Name),
                        SqlValue.FromInt32(i + 1),
                        SqlValue.FromByte(p.Type.SystemTypeId),
                        SqlValue.FromInt32(p.Type.UserTypeId),
                        SqlValue.FromInt16(0),
                        zeroByte,
                        zeroByte,
                        falseBit,
                        trueBit,
                        falseBit,
                    ];
                }
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateColumns(
        Parser.BatchContext batch,
        Database database,
        SqlValue defaultCollation,
        SqlValue nullCollation)
    {
        _ = batch;
        var falseBit = SqlValue.FromBoolean(false);
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
                    ];
                }
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaTables(Parser.BatchContext batch, Database database, SqlValue baseTable, SqlValue viewTableType)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(t.Name),
                    baseTable,
                ];
            }
            foreach (var view in schema.Views.Values.OrderBy(v => v.ObjectId))
            {
                yield return [
                    catalog,
                    schemaName,
                    SqlValue.FromSystemName(view.Name),
                    viewTableType,
                ];
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaColumns(
        Parser.BatchContext batch,
        Database database,
        SqlValue defaultCollation,
        SqlValue unicodeCs,
        SqlValue isoCs,
        SqlValue radix10,
        SqlValue radix2)
    {
        _ = batch;
        var catalog = SqlValue.FromSystemName(database.Name);
        var nullString = SqlValue.Null(SqlType.NVarchar);
        var nullInt32 = SqlValue.Null(SqlType.Int32);
        var nullInt16 = SqlValue.Null(SqlType.SmallInt);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var yesNullable = SqlValue.FromVarchar("YES");
        var noNullable = SqlValue.FromVarchar("NO");
        var dbDefaultCollation = SqlValue.FromSystemName(database.CollationName);
        _ = defaultCollation;
        foreach (var schema in database.Schemas.Values)
        {
            var schemaName = SqlValue.FromSystemName(schema.Name);
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                var tableName = SqlValue.FromSystemName(t.Name);
                for (var i = 0; i < t.Columns.Length; i++)
                {
                    var col = t.Columns[i];
                    var (charLength, octetLength, numericPrecision, numericRadix, numericScale, dateTimePrecision) = GetInformationSchemaColumnMetadata(col);
                    var cs = col.Type.Category switch
                    {
                        SqlTypeCategory.String when col.Type is NVarcharSqlType or NCharSqlType || col.Type == SqlType.SystemName || col.Type == SqlType.NText => unicodeCs,
                        SqlTypeCategory.String => isoCs,
                        _ => nullSysName,
                    };
                    var collation = col.Type.Category != SqlTypeCategory.String ? nullSysName
                        : col.Collation is { } overrideName ? SqlValue.FromSystemName(overrideName)
                        : dbDefaultCollation;
                    yield return [
                        catalog,
                        schemaName,
                        tableName,
                        SqlValue.FromSystemName(col.Name),
                        SqlValue.FromInt32(i + 1),
                        nullString,
                        col.Nullable ? yesNullable : noNullable,
                        SqlValue.FromSystemName(col.Type.SqlServerName),
                        charLength is int cl ? SqlValue.FromInt32(cl) : nullInt32,
                        octetLength is int ol ? SqlValue.FromInt32(ol) : nullInt32,
                        numericPrecision is byte np ? SqlValue.FromByte(np) : nullByte,
                        numericRadix switch { 2 => radix2, 10 => radix10, _ => nullInt16 },
                        numericScale is int ns ? SqlValue.FromInt32(ns) : nullInt32,
                        dateTimePrecision is short dp ? SqlValue.FromInt16(dp) : nullInt16,
                        nullSysName,
                        nullSysName,
                        cs,
                        nullSysName,
                        nullSysName,
                        collation,
                        nullSysName,
                        nullSysName,
                        nullSysName,
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

    /// <summary>
    /// Computes the INFORMATION_SCHEMA.COLUMNS numeric / character / datetime
    /// metadata triple. ISO-standard fields differ from <c>sys.columns</c>:
    /// CHARACTER_MAXIMUM_LENGTH is declared <em>char</em> length (not bytes —
    /// so <c>nvarchar(50)→50</c>), CHARACTER_OCTET_LENGTH carries the byte
    /// length. For text/ntext/image the values are the documented sentinels
    /// (<c>2147483647</c> / <c>1073741823</c> / <c>2147483647</c> for
    /// MAXIMUM_LENGTH; <c>2147483647</c> / <c>2147483646</c> / <c>2147483647</c>
    /// for OCTET_LENGTH). NUMERIC_PRECISION is NULL for bit (and for non-
    /// numeric types); float / real use radix 2 with NULL scale.
    /// </summary>
    private static (int? CharLength, int? OctetLength, byte? NumericPrecision, int? NumericRadix, int? NumericScale, short? DateTimePrecision) GetInformationSchemaColumnMetadata(HeapColumn col)
    {
        var t = col.Type;
        return t switch
        {
            _ when t == SqlType.Bit => (null, null, null, null, null, null),
            _ when t == SqlType.TinyInt => (null, null, 3, 10, 0, null),
            _ when t == SqlType.SmallInt => (null, null, 5, 10, 0, null),
            _ when t == SqlType.Int32 => (null, null, 10, 10, 0, null),
            _ when t == SqlType.BigInt => (null, null, 19, 10, 0, null),
            _ when t == SqlType.Money => (null, null, 19, 10, 4, null),
            _ when t == SqlType.SmallMoney => (null, null, 10, 10, 4, null),
            DecimalSqlType d => (null, null, d.precision, 10, d.scale, null),
            _ when t == SqlType.Float => (null, null, 53, 2, null, null),
            _ when t == SqlType.Real => (null, null, 24, 2, null, null),
            _ when t == SqlType.Date => (null, null, null, null, null, 0),
            _ when t == SqlType.SmallDateTime => (null, null, null, null, null, 0),
            _ when t == SqlType.DateTime => (null, null, null, null, null, 3),
            DateTime2SqlType dt2 => (null, null, null, null, null, (short)dt2.precision),
            TimeSqlType tm => (null, null, null, null, null, (short)tm.precision),
            DateTimeOffsetSqlType dto => (null, null, null, null, null, (short)dto.precision),
            _ when t == SqlType.Text => (2147483647, 2147483647, null, null, null, null),
            _ when t == SqlType.NText => (1073741823, 2147483646, null, null, null, null),
            _ when t == SqlType.Image => (2147483647, 2147483647, null, null, null, null),
            _ when t == SqlType.SystemName => (128, 256, null, null, null, null),
            CharSqlType c => (c.length, c.length, null, null, null, null),
            NCharSqlType nc => (nc.length, nc.length * 2, null, null, null, null),
            BinarySqlType bn => (bn.length, bn.length, null, null, null, null),
            VarcharSqlType vc => DeclaredVarLength(vc.length, col.MaxLength, octetPerChar: 1),
            NVarcharSqlType nv => DeclaredVarLength(nv.length, col.MaxLength, octetPerChar: 2),
            VarbinarySqlType vb => DeclaredVarLength(vb.length, col.MaxLength, octetPerChar: 1),
            _ when t == SqlType.UniqueIdentifier => (null, null, null, null, null, null),
            _ when t == SqlType.RowVersion => (null, null, null, null, null, null),
            _ => throw new NotSupportedException($"No INFORMATION_SCHEMA.COLUMNS metadata for {t}."),
        };
    }

    /// <summary>
    /// Resolves the (char, octet) length pair for a variable-length string /
    /// binary column. <c>typeLength == -1</c> is the MAX form (both reported
    /// as <c>-1</c>); <c>typeLength == 0</c> means the type singleton didn't
    /// pin a length, so fall back to <see cref="HeapColumn.MaxLength"/>.
    /// </summary>
    private static (int? CharLength, int? OctetLength, byte? NumericPrecision, int? NumericRadix, int? NumericScale, short? DateTimePrecision) DeclaredVarLength(short typeLength, int? columnMaxLength, int octetPerChar)
    {
        if (typeLength == -1)
            return (-1, -1, null, null, null, null);
        var chars = typeLength == 0 ? (columnMaxLength ?? 1) : typeLength;
        return (chars, chars * octetPerChar, null, null, null, null);
    }

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
            off, // is_schema_bound
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
        foreach (var schema in database.Schemas.Values)
        {
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
                    SqlValue.FromChar(charTwo, obj.ObjectTypeCode),
                    SqlValue.FromNVarchar(obj.ObjectTypeDescription),
                    SqlValue.FromDateTime(obj.CreateDate),
                    SqlValue.FromDateTime(obj.ModifyDate),
                    notMsShipped,
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
                        key.Kind == KeyConstraintKind.PrimaryKey ? pkType : uqType,
                        key.Kind == KeyConstraintKind.PrimaryKey ? pkTypeDesc : uqTypeDesc,
                        createDate,
                        modifyDate,
                        notMsShipped,
                    ];
                }
                foreach (var chk in t.CheckConstraints)
                {
                    yield return [
                        SqlValue.FromInt32(chk.ObjectId),
                        SqlValue.FromSystemName(chk.Name),
                        schemaIdValue,
                        tableObjectId,
                        checkType,
                        checkTypeDesc,
                        createDate,
                        modifyDate,
                        notMsShipped,
                    ];
                }
                foreach (var fk in t.OutgoingForeignKeys)
                {
                    yield return [
                        SqlValue.FromInt32(fk.ObjectId),
                        SqlValue.FromSystemName(fk.Name),
                        schemaIdValue,
                        tableObjectId,
                        SqlValue.FromChar(charTwo, "F "),
                        SqlValue.FromNVarchar("FOREIGN_KEY_CONSTRAINT"),
                        createDate,
                        modifyDate,
                        notMsShipped,
                    ];
                }
            }
        }
    }
}
