using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server's <c>decimal(p, s)</c> (and its alias <c>numeric(p, s)</c>):
/// fixed-point exact-precision number with declared <see cref="precision"/>
/// (1-38, total significant digits) and <see cref="scale"/>
/// (0 ≤ s ≤ p, digits after the decimal point). On-disk storage width
/// matches SQL Server: 5 bytes for p ≤ 9, 9 for 10-19, 13 for 20-28,
/// 17 for 29-38. Each (p, s) pair is a distinct singleton; reference
/// equality flows through the type-identity pattern used elsewhere.
/// </summary>
/// <remarks>
/// In-memory values are <see cref="Decimal38"/> — the full 38 significant
/// digits real carries — boxed in <see cref="SqlValue"/>'s reference slot.
/// The on-disk form is the probed one: a sign byte (0x00 negative, 0x01
/// non-negative) then the absolute mantissa little-endian, scaled to the
/// declared scale and zero-padded to the type's storage width.
/// </remarks>
internal sealed class DecimalSqlType(byte precision, byte scale) : SqlType(SqlTypeCategory.Decimal)
{
    public readonly byte precision = precision;
    public readonly byte scale = scale;

    public override Type ClrType => typeof(decimal);

    public override string SqlServerName => "decimal";

    public override bool IsFixedLength => true;

    public override int FixedLength => StorageWidth(this.precision);

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        // Sign byte (0x00 = negative, 0x01 = non-negative; matches the
        // documented SQL Server convention, probe-confirmed against
        // CAST(<decimal> AS varbinary)) followed by the absolute mantissa as a
        // little-endian unsigned integer already scaled to the declared scale,
        // zero-padded to the type's storage width.
        var width = StorageWidth(this.precision);
        destination[..width].Clear();
        var d = value.AsDecimal38;
        destination[0] = d.IsNegative ? (byte)0x00 : (byte)0x01;

        // The value carries the declared scale by construction, so the mantissa
        // is the magnitude as it stands; 38 digits need 127 bits, which the
        // 16 mantissa bytes of the widest tier hold.
        Span<byte> mantissa = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(mantissa, d.Magnitude);
        mantissa[..(width - 1)].CopyTo(destination[1..]);
        return width;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        var negative = source[0] == 0x00;
        Span<byte> mantissa = stackalloc byte[16];
        source[1..Math.Min(source.Length, 17)].CopyTo(mantissa);
        var magnitude = BinaryPrimitives.ReadUInt128LittleEndian(mantissa);
        return SqlValue.FromDecimal(this, Decimal38.FromParts(magnitude, negative, this.scale));
    }

    public override SqlValue ConvertParameter(object raw)
    {
        // A value that arrived at full width (a non-SqlClient driver's RPC
        // decimal) keeps it; the declared scale still wins when it is the wider
        // of the two, matching the .NET-decimal path below.
        if (raw is Decimal38 wide)
            return SqlValue.FromDecimal(wide.Scale <= this.scale ? this : Get(Decimal38.MaxPrecision, wide.Scale), wide);

        var value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        // The .NET decimal carries its own scale in the high bits of its
        // flags word — preserve it in the bound SqlValue so a parameter of
        // <c>123.45m</c> doesn't lose its fractional part to a default
        // <c>decimal(18, 0)</c> mapping when the caller didn't supply
        // explicit Precision/Scale on the DbParameter.
        var valueScale = (decimal.GetBits(value)[3] >> 16) & 0xFF;
        return valueScale <= this.scale
            ? SqlValue.FromDecimal(this, value)
            // Widen to a type that fits the natural scale. A .NET decimal's
            // mantissa is 96 bits, so 29 digits of precision always hold it.
            : SqlValue.FromDecimal(Get(Math.Max(29, valueScale), valueScale), value);
    }

    public override string ToString() => $"decimal({this.precision},{this.scale})";

    /// <summary>
    /// SQL Server's documented byte width for <c>decimal(p, *)</c>: 5/9/13/17
    /// for the four precision tiers (1-9, 10-19, 20-28, 29-38). One byte for
    /// the sign, the rest for the unsigned mantissa.
    /// </summary>
    public static int StorageWidth(int precision) => precision switch
    {
        <= 9 => 5,
        <= 19 => 9,
        <= 28 => 13,
        <= 38 => 17,
        _ => throw new ArgumentOutOfRangeException(nameof(precision)),
    };

    /// <summary>
    /// The width <c>DATALENGTH</c> reports, which is <em>not</em>
    /// <see cref="StorageWidth"/>: real sizes a numeric by the magnitude the
    /// value actually holds rather than by its declared precision, so
    /// <c>CAST(1 AS decimal(38, 0))</c> reports 5 while the column storing it
    /// occupies 17. Same sign byte, but only as many 32-bit mantissa words as
    /// the unscaled magnitude needs — so the scale counts, and
    /// <c>decimal(38, 10)</c> holding <c>1.0</c> reports 9 for an unscaled
    /// 10^10. Uniform across columns, variables, literals, arithmetic and
    /// aggregate results; every band boundary probe-confirmed against
    /// SQL Server 2025.
    /// </summary>
    public static int ValueWidth(UInt128 magnitude)
        => magnitude <= uint.MaxValue ? 5
        : magnitude <= ulong.MaxValue ? 9
        : magnitude < ((UInt128)1 << 96) ? 13
        : 17;

    /// <summary>
    /// Resolves <c>decimal(precision, scale)</c> to its singleton instance.
    /// Validates ranges (1 ≤ p ≤ 38, 0 ≤ s ≤ p) before caching; lazy
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> keeps the working set
    /// small (CREATE TABLE only references a handful of (p, s) pairs in
    /// practice).
    /// </summary>
    public static DecimalSqlType Get(int precision, int scale)
    {
        if (precision is < 1 or > 38)
            throw new ArgumentOutOfRangeException(nameof(precision), $"decimal precision must be 1-38; got {precision}.");
        if (scale < 0 || scale > precision)
            throw new ArgumentOutOfRangeException(nameof(scale), $"decimal scale must be 0-{precision}; got {scale}.");
        var key = ((byte)precision, (byte)scale);
        return cache.GetOrAdd(key, k => new DecimalSqlType(k.Precision, k.Scale));
    }

    private static readonly ConcurrentDictionary<(byte Precision, byte Scale), DecimalSqlType> cache = new();
}
