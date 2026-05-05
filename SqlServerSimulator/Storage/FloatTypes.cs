using System.Buffers.Binary;
using System.Globalization;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>float</c> (8-byte IEEE 754 double). <c>float(N)</c> with
/// <c>N ≤ 24</c> resolves to <see cref="SqlType.Real"/> instead — that
/// dispatch lives in <see cref="SqlType.GetByName"/>; here the type is
/// always 8-byte. Empty-string CAST yields 0 (verified against SQL Server
/// 2025 — distinct from <c>decimal</c>, where empty raises Msg 8114).
/// </summary>
internal sealed class FloatSqlType() : SqlType(SqlTypeCategory.Approximate)
{
    public override bool IsFixedLength => true;

    public override int FixedLength => 8;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(destination, value.AsDouble);
        return 8;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromDouble(BinaryPrimitives.ReadDoubleLittleEndian(source));

    public override SqlValue ConvertParameter(object raw)
        => SqlValue.FromDouble(Convert.ToDouble(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "float";
}

/// <summary>
/// SQL Server's <c>real</c> (4-byte IEEE 754 single). Equivalent to
/// <c>float(24)</c>.
/// </summary>
internal sealed class RealSqlType() : SqlType(SqlTypeCategory.Approximate)
{
    public override bool IsFixedLength => true;

    public override int FixedLength => 4;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination, value.AsSingle);
        return 4;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromSingle(BinaryPrimitives.ReadSingleLittleEndian(source));

    public override SqlValue ConvertParameter(object raw)
        => SqlValue.FromSingle(Convert.ToSingle(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "real";
}
