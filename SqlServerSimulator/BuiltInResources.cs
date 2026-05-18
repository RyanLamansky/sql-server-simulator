using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Globalization;

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

        return new(Collation.Default) { [systypes.Name] = systypes };
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
        // sys.schemas: (name sysname, schema_id int, principal_id int null)
        var schemasColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
        };
        var schemasView = new CatalogView("schemas", schemasColumns, (batch, database) =>
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
        var charTwo = CharSqlType.Get(2);
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
        var tablesColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("temporal_type", SqlType.TinyInt, null, true),
            new("temporal_type_desc", SqlType.NVarchar, 60, true),
            new("history_table_id", SqlType.Int32, null, true),
        };
        var tablesView = new CatalogView("tables", tablesColumns, (batch, database) =>
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
        var objectsColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, true),
        };
        var objectsView = new CatalogView("objects", objectsColumns, (batch, database) =>
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
        var columnsColumns = new HeapColumn[]
        {
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
        };
        var columnsView = new CatalogView("columns", columnsColumns, (batch, database) =>
            EnumerateColumns(batch, database, defaultCollation, nullCollation));

        // INFORMATION_SCHEMA.TABLES: ISO-standard 4-column shape. TABLE_TYPE
        // is 'BASE TABLE' for every user table; 'VIEW' (not modeled) would be
        // the other shipped value.
        var baseTable = SqlValue.FromVarchar("BASE TABLE");
        var viewTableType = SqlValue.FromVarchar("VIEW");
        var isTablesColumns = new HeapColumn[]
        {
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("TABLE_TYPE", SqlType.Varchar, 10, true),
        };
        var isTablesView = new CatalogView("TABLES", isTablesColumns, (batch, database) =>
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
        var isColumnsColumns = new HeapColumn[]
        {
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
        };
        var isColumnsView = new CatalogView("COLUMNS", isColumnsColumns, (batch, database) =>
            EnumerateInformationSchemaColumns(batch, database, defaultCollation, unicodeCs, isoCs, radix10, radix2));

        // INFORMATION_SCHEMA.SCHEMATA: ISO-standard 6-column shape. Lists
        // only the schemas the simulator actually models — no padding for
        // role principals (db_owner / db_datareader / …) since there's no
        // principal model. SCHEMA_OWNER mirrors SCHEMA_NAME (matches real
        // SQL Server's behavior for built-in / user schemas without explicit
        // AUTHORIZATION).
        var defaultCsName = SqlValue.FromSystemName("iso_1");
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var isSchemataColumns = new HeapColumn[]
        {
            new("CATALOG_NAME", SqlType.SystemName, 128, true),
            new("SCHEMA_NAME", SqlType.SystemName, 128, false),
            new("SCHEMA_OWNER", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_CATALOG", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_SCHEMA", SqlType.SystemName, 128, true),
            new("DEFAULT_CHARACTER_SET_NAME", SqlType.SystemName, 128, true),
        };
        var isSchemataView = new CatalogView("SCHEMATA", isSchemataColumns, (batch, database) =>
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
        var parametersColumns = new HeapColumn[]
        {
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
        };
        var parametersView = new CatalogView("parameters", parametersColumns, EnumerateParameters);

        // sys.views: per-view rows. Load-bearing subset of real SQL Server's
        // sys.views shape — object_id / name / schema_id / with_check_option /
        // is_date_correlation_view. Other documented columns (principal_id,
        // is_replicated, has_replication_filter, etc.) aren't modeled.
        var viewsColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("with_check_option", SqlType.Bit, null, false),
            new("is_date_correlation_view", SqlType.Bit, null, false),
        };
        var viewsCatalogView = new CatalogView("views", viewsColumns, EnumerateViews);

        // sys.procedures: per-procedure rows. Shipped column subset matches
        // the load-bearing surface — object_id / name / schema_id /
        // create_date / modify_date / is_ms_shipped. Other documented
        // columns (principal_id, is_auto_executed, is_execution_replicated,
        // etc.) aren't modeled.
        var proceduresColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("schema_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
        };
        var proceduresView = new CatalogView("procedures", proceduresColumns, (batch, database) =>
            EnumerateProcedures(batch, database, charTwo, notMsShipped));

        // INFORMATION_SCHEMA.ROUTINES: ISO-shape view listing both procedures
        // and functions. The simulator ships the load-bearing column subset:
        // ROUTINE_CATALOG / SCHEMA / NAME / TYPE / DATA_TYPE. For procedures
        // DATA_TYPE is NULL (procs have no scalar return type); for scalar
        // UDFs it carries the return type's family name; for inline TVFs it
        // is 'TABLE'. Real SQL Server ships dozens of additional columns
        // (CREATED, LAST_ALTERED, ROUTINE_DEFINITION, etc.) that aren't
        // modeled.
        var procedureRoutineType = SqlValue.FromVarchar("PROCEDURE");
        var functionRoutineType = SqlValue.FromVarchar("FUNCTION");
        var tableDataType = SqlValue.FromSystemName("TABLE");
        var isRoutinesColumns = new HeapColumn[]
        {
            new("ROUTINE_CATALOG", SqlType.SystemName, 128, true),
            new("ROUTINE_SCHEMA", SqlType.SystemName, 128, true),
            new("ROUTINE_NAME", SqlType.SystemName, 128, false),
            new("ROUTINE_TYPE", SqlType.Varchar, 9, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
        };
        var isRoutinesView = new CatalogView("ROUTINES", isRoutinesColumns, (batch, database) =>
            EnumerateInformationSchemaRoutines(batch, database, procedureRoutineType, functionRoutineType, tableDataType));

        // INFORMATION_SCHEMA.PARAMETERS: ISO-shape view listing parameters
        // for procedures and functions. PARAMETER_MODE is 'IN' / 'OUT' /
        // 'INOUT'; the simulator emits 'IN' for non-output params, 'INOUT'
        // for OUTPUT-declared params (probe-confirmed: real SQL Server uses
        // INOUT for OUTPUT in procedures). CHARACTER_MAXIMUM_LENGTH is set
        // only for string types.
        var modeIn = SqlValue.FromVarchar("IN");
        var modeInOut = SqlValue.FromVarchar("INOUT");
        var isParametersColumns = new HeapColumn[]
        {
            new("SPECIFIC_CATALOG", SqlType.SystemName, 128, true),
            new("SPECIFIC_SCHEMA", SqlType.SystemName, 128, true),
            new("SPECIFIC_NAME", SqlType.SystemName, 128, false),
            new("ORDINAL_POSITION", SqlType.Int32, null, true),
            new("PARAMETER_MODE", SqlType.Varchar, 10, true),
            new("PARAMETER_NAME", SqlType.SystemName, 128, true),
            new("DATA_TYPE", SqlType.SystemName, 128, true),
            new("CHARACTER_MAXIMUM_LENGTH", SqlType.Int32, null, true),
        };
        var isParametersView = new CatalogView("PARAMETERS", isParametersColumns, (batch, database) =>
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
        var isViewsColumns = new HeapColumn[]
        {
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("VIEW_DEFINITION", SqlType.NVarchar, 4000, true),
            new("CHECK_OPTION", SqlType.Varchar, 7, true),
            new("IS_UPDATABLE", SqlType.Varchar, 2, true),
        };
        var isViewsView = new CatalogView("VIEWS", isViewsColumns, (batch, database) =>
            EnumerateInformationSchemaViews(batch, database, checkOptionNone, checkOptionCascade, isUpdatableNo));

        // sys.types: per-database list of system + user-defined types. Probe-
        // confirmed shipped subset: name / system_type_id / user_type_id /
        // schema_id / is_user_defined / is_table_type / is_nullable. Real SQL
        // Server has many more columns (principal_id, max_length, precision,
        // scale, collation_name, is_assembly_type, default_object_id, etc.);
        // the shipped set is what apps typically test for.
        var typesColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, false),
            new("system_type_id", SqlType.TinyInt, null, false),
            new("user_type_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
            new("is_table_type", SqlType.Bit, null, false),
            new("is_nullable", SqlType.Bit, null, false),
        };
        var typesView = new CatalogView("types", typesColumns, EnumerateSysTypes);

        // sys.table_types: per-database list of user-defined table types
        // only. Probe-confirmed shipped subset: name / type_table_object_id /
        // is_user_defined / schema_id / user_type_id.
        var tableTypesColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, false),
            new("type_table_object_id", SqlType.Int32, null, false),
            new("is_user_defined", SqlType.Bit, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("user_type_id", SqlType.Int32, null, false),
        };
        var tableTypesView = new CatalogView("table_types", tableTypesColumns, EnumerateSysTableTypes);

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
        var sequencesColumns = new HeapColumn[]
        {
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
        };
        var sequencesView = new CatalogView("sequences", sequencesColumns, EnumerateSysSequences);

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
        var triggersColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, false),
            new("object_id", SqlType.Int32, null, false),
            new("parent_class", SqlType.TinyInt, null, false),
            new("parent_class_desc", SqlType.NVarchar, 60, true),
            new("parent_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_disabled", SqlType.Bit, null, false),
            new("is_instead_of_trigger", SqlType.Bit, null, false),
            new("is_not_for_replication", SqlType.Bit, null, false),
        };
        var triggersView = new CatalogView("triggers", triggersColumns, (batch, database) =>
            EnumerateSysTriggers(batch, database, charTwo, parentClassObjectColumn, parentClassObjectColumnDesc));

        // sys.foreign_keys: probe-confirmed 21-column shape against SQL
        // Server 2025 (2026-05-13). EF Core reads name / parent_object_id /
        // referenced_object_id / delete_referential_action /
        // update_referential_action; the simulator ships the full set so
        // catalog-introspection tooling sees an authentic shape.
        var foreignKeysColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", SqlType.NVarchar, 60, true),
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
            new("delete_referential_action_desc", SqlType.NVarchar, 60, true),
            new("update_referential_action", SqlType.TinyInt, null, false),
            new("update_referential_action_desc", SqlType.NVarchar, 60, true),
            new("is_system_named", SqlType.Bit, null, false),
        };
        var foreignKeysView = new CatalogView("foreign_keys", foreignKeysColumns, EnumerateSysForeignKeys);

        // sys.foreign_key_columns: probe-confirmed 6-column shape. One row
        // per (FK, column-pair) — composite FKs emit one row per participant
        // column with constraint_column_id starting at 1.
        var foreignKeyColumnsColumns = new HeapColumn[]
        {
            new("constraint_object_id", SqlType.Int32, null, false),
            new("constraint_column_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("parent_column_id", SqlType.Int32, null, false),
            new("referenced_object_id", SqlType.Int32, null, false),
            new("referenced_column_id", SqlType.Int32, null, false),
        };
        var foreignKeyColumnsView = new CatalogView("foreign_key_columns", foreignKeyColumnsColumns, EnumerateSysForeignKeyColumns);

        // INFORMATION_SCHEMA.DOMAINS: ISO-standard surface. Real SQL Server
        // emits a row for every user-defined type (scalar UDTs surface their
        // base type; table types surface 'table type' as the data_type
        // literal — probe-confirmed G6). Load-bearing subset: DOMAIN_CATALOG /
        // DOMAIN_SCHEMA / DOMAIN_NAME / DATA_TYPE.
        var isDomainsColumns = new HeapColumn[]
        {
            new("DOMAIN_CATALOG", SqlType.SystemName, 128, true),
            new("DOMAIN_SCHEMA", SqlType.SystemName, 128, true),
            new("DOMAIN_NAME", SqlType.SystemName, 128, false),
            new("DATA_TYPE", SqlType.NVarchar, 128, true),
        };
        var tableTypeDataType = SqlValue.FromNVarchar("table type");
        var isDomainsView = new CatalogView("DOMAINS", isDomainsColumns, (batch, database) =>
            EnumerateInformationSchemaDomains(batch, database, tableTypeDataType));

        // sys.check_constraints: probe-confirmed 13-column shape (a subset
        // of sys.objects + the check-specific columns). Used by EF Migrations'
        // model snapshot and tooling that introspects existing CHECK rules.
        var checkConstraintsColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", SqlType.NVarchar, 60, true),
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
        };
        var checkConstraintsView = new CatalogView("check_constraints", checkConstraintsColumns, EnumerateSysCheckConstraints);

        // sys.key_constraints: PK + UNIQUE rows, parallel shape to
        // sys.foreign_keys. Probe-confirmed column set.
        var keyConstraintsColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
            new("unique_index_id", SqlType.Int32, null, false),
            new("is_system_named", SqlType.Bit, null, false),
            new("is_enforced", SqlType.Bit, null, false),
        };
        var keyConstraintsView = new CatalogView("key_constraints", keyConstraintsColumns, EnumerateSysKeyConstraints);

        // sys.default_constraints: per-column named DEFAULT bindings. Real
        // SQL Server emits one row per default (inline or named via ALTER).
        var defaultConstraintsColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("schema_id", SqlType.Int32, null, false),
            new("parent_object_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, true),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("is_ms_shipped", SqlType.Bit, null, false),
            new("is_published", SqlType.Bit, null, false),
            new("is_schema_published", SqlType.Bit, null, false),
            new("parent_column_id", SqlType.Int32, null, false),
            new("definition", SqlType.NVarchar, SqlType.MaxLengthSentinel, true),
            new("is_system_named", SqlType.Bit, null, false),
        };
        var defaultConstraintsView = new CatalogView("default_constraints", defaultConstraintsColumns, EnumerateSysDefaultConstraints);

        // sys.indexes: probe-confirmed 24-column shape against SQL Server
        // 2025 (2026-05-14). One row per (table, index) — PK / UQ
        // constraints surface alongside CREATE-INDEX rows, and a HEAP row
        // (index_id = 0, type = 0, name = NULL) appears for any table with
        // no PRIMARY KEY (matching SQL Server's "the table itself is the
        // heap" semantic). EF Migrations introspection reads name /
        // is_unique / is_primary_key / is_unique_constraint /
        // has_filter / filter_definition.
        var indexesColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, true),
            new("object_id", SqlType.Int32, null, false),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", SqlType.NVarchar, 60, true),
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
        };
        var indexesView = new CatalogView("indexes", indexesColumns, EnumerateSysIndexes);

        // sys.index_columns: probe-confirmed 10-column shape. One row per
        // (index, column) pair — KEY columns get key_ordinal = 1..N and
        // index_column_id = 1..N; INCLUDE columns get key_ordinal = 0 and
        // index_column_id continuing past the key column count.
        var indexColumnsColumns = new HeapColumn[]
        {
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
        };
        var indexColumnsView = new CatalogView("index_columns", indexColumnsColumns, EnumerateSysIndexColumns);

        // sys.dm_tran_locks: per-Hold rows across every schema-bound
        // SchemaLock, every HeapTable.TableDataLock, and every per-row
        // entry in HeapTable.RowLocks. GRANT entries come from
        // LockResource.Holders; WAIT entries from connection registry's
        // WaitingOnResource / WaitingForMode. Shipped column subset is
        // the most commonly read seven fields; the full real-SQL-Server
        // shape has ~18 columns most apps never touch.
        var dmTranLocksColumns = new HeapColumn[]
        {
            new("resource_type", SqlType.NVarchar, 60, false),
            new("resource_database_id", SqlType.Int32, null, false),
            new("resource_description", SqlType.NVarchar, 256, true),
            new("resource_associated_entity_id", SqlType.BigInt, null, true),
            new("request_mode", SqlType.NVarchar, 60, false),
            new("request_status", SqlType.NVarchar, 60, false),
            new("request_session_id", SqlType.Int32, null, false),
        };
        var dmTranLocksView = new CatalogView("dm_tran_locks", dmTranLocksColumns, LockDmvs.EnumerateDmTranLocks);

        // sys.dm_os_waiting_tasks: one row per currently-waiting
        // connection. session_id / blocking_session_id are smallint
        // matching real SQL Server; wait_type is `LCK_M_<mode>` per
        // SQL Server's convention.
        var dmOsWaitingTasksColumns = new HeapColumn[]
        {
            new("session_id", SqlType.SmallInt, null, true),
            new("wait_type", SqlType.NVarchar, 60, true),
            new("resource_description", SqlType.NVarchar, 2000, true),
            new("blocking_session_id", SqlType.SmallInt, null, true),
        };
        var dmOsWaitingTasksView = new CatalogView("dm_os_waiting_tasks", dmOsWaitingTasksColumns, LockDmvs.EnumerateDmOsWaitingTasks);

        // sys.dm_tran_version_store: one row per finalized HistoricalVersion
        // across every per-table chain. Pending HVs (Xmax = PendingXmax)
        // are excluded. Real SQL Server's exact column order is preserved
        // so existing diagnostic queries port unchanged.
        var dmTranVersionStoreColumns = new HeapColumn[]
        {
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
        };
        var dmTranVersionStoreView = new CatalogView("dm_tran_version_store", dmTranVersionStoreColumns, VersionStoreDmvs.EnumerateDmTranVersionStore);

        // sys.dm_tran_version_store_space_usage: aggregate sizing per
        // database. The simulator approximates pages as ceil(bytes / 8192)
        // since HV payloads aren't backed by real pages.
        var dmTranVersionStoreSpaceUsageColumns = new HeapColumn[]
        {
            new("database_id", SqlType.Int32, null, false),
            new("reserved_page_count", SqlType.BigInt, null, false),
            new("reserved_space_kb", SqlType.BigInt, null, false),
        };
        var dmTranVersionStoreSpaceUsageView = new CatalogView("dm_tran_version_store_space_usage", dmTranVersionStoreSpaceUsageColumns, VersionStoreDmvs.EnumerateDmTranVersionStoreSpaceUsage);

        // sys.dm_tran_active_snapshot_database_transactions: one row per
        // active SI tx with an allocated snapshot Xid. RCSI per-statement
        // snapshots are not tracked here (matching real SQL Server).
        var dmTranActiveSnapshotDbTxColumns = new HeapColumn[]
        {
            new("transaction_id", SqlType.BigInt, null, false),
            new("transaction_sequence_num", SqlType.BigInt, null, false),
            new("commit_sequence_num", SqlType.BigInt, null, true),
            new("session_id", SqlType.Int32, null, false),
            new("is_snapshot", SqlType.Bit, null, false),
            new("first_snapshot_sequence_num", SqlType.BigInt, null, true),
            new("max_version_chain_traversed", SqlType.Int32, null, false),
            new("average_version_chain_traversed", SqlType.Float, null, false),
            new("elapsed_time_seconds", SqlType.BigInt, null, false),
        };
        var dmTranActiveSnapshotDbTxView = new CatalogView("dm_tran_active_snapshot_database_transactions", dmTranActiveSnapshotDbTxColumns, VersionStoreDmvs.EnumerateDmTranActiveSnapshotDatabaseTransactions);

        // sys.extended_properties: per-database user-defined annotations
        // attached to schemas / tables / columns / etc. via the
        // sp_addextendedproperty / sp_updateextendedproperty /
        // sp_dropextendedproperty trio. Real SQL Server's `value` column is
        // typed `sql_variant` — the simulator surfaces it as `nvarchar(MAX)`
        // since sql_variant isn't modeled; AW's 538 properties are all
        // nvarchar values so functional fidelity is preserved.
        var extendedPropertiesColumns = new HeapColumn[]
        {
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", SqlType.SystemName, 60, true),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("value", NVarcharSqlType.MaxForm, SqlType.MaxLengthSentinel, true),
        };
        var extendedPropertiesView = new CatalogView("extended_properties", extendedPropertiesColumns, EnumerateSysExtendedProperties);

        // sys.database_principals: probe-confirmed shipped subset of columns
        // (real SQL Server's full row is ~16 cols). The simulator's principal
        // model is a thin name + id + type triple; columns we don't track
        // (authentication_type, default_schema_name, default_language_name,
        // owning_principal_id, modify_date) are emitted as NULL.
        var databasePrincipalsColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, false),
            new("principal_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("default_schema_name", SqlType.SystemName, 128, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("owning_principal_id", SqlType.Int32, null, true),
            new("sid", SqlType.Varbinary, 85, true),
            new("is_fixed_role", SqlType.Bit, null, false),
            new("authentication_type", SqlType.TinyInt, null, true),
            new("authentication_type_desc", SqlType.NVarchar, 60, true),
        };
        var databasePrincipalsView = new CatalogView("database_principals", databasePrincipalsColumns, EnumerateSysDatabasePrincipals);

        // sys.database_permissions: probe-confirmed 8-col shipped subset.
        // Real SQL Server's row carries a few additional internal columns
        // (e.g. revert_audit_flag); the simulator surfaces the user-visible
        // set only.
        var charOne = SqlType.GetChar(1);
        var databasePermissionsColumns = new HeapColumn[]
        {
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", SqlType.NVarchar, 60, true),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("grantee_principal_id", SqlType.Int32, null, false),
            new("grantor_principal_id", SqlType.Int32, null, false),
            new("type", charTwo, 2, false),
            new("permission_name", NVarcharSqlType.Get(128), 128, true),
            new("state", charOne, 1, false),
            new("state_desc", SqlType.NVarchar, 60, true),
        };
        var databasePermissionsView = new CatalogView("database_permissions", databasePermissionsColumns, EnumerateSysDatabasePermissions);

        // sys.database_role_members: 2-col shipped subset (real SQL Server
        // surfaces just these two — no additional internal columns).
        var databaseRoleMembersColumns = new HeapColumn[]
        {
            new("role_principal_id", SqlType.Int32, null, false),
            new("member_principal_id", SqlType.Int32, null, false),
        };
        var databaseRoleMembersView = new CatalogView("database_role_members", databaseRoleMembersColumns, EnumerateSysDatabaseRoleMembers);

        // sys.fulltext_catalogs: per-database full-text catalog metadata.
        // Column subset matches Microsoft Learn's documented surface for
        // SQL Server 2022+ (the reference instance doesn't have full-text
        // installed, so probe-confirmation isn't available — column shapes
        // are taken from learn.microsoft.com/sql/relational-databases/system-catalog-views/sys-fulltext-catalogs-transact-sql).
        var fulltextCatalogsColumns = new HeapColumn[]
        {
            new("fulltext_catalog_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("path", SqlType.NVarchar, 260, true),
            new("is_default", SqlType.Bit, null, false),
            new("is_accent_sensitivity_on", SqlType.Bit, null, false),
            new("data_space_id", SqlType.Int32, null, true),
            new("file_id", SqlType.Int32, null, true),
            new("principal_id", SqlType.Int32, null, false),
            new("is_importing", SqlType.Bit, null, false),
        };
        var fulltextCatalogsView = new CatalogView("fulltext_catalogs", fulltextCatalogsColumns, EnumerateSysFullTextCatalogs);

        // sys.fulltext_indexes: per-database full-text indexes. One row per
        // indexed table. Column subset from Microsoft Learn.
        var fulltextIndexesColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("unique_index_id", SqlType.Int32, null, false),
            new("fulltext_catalog_id", SqlType.Int32, null, false),
            new("is_enabled", SqlType.Bit, null, false),
            new("change_tracking_state", charOne, 1, false),
            new("change_tracking_state_desc", SqlType.NVarchar, 60, true),
            new("has_crawl_completed", SqlType.Bit, null, false),
            new("crawl_type", charOne, 1, false),
            new("crawl_type_desc", SqlType.NVarchar, 60, true),
            new("crawl_start_date", SqlType.DateTime, null, true),
            new("crawl_end_date", SqlType.DateTime, null, true),
            new("stoplist_id", SqlType.Int32, null, true),
            new("data_space_id", SqlType.Int32, null, true),
            new("property_list_id", SqlType.Int32, null, true),
        };
        var fulltextIndexesView = new CatalogView("fulltext_indexes", fulltextIndexesColumns, EnumerateSysFullTextIndexes);

        // sys.fulltext_index_columns: one row per indexed column inside each
        // full-text index. column_id = 1-based storage ordinal of the
        // indexed column; type_column_id = nullable ordinal of the paired
        // doc-extension column for varbinary indexes.
        var fulltextIndexColumnsColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("column_id", SqlType.Int32, null, false),
            new("type_column_id", SqlType.Int32, null, true),
            new("language_id", SqlType.Int32, null, false),
            new("statistical_semantics", SqlType.Bit, null, false),
        };
        var fulltextIndexColumnsView = new CatalogView("fulltext_index_columns", fulltextIndexColumnsColumns, EnumerateSysFullTextIndexColumns);

        // sys.xml_schema_collections: probe-confirmed 6-col shipped subset
        // against SQL Server 2025 (2026-05-15). Real SQL Server's
        // principal_id column is nullable; the simulator's pre-seeded
        // collections leave it NULL.
        var xmlSchemaCollectionsColumns = new HeapColumn[]
        {
            new("xml_collection_id", SqlType.Int32, null, false),
            new("schema_id", SqlType.Int32, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("name", SqlType.SystemName, 128, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
        };
        var xmlSchemaCollectionsView = new CatalogView("xml_schema_collections", xmlSchemaCollectionsColumns, EnumerateSysXmlSchemaCollections);

        // sys.xml_indexes: probe-confirmed 9-col shipped subset (real SQL
        // Server's row is 26 cols including a long is_disabled / is_padded
        // / allow_row_locks tail of admin flags). The simulator surfaces
        // the AW-load-bearing core: identity, primary/secondary
        // discriminator, and the FOR-PATH/VALUE/PROPERTY classifier.
        var xmlIndexesColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", SqlType.NVarchar, 60, true),
            new("using_xml_index_id", SqlType.Int32, null, true),
            new("secondary_type", charOne, 1, true),
            new("secondary_type_desc", SqlType.NVarchar, 60, true),
            new("is_primary_key", SqlType.Bit, null, true),
        };
        var xmlIndexesView = new CatalogView("xml_indexes", xmlIndexesColumns, EnumerateSysXmlIndexes);

        // sys.spatial_indexes: probe-confirmed 23-col shape against SQL Server
        // 2025 (2026-05-15). Same shape as sys.indexes except (type, type_desc)
        // are fixed to (4, 'SPATIAL') and the four trailing spatial-specific
        // columns describe the tessellation. The simulator surfaces the
        // load-bearing identity + spatial classification subset; the
        // is_disabled / is_padded / allow_row_locks tail mirrors real values
        // (false / false / true / true) but isn't read by any application
        // path the loader cares about.
        var spatialIndexesColumns = new HeapColumn[]
        {
            new("object_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, true),
            new("index_id", SqlType.Int32, null, false),
            new("type", SqlType.TinyInt, null, false),
            new("type_desc", SqlType.NVarchar, 60, true),
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
            new("spatial_index_type_desc", SqlType.NVarchar, 60, true),
            new("tessellation_scheme", SqlType.NVarchar, 60, true),
            new("has_filter", SqlType.Bit, null, false),
            new("filter_definition", NVarcharSqlType.MaxForm, null, true),
            new("auto_created", SqlType.Bit, null, true),
        };
        var spatialIndexesView = new CatalogView("spatial_indexes", spatialIndexesColumns, EnumerateSysSpatialIndexes);

        // sys.spatial_index_tessellations: probe-confirmed 16-col shape
        // against SQL Server 2025 (2026-05-15). One row per spatial index
        // carrying the per-index bounding-box + 4-level grid detail.
        var spatialIndexTessellationsColumns = new HeapColumn[]
        {
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
        };
        var spatialIndexTessellationsView = new CatalogView("spatial_index_tessellations", spatialIndexTessellationsColumns, EnumerateSysSpatialIndexTessellations);

        // sys.spatial_reference_systems: real SQL Server seeds this view with
        // ~390 rows of authoritative SRID definitions (EPSG / ESRI). The
        // simulator surfaces an empty view — the column shape matches probe
        // and the catalog is reachable, but no SRID rows pre-populate. This
        // keeps applications that reference the view's schema from breaking
        // without the byte-tonnage of the WKT-laden seed data.
        var spatialReferenceSystemsColumns = new HeapColumn[]
        {
            new("spatial_reference_id", SqlType.Int32, null, true),
            new("authority_name", SqlType.NVarchar, 256, true),
            new("authorized_spatial_reference_id", SqlType.Int32, null, true),
            new("well_known_text", SqlType.NVarchar, 8000, true),
            new("unit_of_measure", SqlType.NVarchar, 256, true),
            new("unit_conversion_factor", SqlType.Float, null, true),
        };
        var spatialReferenceSystemsView = new CatalogView("spatial_reference_systems", spatialReferenceSystemsColumns, EnumerateSysSpatialReferenceSystems);

        // sys.databases: real SQL Server emits ~95 columns; the simulator
        // exposes the load-bearing subset that tooling actually queries (name,
        // database_id, compatibility_level, collation_name, snapshot-isolation
        // state, and the common boolean toggles). Single row for the current
        // database; multi-database support would extend this enumeration.
        var databasesColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, false),
            new("database_id", SqlType.SmallInt, null, false),
            new("compatibility_level", SqlType.TinyInt, null, true),
            new("collation_name", SqlType.SystemName, 128, true),
            new("snapshot_isolation_state", SqlType.TinyInt, null, false),
            new("snapshot_isolation_state_desc", SqlType.NVarchar, 60, true),
            new("is_read_committed_snapshot_on", SqlType.Bit, null, false),
            new("state", SqlType.TinyInt, null, false),
            new("state_desc", SqlType.NVarchar, 60, true),
        };
        var databasesView = new CatalogView("databases", databasesColumns, EnumerateSysDatabases);

        // sys.fn_helpcollations() — table-valued metadata function listing the
        // collations the simulator recognizes. Real SQL Server emits ~5540
        // rows; the simulator emits the whitelist defined in Collation.Recognized
        // (currently 2). Each row carries the canonical name + a human
        // description, matching real SQL Server's column shape.
        var fnHelpCollationsColumns = new HeapColumn[]
        {
            new("name", SqlType.SystemName, 128, true),
            new("description", SqlType.NVarchar, 1000, true),
        };
        var fnHelpCollationsView = new CatalogView("fn_helpcollations", fnHelpCollationsColumns, EnumerateFnHelpCollations);

        return new Dictionary<string, CatalogView>(Collation.Default)
        {
            ["sys.databases"] = databasesView,
            ["sys.fn_helpcollations"] = fnHelpCollationsView,
            ["sys.schemas"] = schemasView,
            ["sys.tables"] = tablesView,
            ["sys.objects"] = objectsView,
            ["sys.columns"] = columnsView,
            ["sys.parameters"] = parametersView,
            ["sys.views"] = viewsCatalogView,
            ["sys.procedures"] = proceduresView,
            ["sys.types"] = typesView,
            ["sys.table_types"] = tableTypesView,
            ["sys.sequences"] = sequencesView,
            ["sys.triggers"] = triggersView,
            ["sys.foreign_keys"] = foreignKeysView,
            ["sys.foreign_key_columns"] = foreignKeyColumnsView,
            ["sys.check_constraints"] = checkConstraintsView,
            ["sys.key_constraints"] = keyConstraintsView,
            ["sys.default_constraints"] = defaultConstraintsView,
            ["sys.indexes"] = indexesView,
            ["sys.index_columns"] = indexColumnsView,
            ["sys.dm_tran_locks"] = dmTranLocksView,
            ["sys.dm_os_waiting_tasks"] = dmOsWaitingTasksView,
            ["sys.dm_tran_version_store"] = dmTranVersionStoreView,
            ["sys.dm_tran_version_store_space_usage"] = dmTranVersionStoreSpaceUsageView,
            ["sys.dm_tran_active_snapshot_database_transactions"] = dmTranActiveSnapshotDbTxView,
            ["sys.extended_properties"] = extendedPropertiesView,
            ["sys.database_principals"] = databasePrincipalsView,
            ["sys.database_permissions"] = databasePermissionsView,
            ["sys.database_role_members"] = databaseRoleMembersView,
            ["sys.fulltext_catalogs"] = fulltextCatalogsView,
            ["sys.fulltext_indexes"] = fulltextIndexesView,
            ["sys.fulltext_index_columns"] = fulltextIndexColumnsView,
            ["sys.xml_schema_collections"] = xmlSchemaCollectionsView,
            ["sys.xml_indexes"] = xmlIndexesView,
            ["sys.spatial_indexes"] = spatialIndexesView,
            ["sys.spatial_index_tessellations"] = spatialIndexTessellationsView,
            ["sys.spatial_reference_systems"] = spatialReferenceSystemsView,
            ["INFORMATION_SCHEMA.TABLES"] = isTablesView,
            ["INFORMATION_SCHEMA.COLUMNS"] = isColumnsView,
            ["INFORMATION_SCHEMA.SCHEMATA"] = isSchemataView,
            ["INFORMATION_SCHEMA.VIEWS"] = isViewsView,
            ["INFORMATION_SCHEMA.ROUTINES"] = isRoutinesView,
            ["INFORMATION_SCHEMA.PARAMETERS"] = isParametersView,
            ["INFORMATION_SCHEMA.DOMAINS"] = isDomainsView,
        };
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
                kvp.Value.IsNull ? SqlValue.Null(NVarcharSqlType.MaxForm) : kvp.Value.CoerceTo(NVarcharSqlType.MaxForm),
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
    /// Rows for <c>sys.sequences</c>: one per registered sequence object,
    /// schema-ordered. <c>cache_size</c> is always NULL (the simulator
    /// doesn't model the batched-allocation cache; real SQL Server returns
    /// NULL when no explicit <c>CACHE n</c> was given anyway). Type-id
    /// columns derive from the declared type via <see cref="SystypesRowData"/>.
    /// </summary>
    /// <summary>
    /// Rows for <c>sys.triggers</c>: one row per <see cref="Trigger"/> in
    /// every schema. <c>parent_class</c> is always 1 (DML triggers attached
    /// to tables — DDL/server triggers aren't modeled);
    /// <c>is_not_for_replication</c> is always 0 (the simulator
    /// parse-and-ignores the WITH clause). Probe-confirmed columns; modify
    /// date mirrors create date because <c>ALTER TRIGGER</c> replaces the
    /// instance wholesale.
    /// </summary>
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
        var fkType = SqlValue.FromChar(CharSqlType.Get(2), "F ");
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
        var ckType = SqlValue.FromChar(CharSqlType.Get(2), "C ");
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
                            if (Collation.Default.Equals(table.Columns[i].Name, inlineCol))
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
        var charTwo = CharSqlType.Get(2);
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
        var dfType = SqlValue.FromChar(CharSqlType.Get(2), "D ");
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
                "G" => "GRANT",
                "W" => "GRANT_WITH_GRANT_OPTION",
                "D" => "DENY",
                "R" => "REVOKE",
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
        var charOneType = CharSqlType.Get(1);
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
        var charOneType = CharSqlType.Get(1);
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
                var primaryIds = new Dictionary<string, int>(Collation.Default);
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
        var nullDesc = SqlValue.Null(NVarcharSqlType.MaxForm);
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
    /// Rows for <c>sys.databases</c>. One row per <see cref="Database"/>
    /// hosted by the connected <see cref="Simulation"/>; matches real SQL
    /// Server's "instance-scoped catalog view" semantic. State is always
    /// <c>0 / ONLINE</c>; snapshot-isolation columns track each
    /// <see cref="Database"/>'s live <c>AllowSnapshotIsolation</c> /
    /// <c>ReadCommittedSnapshot</c> flags.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateSysDatabases(Parser.BatchContext batch, Database database)
    {
        // Stable ordering: by name. Real SQL Server orders sys.databases by
        // database_id; the simulator doesn't allocate per-database ids yet,
        // so name-ordering is the next-best deterministic choice.
        var databases = batch.Connection.Simulation.Databases.Values
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        short id = 1;
        foreach (var db in databases)
        {
            var snapshotState = (byte)(db.AllowSnapshotIsolation ? 1 : 0);
            yield return [
                SqlValue.FromSystemName(db.Name),
                SqlValue.FromInt16(id++),
                SqlValue.FromByte((byte)db.CompatibilityLevel),
                SqlValue.FromSystemName(db.CollationName),
                SqlValue.FromByte(snapshotState),
                SqlValue.FromNVarchar(db.AllowSnapshotIsolation ? "ON" : "OFF"),
                SqlValue.FromBoolean(db.ReadCommittedSnapshot),
                SqlValue.FromByte(0),
                SqlValue.FromNVarchar("ONLINE"),
            ];
        }
    }

    /// <summary>
    /// Rows for <c>sys.fn_helpcollations()</c>. Emits one row per entry in
    /// <see cref="Collation.Recognized"/> — the simulator's whitelist of
    /// metadata-accepted collation names. Real SQL Server returns ~5540
    /// rows here; the simulator's shorter list is honest about which
    /// collation names round-trip through <see cref="Database.CollationName"/>
    /// / <see cref="HeapColumn.Collation"/>.
    /// </summary>
    private static IEnumerable<SqlValue[]> EnumerateFnHelpCollations(Parser.BatchContext batch, Database database)
    {
        _ = batch;
        foreach (var entry in Collation.Recognized.OrderBy(e => e.Key, StringComparer.Ordinal))
            yield return [SqlValue.FromSystemName(entry.Key), SqlValue.FromNVarchar(entry.Value)];
    }

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
                ];
            }
        }
    }

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
                        numericRadix switch { 10 => radix10, 2 => radix2, _ => nullInt16 },
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
                    0 => (short)((col.MaxLength ?? 1) * 2),
                    -1 => -1,
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
