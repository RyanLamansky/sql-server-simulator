using System.Numerics;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>
/// Translates result-set schemas and values into the TDS wire encoding:
/// COLMETADATA TYPE_INFO per column and the per-cell value bytes of ROW
/// tokens. Nullable wire variants are used throughout (INTN, BITN, FLTN,
/// MONEYN, DATETIMN, DECIMALN, GUIDN), which SqlClient accepts for
/// non-nullable data as well.
/// </summary>
internal static class TdsTypeCodec
{
    private static readonly DateTime Epoch1900 = new(1900, 1, 1);

    /// <summary>
    /// Rejects result-set column types that have no wire encoding here, so
    /// the failure surfaces as an ERROR token before any COLMETADATA bytes
    /// rather than as a mid-stream protocol desync.
    /// </summary>
    public static void ValidateSchema(SqlType[] schema)
    {
        foreach (var type in schema)
        {
            switch (type)
            {
                case TextSqlType or NTextSqlType or ImageSqlType or HierarchyIdSqlType or GeographySqlType or GeometrySqlType:
                    throw new NotSupportedException($"The network listener does not support '{type.SqlServerName}' result columns.");
            }
        }
    }

    public static void WriteColMetadata(TdsTokenWriter writer, SqlType[] schema, string[] columnNames)
    {
        writer.WriteByte(Tds.TokenColMetadata);
        writer.WriteUInt16(checked((ushort)schema.Length));
        for (var i = 0; i < schema.Length; i++)
        {
            var type = schema[i];
            writer.WriteUInt32(type is RowVersionSqlType ? 0x50u : 0u);
            writer.WriteByte(0x09);
            writer.WriteByte(0);
            WriteTypeInfo(writer, type);
            writer.WriteBVarchar(columnNames[i]);
        }
    }

    public static void WriteRow(TdsTokenWriter writer, SqlType[] schema, RowCursor cursor)
    {
        writer.WriteByte(Tds.TokenRow);
        for (var i = 0; i < schema.Length; i++)
            WriteValue(writer, schema[i], cursor[i]);
    }

    private static void WriteTypeInfo(TdsTokenWriter writer, SqlType type)
    {
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
                writer.WriteByte(0x6A);
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
            default:
                throw new NotSupportedException($"The network listener does not support '{type.SqlServerName}' result columns.");
        }
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
        writer.WriteUInt16(checked((ushort)bytes.Length));
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
        writer.WriteUInt16(checked((ushort)(text.Length * 2)));
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
        writer.WriteUInt16(checked((ushort)bytes.Length));
        writer.WriteBytes(bytes);
    }

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
}
