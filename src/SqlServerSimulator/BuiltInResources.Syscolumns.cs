using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

internal static partial class BuiltInResources
{
    /// <summary>
    /// The legacy <c>syscolumns</c> compatibility view: one row per column of
    /// every table, view, table-valued function and table type, and per
    /// parameter of every procedure and function — a scalar function's return
    /// value as a nameless row 0. Built over the rows <c>sys.columns</c> and
    /// <c>sys.parameters</c> project, adding the SQL Server 2000 storage
    /// encodings (<c>type</c> / <c>usertype</c> / <c>status</c> /
    /// <c>typestat</c> / <c>offset</c>) and the collation id and TDS bytes.
    /// Every rule probed 2026-09-26 against SQL Server 2025.
    /// </summary>
    private static void RegisterSyscolumns(Dictionary<string, CatalogView> views)
    {
        var columnsView = views["sys.columns"];
        var parametersView = views["sys.parameters"];
        HeapColumn[] columns =
        [
            new("name", SqlType.SystemName, 128, true),
            new("id", SqlType.Int32, null, false),
            new("xtype", SqlType.TinyInt, null, false),
            new("typestat", SqlType.TinyInt, null, true),
            new("xusertype", SqlType.SmallInt, null, true),
            new("length", SqlType.SmallInt, null, false),
            new("xprec", SqlType.TinyInt, null, false),
            new("xscale", SqlType.TinyInt, null, false),
            new("colid", SqlType.SmallInt, null, true),
            new("xoffset", SqlType.SmallInt, null, true),
            new("bitpos", SqlType.TinyInt, null, true),
            new("reserved", SqlType.TinyInt, null, true),
            new("colstat", SqlType.SmallInt, null, true),
            new("cdefault", SqlType.Int32, null, false),
            new("domain", SqlType.Int32, null, false),
            new("number", SqlType.SmallInt, null, true),
            new("colorder", SqlType.SmallInt, null, true),
            new("autoval", SqlType.Varbinary, 8000, true),
            new("offset", SqlType.SmallInt, null, true),
            new("collationid", SqlType.Int32, null, true),
            new("language", SqlType.Int32, null, true),
            new("status", SqlType.TinyInt, null, true),
            new("type", SqlType.TinyInt, null, false),
            new("usertype", SqlType.SmallInt, null, true),
            new("printfmt", SqlType.Varchar, 255, true),
            new("prec", SqlType.SmallInt, null, true),
            new("scale", SqlType.Int32, null, true),
            new("iscomputed", SqlType.Int32, null, true),
            new("isoutparam", SqlType.Int32, null, true),
            new("isnullable", SqlType.Int32, null, true),
            new("collation", SqlType.SystemName, 128, true),
            new("tdscollation", BinarySqlType.Get(5), 5, true),
        ];
        var view = new CatalogView("syscolumns", columns, (batch, database) => EnumerateSyscolumns(batch, database, columnsView, parametersView));
        views["syscolumns"] = view;
        views["sys.syscolumns"] = view;
    }

    /// <summary>One column or parameter, as the <c>syscolumns</c> row builder reads it.</summary>
    private readonly struct SyscolumnsEntry(
        string name,
        int objectId,
        byte systemTypeId,
        int userTypeId,
        short maxLength,
        byte precision,
        byte scale,
        short colid,
        short number,
        bool nullable,
        bool identity,
        bool computed,
        bool rowGuidCol,
        int defaultObjectId,
        string? collation,
        bool isParameter,
        bool isOutput)
    {
        public readonly string Name = name;
        public readonly int ObjectId = objectId;
        public readonly byte SystemTypeId = systemTypeId;
        public readonly int UserTypeId = userTypeId;
        public readonly short MaxLength = maxLength;
        public readonly byte Precision = precision;
        public readonly byte Scale = scale;
        public readonly short Colid = colid;
        public readonly short Number = number;
        public readonly bool Nullable = nullable;
        public readonly bool Identity = identity;
        public readonly bool Computed = computed;
        public readonly bool RowGuidCol = rowGuidCol;
        public readonly int DefaultObjectId = defaultObjectId;
        public readonly string? Collation = collation;
        public readonly bool IsParameter = isParameter;
        public readonly bool IsOutput = isOutput;
    }

