using System.Data;
using System.Data.SqlTypes;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// The table <see cref="SimulatedDbDataReader.GetSchemaTable"/> answers: the
/// 31 columns SqlClient's <c>SqlDataReader.GetSchemaTable</c> has, each cell
/// derived the way SqlClient derives it from the metadata a result carries —
/// the types and nullability COLMETADATA describes, its column-character
/// flags (identity, computed, read-only), the hidden columns and browse
/// metadata of a <c>SET NO_BROWSETABLE</c> result. Verified cell by cell
/// against SqlClient reading the same results over the TDS endpoint.
/// </summary>
internal static class ResultSchemaTable
{
    // DataTable columns typed System.Type are what SqlClient's schema table
    // carries (DataType, ProviderSpecificDataType), and a CLR type resolves
    // by name as SqlClient resolves it; the trimmer can't see through either,
    // and nothing here reflects over the Type values.
#pragma warning disable IL2111, IL2057
    public static DataTable Build(SimulatedQueryResult result, string databaseName)
    {
        var table = new DataTable("SchemaTable") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        var columns = table.Columns;
        _ = columns.Add("ColumnName", typeof(string));
        _ = columns.Add("ColumnOrdinal", typeof(int));
        _ = columns.Add("ColumnSize", typeof(int));
        _ = columns.Add("NumericPrecision", typeof(short));
        _ = columns.Add("NumericScale", typeof(short));
        _ = columns.Add("IsUnique", typeof(bool));
        _ = columns.Add("IsKey", typeof(bool));
        _ = columns.Add("BaseServerName", typeof(string));
        _ = columns.Add("BaseCatalogName", typeof(string));
        _ = columns.Add("BaseColumnName", typeof(string));
        _ = columns.Add("BaseSchemaName", typeof(string));
        _ = columns.Add("BaseTableName", typeof(string));
        _ = columns.Add("DataType", typeof(Type));
        _ = columns.Add("AllowDBNull", typeof(bool));
        _ = columns.Add("ProviderType", typeof(int));
        _ = columns.Add("IsAliased", typeof(bool));
        _ = columns.Add("IsExpression", typeof(bool));
        _ = columns.Add("IsIdentity", typeof(bool));
        _ = columns.Add("IsAutoIncrement", typeof(bool));
        _ = columns.Add("IsRowVersion", typeof(bool));
        _ = columns.Add("IsHidden", typeof(bool));
        _ = columns.Add("IsLong", typeof(bool));
        _ = columns.Add("IsReadOnly", typeof(bool));
        _ = columns.Add("ProviderSpecificDataType", typeof(Type));
        _ = columns.Add("DataTypeName", typeof(string));
        _ = columns.Add("XmlSchemaCollectionDatabase", typeof(string));
        _ = columns.Add("XmlSchemaCollectionOwningSchema", typeof(string));
        _ = columns.Add("XmlSchemaCollectionName", typeof(string));
        _ = columns.Add("UdtAssemblyQualifiedName", typeof(string));
        _ = columns.Add("NonVersionedProviderType", typeof(int));
        _ = columns.Add("IsColumnSet", typeof(bool));

        var browse = result.Browse;
        for (var i = 0; i < result.Schema.Length; i++)
        {
            var type = result.Schema[i];
            var info = Describe(type, databaseName);
            // A CLR type's DataType is whatever its assembly-qualified name
            // resolves to in this process — null when Microsoft.SqlServer.Types
            // isn't loadable — as SqlClient resolves it.
            var udtType = info.UdtName is { } udtName ? Type.GetType(udtName, throwOnError: false) : null;
            // A plain column is updatability-unknown (0x08); every other
            // character — identity, computed, read-only — reads read-only.
            var character = result.ColumnWireFlags is { } flags ? flags[i] : (byte)0x08;
            var identity = (character & 0x10) != 0;
            var row = table.NewRow();
            row["ColumnName"] = result.ColumnNames[i];
            row["ColumnOrdinal"] = i;
            row["ColumnSize"] = info.Size;
            row["NumericPrecision"] = info.Precision;
            row["NumericScale"] = info.Scale;
            row["IsUnique"] = type is RowVersionSqlType;
            // An unnamed column has no base name either.
            row["BaseColumnName"] = result.ColumnNames[i].Length == 0 ? DBNull.Value : result.ColumnNames[i];
            row["DataType"] = (object?)(info.DataType ?? udtType) ?? DBNull.Value;
            row["AllowDBNull"] = result.ColumnNullability is not { } nullability || nullability[i];
            row["ProviderType"] = (int)info.ProviderType;
            row["IsIdentity"] = identity;
            row["IsAutoIncrement"] = identity;
            row["IsRowVersion"] = type is RowVersionSqlType;
            row["IsLong"] = info.IsLong;
            row["IsReadOnly"] = (character & 0x08) == 0;
            row["ProviderSpecificDataType"] = (object?)(info.ProviderSpecificType ?? udtType) ?? DBNull.Value;
            row["DataTypeName"] = info.Name;
            row["UdtAssemblyQualifiedName"] = (object?)info.UdtName ?? DBNull.Value;
            row["NonVersionedProviderType"] = (int)info.ProviderType;
            row["IsColumnSet"] = false;
            if (browse is not null)
            {
                var (tableNumber, status, baseName) = browse.Columns[i];
                row["IsKey"] = (status & 0x08) != 0;
                row["IsHidden"] = (status & 0x10) != 0;
                row["IsExpression"] = (status & 0x04) != 0;
                row["IsAliased"] = (status & 0x20) != 0;
                if (baseName is not null)
                    row["BaseColumnName"] = baseName;
                if (tableNumber > 0)
                {
                    var parts = browse.Tables[tableNumber - 1];
                    row["BaseTableName"] = parts[^1];
                    if (parts.Length > 1)
                        row["BaseSchemaName"] = parts[^2];
                }
            }
            table.Rows.Add(row);
        }
        return table;
    }
#pragma warning restore IL2111, IL2057

