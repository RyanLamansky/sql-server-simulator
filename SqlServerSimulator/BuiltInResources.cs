using SqlServerSimulator.Storage;
using System.Globalization;

namespace SqlServerSimulator;

internal static class BuiltInResources
{
    private static readonly object?[][] SystypesRowData =
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

        // sys.objects: a superset of sys.tables that also emits one row per
        // PK / UQ / CHECK constraint, with parent_object_id linking back to
        // the owning table. type_desc strings probe-confirmed: 'U ' /
        // USER_TABLE, 'PK' / PRIMARY_KEY_CONSTRAINT, 'UQ' / UNIQUE_CONSTRAINT,
        // 'C ' / CHECK_CONSTRAINT.
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
            EnumerateObjects(batch, tableType, tableTypeDesc, pkType, pkTypeDesc, uqType, uqTypeDesc, checkType, checkTypeDesc, zeroParent, notMsShipped));

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
        var isTablesColumns = new HeapColumn[]
        {
            new("TABLE_CATALOG", SqlType.SystemName, 128, true),
            new("TABLE_SCHEMA", SqlType.SystemName, 128, true),
            new("TABLE_NAME", SqlType.SystemName, 128, false),
            new("TABLE_TYPE", SqlType.Varchar, 10, true),
        };
        var isTablesView = new CatalogView("TABLES", isTablesColumns, batch =>
            EnumerateInformationSchemaTables(batch, baseTable));

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

        return new Dictionary<string, CatalogView>(Collation.Default)
        {
            ["sys.schemas"] = schemasView,
            ["sys.tables"] = tablesView,
            ["sys.objects"] = objectsView,
            ["sys.columns"] = columnsView,
            ["INFORMATION_SCHEMA.TABLES"] = isTablesView,
            ["INFORMATION_SCHEMA.COLUMNS"] = isColumnsView,
            ["INFORMATION_SCHEMA.SCHEMATA"] = isSchemataView,
        };
    }

    private static IEnumerable<SqlValue[]> EnumerateColumns(
        Parser.BatchContext batch,
        SqlValue defaultCollation,
        SqlValue nullCollation)
    {
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
        }
    }

    private static IEnumerable<SqlValue[]> EnumerateInformationSchemaTables(Parser.BatchContext batch, SqlValue baseTable)
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
        SqlValue tableType, SqlValue tableTypeDesc,
        SqlValue pkType, SqlValue pkTypeDesc,
        SqlValue uqType, SqlValue uqTypeDesc,
        SqlValue checkType, SqlValue checkTypeDesc,
        SqlValue zeroParent, SqlValue notMsShipped)
    {
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var t in schema.HeapTables.Values.OrderBy(t => t.ObjectId))
            {
                var schemaId = SqlValue.FromInt32(t.SchemaId);
                var createDate = SqlValue.FromDateTime(t.CreateDate);
                var modifyDate = SqlValue.FromDateTime(t.ModifyDate);
                yield return [
                    SqlValue.FromInt32(t.ObjectId),
                    SqlValue.FromSystemName(t.Name),
                    schemaId,
                    zeroParent,
                    tableType,
                    tableTypeDesc,
                    createDate,
                    modifyDate,
                    notMsShipped,
                ];
                var parent = SqlValue.FromInt32(t.ObjectId);
                foreach (var key in t.KeyConstraints)
                {
                    yield return [
                        SqlValue.FromInt32(key.ObjectId),
                        SqlValue.FromSystemName(key.Name),
                        schemaId,
                        parent,
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
                        schemaId,
                        parent,
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