    private static IEnumerable<SqlValue[]> EnumerateSyscolumns(Parser.BatchContext batch, Database database, CatalogView columnsView, CatalogView parametersView)
    {
        var entries = new List<SyscolumnsEntry>();
        var c = OrdinalsOf(columnsView, "object_id", "name", "column_id", "system_type_id", "user_type_id", "max_length", "precision", "scale",
            "is_nullable", "is_identity", "is_computed", "is_rowguidcol", "default_object_id", "collation_name");
        foreach (var row in columnsView.RowGenerator(batch, database))
        {
            entries.Add(new(
                row[c[1]].AsString, row[c[0]].AsInt32, row[c[3]].AsByte, row[c[4]].AsInt32, row[c[5]].AsInt16, row[c[6]].AsByte, row[c[7]].AsByte,
                (short)row[c[2]].AsInt32, 0, row[c[8]].AsBoolean, row[c[9]].AsBoolean, row[c[10]].AsBoolean, row[c[11]].AsBoolean,
                row[c[12]].AsInt32, row[c[13]].IsNull ? null : row[c[13]].AsString, isParameter: false, isOutput: false));
        }

        var p = OrdinalsOf(parametersView, "object_id", "name", "parameter_id", "system_type_id", "user_type_id", "max_length", "precision", "scale", "is_output");
        foreach (var row in parametersView.RowGenerator(batch, database))
        {
            var objectId = row[p[0]].AsInt32;
            var systemTypeId = row[p[3]].AsByte;
            // A scalar function's parameters and return value are its own
            // group 0; every other module's parameters are group 1.
            var number = (short)(ObjectProperty.FindObject(database, objectId) is UserDefinedFunction { ObjectTypeCode: "FN" or "FS" } ? 0 : 1);
            entries.Add(new(
                row[p[1]].AsString, objectId, systemTypeId, row[p[4]].AsInt32, row[p[5]].AsInt16, row[p[6]].AsByte, row[p[7]].AsByte,
                (short)row[p[2]].AsInt32, number, nullable: true, identity: false, computed: false, rowGuidCol: false, defaultObjectId: 0,
                IsStringTypeId(systemTypeId) ? database.CollationName : null, isParameter: true, isOutput: row[p[8]].AsBoolean));
        }

        // Only a table has a row layout; a view's, function's or table
        // type's columns read offset 0.
        var offsets = new Dictionary<(int ObjectId, short Colid), short>();
        var visited = new HashSet<int>();
        foreach (var entry in entries)
        {
            if (!entry.IsParameter && visited.Add(entry.ObjectId) && ObjectProperty.FindObject(database, entry.ObjectId) is HeapTable)
                AssignLegacyOffsets(entries, entry.ObjectId, offsets);
        }

        foreach (var entry in entries)
            yield return SyscolumnsRow(entry, entry.IsParameter ? (short)0 : offsets.GetValueOrDefault((entry.ObjectId, entry.Colid)));
    }

    private static int[] OrdinalsOf(CatalogView view, params ReadOnlySpan<string> names)
    {
        var ordinals = new int[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            for (var j = 0; j < view.Columns.Length; j++)
            {
                if (view.Columns[j].Name == names[i])
                    ordinals[i] = j;
            }
        }
        return ordinals;
    }

    /// <summary>
    /// SQL Server 2000's row layout as <c>syscolumns.offset</c> reports it: a
    /// fixed-width NOT NULL column (and every <c>bit</c>, eight to a byte)
    /// packs from offset 2 in column order, every other column counts
    /// -1, -2, … in the same order, and a computed column reads 0.
    /// </summary>
    private static void AssignLegacyOffsets(List<SyscolumnsEntry> entries, int objectId, Dictionary<(int ObjectId, short Colid), short> offsets)
    {
        short next = 2;
        short variable = 0;
        var bitsLeft = 0;
        foreach (var entry in entries)
        {
            if (entry.ObjectId != objectId || entry.IsParameter || entry.Computed)
                continue;
            if (entry.SystemTypeId == 104)
            {
                if (bitsLeft == 0)
                {
                    offsets[(objectId, entry.Colid)] = next;
                    next++;
                    bitsLeft = 8;
                }
                else
                {
                    offsets[(objectId, entry.Colid)] = (short)(next - 1);
                }
                bitsLeft--;
            }
            else if (!entry.Nullable && IsFixedWidthTypeId(entry.SystemTypeId))
            {
                offsets[(objectId, entry.Colid)] = next;
                next += entry.MaxLength;
            }
            else
            {
                offsets[(objectId, entry.Colid)] = --variable;
            }
        }
    }

