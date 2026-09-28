using System.Data;
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
        NVarcharSqlType or NCharSqlType => clrType == typeof(SqlString),
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
        ClrUdtSqlType udt => clrType == udt.Udt.Type,
        _ => false,
    };

    /// <summary>
    /// Converts an argument value into the boxed
    /// <see cref="System.Data.SqlTypes"/> struct the bound method expects.
    /// NULL maps to the type's own <c>Null</c> sentinel, so the routine sees
    /// <c>IsNull</c> rather than a CLR <see langword="null"/>.
    /// </summary>
    public static object? ToClr(SqlValue value, Type clrType) => value.Type is ClrUdtSqlType udt
        ? udt.Udt.ToClr(value)
        : value.IsNull
        ? NullOf(clrType)
        : clrType == typeof(SqlString) ? new SqlString(value.AsString)
            : clrType == typeof(SqlInt32) ? new SqlInt32(value.AsInt32)
            : clrType == typeof(SqlInt64) ? new SqlInt64(value.AsInt64)
            : clrType == typeof(SqlInt16) ? new SqlInt16(value.AsInt16)
            : clrType == typeof(SqlByte) ? new SqlByte(value.AsByte)
            : clrType == typeof(SqlBoolean) ? new SqlBoolean(value.AsBoolean)
            : clrType == typeof(SqlDouble) ? new SqlDouble(value.AsDouble)
            : clrType == typeof(SqlSingle) ? new SqlSingle(value.AsSingle)
            : clrType == typeof(SqlDecimal) ? ToSqlDecimal(value.AsDecimal38)
            : clrType == typeof(SqlMoney) ? new SqlMoney(value.AsMoney)
            : clrType == typeof(SqlDateTime) ? new SqlDateTime(value.Type is SmallDateTimeSqlType ? value.AsSmallDateTime : value.AsDateTime)
            : clrType == typeof(SqlBinary) ? new SqlBinary(value.AsBytes)
            : clrType == typeof(SqlGuid) ? new SqlGuid(value.AsGuid)
            : clrType == typeof(SqlXml) ? new SqlXml(new MemoryStream(System.Text.Encoding.Unicode.GetBytes(value.AsString)))
            : throw new NotSupportedException($"CLR type '{clrType.FullName}' is not a modeled SQLCLR parameter type.");

    /// <summary>
    /// Converts the method's return value back into a <see cref="SqlValue"/> of
    /// the routine's declared <c>RETURNS</c> type.
    /// </summary>
    public static SqlValue FromClr(object? result, SqlType returnType) => returnType is ClrUdtSqlType udt
        ? udt.Udt.FromClr(result)
        : result is null or INullable { IsNull: true }
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
            SqlDecimal d => SqlValue.FromDecimal(returnType, FromSqlDecimal(d)),
            SqlMoney m => SqlValue.FromMoney(returnType, m.Value),
            SqlDateTime d => returnType is SmallDateTimeSqlType ? SqlValue.FromSmallDateTime(d.Value) : SqlValue.FromDateTime(d.Value),
            SqlBinary b => SqlValue.FromVarbinary(b.Value),
            SqlGuid g => SqlValue.FromGuid(g.Value),
            SqlXml x => SqlValue.FromXml(x.Value),
            _ => throw new NotSupportedException($"CLR type '{result.GetType().FullName}' is not a modeled SQLCLR return type."),
        };

    /// <summary>
    /// The exact-numeric value as a <see cref="SqlDecimal"/>, all 38 digits of
    /// it — the CLR type carries the same width the server does, so this
    /// crossing is lossless where the .NET <see cref="decimal"/> one is not.
    /// </summary>
    private static SqlDecimal ToSqlDecimal(in Decimal38 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(bytes, value.Magnitude);
        var precision = (byte)Math.Clamp(Math.Max(value.SignificantDigits(), value.Scale), 1, Decimal38.MaxPrecision);
        return new SqlDecimal(
            precision,
            value.Scale,
            !value.IsNegative,
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]));
    }

    /// <summary>The inverse of <see cref="ToSqlDecimal"/>, reading the routine's returned value at full width.</summary>
    private static Decimal38 FromSqlDecimal(SqlDecimal value)
    {
        var data = value.Data;
        var magnitude = ((UInt128)(uint)data[3] << 96)
            | ((UInt128)(uint)data[2] << 64)
            | ((UInt128)(uint)data[1] << 32)
            | (uint)data[0];
        return Decimal38.FromParts(magnitude, !value.IsPositive, value.Scale);
    }

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

    /// <summary>
    /// The declared width, in characters, that <paramref name="value"/>
    /// overflows as the return value or output parameter of a CLR routine
    /// declaring <paramref name="declared"/>, or <see langword="null"/> when it
    /// fits. Real refuses such a value with a TruncationException rather than
    /// cutting it (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public static int? OverflowedWidth(SqlValue value, SqlType declared) =>
        !value.IsNull && declared is NVarcharSqlType { length: > 0 } nvarchar && value.AsString.Length > nvarchar.length
            ? nvarchar.length
            : null;

    /// <summary>
    /// The T-SQL type of a column a CLR procedure declared through
    /// <c>SqlMetaData</c>, character columns taking
    /// <paramref name="collation"/> (the database's).
    /// </summary>
    public static SqlType ColumnType(SqlDbType dbType, long maxLength, byte precision, byte scale, Collation collation)
    {
        var length = maxLength < 0 ? SqlType.MaxLengthSentinel : (int)maxLength;
        return dbType switch
        {
            SqlDbType.BigInt => SqlType.BigInt,
            SqlDbType.Binary => BinarySqlType.Get(length),
            SqlDbType.Bit => SqlType.Bit,
            SqlDbType.Char => CharSqlType.Get(length, collation, Coercibility.CoercibleDefault),
            SqlDbType.Date => SqlType.Date,
            SqlDbType.DateTime => SqlType.DateTime,
            SqlDbType.DateTime2 => SqlType.GetDateTime2(scale),
            SqlDbType.DateTimeOffset => SqlType.GetDateTimeOffset(scale),
            SqlDbType.Decimal => DecimalSqlType.Get(precision, scale),
            SqlDbType.Float => SqlType.Float,
            SqlDbType.Image => SqlType.Image,
            SqlDbType.Int => SqlType.Int32,
            SqlDbType.Money => SqlType.Money,
            SqlDbType.NChar => NCharSqlType.Get(length, collation, Coercibility.CoercibleDefault),
            SqlDbType.NText => SqlType.NText,
            SqlDbType.NVarChar => NVarcharSqlType.Get(length, collation, Coercibility.CoercibleDefault),
            SqlDbType.Real => SqlType.Real,
            SqlDbType.SmallDateTime => SqlType.SmallDateTime,
            SqlDbType.SmallInt => SqlType.SmallInt,
            SqlDbType.SmallMoney => SqlType.SmallMoney,
            SqlDbType.Text => SqlType.Text,
            SqlDbType.Time => SqlType.GetTime(scale),
            SqlDbType.Timestamp => SqlType.RowVersion,
            SqlDbType.TinyInt => SqlType.TinyInt,
            SqlDbType.UniqueIdentifier => SqlType.UniqueIdentifier,
            SqlDbType.VarBinary => VarbinarySqlType.Get(length),
            SqlDbType.VarChar => VarcharSqlType.Get(length, collation, Coercibility.CoercibleDefault),
            SqlDbType.Variant => SqlType.SqlVariant,
            SqlDbType.Xml => SqlType.Xml,
            _ => throw new NotSupportedException($"A SqlMetaData column of type {dbType} is not modeled."),
        };
    }

    /// <summary>
    /// Converts a value a routine stored in a <c>SqlDataRecord</c> — a plain
    /// CLR value or a <see cref="System.Data.SqlTypes"/> struct — to
    /// <paramref name="target"/>, the type of the column it fills. A decimal
    /// with more fractional digits than its column drops the extra ones
    /// rather than rounding (probed 2026-09-28 against SQL Server 2025: 1.5
    /// sent as <c>decimal(18, 0)</c> reads 1).
    /// </summary>
    public static SqlValue FromRecordValue(object? value, SqlType target)
    {
        if (value is null or DBNull or INullable { IsNull: true })
            return SqlValue.Null(target);

        if (target is DecimalSqlType { scale: var targetScale } && value is decimal or SqlDecimal)
        {
            var exact = value is decimal plain ? new SqlDecimal(plain) : (SqlDecimal)value;
            if (exact.Scale > targetScale)
                value = SqlDecimal.AdjustScale(exact, targetScale - exact.Scale, fRound: false);
        }

        var natural = value switch
        {
            string text => SqlValue.FromNVarchar(text),
            char character => SqlValue.FromNVarchar(character.ToString()),
            int number => SqlValue.FromInt32(number),
            long number => SqlValue.FromInt64(number),
            short number => SqlValue.FromInt16(number),
            byte number => SqlValue.FromByte(number),
            bool flag => SqlValue.FromBoolean(flag),
            double number => SqlValue.FromDouble(number),
            float number => SqlValue.FromSingle(number),
            decimal number => SqlValue.FromDecimal(DecimalSqlType.Get(Decimal38.MaxPrecision, number.Scale), number),
            DateTime moment => SqlValue.FromDateTime2(SqlType.GetDateTime2(7), moment),
            DateTimeOffset moment => SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(7), moment),
            TimeSpan time => SqlValue.FromTime(SqlType.GetTime(7), time),
            Guid guid => SqlValue.FromGuid(guid),
            byte[] bytes => SqlValue.FromVarbinary(bytes),
            SqlString text => SqlValue.FromNVarchar(text.Value),
            SqlDecimal number => FromClr(number, DecimalSqlType.Get(Math.Max(number.Precision, (byte)1), number.Scale)),
            SqlMoney money => SqlValue.FromMoney(SqlType.Money, money.Value),
            SqlBinary bytes => SqlValue.FromVarbinary(bytes.Value),
            SqlBytes bytes => SqlValue.FromVarbinary(bytes.Value),
            SqlChars chars => SqlValue.FromNVarchar(new string(chars.Value)),
            INullable => FromClr(value, SqlType.SqlVariant),
            _ => throw new InvalidCastException($"A value of type {value.GetType().FullName} cannot be sent in a SqlDataRecord."),
        };
        return natural.CoerceTo(target);
    }

    /// <summary>
    /// A CLR type as the server's messages spell it: the C# keyword for a
    /// primitive, the simple name otherwise.
    /// </summary>
    public static string DisplayName(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean => "bool",
        TypeCode.Byte => "byte",
        TypeCode.Char => "char",
        TypeCode.Decimal => "decimal",
        TypeCode.Double => "double",
        TypeCode.Int16 => "short",
        TypeCode.Int32 => "int",
        TypeCode.Int64 => "long",
        TypeCode.SByte => "sbyte",
        TypeCode.Single => "float",
        TypeCode.String => "string",
        TypeCode.UInt16 => "ushort",
        TypeCode.UInt32 => "uint",
        TypeCode.UInt64 => "ulong",
        _ => type == typeof(object) ? "object" : type.Name,
    };
}
