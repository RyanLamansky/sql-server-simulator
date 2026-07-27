using System.Data.SqlTypes;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Clr;

/// <summary>
/// Maps between the storage layer's <see cref="SqlValue"/> / <see cref="SqlType"/>
/// pair and the <see cref="System.Data.SqlTypes"/> structs a SQLCLR routine
/// declares.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is <strong>strict and one-to-one</strong>, matching real SQL
/// Server: probe-confirmed that <c>varchar</c> does not bind to
/// <see cref="SqlString"/> (only <c>nvarchar</c> / <c>nchar</c> do) and that
/// <c>bit</c> / <c>bigint</c> do not bind to <see cref="SqlInt32"/> — each
/// mismatch raises Msg 6551 (return) or Msg 6552 (parameter) at CREATE.
/// </para>
/// <para>
/// Only the <see cref="System.Data.SqlTypes"/> family is bound. The plain-CLR
/// form real SQL Server also accepts (<c>string</c>, <c>int?</c>,
/// <see cref="SqlChars"/>, <see cref="SqlBytes"/>) is not modeled — a routine
/// declaring one binds nothing and fails with the same Msg 6551 / 6552 as any
/// other mismatch.
/// </para>
/// </remarks>
internal static class ClrTypeMarshaller
{
    /// <summary>
    /// Whether <paramref name="sqlType"/> is the T-SQL type that binds to
    /// <paramref name="clrType"/>.
    /// </summary>
    public static bool Matches(SqlType sqlType, Type clrType) => sqlType switch
    {
        NVarcharSqlType => clrType == typeof(SqlString),
        Int32SqlType => clrType == typeof(SqlInt32),
        BigIntSqlType => clrType == typeof(SqlInt64),
        SmallIntSqlType => clrType == typeof(SqlInt16),
        TinyIntSqlType => clrType == typeof(SqlByte),
        BitSqlType => clrType == typeof(SqlBoolean),
        FloatSqlType => clrType == typeof(SqlDouble),
        RealSqlType => clrType == typeof(SqlSingle),
        DecimalSqlType => clrType == typeof(SqlDecimal),
        MoneySqlType or SmallMoneySqlType => clrType == typeof(SqlMoney),
        DateTimeSqlType or SmallDateTimeSqlType => clrType == typeof(SqlDateTime),
        VarbinarySqlType or BinarySqlType => clrType == typeof(SqlBinary),
        UniqueIdentifierSqlType => clrType == typeof(SqlGuid),
        XmlSqlType => clrType == typeof(SqlXml),
        _ => false,
    };

    /// <summary>
    /// Converts an argument value into the boxed
    /// <see cref="System.Data.SqlTypes"/> struct the bound method expects.
    /// NULL maps to the type's own <c>Null</c> sentinel, so the routine sees
    /// <c>IsNull</c> rather than a CLR <see langword="null"/>.
    /// </summary>
    public static object ToClr(SqlValue value, Type clrType) => value.IsNull
        ? NullOf(clrType)
        : clrType == typeof(SqlString) ? new SqlString(value.AsString)
            : clrType == typeof(SqlInt32) ? new SqlInt32(value.AsInt32)
            : clrType == typeof(SqlInt64) ? new SqlInt64(value.AsInt64)
            : clrType == typeof(SqlInt16) ? new SqlInt16(value.AsInt16)
            : clrType == typeof(SqlByte) ? new SqlByte(value.AsByte)
            : clrType == typeof(SqlBoolean) ? new SqlBoolean(value.AsBoolean)
            : clrType == typeof(SqlDouble) ? new SqlDouble(value.AsDouble)
            : clrType == typeof(SqlSingle) ? new SqlSingle(value.AsSingle)
            : clrType == typeof(SqlDecimal) ? new SqlDecimal(value.AsDecimal)
            : clrType == typeof(SqlMoney) ? new SqlMoney(value.AsDecimal)
            : clrType == typeof(SqlDateTime) ? new SqlDateTime(value.Type is SmallDateTimeSqlType ? value.AsSmallDateTime : value.AsDateTime)
            : clrType == typeof(SqlBinary) ? new SqlBinary(value.AsBytes)
            : clrType == typeof(SqlGuid) ? new SqlGuid(value.AsGuid)
            : clrType == typeof(SqlXml) ? new SqlXml(new MemoryStream(System.Text.Encoding.Unicode.GetBytes(value.AsString)))
            : throw new NotSupportedException($"CLR type '{clrType.FullName}' is not a modeled SQLCLR parameter type.");

    /// <summary>
    /// Converts the method's return value back into a <see cref="SqlValue"/> of
    /// the routine's declared <c>RETURNS</c> type.
    /// </summary>
    public static SqlValue FromClr(object? result, SqlType returnType) => result is null or INullable { IsNull: true }
        ? SqlValue.Null(returnType)
        : result switch
        {
            SqlString s => SqlValue.FromString(returnType, s.Value),
            SqlInt32 i => SqlValue.FromInt32(i.Value),
            SqlInt64 i => SqlValue.FromInt64(i.Value),
            SqlInt16 i => SqlValue.FromInt16(i.Value),
            SqlByte b => SqlValue.FromByte(b.Value),
            SqlBoolean b => SqlValue.FromBoolean(b.Value),
            SqlDouble d => SqlValue.FromDouble(d.Value),
            SqlSingle f => SqlValue.FromSingle(f.Value),
            SqlDecimal d => SqlValue.FromDecimal(returnType, d.Value),
            SqlMoney m => SqlValue.FromMoney(returnType, m.Value),
            SqlDateTime d => returnType is SmallDateTimeSqlType ? SqlValue.FromSmallDateTime(d.Value) : SqlValue.FromDateTime(d.Value),
            SqlBinary b => SqlValue.FromVarbinary(b.Value),
            SqlGuid g => SqlValue.FromGuid(g.Value),
            SqlXml x => SqlValue.FromXml(x.Value),
            _ => throw new NotSupportedException($"CLR type '{result.GetType().FullName}' is not a modeled SQLCLR return type."),
        };

    /// <summary>The <c>Null</c> sentinel for a
    /// <see cref="System.Data.SqlTypes"/> struct, boxed.</summary>
    private static object NullOf(Type clrType) =>
        clrType == typeof(SqlString) ? SqlString.Null
        : clrType == typeof(SqlInt32) ? SqlInt32.Null
        : clrType == typeof(SqlInt64) ? SqlInt64.Null
        : clrType == typeof(SqlInt16) ? SqlInt16.Null
        : clrType == typeof(SqlByte) ? SqlByte.Null
        : clrType == typeof(SqlBoolean) ? SqlBoolean.Null
        : clrType == typeof(SqlDouble) ? SqlDouble.Null
        : clrType == typeof(SqlSingle) ? SqlSingle.Null
        : clrType == typeof(SqlDecimal) ? SqlDecimal.Null
        : clrType == typeof(SqlMoney) ? SqlMoney.Null
        : clrType == typeof(SqlDateTime) ? SqlDateTime.Null
        : clrType == typeof(SqlBinary) ? SqlBinary.Null
        : clrType == typeof(SqlGuid) ? SqlGuid.Null
        : clrType == typeof(SqlXml) ? SqlXml.Null
        : throw new NotSupportedException($"CLR type '{clrType.FullName}' is not a modeled SQLCLR parameter type.");
}
