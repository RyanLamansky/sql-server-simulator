using System.Buffers.Binary;
using System.Data;
using System.Numerics;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>
/// Translates result-set schemas and values into the TDS wire encoding:
/// COLMETADATA TYPE_INFO per column and the per-cell value bytes of ROW
/// tokens. Nullable columns use the N-variant wire form (INTN, BITN, FLTN,
/// MONEYN, DATETIMN, DECIMALN, GUIDN); a NOT NULL column of a fixed-width
/// family carries the FIXEDLENTYPE token instead (INT4, BIT, MONEY, DATETIME,
/// …) with its ROW value written raw, matching real byte-for-byte.
/// </summary>
internal static class TdsTypeCodec
{
    private static readonly DateTime Epoch1900 = new(1900, 1, 1);

    // Real SQL Server 2025 sends these exact placeholder bytes as the 16-byte
    // text pointer and 8-byte timestamp of every text / ntext / image result
    // value — ASCII "dummy textptr\0\0\0" and "dummyTS\0". SqlClient ignores
    // their content (they exist only so the deprecated in-band value form is
    // well-shaped), so mirroring the literal bytes matches real exactly.
    // Probe-captured against SQL Server 2025 through a cleartext tee proxy
    // (Encrypt=False leaves post-login tokens in the clear), 2026-07-19.
    private static readonly byte[] LegacyLobTextPointer = "dummy textptr\0\0\0"u8.ToArray();

    private static readonly byte[] LegacyLobTimestamp = "dummyTS\0"u8.ToArray();

    /// <summary>
    /// <paramref name="columnNullability"/> feeds each column's fNullable
    /// flag (first flags byte 0x09 nullable / 0x08 not) and selects the type
    /// token for fixed-width families — NOT NULL emits the FIXEDLENTYPE token
    /// (INT4 / BIT / MONEY / DATETIME / …), nullable the N-variant; null claims
    /// every column nullable. NOT NULL flags are load-bearing for DacFx bacpac
    /// export — its BCP data files drop the per-value length prefix on
    /// fixed-width columns whose result metadata says NOT NULL, and the
    /// bacpac loader decodes per the model.xml declaration, so a false
    /// nullable claim here misaligns every exported row.
    /// </summary>
    public static void WriteColMetadata(TdsTokenWriter writer, SqlType[] schema, string[] columnNames, bool[]? columnNullability, bool[]? columnReportsNumeric = null)
    {
        writer.EnterComposite();
        writer.WriteByte(Tds.TokenColMetadata);
        writer.WriteUInt16(checked((ushort)schema.Length));
        for (var i = 0; i < schema.Length; i++)
        {
            var type = schema[i];
            writer.WriteUInt32(type is RowVersionSqlType ? 0x50u : 0u);
            var notNull = columnNullability is not null && !columnNullability[i];
            writer.WriteByte(notNull ? (byte)0x08 : (byte)0x09);
            writer.WriteByte(0);
            var reportsNumeric = columnReportsNumeric is not null && columnReportsNumeric[i];
            WriteTypeInfo(writer, type, notNull, reportsNumeric);
            writer.WriteBVarchar(columnNames[i]);
        }

        writer.LeaveComposite();
    }

    public static void WriteRow(TdsTokenWriter writer, SqlType[] schema, RowCursor cursor, bool[]? columnNullability)
    {
        writer.EnterComposite();
        writer.WriteByte(Tds.TokenRow);
        for (var i = 0; i < schema.Length; i++)
        {
            if (columnNullability is not null && !columnNullability[i] && IsRawWhenNotNull(schema[i]))
                WriteRawFixedValue(writer, schema[i], cursor[i]);
            else
                WriteValue(writer, schema[i], cursor[i]);
        }

        writer.LeaveComposite();
    }

    /// <summary>
    /// The fixed-width wire families whose NOT NULL COLMETADATA carries the
    /// FIXEDLENTYPE token (INT1 / INT2 / INT4 / INT8 / BIT / FLT4 / FLT8 /
    /// MONEY4 / MONEY / DATETIM4 / DATETIME) rather than the nullable N-variant,
    /// and whose ROW value is therefore written raw at the declared width — real
    /// sends exactly this form (probe-captured against SQL Server 2025), and it
    /// keeps COLMETADATA and ROW consistent (a FIXEDLENTYPE value carries no
    /// length prefix). The other BYTELEN families (date / time / datetime2 /
    /// datetimeoffset, DECIMALN, GUIDN) and every USHORTLEN / PLP form have no
    /// FIXEDLENTYPE token, so they keep the N-variant plus length prefix
    /// regardless of the nullability flag. Must stay aligned with the fNullable
    /// flag <see cref="WriteColMetadata"/> emits and the token
    /// <see cref="TryFixedLenToken"/> selects — all three read the same
    /// per-column nullability array.
    /// </summary>
    private static bool IsRawWhenNotNull(SqlType type) => type
        is TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType
        or BitSqlType or RealSqlType or FloatSqlType
        or SmallMoneySqlType or MoneySqlType
        or DateTimeSqlType or SmallDateTimeSqlType;

