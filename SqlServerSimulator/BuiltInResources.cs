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

            systypes.Heap.Insert(RowEncoder.EncodeRow(systypes.Schema, values));
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
        var schemasView = new CatalogView("schemas", schemasColumns, batch =>
            batch.CurrentDatabase.Schemas.Values.OrderBy(s => s.SchemaId).Select(s => new SqlValue[]
            {
                SqlValue.FromSystemName(s.Name),
                SqlValue.FromInt32(s.SchemaId),
                SqlValue.Null(SqlType.Int32),
            }));

        // sys.tables: object_id / name / schema_id / type / type_desc /
        // create_date / modify_date / is_ms_shipped. Real SQL Server has many
        // more columns; the shipped subset covers the dominant query shapes.
        // type is char(2) with trailing space ('U ') — probe-confirmed.
        var charTwo = CharSqlType.Get(2);
        var tableType = SqlValue.FromChar(charTwo, "U ");
        var tableTypeDesc = SqlValue.FromNVarchar("USER_TABLE");
        var notMsShipped = SqlValue.FromBoolean(false);
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
        };
        var tablesView = new CatalogView("tables", tablesColumns, batch =>
            batch.CurrentDatabase.Schemas.Values
                .SelectMany(s => s.HeapTables.Values)
                .OrderBy(t => t.ObjectId)
                .Select(t => new SqlValue[]
                {
                    SqlValue.FromInt32(t.ObjectId),
                    SqlValue.FromSystemName(t.Name),
                    SqlValue.FromInt32(t.SchemaId),
                    tableType,
                    tableTypeDesc,
                    SqlValue.FromDateTime(t.CreateDate),
                    SqlValue.FromDateTime(t.ModifyDate),
                    notMsShipped,
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
        var objectsView = new CatalogView("objects", objectsColumns, batch =>
            EnumerateObjects(batch, charTwo, pkType, pkTypeDesc, uqType, uqTypeDesc, checkType, checkTypeDesc, zeroParent, notMsShipped));

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
        var columnsView = new CatalogView("columns", columnsColumns, batch =>
            EnumerateColumns(batch, defaultCollation, nullCollation));

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
        var isTablesView = new CatalogView("TABLES", isTablesColumns, batch =>
            EnumerateInformationSchemaTables(batch, baseTable, viewTableType));

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
        var isColumnsView = new CatalogView("COLUMNS", isColumnsColumns, batch =>
            EnumerateInformationSchemaColumns(batch, defaultCollation, unicodeCs, isoCs, radix10, radix2));

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
        var isSchemataView = new CatalogView("SCHEMATA", isSchemataColumns, batch =>
            batch.CurrentDatabase.Schemas.Values.OrderBy(s => s.SchemaId).Select(s => new SqlValue[]
            {
                SqlValue.FromSystemName(batch.CurrentDatabase.Name),
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
        var proceduresView = new CatalogView("procedures", proceduresColumns, batch =>
            EnumerateProcedures(batch, charTwo, notMsShipped));

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
        var isRoutinesView = new CatalogView("ROUTINES", isRoutinesColumns, batch =>
            EnumerateInformationSchemaRoutines(batch, procedureRoutineType, functionRoutineType, tableDataType));

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
        var isParametersView = new CatalogView("PARAMETERS", isParametersColumns, batch =>
            EnumerateInformationSchemaParameters(batch, modeIn, modeInOut));

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
        var isViewsView = new CatalogView("VIEWS", isViewsColumns, batch =>
            EnumerateInformationSchemaViews(batch, checkOptionNone, checkOptionCascade, isUpdatableNo));

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
        var triggersView = new CatalogView("triggers", triggersColumns, batch =>
            EnumerateSysTriggers(batch, charTwo, parentClassObjectColumn, parentClassObjectColumnDesc));

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
        var isDomainsView = new CatalogView("DOMAINS", isDomainsColumns, batch =>
            EnumerateInformationSchemaDomains(batch, tableTypeDataType));

        return new Dictionary<string, CatalogView>(Collation.Default)
        {
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
    private static IEnumerable<SqlValue[]> EnumerateSysTypes(Parser.BatchContext batch)
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
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
    }

    private static IEnumerable<SqlValue[]> EnumerateSysTableTypes(Parser.BatchContext batch)
    {
        var trueBit = SqlValue.FromBoolean(true);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
    private static IEnumerable<SqlValue[]> EnumerateSysTriggers(
        Parser.BatchContext batch,
        SqlType charTwo,
        SqlValue parentClassObjectColumn,
        SqlValue parentClassObjectColumnDesc)
    {
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        // 'TR' / 'SQL_TRIGGER' — matches Trigger.ObjectTypeCode /
        // Trigger.ObjectTypeDescription, kept as local constants here to
        // avoid one SqlValue allocation per row.
        var triggerType = SqlValue.FromChar(charTwo, "TR");
        var triggerTypeDesc = SqlValue.FromNVarchar("SQL_TRIGGER");
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
    }

    private static IEnumerable<SqlValue[]> EnumerateSysSequences(Parser.BatchContext batch)
    {
        var nullCache = SqlValue.Null(SqlType.Int32);
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaDomains(Parser.BatchContext batch, SqlValue tableTypeDataType)
    {
        var catalog = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
        SqlType charTwo,
        SqlValue notMsShipped)
    {
        // 'P ' / 'SQL_STORED_PROCEDURE' — matches Procedure.ObjectTypeCode /
        // Procedure.ObjectTypeDescription, kept as local constants here to
        // avoid one SqlValue allocation per row.
        var procType = SqlValue.FromChar(charTwo, "P ");
        var procTypeDesc = SqlValue.FromNVarchar("SQL_STORED_PROCEDURE");
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
        SqlValue procedureRoutineType,
        SqlValue functionRoutineType,
        SqlValue tableDataType)
    {
        var catalog = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        var nullDataType = SqlValue.Null(SqlType.SystemName);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
        SqlValue modeIn,
        SqlValue modeInOut)
    {
        var catalog = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        var nullInt = SqlValue.Null(SqlType.Int32);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
    private static IEnumerable<SqlValue[]> EnumerateViews(Parser.BatchContext batch)
    {
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
        SqlValue checkOptionNone,
        SqlValue checkOptionCascade,
        SqlValue isUpdatableNo)
    {
        var catalog = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
    private static IEnumerable<SqlValue[]> EnumerateParameters(Parser.BatchContext batch)
    {
        var emptyName = SqlValue.FromSystemName("");
        var trueBit = SqlValue.FromBoolean(true);
        var falseBit = SqlValue.FromBoolean(false);
        var zeroByte = SqlValue.FromByte(0);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
        SqlValue defaultCollation,
        SqlValue nullCollation)
    {
        var falseBit = SqlValue.FromBoolean(false);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
                        col.Type.Category == SqlTypeCategory.String ? defaultCollation : nullCollation,
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
                        col.Type.Category == SqlTypeCategory.String ? defaultCollation : nullCollation,
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
                        col.Type.Category == SqlTypeCategory.String ? defaultCollation : nullCollation,
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
                        col.Type.Category == SqlTypeCategory.String ? defaultCollation : nullCollation,
                    ];
                }
            }
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaTables(Parser.BatchContext batch, SqlValue baseTable, SqlValue viewTableType)
    {
        var catalog = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
        SqlValue defaultCollation,
        SqlValue unicodeCs,
        SqlValue isoCs,
        SqlValue radix10,
        SqlValue radix2)
    {
        var catalog = SqlValue.FromSystemName(batch.CurrentDatabase.Name);
        var nullString = SqlValue.Null(SqlType.NVarchar);
        var nullInt32 = SqlValue.Null(SqlType.Int32);
        var nullInt16 = SqlValue.Null(SqlType.SmallInt);
        var nullByte = SqlValue.Null(SqlType.TinyInt);
        var nullSysName = SqlValue.Null(SqlType.SystemName);
        var yesNullable = SqlValue.FromVarchar("YES");
        var noNullable = SqlValue.FromVarchar("NO");
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
                    var collation = col.Type.Category == SqlTypeCategory.String ? defaultCollation : nullSysName;
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
        SqlType charTwo,
        SqlValue pkType, SqlValue pkTypeDesc,
        SqlValue uqType, SqlValue uqTypeDesc,
        SqlValue checkType, SqlValue checkTypeDesc,
        SqlValue zeroParent, SqlValue notMsShipped)
    {
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
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
            }
        }
    }
}
