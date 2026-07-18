using System.Buffers.Binary;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Network;

/// <summary>
/// Decodes one column's TYPE_INFO and its per-row value from a client-authored
/// value stream — the encoding shared by <c>SqlBulkCopy</c>'s BulkLoadBCP rows
/// (<see cref="TdsBulkLoadReader"/>) and table-valued-parameter rows
/// (<see cref="TdsTableValuedParameterReader"/>). Both carry column metadata
/// (TYPE_INFO) followed by row tokens whose per-column values use the same wire
/// forms <see cref="TdsTypeCodec"/> writes for result rows; this is the read
/// mirror of that value codec. Each decoded value is a <see cref="SqlValue"/>
/// typed at its wire type; the engine coerces it to the destination on insert.
/// </summary>
internal static class TdsColumnDecoder
{
    private static readonly DateTime Epoch1900 = new(1900, 1, 1);

    /// <summary>
    /// Reads a column's TYPE_INFO (the type token plus its type-specific
    /// metadata bytes) into a <see cref="Column"/> whose
    /// <see cref="Column.ReadValue"/> decodes the matching per-row value.
    /// </summary>
    public static Column ReadColumnMetadata(TdsValueReader reader)
    {
        var token = reader.ReadByte();
#pragma warning disable SSS005 // Grouped by TDS TYPE_INFO shape (fixed-length / decimal / temporal / string / binary), not numeric order.
        switch (token)
        {
            // FIXEDLENTYPE tokens (MS-TDS §2.2.5.4.1): SqlClient sends these for
            // NOT NULL columns — no TYPE_INFO bytes and a raw, un-prefixed value.
            case 0x30: // INT1TYPE
            case 0x32: // BITTYPE
            case 0x34: // INT2TYPE
            case 0x38: // INT4TYPE
            case 0x3A: // DATETIM4TYPE
            case 0x3B: // FLT4TYPE
            case 0x3C: // MONEYTYPE
            case 0x3D: // DATETIMETYPE
            case 0x3E: // FLT8TYPE
            case 0x7A: // MONEY4TYPE
            case 0x7F: // INT8TYPE
                return new Column(token, 0, 0, 0, false);
            case 0x26: // INTN
            case 0x68: // BITN
            case 0x6D: // FLTN
            case 0x6E: // MONEYN
            case 0x6F: // DATETIMN
            case 0x24: // GUIDN
                return new Column(token, reader.ReadByte(), 0, 0, false);
            case 0x6A: // DECIMALN
            case 0x6C: // NUMERICN
                {
                    var declared = reader.ReadByte();
                    var precision = reader.ReadByte();
                    var scale = reader.ReadByte();
                    return new Column(token, declared, precision, scale, false);
                }

            case 0x28: // DATEN (no scale)
                return new Column(token, 0, 0, 0, false);
            case 0x29: // TIMEN
            case 0x2A: // DATETIME2N
            case 0x2B: // DATETIMEOFFSETN
                return new Column(token, 0, 0, reader.ReadByte(), false);
            case 0xA7: // BIGVARCHAR
            case 0xAF: // BIGCHAR
                {
                    var maxLength = reader.ReadUInt16();
                    var utf8 = ReadCollation(reader);
                    return new Column(token, maxLength, 0, 0, utf8);
                }

            case 0xE7: // NVARCHAR
            case 0xEF: // NCHAR
                {
                    var maxLength = reader.ReadUInt16();
                    _ = ReadCollation(reader);
                    return new Column(token, maxLength, 0, 0, false);
                }

            case 0xA5: // BIGVARBINARY
            case 0xAD: // BIGBINARY
                return new Column(token, reader.ReadUInt16(), 0, 0, false);
            case 0xF1: // XML
                {
                    if (reader.ReadByte() == 1)
                    {
                        _ = reader.ReadUcs2(reader.ReadByte());
                        _ = reader.ReadUcs2(reader.ReadByte());
                        _ = reader.ReadUcs2(reader.ReadUInt16());
                    }

                    return new Column(token, 0, 0, 0, false);
                }

            case 0x22: throw Unsupported("image");
            case 0x23: throw Unsupported("text");
            case 0x63: throw Unsupported("ntext");
            case 0x62: throw Unsupported("sql_variant");
            case 0xF0: throw Unsupported("CLR UDT / spatial / hierarchyid");
            default:
                throw new NotSupportedException($"The network listener does not decode client value column type token 0x{token:X2}.");
        }
#pragma warning restore SSS005
    }

