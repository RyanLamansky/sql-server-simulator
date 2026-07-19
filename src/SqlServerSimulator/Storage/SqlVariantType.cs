using System.Buffers.Binary;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>sql_variant</c> data type — a value slot that carries its
/// own base type per cell. The simulator models it as a wrapper around an
/// inner <see cref="SqlValue"/>: a variant value stores the inner value in the
/// reference slot, so a single <c>sql_variant</c>-typed column can surface an
/// <c>int</c> on one row and a <c>bit</c> or <c>nvarchar</c> on the next
/// (the shape <c>sys.database_scoped_configurations.value</c> requires).
/// </summary>
/// <remarks>
/// <para>
/// The inner value is any non-variant, non-MAX, non-LOB base type — the set
/// SQL Server allows inside a sql_variant. The storage codec below serializes
/// a compact inner-type descriptor (a 1-byte kind plus per-family parameters)
/// followed by the inner value's own byte form, so a variant column round-trips
/// through <see cref="RowEncoder"/> / <see cref="RowDecoder"/> byte-for-byte.
/// The on-disk descriptor is simulator-internal — it is not SQL Server's
/// documented sql_variant storage layout, which isn't publicly specified. The
/// TDS wire form (a separate encoding in <c>Network/TdsTypeCodec.cs</c>) is the
/// MS-TDS-specified format SqlClient decodes.
/// </para>
/// </remarks>
internal sealed class SqlVariantSqlType() : SqlType(SqlTypeCategory.Other)
{
    // Untyped accessors surface the inner value's CLR type per row, so the
    // column-level field type is object — matching SqlClient's GetFieldType
    // for a sql_variant column.
    public override Type ClrType => typeof(object);

    public override string SqlServerName => "sql_variant";

    public override bool IsFixedLength => false;

    public override int GetVariableByteCount(SqlValue value) => ByteCount(value.AsVariantInner);

