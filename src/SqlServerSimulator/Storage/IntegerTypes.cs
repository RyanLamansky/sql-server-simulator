using System.Buffers.Binary;
using System.Globalization;

namespace SqlServerSimulator.Storage;

internal sealed class Int32SqlType() : SqlType(SqlTypeCategory.Integer)
{
    public override Type ClrType => typeof(int);

    public override bool IsFixedLength => true;

    public override int FixedLength => 4;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, value.AsInt32);
        return 4;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromInt32(BinaryPrimitives.ReadInt32LittleEndian(source));

    public override SqlValue ConvertParameter(object raw)
        => SqlValue.FromInt32(Convert.ToInt32(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "int";
}

internal sealed class BigIntSqlType() : SqlType(SqlTypeCategory.Integer)
{
    public override Type ClrType => typeof(long);

    public override bool IsFixedLength => true;

    public override int FixedLength => 8;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.AsInt64);
        return 8;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromInt64(BinaryPrimitives.ReadInt64LittleEndian(source));

    public override SqlValue ConvertParameter(object raw)
        => SqlValue.FromInt64(Convert.ToInt64(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "bigint";
}

internal sealed class SmallIntSqlType() : SqlType(SqlTypeCategory.Integer)
{
    public override Type ClrType => typeof(short);

    public override bool IsFixedLength => true;

    public override int FixedLength => 2;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt16LittleEndian(destination, value.AsInt16);
        return 2;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromInt16(BinaryPrimitives.ReadInt16LittleEndian(source));

    public override SqlValue ConvertParameter(object raw)
        => SqlValue.FromInt16(Convert.ToInt16(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "smallint";
}

/// <remarks>
/// SQL Server's <c>tinyint</c> is unsigned 0-255, stored as a single byte.
/// </remarks>
internal sealed class TinyIntSqlType() : SqlType(SqlTypeCategory.Integer)
{
    public override Type ClrType => typeof(byte);

    public override bool IsFixedLength => true;

    public override int FixedLength => 1;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        destination[0] = value.AsByte;
        return 1;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromByte(source[0]);

    public override SqlValue ConvertParameter(object raw)
        => SqlValue.FromByte(Convert.ToByte(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "tinyint";
}

/// <remarks>
/// <see cref="FixedLength"/> is 1 (a single bit's standalone byte width),
/// but the row encoder doesn't actually use this path: it packs runs of
/// consecutive bit columns into shared bytes (8 bits per byte). The base
/// class's virtual <see cref="SqlType.Encode"/> / <see cref="SqlType.Decode"/>
/// throw if anyone routes a bit value through the standalone-encoding path
/// — that should never happen in practice, but the throw is a tripwire
/// that surfaces architectural drift if RowEncoder's bit-special-case
/// is ever removed without thinking it through.
/// </remarks>
internal sealed class BitSqlType() : SqlType(SqlTypeCategory.Integer)
{
    public override Type ClrType => typeof(bool);

    public override bool IsFixedLength => true;

    public override int FixedLength => 1;

    public override SqlValue ConvertParameter(object raw) => SqlValue.FromBoolean((bool)raw);

    public override string ToString() => "bit";
}