    /// <summary>
    /// Maps an <see cref="IsRawWhenNotNull"/> family to its FIXEDLENTYPE
    /// COLMETADATA token — the byte real emits for a NOT NULL column of that
    /// type (probe-captured against SQL Server 2025, 2026-07-22). A FIXEDLENTYPE
    /// token is a single byte with no trailing max-length byte. Returns false
    /// for every other type (those keep their nullable N-variant token
    /// unconditionally).
    /// </summary>
    private static bool TryFixedLenToken(SqlType type, out byte token)
    {
        token = type switch
        {
            TinyIntSqlType => 0x30,        // INT1
            SmallIntSqlType => 0x34,       // INT2
            Int32SqlType => 0x38,          // INT4
            BigIntSqlType => 0x7F,         // INT8
            BitSqlType => 0x32,            // BIT
            RealSqlType => 0x3B,           // FLT4
            FloatSqlType => 0x3E,          // FLT8
            SmallMoneySqlType => 0x7A,     // MONEY4
            MoneySqlType => 0x3C,          // MONEY
            SmallDateTimeSqlType => 0x3A,  // DATETIM4
            DateTimeSqlType => 0x3D,       // DATETIME
            _ => 0,
        };

        return token != 0;
    }

    /// <summary>
    /// Writes one cell of an <see cref="IsRawWhenNotNull"/> family as raw
    /// payload bytes — no length prefix, no NULL form (the column's
    /// metadata claims NOT NULL, so a NULL cell can't occur here).
    /// </summary>
    private static void WriteRawFixedValue(TdsTokenWriter writer, SqlType type, SqlValue value)
    {
        switch (type)
        {
            case TinyIntSqlType:
                writer.WriteByte(value.AsByte);
                break;
            case SmallIntSqlType:
                writer.WriteUInt16((ushort)value.AsInt16);
                break;
            case Int32SqlType:
                writer.WriteInt32(value.AsInt32);
                break;
            case BigIntSqlType:
                writer.WriteInt64(value.AsInt64);
                break;
            case BitSqlType:
                writer.WriteByte(value.AsBoolean ? (byte)1 : (byte)0);
                break;
            case RealSqlType:
                writer.WriteUInt32(BitConverter.SingleToUInt32Bits(value.AsSingle));
                break;
            case FloatSqlType:
                writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(value.AsDouble));
                break;
            case SmallMoneySqlType:
                writer.WriteInt32((int)value.AsMoneyScaledUnits);
                break;
            case MoneySqlType:
                var scaled = value.AsMoneyScaledUnits;
                writer.WriteInt32((int)(scaled >> 32));
                writer.WriteUInt32((uint)scaled);
                break;
            case SmallDateTimeSqlType:
                {
                    var dt = value.AsSmallDateTime;
                    writer.WriteUInt16((ushort)(dt.Date - Epoch1900).Days);
                    writer.WriteUInt16((ushort)((dt.Hour * 60) + dt.Minute));
                    break;
                }

            case DateTimeSqlType:
                {
                    var dt = value.AsDateTime;
                    var days = (dt.Date - Epoch1900).Days;
                    var thirds = (uint)(((dt.TimeOfDay.Ticks * 3) + 50_000) / 100_000);
                    if (thirds == 25_920_000)
                    {
                        days++;
                        thirds = 0;
                    }

                    writer.WriteInt32(days);
                    writer.WriteUInt32(thirds);
                    break;
                }

            default:
                throw new InvalidOperationException($"not an IsRawWhenNotNull type: {type}");
        }
    }

    /// <summary>
    /// RETURNVALUE token carrying an output parameter (or a prepared-statement
    /// handle) back to the client, which matches it by name, falling back to
    /// ordinal when the name is empty.
    /// </summary>
    public static void WriteReturnValue(TdsTokenWriter writer, ushort ordinal, string name, DbType dbType, object? value)
    {
        var declared = SqlType.GetByDbType(dbType);
        var sqlValue = value is null or DBNull ? SqlValue.Null(declared) : declared.ConvertParameter(value);
        WriteReturnValue(writer, ordinal, name, sqlValue.IsNull ? declared : sqlValue.Type, sqlValue);
    }

    /// <summary>
    /// RETURNVALUE token from an engine-typed <see cref="SqlValue"/> — the
    /// path for <c>sql_variant</c> and CLR-UDT (<c>hierarchyid</c> /
    /// <c>geography</c> / <c>geometry</c>) output parameters, whose wire form
    /// (self-describing variant body; UDT_INFO + PLP value) needs the value's
    /// own type identity rather than a <see cref="DbType"/> mapping.
    /// Probe-captured against SQL Server 2025 (2026-07-19): the RETURNVALUE
    /// TYPE_INFO and value bytes are the same forms the matching result
    /// columns carry, so the column writers are reused as-is.
    /// </summary>
    public static void WriteReturnValue(TdsTokenWriter writer, ushort ordinal, string name, SqlValue value)
        => WriteReturnValue(writer, ordinal, name, value.Type, value);

    private static void WriteReturnValue(TdsTokenWriter writer, ushort ordinal, string name, SqlType wireType, SqlValue sqlValue)
    {
        writer.EnterComposite();
        writer.WriteByte(Tds.TokenReturnValue);
        writer.WriteUInt16(ordinal);
        writer.WriteBVarchar(name);
        writer.WriteByte(1);
        writer.WriteUInt32(0);
        writer.WriteByte(0x09);
        writer.WriteByte(0);

        // Output parameters are nullable — real sends the N-variant token in a
        // RETURNVALUE regardless of the value, so the fixed-token path is off.
        WriteTypeInfo(writer, wireType, notNull: false);
        WriteValue(writer, wireType, sqlValue);
        writer.LeaveComposite();
    }

    private static void WriteTypeInfo(TdsTokenWriter writer, SqlType type, bool notNull, bool reportsNumeric = false)
    {
        // A NOT NULL fixed-width column carries the FIXEDLENTYPE token (single
        // byte, no max-length byte), matching real — the N-variant token below
        // is the nullable-only form. The paired ROW value is written raw (see
        // WriteRawFixedValue); COLMETADATA and ROW must agree on which token
        // family a column uses, so both read the same per-column nullability.
        if (notNull && TryFixedLenToken(type, out var fixedToken))
        {
            writer.WriteByte(fixedToken);
            return;
        }

        switch (type)
        {
            case TinyIntSqlType:
                writer.WriteByte(0x26);
                writer.WriteByte(1);
                break;
            case SmallIntSqlType:
                writer.WriteByte(0x26);
                writer.WriteByte(2);
                break;
            case Int32SqlType:
                writer.WriteByte(0x26);
                writer.WriteByte(4);
                break;
            case BigIntSqlType:
                writer.WriteByte(0x26);
                writer.WriteByte(8);
                break;
            case BitSqlType:
                writer.WriteByte(0x68);
                writer.WriteByte(1);
                break;
            case RealSqlType:
                writer.WriteByte(0x6D);
                writer.WriteByte(4);
                break;
            case FloatSqlType:
                writer.WriteByte(0x6D);
                writer.WriteByte(8);
                break;
            case SmallMoneySqlType:
                writer.WriteByte(0x6E);
                writer.WriteByte(4);
                break;
            case MoneySqlType:
                writer.WriteByte(0x6E);
                writer.WriteByte(8);
                break;
            case DecimalSqlType d:
                // NUMERICN (0x6C) vs DECIMALN (0x6A): identical wire body, but
                // the token drives the reported type name (JDBC
                // getColumnTypeName). A numeric-named projection column emits
                // NUMERICN; both store identically. RETURNVALUE keeps DECIMALN.
                writer.WriteByte(reportsNumeric ? (byte)0x6C : (byte)0x6A);
                writer.WriteByte((byte)(MagnitudeBytes(d.precision) + 1));
                writer.WriteByte(d.precision);
                writer.WriteByte(d.scale);
                break;
            case UniqueIdentifierSqlType:
                writer.WriteByte(0x24);
                writer.WriteByte(16);
                break;
            case DateSqlType:
                writer.WriteByte(0x28);
                break;
            case TimeSqlType t:
                writer.WriteByte(0x29);
                writer.WriteByte((byte)t.precision);
                break;
            case DateTime2SqlType d2:
                writer.WriteByte(0x2A);
                writer.WriteByte((byte)d2.precision);
                break;
            case DateTimeOffsetSqlType dto:
                writer.WriteByte(0x2B);
                writer.WriteByte((byte)dto.precision);
                break;
            case SmallDateTimeSqlType:
                writer.WriteByte(0x6F);
                writer.WriteByte(4);
                break;
            case DateTimeSqlType:
                writer.WriteByte(0x6F);
                writer.WriteByte(8);
                break;
            case VarcharSqlType v:
                writer.WriteByte(0xA7);
                writer.WriteUInt16(VariableMaxLength(v.length, 8000));
                TdsCollationCodec.For(v.Collation).Write(writer);
                break;
            case CharSqlType c:
                writer.WriteByte(0xAF);
                writer.WriteUInt16(VariableMaxLength(c.length, 8000));
                TdsCollationCodec.For(c.Collation).Write(writer);
                break;
            case NVarcharSqlType nv:
                writer.WriteByte(0xE7);
                writer.WriteUInt16(NationalMaxLength(nv.length));
                TdsCollationCodec.For(nv.Collation).Write(writer);
                break;
            case SystemNameSqlType:
                writer.WriteByte(0xE7);
                writer.WriteUInt16(256);
                TdsCollationCodec.For(null).Write(writer);
                break;
            case NCharSqlType nc:
                writer.WriteByte(0xEF);
                writer.WriteUInt16(NationalMaxLength(nc.length));
                TdsCollationCodec.For(nc.Collation).Write(writer);
                break;
            case VarbinarySqlType vb:
                writer.WriteByte(0xA5);
                writer.WriteUInt16(VariableMaxLength(vb.length, 8000));
                break;
            case BinarySqlType b:
                writer.WriteByte(0xAD);
                writer.WriteUInt16(VariableMaxLength(b.length, 8000));
                break;
            case RowVersionSqlType:
                writer.WriteByte(0xAD);
                writer.WriteUInt16(8);
                break;
            case XmlSqlType:
                writer.WriteByte(0xF1);
                writer.WriteByte(0);
                break;
            case SqlVariantSqlType:
                // SSVARIANTTYPE: a LONGLEN type whose TYPE_INFO is the type
                // byte plus a 4-byte max length (8009 = 8000 data bytes + the
                // 9-byte inner-type header cap). MS-TDS 2.2.5.4.3 / 2.2.5.5.3.
                writer.WriteByte(0x62);
                writer.WriteUInt32(8009);
                break;
            case SpatialSqlType spatial:
                // UDTTYPE (MS-TDS 2.2.5.5.2): a PLP type whose TYPE_INFO is a
                // ushort max-byte-size (0xFFFF = max) then the three B_VARCHAR
                // names (db / schema / type) and the US_VARCHAR assembly-
                // qualified type name. The db name is unavailable in this static
                // codec (its call site can't thread it), so it goes empty; the
                // schema/type/AQN carry the functional identity SqlClient and
                // DacFx read. Probe-confirmed against SQL Server 2025 (2026-07-16).
                writer.WriteByte(0xF0);
                writer.WriteUInt16(0xFFFF);
                writer.WriteBVarchar(string.Empty);
                writer.WriteBVarchar("sys");
                writer.WriteBVarchar(spatial.SqlServerName);
                writer.WriteUsVarchar(SpatialAssemblyQualifiedName(spatial));
                break;
            case HierarchyIdSqlType:
                // UDTTYPE (MS-TDS 2.2.5.5.2), the same shape as the spatial arm
                // above but with hierarchyid's fixed 892-byte max size (not the
                // 0xFFFF max sentinel) and the SqlHierarchyId assembly-qualified
                // name. Probe-confirmed against SQL Server 2025 (2026-07-16:
                // GetSchemaTable ColumnSize = 892, UdtAssemblyQualifiedName as
                // below). The db name goes empty for the same static-codec reason.
                writer.WriteByte(0xF0);
                writer.WriteUInt16(892);
                writer.WriteBVarchar(string.Empty);
                writer.WriteBVarchar("sys");
                writer.WriteBVarchar("hierarchyid");
                writer.WriteUsVarchar(HierarchyIdAssemblyQualifiedName);
                break;
            case TextSqlType tx:
                // TEXTTYPE (MS-TDS 2.2.5.4.2): LONGLEN max size 0x7FFFFFFF, the
                // 5-byte collation, then the TableName field these legacy LOB
                // types uniquely carry. Probe-captured 2026-07-19.
                writer.WriteByte(0x23);
                writer.WriteUInt32(0x7FFFFFFF);
                TdsCollationCodec.For(tx.Collation).Write(writer);
                WriteLegacyLobTableName(writer);
                break;
            case NTextSqlType nx:
                // NTEXTTYPE: identical shape to TEXTTYPE but LONGLEN max size
                // 0x7FFFFFFE (the value real sends for ntext).
                writer.WriteByte(0x63);
                writer.WriteUInt32(0x7FFFFFFE);
                TdsCollationCodec.For(nx.Collation).Write(writer);
                WriteLegacyLobTableName(writer);
                break;
            case ImageSqlType:
                // IMAGETYPE: LONGLEN max size 0x7FFFFFFF then TableName — no
                // collation (binary).
                writer.WriteByte(0x22);
                writer.WriteUInt32(0x7FFFFFFF);
                WriteLegacyLobTableName(writer);
                break;
            default:
                throw new NotSupportedException($"The network listener does not support '{type.SqlServerName}' result columns.");
        }
    }

    private static void WriteValue(TdsTokenWriter writer, SqlType type, SqlValue value)
    {
        switch (type)
        {
            case TinyIntSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(1);
                writer.WriteByte(value.AsByte);
                break;
            case SmallIntSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(2);
                writer.WriteUInt16((ushort)value.AsInt16);
                break;
            case Int32SqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(4);
                writer.WriteInt32(value.AsInt32);
                break;
            case BigIntSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(8);
                writer.WriteInt64(value.AsInt64);
                break;
            case BitSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(1);
                writer.WriteByte(value.AsBoolean ? (byte)1 : (byte)0);
                break;
            case RealSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(4);
                writer.WriteUInt32(BitConverter.SingleToUInt32Bits(value.AsSingle));
                break;
            case FloatSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(8);
                writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(value.AsDouble));
                break;
            case SmallMoneySqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(4);
                writer.WriteInt32((int)value.AsMoneyScaledUnits);
                break;
            case MoneySqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(8);
                var scaled = value.AsMoneyScaledUnits;
                writer.WriteInt32((int)(scaled >> 32));
                writer.WriteUInt32((uint)scaled);
                break;
            case DecimalSqlType d:
                WriteDecimal(writer, d, value);
                break;
            case UniqueIdentifierSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(16);
                Span<byte> guid = stackalloc byte[16];
                _ = value.AsGuid.TryWriteBytes(guid);
                writer.WriteBytes(guid);
                break;
            case DateSqlType:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(3);
                WriteThreeByteDays(writer, value.AsDate.DayNumber);
                break;
            case TimeSqlType t:
                if (WroteByteLengthNull(writer, value))
                    break;

                writer.WriteByte(TimeValueBytes(t.precision));
                WriteScaledTime(writer, value.AsTime.Ticks, t.precision);
                break;
            case DateTime2SqlType d2:
                {
                    if (WroteByteLengthNull(writer, value))
                        break;

                    writer.WriteByte((byte)(TimeValueBytes(d2.precision) + 3));
                    var dt = value.AsDateTime2;
                    WriteScaledTime(writer, dt.TimeOfDay.Ticks, d2.precision);
                    WriteThreeByteDays(writer, DateOnly.FromDateTime(dt).DayNumber);
                    break;
                }

            case DateTimeOffsetSqlType dto:
                {
                    if (WroteByteLengthNull(writer, value))
                        break;

                    writer.WriteByte((byte)(TimeValueBytes(dto.precision) + 5));
                    var dtoValue = value.AsDateTimeOffset;
                    var utc = dtoValue.UtcDateTime;
                    WriteScaledTime(writer, utc.TimeOfDay.Ticks, dto.precision);
                    WriteThreeByteDays(writer, DateOnly.FromDateTime(utc).DayNumber);
                    writer.WriteUInt16((ushort)(short)dtoValue.Offset.TotalMinutes);
                    break;
                }

            case SmallDateTimeSqlType:
                {
                    if (WroteByteLengthNull(writer, value))
                        break;

                    writer.WriteByte(4);
                    var dt = value.AsSmallDateTime;
                    writer.WriteUInt16((ushort)(dt.Date - Epoch1900).Days);
                    writer.WriteUInt16((ushort)((dt.Hour * 60) + dt.Minute));
                    break;
                }

            case DateTimeSqlType:
                {
                    if (WroteByteLengthNull(writer, value))
                        break;

                    writer.WriteByte(8);
                    var dt = value.AsDateTime;
                    var days = (dt.Date - Epoch1900).Days;
                    var thirds = (uint)(((dt.TimeOfDay.Ticks * 3) + 50_000) / 100_000);
                    if (thirds == 25_920_000)
                    {
                        days++;
                        thirds = 0;
                    }

                    writer.WriteInt32(days);
                    writer.WriteUInt32(thirds);
                    break;
                }

            case VarcharSqlType v:
                WriteSingleByteString(writer, v.Collation, v.length, value);
                break;
            case CharSqlType c:
                WriteSingleByteString(writer, c.Collation, c.length, value);
                break;
            case NVarcharSqlType nv:
                WriteNationalString(writer, nv.length, value);
                break;
            case SystemNameSqlType:
                WriteNationalString(writer, 128, value);
                break;
            case NCharSqlType nc:
                WriteNationalString(writer, nc.length, value);
                break;
            case VarbinarySqlType vb:
                WriteBinary(writer, vb.length, value);
                break;
            case BinarySqlType b:
                WriteBinary(writer, b.length, value);
                break;
            case RowVersionSqlType:
                WriteBinary(writer, 8, value);
                break;
            case XmlSqlType:
                if (value.IsNull)
                    writer.WriteUInt64(ulong.MaxValue);
                else
                    WritePlpChunks(writer, System.Text.Encoding.Unicode.GetBytes(value.AsString));
                break;
            case SqlVariantSqlType:
                WriteVariant(writer, value);
                break;
            case SpatialSqlType spatial:
                if (value.IsNull)
                {
                    writer.WriteUInt64(ulong.MaxValue);
                }
                else
                {
                    var isGeography = spatial is GeographySqlType;
                    WritePlpChunks(writer, value.AsSpatial.Encoded(isGeography));
                }

                break;
            case HierarchyIdSqlType:
                if (value.IsNull)
                    writer.WriteUInt64(ulong.MaxValue);
                else
                    WritePlpChunks(writer, value.AsHierarchyIdBytes);
                break;
            case TextSqlType tx:
                WriteLegacyLob(writer, value.IsNull ? null : TdsCollationCodec.For(tx.Collation).WireEncoding.GetBytes(value.AsString));
                break;
            case NTextSqlType:
                WriteLegacyLob(writer, value.IsNull ? null : System.Text.Encoding.Unicode.GetBytes(value.AsString));
                break;
            case ImageSqlType:
                WriteLegacyLob(writer, value.IsNull ? null : value.AsBytes);
                break;
            default:
                throw new NotSupportedException($"The network listener does not support '{type.SqlServerName}' result columns.");
        }
    }

    /// <summary>
    /// The TableName field carried only by the text / ntext / image TYPE_INFO
    /// (MS-TDS 2.2.7.4). Real SQL Server sends the value's base table as a
    /// <c>[schema, table]</c> (or <c>[schema, view]</c>) name; the simulator's
    /// result metadata does not thread per-column source-table identity, so it
    /// emits the single-empty-part form real uses for expression columns
    /// (e.g. <c>CAST(x AS text)</c>) — NumParts 1 plus one empty US_VARCHAR.
    /// Probe-captured 2026-07-19; SqlClient accepts it (the field surfaces only
    /// as the cosmetic <c>BaseTableName</c> in <c>GetSchemaTable</c>).
    /// </summary>
    private static void WriteLegacyLobTableName(TdsTokenWriter writer)
    {
        writer.WriteByte(1);
        writer.WriteUsVarchar(string.Empty);
    }

    /// <summary>
    /// Writes a text / ntext / image ROW value in the legacy in-band form
    /// (MS-TDS 2.2.5.5.1): a NULL is a single zero text-pointer-length byte; a
    /// non-NULL value is the 16-byte text pointer + 8-byte timestamp
    /// placeholders real sends, a 4-byte data length, then the raw data bytes
    /// (CP1252 for text, UTF-16LE for ntext, verbatim for image). The
    /// transport packetizes an oversize value transparently, so the data is
    /// written as one contiguous block. Probe-captured 2026-07-19.
    /// </summary>
    private static void WriteLegacyLob(TdsTokenWriter writer, byte[]? data)
    {
        if (data is null)
        {
            writer.WriteByte(0);
            return;
        }

        writer.WriteByte(16);
        writer.WriteBytes(LegacyLobTextPointer);
        writer.WriteBytes(LegacyLobTimestamp);
        writer.WriteUInt32((uint)data.Length);
        writer.WriteBytes(data);
    }

    /// <summary>
    /// Writes a <c>sql_variant</c> cell per MS-TDS 2.2.5.5.3: a 4-byte total
    /// length (0xFFFFFFFF for NULL) followed by the variant body — a 1-byte
    /// base-type token, a 1-byte property-byte count, the property bytes
    /// (collation + max length for strings, precision + scale for decimal,
    /// scale for the fractional temporal types), then the inner value's raw
    /// data bytes. The integer / bit / string / NULL forms the catalog surface
    /// produces are the oracle-verified subset; the remaining base types follow
    /// the same MS-TDS layout for completeness.
    /// </summary>
    private static void WriteVariant(TdsTokenWriter writer, SqlValue value)
    {
        // A NULL sql_variant is a zero total length: a non-NULL variant always
        // carries at least the 2-byte type + prop-count header, so SqlClient
        // reads length 0 as NULL (not the 0xFFFFFFFF charbin sentinel).
        if (value.IsNull)
        {
            writer.WriteUInt32(0);
            return;
        }

        var body = BuildVariantBody(value.AsVariantInner);
        writer.WriteUInt32((uint)body.Length);
        writer.WriteBytes(body);
    }

    private static byte[] BuildVariantBody(SqlValue inner)
    {
        var t = inner.Type;
        switch (t)
        {
            case BitSqlType: return [0x32, 0, inner.AsBoolean ? (byte)1 : (byte)0];
            case TinyIntSqlType: return [0x30, 0, inner.AsByte];
            case SmallIntSqlType:
                {
                    var body = NumericBody(0x34, 2);
                    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(2), inner.AsInt16);
                    return body;
                }

            case Int32SqlType:
                {
                    var body = NumericBody(0x38, 4);
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), inner.AsInt32);
                    return body;
                }

            case BigIntSqlType:
                {
                    var body = NumericBody(0x7F, 8);
                    BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(2), inner.AsInt64);
                    return body;
                }

            case RealSqlType:
                {
                    var body = NumericBody(0x3B, 4);
                    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), BitConverter.SingleToUInt32Bits(inner.AsSingle));
                    return body;
                }

            case FloatSqlType:
                {
                    var body = NumericBody(0x3E, 8);
                    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(2), BitConverter.DoubleToUInt64Bits(inner.AsDouble));
                    return body;
                }

            case SmallMoneySqlType:
                {
                    var body = NumericBody(0x7A, 4);
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), (int)inner.AsMoneyScaledUnits);
                    return body;
                }

            case MoneySqlType:
                {
                    var body = NumericBody(0x3C, 8);
                    var scaled = inner.AsMoneyScaledUnits;
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2, 4), (int)(scaled >> 32));
                    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6, 4), (uint)scaled);
                    return body;
                }

            case UniqueIdentifierSqlType:
                {
                    var body = NumericBody(0x24, 16);
                    _ = inner.AsGuid.TryWriteBytes(body.AsSpan(2));
                    return body;
                }

            case DateSqlType:
                {
                    var body = NumericBody(0x28, 3);
                    WriteThreeByteDaysSpan(body.AsSpan(2), inner.AsDate.DayNumber);
                    return body;
                }

            case SmallDateTimeSqlType:
                {
                    var body = NumericBody(0x3A, 4);
                    var dt = inner.AsSmallDateTime;
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), (ushort)(dt.Date - Epoch1900).Days);
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4, 2), (ushort)((dt.Hour * 60) + dt.Minute));
                    return body;
                }

            case DateTimeSqlType:
                {
                    var body = NumericBody(0x3D, 8);
                    var dt = inner.AsDateTime;
                    var days = (dt.Date - Epoch1900).Days;
                    var thirds = (uint)(((dt.TimeOfDay.Ticks * 3) + 50_000) / 100_000);
                    if (thirds == 25_920_000)
                    {
                        days++;
                        thirds = 0;
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2, 4), days);
                    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6, 4), thirds);
                    return body;
                }

            case TimeSqlType tm: return ScaledTemporalVariantBody(0x29, tm.precision, TimeValueBytes(tm.precision), inner.AsTime.Ticks, dayNumber: null);
            case DateTime2SqlType dt2:
                {
                    var dt = inner.AsDateTime2;
                    return ScaledTemporalVariantBody(0x2A, dt2.precision, TimeValueBytes(dt2.precision), dt.TimeOfDay.Ticks, DateOnly.FromDateTime(dt).DayNumber);
                }

            case DateTimeOffsetSqlType dto:
                {
                    var dtoValue = inner.AsDateTimeOffset;
                    var utc = dtoValue.UtcDateTime;
                    var body = ScaledTemporalVariantBody(0x2B, dto.precision, TimeValueBytes(dto.precision) + 2, utc.TimeOfDay.Ticks, DateOnly.FromDateTime(utc).DayNumber);
                    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(body.Length - 2), (short)dtoValue.Offset.TotalMinutes);
                    return body;
                }

            case DecimalSqlType d: return BuildVariantDecimal(d, inner);
            case VarcharSqlType v: return BuildVariantString(0xA7, v.Collation, inner, national: false);
            case CharSqlType c: return BuildVariantString(0xAF, c.Collation, inner, national: false);
            case NVarcharSqlType nv: return BuildVariantString(0xE7, nv.Collation, inner, national: true);
            case SystemNameSqlType: return BuildVariantString(0xE7, null, inner, national: true);
            case NCharSqlType nc: return BuildVariantString(0xEF, nc.Collation, inner, national: true);
            case VarbinarySqlType: return BuildVariantBinary(0xA5, inner);
            case BinarySqlType: return BuildVariantBinary(0xAD, inner);
            default:
                throw new NotSupportedException($"sql_variant inner type '{t.SqlServerName}' has no wire encoding.");
        }
    }

    /// <summary>A variant body with a 0-property-byte header (type token + cbProps=0) sized for <paramref name="dataLength"/> data bytes.</summary>
    private static byte[] NumericBody(byte typeToken, int dataLength)
    {
        var body = new byte[2 + dataLength];
        body[0] = typeToken;
        body[1] = 0;
        return body;
    }

    private static byte[] ScaledTemporalVariantBody(byte typeToken, int scale, int dataLength, long timeTicks, int? dayNumber)
    {
        var body = new byte[3 + dataLength];
        body[0] = typeToken;
        body[1] = 1;
        body[2] = (byte)scale;
        var data = body.AsSpan(3);
        var timeBytes = TimeValueBytes(scale);
        WriteScaledTimeSpan(data[..timeBytes], timeTicks, scale);
        if (dayNumber is { } dn)
            WriteThreeByteDaysSpan(data.Slice(timeBytes, 3), dn);
        return body;
    }

    private static byte[] BuildVariantString(byte typeToken, Collation? collation, SqlValue inner, bool national)
    {
        var codec = TdsCollationCodec.For(collation);
        var data = national
            ? System.Text.Encoding.Unicode.GetBytes(inner.AsString)
            : codec.WireEncoding.GetBytes(inner.AsString);
        var body = new byte[2 + 7 + data.Length];
        body[0] = typeToken;
        body[1] = 7;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2, 4), codec.Info);
        body[6] = codec.SortId;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7, 2), (ushort)Math.Clamp(data.Length, national ? 2 : 1, 8000));
        data.CopyTo(body.AsSpan(9));
        return body;
    }

    private static byte[] BuildVariantBinary(byte typeToken, SqlValue inner)
    {
        var data = inner.AsBytes;
        var body = new byte[2 + 2 + data.Length];
        body[0] = typeToken;
        body[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2, 2), (ushort)Math.Clamp(data.Length, 1, 8000));
        data.CopyTo(body.AsSpan(4));
        return body;
    }

    private static byte[] BuildVariantDecimal(DecimalSqlType type, SqlValue inner)
    {
        // Server-sent variant decimal data is always 1 sign byte + a 16-byte
        // magnitude regardless of precision (probe-captured 2026-07-19 in both
        // a result column and a RETURNVALUE; RPC *request* decimals are the
        // precision-width exception, on the decode side). SqlClient's row
        // reader tolerates a narrower magnitude but its RETURNVALUE reader
        // reads the fixed 17 and desyncs on anything shorter.
        const int magnitudeBytes = 16;
        // 2 prop bytes (precision, scale); data = 1 sign byte + magnitude.
        var body = new byte[2 + 2 + 1 + magnitudeBytes];
        body[0] = 0x6A;
        body[1] = 2;
        body[2] = type.precision;
        body[3] = type.scale;

        var number = inner.AsDecimal;
        body[4] = number >= 0 ? (byte)1 : (byte)0;

        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(Math.Abs(number), bits);
        var storedScale = (bits[3] >> 16) & 0xFF;
        var magnitude = ((BigInteger)(uint)bits[2] << 64) | ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
        if (storedScale < type.scale)
        {
            magnitude *= BigInteger.Pow(10, type.scale - storedScale);
        }
        else if (storedScale > type.scale)
        {
            var divisor = BigInteger.Pow(10, storedScale - type.scale);
            var (quotient, remainder) = BigInteger.DivRem(magnitude, divisor);
            if (remainder * 2 >= divisor)
                quotient++;

            magnitude = quotient;
        }

        _ = magnitude.TryWriteBytes(body.AsSpan(5, magnitudeBytes), out _, isUnsigned: true, isBigEndian: false);
        return body;
    }

    private static void WriteScaledTimeSpan(Span<byte> destination, long ticks, int scale)
    {
        var units = ticks;
        for (var i = scale; i < 7; i++)
            units /= 10;
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)units;
            units >>= 8;
        }
    }

    private static void WriteThreeByteDaysSpan(Span<byte> destination, int dayNumber)
    {
        destination[0] = (byte)dayNumber;
        destination[1] = (byte)(dayNumber >> 8);
        destination[2] = (byte)(dayNumber >> 16);
    }

    private static bool WroteByteLengthNull(TdsTokenWriter writer, SqlValue value)
    {
        if (!value.IsNull)
            return false;

        writer.WriteByte(0);
        return true;
    }

    private static void WriteDecimal(TdsTokenWriter writer, DecimalSqlType type, SqlValue value)
    {
        if (WroteByteLengthNull(writer, value))
            return;

        var magnitudeBytes = MagnitudeBytes(type.precision);
        writer.WriteByte((byte)(magnitudeBytes + 1));

        var number = value.AsDecimal;
        writer.WriteByte(number >= 0 ? (byte)1 : (byte)0);

        Span<int> bits = stackalloc int[4];
        _ = decimal.GetBits(Math.Abs(number), bits);
        var storedScale = (bits[3] >> 16) & 0xFF;
        var magnitude = ((BigInteger)(uint)bits[2] << 64) | ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
        if (storedScale < type.scale)
        {
            magnitude *= BigInteger.Pow(10, type.scale - storedScale);
        }
        else if (storedScale > type.scale)
        {
            var divisor = BigInteger.Pow(10, storedScale - type.scale);
            var (quotient, remainder) = BigInteger.DivRem(magnitude, divisor);
            if (remainder * 2 >= divisor)
                quotient++;

            magnitude = quotient;
        }

        Span<byte> raw = stackalloc byte[16];
        if (!magnitude.TryWriteBytes(raw[..magnitudeBytes], out var written, isUnsigned: true, isBigEndian: false))
            throw new OverflowException($"decimal({type.precision}, {type.scale}) value does not fit its wire width.");

        raw[written..magnitudeBytes].Clear();
        writer.WriteBytes(raw[..magnitudeBytes]);
    }

    private static void WriteSingleByteString(TdsTokenWriter writer, Collation? collation, short declaredLength, SqlValue value)
    {
        var codec = TdsCollationCodec.For(collation);
        if (declaredLength == SqlType.MaxLengthSentinel)
        {
            if (value.IsNull)
                writer.WriteUInt64(ulong.MaxValue);
            else
                WritePlpChunks(writer, codec.WireEncoding.GetBytes(value.AsString));
            return;
        }

        if (value.IsNull)
        {
            writer.WriteUInt16(0xFFFF);
            return;
        }

        var bytes = codec.WireEncoding.GetBytes(value.AsString);
        writer.WriteUInt16(BoundedWireLength(bytes.Length));
        writer.WriteBytes(bytes);
    }

    private static void WriteNationalString(TdsTokenWriter writer, short declaredLength, SqlValue value)
    {
        if (declaredLength == SqlType.MaxLengthSentinel)
        {
            if (value.IsNull)
                writer.WriteUInt64(ulong.MaxValue);
            else
                WritePlpChunks(writer, System.Text.Encoding.Unicode.GetBytes(value.AsString));
            return;
        }

        if (value.IsNull)
        {
            writer.WriteUInt16(0xFFFF);
            return;
        }

        var text = value.AsString;
        writer.WriteUInt16(BoundedWireLength(text.Length * 2));
        writer.WriteUcs2(text);
    }

    private static void WriteBinary(TdsTokenWriter writer, short declaredLength, SqlValue value)
    {
        if (declaredLength == SqlType.MaxLengthSentinel)
        {
            if (value.IsNull)
                writer.WriteUInt64(ulong.MaxValue);
            else
                WritePlpChunks(writer, value.AsBytes);
            return;
        }

        if (value.IsNull)
        {
            writer.WriteUInt16(0xFFFF);
            return;
        }

        var bytes = value.AsBytes;
        writer.WriteUInt16(BoundedWireLength(bytes.Length));
        writer.WriteBytes(bytes);
    }

    /// <summary>
    /// The 2-byte length prefix for a bounded (non-MAX) string / binary value.
    /// An oversize value here would be a wire-encoding impossibility for the
    /// declared bounded type; throw <see cref="InvalidDataException"/> — the
    /// one exception type <c>TdsSession.RunAsync</c>'s catch boundary treats as
    /// a clean session end — rather than letting an unchecked
    /// <see cref="OverflowException"/> escape and kill the session as a silent
    /// transport error. In practice the engine clamps values to their declared
    /// bounds, so this fires only for a scalar mistyped as bounded-instead-of-
    /// MAX (the class of bug that made OBJECT_DEFINITION's ~250 KB result crash
    /// the session before it was retyped <see cref="SqlType.NVarcharMax"/>).
    /// </summary>
    private static ushort BoundedWireLength(int byteLength) =>
        byteLength > ushort.MaxValue
            ? throw new InvalidDataException(
                $"A bounded string/binary value of {byteLength} bytes exceeds the {ushort.MaxValue}-byte wire limit for its declared type; a MAX type is required to stream it.")
            : (ushort)byteLength;

    /// <summary>Known-length PLP: total length, one chunk, zero terminator.</summary>
    private static void WritePlpChunks(TdsTokenWriter writer, byte[] bytes)
    {
        writer.WriteUInt64((ulong)bytes.Length);
        if (bytes.Length > 0)
        {
            writer.WriteUInt32((uint)bytes.Length);
            writer.WriteBytes(bytes);
        }

        writer.WriteUInt32(0);
    }

    private static ushort VariableMaxLength(short declaredLength, int fallback) =>
        declaredLength == SqlType.MaxLengthSentinel
            ? (ushort)0xFFFF
            : declaredLength > 0 ? (ushort)declaredLength : (ushort)fallback;

    private static ushort NationalMaxLength(short declaredChars) =>
        declaredChars == SqlType.MaxLengthSentinel
            ? (ushort)0xFFFF
            : declaredChars > 0 ? (ushort)(declaredChars * 2) : (ushort)8000;

    private static byte TimeValueBytes(int scale) => scale <= 2 ? (byte)3 : scale <= 4 ? (byte)4 : (byte)5;

    private static void WriteScaledTime(TdsTokenWriter writer, long ticks, int scale)
    {
        var units = ticks;
        for (var i = scale; i < 7; i++)
            units /= 10;

        var bytes = TimeValueBytes(scale);
        for (var i = 0; i < bytes; i++)
        {
            writer.WriteByte((byte)units);
            units >>= 8;
        }
    }

    private static void WriteThreeByteDays(TdsTokenWriter writer, int dayNumber)
    {
        writer.WriteByte((byte)dayNumber);
        writer.WriteByte((byte)(dayNumber >> 8));
        writer.WriteByte((byte)(dayNumber >> 16));
    }

    private static int MagnitudeBytes(byte precision) => precision <= 9 ? 4 : precision <= 19 ? 8 : precision <= 28 ? 12 : 16;

    /// <summary>
    /// The assembly-qualified CLR type name a UDT COLMETADATA advertises for a
    /// spatial column — the string SqlClient exposes as
    /// <c>UdtAssemblyQualifiedName</c> and uses to locate
    /// <c>Microsoft.SqlServer.Types</c>. When that assembly is absent (the
    /// common DacFx case) SqlClient returns the raw serialization bytes via
    /// <c>GetSqlBytes</c> / <c>GetBytes</c>. Version/token match SQL Server 2025
    /// (probed 2026-07-16).
    /// </summary>
    private static string SpatialAssemblyQualifiedName(SpatialSqlType type)
    {
        const string tail = ", Microsoft.SqlServer.Types, Version=11.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91";
        return type is GeographySqlType
            ? "Microsoft.SqlServer.Types.SqlGeography" + tail
            : "Microsoft.SqlServer.Types.SqlGeometry" + tail;
    }

    /// <summary>
    /// The assembly-qualified CLR type name a hierarchyid UDT COLMETADATA
    /// advertises — the string SqlClient exposes as
    /// <c>UdtAssemblyQualifiedName</c>. With <c>Microsoft.SqlServer.Types</c>
    /// absent (the DacFx case) SqlClient hands back the raw OrdPath bytes via
    /// <c>GetSqlBytes</c> / <c>GetBytes</c>. Version/token probe-matched to SQL
    /// Server 2025 (2026-07-16).
    /// </summary>
    private const string HierarchyIdAssemblyQualifiedName =
        "Microsoft.SqlServer.Types.SqlHierarchyId, Microsoft.SqlServer.Types, Version=11.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91";
}