    public override int Encode(SqlValue value, Span<byte> destination) => EncodeInner(value.AsVariantInner, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVariant(DecodeInner(source));

    public override string ToString() => "sql_variant";

    // Inner-type discriminators for the storage descriptor. Simulator-internal;
    // stable only within a single build's encode/decode round-trip.
    private const byte KindBit = 0;
    private const byte KindTinyInt = 1;
    private const byte KindSmallInt = 2;
    private const byte KindInt = 3;
    private const byte KindBigInt = 4;
    private const byte KindReal = 5;
    private const byte KindFloat = 6;
    private const byte KindMoney = 7;
    private const byte KindSmallMoney = 8;
    private const byte KindDate = 9;
    private const byte KindDateTime = 10;
    private const byte KindSmallDateTime = 11;
    private const byte KindGuid = 12;
    private const byte KindDecimal = 13;
    private const byte KindTime = 14;
    private const byte KindDateTime2 = 15;
    private const byte KindDateTimeOffset = 16;
    private const byte KindVarchar = 17;
    private const byte KindNVarchar = 18;
    private const byte KindChar = 19;
    private const byte KindNChar = 20;
    private const byte KindSysname = 21;
    private const byte KindBinary = 22;
    private const byte KindVarbinary = 23;

    // The set of storable inner types (everything except MAX / LOB / xml /
    // spatial / hierarchyid / rowversion) is enforced by the default arm of
    // ByteCount / EncodeInner throwing NotSupportedException — the same set
    // SQL_VARIANT_PROPERTY reports NULL for.
    private static int ByteCount(SqlValue inner) => inner.Type switch
    {
        BitSqlType => 2,
        _ when inner.Type.IsFixedLength && inner.Type is not (DecimalSqlType or TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType or CharSqlType or NCharSqlType or BinarySqlType)
            => 1 + inner.Type.FixedLength,
        DecimalSqlType d => 3 + d.FixedLength,
        TimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType => 2 + inner.Type.FixedLength,
        SystemNameSqlType => 1 + 4 + Encoding.Unicode.GetByteCount(inner.AsString),
        CharSqlType or NCharSqlType or VarcharSqlType or NVarcharSqlType => StringByteCount(inner),
        BinarySqlType or VarbinarySqlType => BinaryByteCount(inner),
        _ => throw new NotSupportedException($"{inner.Type} cannot be stored inside a sql_variant."),
    };

    private static int StringByteCount(SqlValue inner)
    {
        var collLen = Encoding.ASCII.GetByteCount((inner.Type.Collation ?? Collation.Baseline).Name);
        var payload = inner.Type.IsFixedLength ? inner.Type.FixedLength : inner.Type.GetVariableByteCount(inner);
        return 1 + 2 + 1 + collLen + 4 + payload;
    }

    private static int BinaryByteCount(SqlValue inner)
    {
        var payload = inner.Type.IsFixedLength ? inner.Type.FixedLength : inner.Type.GetVariableByteCount(inner);
        return 1 + 2 + 4 + payload;
    }

    private static int EncodeInner(SqlValue inner, Span<byte> destination)
    {
        var t = inner.Type;
        switch (t)
        {
            case BitSqlType:
                destination[0] = KindBit;
                destination[1] = inner.AsBoolean ? (byte)1 : (byte)0;
                return 2;
            case TinyIntSqlType: return EncodeFixed(inner, destination, KindTinyInt);
            case SmallIntSqlType: return EncodeFixed(inner, destination, KindSmallInt);
            case Int32SqlType: return EncodeFixed(inner, destination, KindInt);
            case BigIntSqlType: return EncodeFixed(inner, destination, KindBigInt);
            case RealSqlType: return EncodeFixed(inner, destination, KindReal);
            case FloatSqlType: return EncodeFixed(inner, destination, KindFloat);
            case MoneySqlType: return EncodeFixed(inner, destination, KindMoney);
            case SmallMoneySqlType: return EncodeFixed(inner, destination, KindSmallMoney);
            case DateSqlType: return EncodeFixed(inner, destination, KindDate);
            case DateTimeSqlType: return EncodeFixed(inner, destination, KindDateTime);
            case SmallDateTimeSqlType: return EncodeFixed(inner, destination, KindSmallDateTime);
            case UniqueIdentifierSqlType: return EncodeFixed(inner, destination, KindGuid);
            case DecimalSqlType d:
                destination[0] = KindDecimal;
                destination[1] = d.precision;
                destination[2] = d.scale;
                _ = t.Encode(inner, destination[3..(3 + d.FixedLength)]);
                return 3 + d.FixedLength;
            case TimeSqlType tm: return EncodePrecision(inner, destination, KindTime, (byte)tm.precision);
            case DateTime2SqlType dt2: return EncodePrecision(inner, destination, KindDateTime2, (byte)dt2.precision);
            case DateTimeOffsetSqlType dto: return EncodePrecision(inner, destination, KindDateTimeOffset, (byte)dto.precision);
            case SystemNameSqlType:
                {
                    destination[0] = KindSysname;
                    var bytes = Encoding.Unicode.GetBytes(inner.AsString);
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(1, 4), bytes.Length);
                    bytes.CopyTo(destination[5..]);
                    return 5 + bytes.Length;
                }

            case VarcharSqlType v: return EncodeString(inner, destination, KindVarchar, v.length);
            case NVarcharSqlType nv: return EncodeString(inner, destination, KindNVarchar, nv.length);
            case CharSqlType c: return EncodeString(inner, destination, KindChar, c.length);
            case NCharSqlType nc: return EncodeString(inner, destination, KindNChar, nc.length);
            case BinarySqlType b: return EncodeBinary(inner, destination, KindBinary, b.length);
            case VarbinarySqlType vb: return EncodeBinary(inner, destination, KindVarbinary, vb.length);
            default:
                throw new NotSupportedException($"{t} cannot be stored inside a sql_variant.");
        }
    }

    private static int EncodeFixed(SqlValue inner, Span<byte> destination, byte kind)
    {
        destination[0] = kind;
        var width = inner.Type.FixedLength;
        _ = inner.Type.Encode(inner, destination.Slice(1, width));
        return 1 + width;
    }

    private static int EncodePrecision(SqlValue inner, Span<byte> destination, byte kind, byte precision)
    {
        destination[0] = kind;
        destination[1] = precision;
        var width = inner.Type.FixedLength;
        _ = inner.Type.Encode(inner, destination.Slice(2, width));
        return 2 + width;
    }

