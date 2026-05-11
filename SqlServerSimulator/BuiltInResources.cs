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
    /// Registers the <c>sys.&lt;view&gt;</c> virtual tables. Each row generator
    /// projects from live <see cref="Database"/> / <see cref="Schema"/> /
    /// <see cref="HeapTable"/> metadata at iteration time, so changes made
    /// earlier in the same batch (CREATE TABLE, CREATE SCHEMA, DROP TABLE)
    /// appear immediately in the next read. The shipped views are
    /// <c>sys.schemas</c> / <c>sys.tables</c> / <c>sys.objects</c>; columns
    /// match the load-bearing subset of real SQL Server's catalog (apps that
    /// <c>SELECT *</c> see fewer columns — documented quirk).
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

        return new Dictionary<string, CatalogView>(Collation.Default)
        {
            [schemasView.Name] = schemasView,
            [tablesView.Name] = tablesView,
            [objectsView.Name] = objectsView,
        };
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