    private static NotSupportedException Unsupported(string feature) =>
        new($"The network listener does not accept {feature} columns in a client value stream (SqlBulkCopy / table-valued parameter).");

    private static bool ReadCollation(TdsValueReader reader)
    {
        var info = reader.ReadUInt32();
        _ = reader.ReadByte();
        return (info & (1u << 26)) != 0;
    }

    /// <summary>One column's TYPE_INFO and its per-value decoder.</summary>
    public sealed class Column(byte token, int declaredOrMax, byte precision, byte scale, bool utf8)
    {
        public SqlValue ReadValue(TdsValueReader reader) => token switch
        {
            // FIXEDLENTYPE: raw value bytes, no length prefix.
            0x30 => SqlValue.FromByte(reader.ReadByte()),
            0x32 => SqlValue.FromBoolean(reader.ReadByte() != 0),
            0x34 => SqlValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(reader.ReadBytes(2))),
            0x38 => SqlValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(reader.ReadBytes(4))),
            0x7F => SqlValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(reader.ReadBytes(8))),
            0x3B => SqlValue.FromSingle(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(reader.ReadBytes(4)))),
            0x3E => SqlValue.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(reader.ReadBytes(8)))),
            0x7A => SqlValue.FromMoney(SqlType.SmallMoney, BinaryPrimitives.ReadInt32LittleEndian(reader.ReadBytes(4)) / 10000m),
            0x3C => ReadFixedMoney(reader),
            0x3A => ReadFixedSmallDateTime(reader),
            0x3D => ReadFixedDateTime(reader),
            0x26 => this.ReadIntN(reader),
            0x68 => ByteLenNull(reader, SqlType.Bit) ?? SqlValue.FromBoolean(reader.ReadByte() != 0),
            0x6D => this.ReadFloatN(reader),
            0x6E => this.ReadMoneyN(reader),
            0x6F => this.ReadDateTimeN(reader),
            0x24 => ByteLenNull(reader, SqlType.UniqueIdentifier) ?? SqlValue.FromGuid(new Guid(reader.ReadBytes(16))),
            0x6A or 0x6C => this.ReadDecimal(reader),
            0x28 => ByteLenNull(reader, SqlType.Date) ?? SqlValue.FromDate(DateOnly.FromDayNumber(ReadThreeByteInt(reader.ReadBytes(3)))),
            0x29 => this.ReadTime(reader),
            0x2A => this.ReadDateTime2(reader),
            0x2B => this.ReadDateTimeOffset(reader),
            0xA7 or 0xAF => this.ReadAnsiString(reader),
            0xE7 or 0xEF => this.ReadNationalString(reader),
            0xA5 or 0xAD => this.ReadBinary(reader),
            0xF1 => ReadXml(reader),
            _ => throw new InvalidDataException($"No client-value decoder for type token 0x{token:X2}."),
        };

        private SqlValue ReadIntN(TdsValueReader reader)
        {
            var length = reader.ReadByte();
            if (length == 0)
                return SqlValue.Null(IntType(declaredOrMax));
            var raw = AssembleLittleEndian(reader.ReadBytes(length));
            return declaredOrMax switch
            {
                1 => SqlValue.FromByte((byte)raw),
                2 => SqlValue.FromInt16((short)raw),
                4 => SqlValue.FromInt32((int)raw),
                _ => SqlValue.FromInt64(raw),
            };
        }

        private SqlValue ReadFloatN(TdsValueReader reader)
        {
            var length = reader.ReadByte();
            if (length == 0)
                return SqlValue.Null(declaredOrMax == 4 ? SqlType.Real : SqlType.Float);
            var payload = reader.ReadBytes(length);
            return length == 4
                ? SqlValue.FromSingle(BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload)))
                : SqlValue.FromDouble(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload)));
        }

        private SqlValue ReadMoneyN(TdsValueReader reader)
        {
            var length = reader.ReadByte();
            if (length == 0)
                return SqlValue.Null(declaredOrMax == 4 ? SqlType.SmallMoney : SqlType.Money);
            var payload = reader.ReadBytes(length);
            long scaled;
            if (length == 4)
            {
                scaled = BinaryPrimitives.ReadInt32LittleEndian(payload);
            }
            else
            {
                var high = BinaryPrimitives.ReadInt32LittleEndian(payload);
                var low = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                scaled = ((long)high << 32) | low;
            }

            return SqlValue.FromMoney(length == 4 ? SqlType.SmallMoney : SqlType.Money, scaled / 10000m);
        }

        private SqlValue ReadDateTimeN(TdsValueReader reader)
        {
            var length = reader.ReadByte();
            if (length == 0)
                return SqlValue.Null(declaredOrMax == 4 ? SqlType.SmallDateTime : SqlType.DateTime);
            var payload = reader.ReadBytes(length);
            if (length == 4)
            {
                var days = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                var minutes = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
                return SqlValue.FromSmallDateTime(Epoch1900.AddDays(days).AddMinutes(minutes));
            }

            var dtDays = BinaryPrimitives.ReadInt32LittleEndian(payload);
            var thirds = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            var ticks = (((long)thirds * 10_000_000) + 150) / 300;
            return SqlValue.FromDateTime(Epoch1900.AddDays(dtDays).AddTicks(ticks));
        }

        private SqlValue ReadDecimal(TdsValueReader reader)
        {
            // SqlClient writes every non-NULL numeric value as a sign byte
            // followed by a fixed 16-byte little-endian mantissa (17 value
            // bytes), regardless of the precision-implied width the length byte
            // reports (probe-confirmed 2026-07-18: decimal(5,2) and
            // decimal(12,3) both carry 17 value bytes behind length bytes 5 and
            // 9). The length byte only distinguishes NULL (0) from non-NULL.
            var type = SqlType.GetDecimal(precision, scale);
            if (reader.ReadByte() == 0)
                return SqlValue.Null(type);
            var isNegative = reader.ReadByte() != 1;
            var magnitude = reader.ReadBytes(16);
            for (var i = 12; i < 16; i++)
            {
                if (magnitude[i] != 0)
                    throw new NotSupportedException("A decimal client value exceeds the range of System.Decimal.");
            }

            var lo = BinaryPrimitives.ReadInt32LittleEndian(magnitude);
            var mid = BinaryPrimitives.ReadInt32LittleEndian(magnitude[4..]);
            var hi = BinaryPrimitives.ReadInt32LittleEndian(magnitude[8..]);
            return SqlValue.FromDecimal(type, new decimal(lo, mid, hi, isNegative, scale));
        }

        private SqlValue ReadTime(TdsValueReader reader)
        {
            var length = reader.ReadByte();
            var type = SqlType.GetTime(scale);
            if (length == 0)
                return SqlValue.Null(type);
            var ticks = ScaledUnitsToTicks(AssembleLittleEndian(reader.ReadBytes(length)), scale);
            return SqlValue.FromTime(type, TimeSpan.FromTicks(ticks));
        }

        private SqlValue ReadDateTime2(TdsValueReader reader)
        {
            var length = reader.ReadByte();
            var type = SqlType.GetDateTime2(scale);
            if (length == 0)
                return SqlValue.Null(type);
            var timeBytes = length - 3;
            var payload = reader.ReadBytes(length);
            var ticks = ScaledUnitsToTicks(AssembleLittleEndian(payload[..timeBytes]), scale);
            var days = ReadThreeByteInt(payload[timeBytes..]);
            return SqlValue.FromDateTime2(type, DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).AddTicks(ticks));
        }

        private SqlValue ReadDateTimeOffset(TdsValueReader reader)
        {
            var length = reader.ReadByte();
            var type = SqlType.GetDateTimeOffset(scale);
            if (length == 0)
                return SqlValue.Null(type);
            var timeBytes = length - 5;
            var payload = reader.ReadBytes(length);
            var ticks = ScaledUnitsToTicks(AssembleLittleEndian(payload[..timeBytes]), scale);
            var days = ReadThreeByteInt(payload[timeBytes..(timeBytes + 3)]);
            var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(payload[(timeBytes + 3)..]);
            var offset = TimeSpan.FromMinutes(offsetMinutes);
            var utcTicks = DateOnly.FromDayNumber(days).ToDateTime(TimeOnly.MinValue).Ticks + ticks;
            return SqlValue.FromDateTimeOffset(type, new DateTimeOffset(utcTicks + offset.Ticks, offset));
        }

        private SqlValue ReadAnsiString(TdsValueReader reader)
        {
            var encoding = utf8 ? Encoding.UTF8 : CharSqlType.Cp1252Encoder;
            var bytes = this.ReadStringBytes(reader);
            return bytes is null ? SqlValue.Null(SqlType.Varchar) : SqlValue.FromVarchar(encoding.GetString(bytes));
        }

        private SqlValue ReadNationalString(TdsValueReader reader)
        {
            var bytes = this.ReadStringBytes(reader);
            return bytes is null ? SqlValue.Null(SqlType.NVarchar) : SqlValue.FromNVarchar(Encoding.Unicode.GetString(bytes));
        }

        private byte[]? ReadStringBytes(TdsValueReader reader)
        {
            if (declaredOrMax == 0xFFFF)
                return ReadPlp(reader);
            var length = reader.ReadUInt16();
            return length == 0xFFFF ? null : reader.ReadBytes(length).ToArray();
        }

        private SqlValue ReadBinary(TdsValueReader reader)
        {
            if (declaredOrMax == 0xFFFF)
            {
                var plp = ReadPlp(reader);
                return plp is null ? SqlValue.Null(SqlType.VarbinaryMax) : SqlValue.FromVarbinary(plp);
            }

            var length = reader.ReadUInt16();
            return length == 0xFFFF ? SqlValue.Null(SqlType.Varbinary) : SqlValue.FromVarbinary(reader.ReadBytes(length).ToArray());
        }

        private static SqlValue ReadXml(TdsValueReader reader)
        {
            var bytes = ReadPlp(reader);
            if (bytes is null)
                return SqlValue.Null(SqlType.Xml);
            var value = Encoding.Unicode.GetString(bytes);
            if (value is ['\uFEFF', ..])
                value = value[1..];
            return SqlValue.FromXml(value);
        }

        private static SqlType IntType(int declaredLength) => declaredLength switch
        {
            1 => SqlType.TinyInt,
            2 => SqlType.SmallInt,
            4 => SqlType.Int32,
            _ => SqlType.BigInt,
        };

        private static SqlValue? ByteLenNull(TdsValueReader reader, SqlType nullType) =>
            reader.ReadByte() == 0 ? SqlValue.Null(nullType) : null;

        private static SqlValue ReadFixedMoney(TdsValueReader reader)
        {
            var payload = reader.ReadBytes(8);
            var high = BinaryPrimitives.ReadInt32LittleEndian(payload);
            var low = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            return SqlValue.FromMoney(SqlType.Money, (((long)high << 32) | low) / 10000m);
        }

        private static SqlValue ReadFixedSmallDateTime(TdsValueReader reader)
        {
            var payload = reader.ReadBytes(4);
            var days = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            var minutes = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
            return SqlValue.FromSmallDateTime(Epoch1900.AddDays(days).AddMinutes(minutes));
        }

        private static SqlValue ReadFixedDateTime(TdsValueReader reader)
        {
            var payload = reader.ReadBytes(8);
            var days = BinaryPrimitives.ReadInt32LittleEndian(payload);
            var thirds = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
            var ticks = (((long)thirds * 10_000_000) + 150) / 300;
            return SqlValue.FromDateTime(Epoch1900.AddDays(days).AddTicks(ticks));
        }
    }

    private static byte[]? ReadPlp(TdsValueReader reader)
    {
        var total = reader.ReadUInt64();
        if (total == 0xFFFFFFFFFFFFFFFF)
            return null;

        using var accumulated = new MemoryStream();
        while (true)
        {
            var chunkLength = reader.ReadUInt32();
            if (chunkLength == 0)
                break;
            accumulated.Write(reader.ReadBytes((int)chunkLength));
        }

        return accumulated.ToArray();
    }

    private static long ScaledUnitsToTicks(long units, byte scale)
    {
        var factor = 1L;
        for (var i = scale; i < 7; i++)
            factor *= 10;
        return units * factor;
    }

    private static long AssembleLittleEndian(ReadOnlySpan<byte> bytes)
    {
        var value = 0L;
        for (var i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);
        return value;
    }

    private static int ReadThreeByteInt(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}