    private static int EncodeString(SqlValue inner, Span<byte> destination, byte kind, short declaredLength)
    {
        destination[0] = kind;
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(1, 2), declaredLength);
        var collName = Encoding.ASCII.GetBytes((inner.Type.Collation ?? Collation.Baseline).Name);
        destination[3] = (byte)collName.Length;
        collName.CopyTo(destination[4..]);
        var payloadStart = 4 + collName.Length;
        var payloadLen = inner.Type.IsFixedLength ? inner.Type.FixedLength : inner.Type.GetVariableByteCount(inner);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(payloadStart, 4), payloadLen);
        _ = inner.Type.Encode(inner, destination.Slice(payloadStart + 4, payloadLen));
        return payloadStart + 4 + payloadLen;
    }

    private static int EncodeBinary(SqlValue inner, Span<byte> destination, byte kind, short declaredLength)
    {
        destination[0] = kind;
        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(1, 2), declaredLength);
        var payloadLen = inner.Type.IsFixedLength ? inner.Type.FixedLength : inner.Type.GetVariableByteCount(inner);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(3, 4), payloadLen);
        _ = inner.Type.Encode(inner, destination.Slice(7, payloadLen));
        return 7 + payloadLen;
    }

    private static SqlValue DecodeInner(ReadOnlySpan<byte> source)
    {
        var kind = source[0];
        switch (kind)
        {
            case KindBit: return SqlValue.FromBoolean(source[1] != 0);
            case KindTinyInt: return SqlType.TinyInt.Decode(source.Slice(1, SqlType.TinyInt.FixedLength));
            case KindSmallInt: return SqlType.SmallInt.Decode(source.Slice(1, SqlType.SmallInt.FixedLength));
            case KindInt: return SqlType.Int32.Decode(source.Slice(1, SqlType.Int32.FixedLength));
            case KindBigInt: return SqlType.BigInt.Decode(source.Slice(1, SqlType.BigInt.FixedLength));
            case KindReal: return SqlType.Real.Decode(source.Slice(1, SqlType.Real.FixedLength));
            case KindFloat: return SqlType.Float.Decode(source.Slice(1, SqlType.Float.FixedLength));
            case KindMoney: return SqlType.Money.Decode(source.Slice(1, SqlType.Money.FixedLength));
            case KindSmallMoney: return SqlType.SmallMoney.Decode(source.Slice(1, SqlType.SmallMoney.FixedLength));
            case KindDate: return SqlType.Date.Decode(source.Slice(1, SqlType.Date.FixedLength));
            case KindDateTime: return SqlType.DateTime.Decode(source.Slice(1, SqlType.DateTime.FixedLength));
            case KindSmallDateTime: return SqlType.SmallDateTime.Decode(source.Slice(1, SqlType.SmallDateTime.FixedLength));
            case KindGuid: return SqlType.UniqueIdentifier.Decode(source.Slice(1, SqlType.UniqueIdentifier.FixedLength));
            case KindDecimal:
                var dec = SqlType.GetDecimal(source[1], source[2]);
                return dec.Decode(source.Slice(3, dec.FixedLength));
            case KindTime:
                var tm = SqlType.GetTime(source[1]);
                return tm.Decode(source.Slice(2, tm.FixedLength));
            case KindDateTime2:
                var dt2 = SqlType.GetDateTime2(source[1]);
                return dt2.Decode(source.Slice(2, dt2.FixedLength));
            case KindDateTimeOffset:
                var dto = SqlType.GetDateTimeOffset(source[1]);
                return dto.Decode(source.Slice(2, dto.FixedLength));
            case KindSysname:
                var slen = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(1, 4));
                return SqlType.SystemName.Decode(source.Slice(5, slen));
            case KindVarchar or KindNVarchar or KindChar or KindNChar:
                return DecodeString(kind, source);
            case KindBinary or KindVarbinary:
                return DecodeBinary(kind, source);
            default:
                throw new InvalidDataException($"Unknown sql_variant inner kind 0x{kind:X2}.");
        }
    }

    private static SqlValue DecodeString(byte kind, ReadOnlySpan<byte> source)
    {
        var declaredLength = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(1, 2));
        var collLen = source[3];
        var collation = Collation.Get(Encoding.ASCII.GetString(source.Slice(4, collLen)));
        var payloadStart = 4 + collLen;
        var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(payloadStart, 4));
        var payload = source.Slice(payloadStart + 4, payloadLen);
        SqlType type = kind switch
        {
            KindVarchar => VarcharSqlType.Get(declaredLength, collation, Coercibility.CoercibleDefault),
            KindNVarchar => NVarcharSqlType.Get(declaredLength, collation, Coercibility.CoercibleDefault),
            KindChar => CharSqlType.Get(declaredLength, collation, Coercibility.CoercibleDefault),
            _ => NCharSqlType.Get(declaredLength, collation, Coercibility.CoercibleDefault),
        };
        return type.Decode(payload);
    }

    private static SqlValue DecodeBinary(byte kind, ReadOnlySpan<byte> source)
    {
        var declaredLength = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(1, 2));
        var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(3, 4));
        var payload = source.Slice(7, payloadLen);
        var type = kind == KindBinary ? SqlType.GetBinary(declaredLength) : VarbinarySqlType.Get(declaredLength);
        return type.Decode(payload);
    }
}