    private static SqlValue[] SyscolumnsRow(SyscolumnsEntry entry, short legacyOffset)
    {
        var systemTypeId = entry.SystemTypeId;
        var isString = IsStringTypeId(systemTypeId);
        var paddable = systemTypeId is 165 or 167 or 173 or 175 or 231 or 239;
        var nullSmall = SqlValue.Null(SqlType.SmallInt);
        var collation = isString ? entry.Collation : null;
        var collationMetrics = default(Collation.CollationMetrics);
        var hasCollation = collation is not null && Collation.TryGetMetrics(collation, out collationMetrics);

        byte status;
        if (entry.IsParameter)
        {
            status = (byte)(0x08 | (entry.IsOutput ? 0x40 : 0));
        }
        else
        {
            status = (byte)((entry.Nullable ? 0x08 : 0)
                | (paddable ? 0x10 : 0)
                | (entry.Nullable && systemTypeId is 173 or 175 ? 0x20 : 0)
                | (entry.Computed ? 0x40 : 0)
                | (entry.Identity ? 0x80 : 0));
        }
        var typestat = entry.IsParameter ? (byte)0 : (byte)((entry.Nullable ? 0 : 1) | (paddable ? 2 : 0));
        var colstat = (short)((entry.Identity ? 1 : 0) | (entry.RowGuidCol ? 2 : 0) | (entry.Computed ? 4 : 0));

        var prec = systemTypeId switch
        {
            34 or 35 or 99 => nullSmall,
            165 or 167 or 173 or 175 => SqlValue.FromInt16(entry.MaxLength),
            231 or 239 => SqlValue.FromInt16(entry.MaxLength < 0 ? entry.MaxLength : (short)(entry.MaxLength / 2)),
            189 or 240 or 241 => SqlValue.FromInt16(entry.MaxLength),
            _ => SqlValue.FromInt16(entry.Precision),
        };
        var scale = systemTypeId is 40 or 41 or 42 or 43 or 48 or 52 or 56 or 58 or 60 or 61 or 106 or 108 or 122 or 127
            ? SqlValue.FromInt32(entry.Scale)
            : SqlValue.Null(SqlType.Int32);

        return
        [
            SqlValue.FromSystemName(entry.Name),
            SqlValue.FromInt32(entry.ObjectId),
            SqlValue.FromByte(systemTypeId),
            SqlValue.FromByte(typestat),
            SqlValue.FromInt16((short)entry.UserTypeId),
            SqlValue.FromInt16(entry.MaxLength),
            SqlValue.FromByte(entry.Precision),
            SqlValue.FromByte(entry.Scale),
            SqlValue.FromInt16(entry.Colid),
            SqlValue.FromInt16(0),
            SqlValue.FromByte(0),
            SqlValue.FromByte(0),
            SqlValue.FromInt16(colstat),
            SqlValue.FromInt32(entry.DefaultObjectId),
            SqlValue.FromInt32(0),
            SqlValue.FromInt16(entry.Number),
            SqlValue.FromInt16(entry.Colid),
            SqlValue.Null(SqlType.Varbinary),
            SqlValue.FromInt16(legacyOffset),
            SqlValue.FromInt32(hasCollation ? collationMetrics.CollationId : 0),
            SqlValue.FromInt32(0),
            SqlValue.FromByte(status),
            SqlValue.FromByte(LegacyStorageType(systemTypeId, entry.Nullable)),
            SqlValue.FromInt16(LegacyUserType(entry.UserTypeId)),
            SqlValue.Null(SqlType.Varchar),
            prec,
            scale,
            SqlValue.FromInt32(entry.Computed ? 1 : 0),
            SqlValue.FromInt32(entry.IsOutput ? 1 : 0),
            SqlValue.FromInt32(entry.Nullable ? 1 : 0),
            collation is null ? SqlValue.Null(SqlType.SystemName) : SqlValue.FromSystemName(collation),
            SqlValue.FromBinary(BinarySqlType.Get(5), hasCollation
                ? Network.TdsCollationCodec.For(Collation.Get(collation!)).PropertyBytes(collation!.Contains("_BIN2", StringComparison.OrdinalIgnoreCase))
                : new byte[5]),
        ];
    }

    private static bool IsStringTypeId(byte systemTypeId) => systemTypeId is 35 or 99 or 167 or 175 or 231 or 239;

    private static bool IsFixedWidthTypeId(byte systemTypeId) =>
        systemTypeId is 36 or 40 or 41 or 42 or 43 or 48 or 52 or 56 or 58 or 59 or 60 or 61 or 62 or 106 or 108 or 122 or 127 or 173 or 175 or 189 or 239;

    /// <summary>
    /// <c>syscolumns.type</c>: SQL Server 2000's storage type, which gives
    /// most fixed-width types a separate nullable code.
    /// </summary>
    private static byte LegacyStorageType(byte systemTypeId, bool nullable) => systemTypeId switch
    {
        34 => 34,
        35 or 99 => 35,
        36 => 37,
        48 or 52 or 56 => nullable ? (byte)38 : systemTypeId,
        58 or 61 => nullable ? (byte)111 : systemTypeId,
        59 or 62 => nullable ? (byte)109 : systemTypeId,
        60 or 122 => nullable ? (byte)110 : systemTypeId,
        98 or 167 or 231 => 39,
        104 => 50,
        106 => nullable ? (byte)106 : (byte)55,
        108 or 127 => nullable ? (byte)108 : (byte)63,
        165 => 37,
        173 => nullable ? (byte)37 : (byte)45,
        175 or 239 => nullable ? (byte)39 : (byte)47,
        189 => 45,
        _ => 0,
    };

    /// <summary>
    /// <c>syscolumns.usertype</c>: SQL Server 2000's user-type number, an alias
    /// type's own id, and 0 for the types it didn't have.
    /// </summary>
    private static short LegacyUserType(int userTypeId) => userTypeId switch
    {
        34 => 20,
        35 => 19,
        48 => 5,
        52 => 6,
        56 => 7,
        58 => 22,
        59 => 23,
        60 => 11,
        61 => 12,
        62 => 8,
        104 => 16,
        106 => 24,
        108 => 10,
        122 => 21,
        165 => 4,
        167 => 2,
        173 => 3,
        175 => 1,
        189 => 80,
        256 => 18,
        > 256 => (short)userTypeId,
        _ => 0,
    };
}