    private readonly struct TypeInfo(int size, short precision, short scale, Type? dataType, SqlDbType providerType, Type? providerSpecificType, string name, bool isLong = false, string? udtName = null)
    {
        public readonly int Size = size;
        public readonly short Precision = precision;
        public readonly short Scale = scale;
        public readonly Type? DataType = dataType;
        public readonly SqlDbType ProviderType = providerType;
        public readonly Type? ProviderSpecificType = providerSpecificType;
        public readonly string Name = name;
        public readonly bool IsLong = isLong;
        public readonly string? UdtName = udtName;
    }

    // What SqlClient reports per type: the size it reads from TYPE_INFO
    // (characters for a national string), the precision and scale where the
    // type has them (255 where it doesn't), and its CLR and SqlTypes types.
    private static TypeInfo Describe(SqlType type, string databaseName) => type switch
    {
        TinyIntSqlType => new(1, 3, 255, typeof(byte), SqlDbType.TinyInt, typeof(SqlByte), "tinyint"),
        SmallIntSqlType => new(2, 5, 255, typeof(short), SqlDbType.SmallInt, typeof(SqlInt16), "smallint"),
        Int32SqlType => new(4, 10, 255, typeof(int), SqlDbType.Int, typeof(SqlInt32), "int"),
        BigIntSqlType => new(8, 19, 255, typeof(long), SqlDbType.BigInt, typeof(SqlInt64), "bigint"),
        BitSqlType => new(1, 255, 255, typeof(bool), SqlDbType.Bit, typeof(SqlBoolean), "bit"),
        DecimalSqlType d => new(17, d.precision, d.scale, typeof(decimal), SqlDbType.Decimal, typeof(SqlDecimal), "decimal"),
        FloatSqlType => new(8, 15, 255, typeof(double), SqlDbType.Float, typeof(SqlDouble), "float"),
        RealSqlType => new(4, 7, 255, typeof(float), SqlDbType.Real, typeof(SqlSingle), "real"),
        MoneySqlType => new(8, 19, 255, typeof(decimal), SqlDbType.Money, typeof(SqlMoney), "money"),
        SmallMoneySqlType => new(4, 10, 255, typeof(decimal), SqlDbType.SmallMoney, typeof(SqlMoney), "smallmoney"),
        DateTimeSqlType => new(8, 23, 3, typeof(DateTime), SqlDbType.DateTime, typeof(SqlDateTime), "datetime"),
        SmallDateTimeSqlType => new(4, 16, 0, typeof(DateTime), SqlDbType.SmallDateTime, typeof(SqlDateTime), "smalldatetime"),
        DateSqlType => new(3, 255, 255, typeof(DateTime), SqlDbType.Date, typeof(DateTime), "date"),
        TimeSqlType t => new(ScaledSize(t.precision, 3), 255, (short)t.precision, typeof(TimeSpan), SqlDbType.Time, typeof(TimeSpan), "time"),
        DateTime2SqlType d2 => new(ScaledSize(d2.precision, 6), 255, (short)d2.precision, typeof(DateTime), SqlDbType.DateTime2, typeof(DateTime), "datetime2"),
        DateTimeOffsetSqlType dto => new(ScaledSize(dto.precision, 8), 255, (short)dto.precision, typeof(DateTimeOffset), SqlDbType.DateTimeOffset, typeof(DateTimeOffset), "datetimeoffset"),
        CharSqlType c => new(c.length, 255, 255, typeof(string), SqlDbType.Char, typeof(SqlString), "char"),
        VarcharSqlType v => new(v.length == SqlType.MaxLengthSentinel ? int.MaxValue : v.length, 255, 255, typeof(string), SqlDbType.VarChar, typeof(SqlString), "varchar", v.length == SqlType.MaxLengthSentinel),
        NCharSqlType nc => new(nc.length, 255, 255, typeof(string), SqlDbType.NChar, typeof(SqlString), "nchar"),
        NVarcharSqlType nv => new(nv.length == SqlType.MaxLengthSentinel ? int.MaxValue : nv.length, 255, 255, typeof(string), SqlDbType.NVarChar, typeof(SqlString), "nvarchar", nv.length == SqlType.MaxLengthSentinel),
        SystemNameSqlType => new(128, 255, 255, typeof(string), SqlDbType.NVarChar, typeof(SqlString), "nvarchar"),
        TextSqlType => new(int.MaxValue, 255, 255, typeof(string), SqlDbType.Text, typeof(SqlString), "text", isLong: true),
        NTextSqlType => new(int.MaxValue / 2, 255, 255, typeof(string), SqlDbType.NText, typeof(SqlString), "ntext", isLong: true),
        ImageSqlType => new(int.MaxValue, 255, 255, typeof(byte[]), SqlDbType.Image, typeof(SqlBinary), "image", isLong: true),
        RowVersionSqlType => new(8, 255, 255, typeof(byte[]), SqlDbType.Timestamp, typeof(SqlBinary), "timestamp"),
        BinarySqlType b => new(b.length, 255, 255, typeof(byte[]), SqlDbType.Binary, typeof(SqlBinary), "binary"),
        VarbinarySqlType vb => new(vb.length == SqlType.MaxLengthSentinel ? int.MaxValue : vb.length, 255, 255, typeof(byte[]), SqlDbType.VarBinary, typeof(SqlBinary), "varbinary", vb.length == SqlType.MaxLengthSentinel),
        UniqueIdentifierSqlType => new(16, 255, 255, typeof(Guid), SqlDbType.UniqueIdentifier, typeof(SqlGuid), "uniqueidentifier"),
        XmlSqlType => new(int.MaxValue, 255, 255, typeof(string), SqlDbType.Xml, typeof(SqlXml), "xml", isLong: true),
        SqlVariantSqlType => new(8009, 255, 255, typeof(object), SqlDbType.Variant, typeof(object), "sql_variant"),
        HierarchyIdSqlType => new(892, 255, 255, null, SqlDbType.Udt, null, $"{databaseName}.sys.hierarchyid", udtName: Network.TdsTypeCodec.HierarchyIdAssemblyQualifiedName),
        SpatialSqlType spatial => new(-1, 255, 255, null, SqlDbType.Udt, null, $"{databaseName}.sys.{spatial.SqlServerName}", udtName: Network.TdsTypeCodec.SpatialAssemblyQualifiedName(spatial)),
        _ => new(-1, 255, 255, type.ClrType, SqlDbType.Variant, null, type.SqlServerName),
    };

    // The byte length a fractional-second type's TYPE_INFO declares: its
    // base width at scale 0-2, one byte more at 3-4, two more at 5-7.
    private static int ScaledSize(int scale, int baseWidth) => baseWidth + (scale <= 2 ? 0 : scale <= 4 ? 1 : 2);
}
