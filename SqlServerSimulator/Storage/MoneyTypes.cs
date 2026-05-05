using System.Buffers.Binary;
using System.Globalization;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>money</c> (8-byte fixed-scale-4 currency) and
/// <c>smallmoney</c> (4-byte) types. Storage is a scaled signed integer:
/// the wire value <c>5.95</c> rides as <c>59500</c>. Money is treated as
/// <c>decimal(19, 4)</c> for arithmetic precision-promotion (smallmoney as
/// <c>decimal(10, 4)</c>); the per-operator decimal formulas already cover
/// the cross-promotion path.
/// </summary>
internal sealed class MoneySqlType() : SqlType(SqlTypeCategory.Money)
{
    /// <summary>Number of fractional decimal digits represented in the scaled int.</summary>
    public const int Scale = 4;

    /// <summary>10^Scale — multiplier between the user-visible decimal and the stored scaled int.</summary>
    public const long ScaleFactor = 10_000;

    public override bool IsFixedLength => true;

    public override int FixedLength => 8;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.AsMoneyScaledUnits);
        return 8;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromMoneyScaledUnits(this, BinaryPrimitives.ReadInt64LittleEndian(source));

    public override SqlValue ConvertParameter(object raw) =>
        SqlValue.FromMoney(this, Convert.ToDecimal(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "money";
}

/// <summary>
/// SQL Server's <c>smallmoney</c>: 4-byte scaled int32, scale 4, range
/// <c>[-214748.3648, 214748.3647]</c>.
/// </summary>
internal sealed class SmallMoneySqlType() : SqlType(SqlTypeCategory.Money)
{
    public override bool IsFixedLength => true;

    public override int FixedLength => 4;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, checked((int)value.AsMoneyScaledUnits));
        return 4;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
        => SqlValue.FromMoneyScaledUnits(this, BinaryPrimitives.ReadInt32LittleEndian(source));

    public override SqlValue ConvertParameter(object raw) =>
        SqlValue.FromMoney(this, Convert.ToDecimal(raw, CultureInfo.InvariantCulture));

    public override string ToString() => "smallmoney";
}
