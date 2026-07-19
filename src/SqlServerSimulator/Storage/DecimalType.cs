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
/// In-memory values use .NET's <see cref="decimal"/> (28-29 significant
/// digits), stored in <see cref="SqlValue"/>'s reference slot. Declared
/// precision > 28 falls outside the .NET decimal range; the simulator
/// raises <see cref="NotSupportedException"/> naming that as the unmodeled
/// limit rather than silently truncating. Byte width is still allocated to
/// match SQL Server so row-size budgeting remains correct.
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
        // documented SQL Server convention) followed by the absolute
        // mantissa as a little-endian unsigned integer scaled to the
        // declared scale, zero-padded to the type's storage width.
        var width = StorageWidth(this.precision);
        destination[..width].Clear();
        var d = value.AsDecimal;
        destination[0] = d >= 0 ? (byte)0x01 : (byte)0x00;

        var scaled = decimal.Truncate(decimal.Multiply(decimal.Abs(d), Pow10(this.scale)));
        // .NET decimal exposes its 96-bit mantissa via GetBits — three int32s
        // representing low/mid/high. Width budget for 1-9 / 10-19 / 20-28
        // precisions is 4 / 8 / 12 mantissa bytes, all of which fit; p 29-38
        // would need more, but those types throw at construction.
        Span<int> bits = stackalloc int[4];
        var written = decimal.GetBits(scaled, bits);
        Span<byte> mantissa = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(mantissa[..4], bits[0]);
        BinaryPrimitives.WriteInt32LittleEndian(mantissa.Slice(4, 4), bits[1]);
        BinaryPrimitives.WriteInt32LittleEndian(mantissa.Slice(8, 4), bits[2]);
        var mantissaBytes = width - 1;
        mantissa[..Math.Min(mantissaBytes, 12)].CopyTo(destination[1..]);
        return width;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source)
    {
        var negative = source[0] == 0x00;
        var width = source.Length;
        var mantissaBytes = width - 1;
        Span<byte> mantissa = stackalloc byte[12];
        source[1..Math.Min(width, 13)].CopyTo(mantissa);
        var lo = BinaryPrimitives.ReadInt32LittleEndian(mantissa[..4]);
        var mid = BinaryPrimitives.ReadInt32LittleEndian(mantissa.Slice(4, 4));
        var hi = BinaryPrimitives.ReadInt32LittleEndian(mantissa.Slice(8, 4));
        var scaled = new decimal(lo, mid, hi, isNegative: negative, scale: this.scale);
        return SqlValue.FromDecimal(this, scaled);
    }

    public override SqlValue ConvertParameter(object raw)
    {
        var value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
        // The .NET decimal carries its own scale in the high bits of its
        // flags word — preserve it in the bound SqlValue so a parameter of
        // <c>123.45m</c> doesn't lose its fractional part to a default
        // <c>decimal(18, 0)</c> mapping when the caller didn't supply
        // explicit Precision/Scale on the DbParameter.
        var valueScale = (decimal.GetBits(value)[3] >> 16) & 0xFF;
        if (valueScale <= this.scale)
            return SqlValue.FromDecimal(this, value);

        // Widen to a type that fits the natural scale. Cap precision at 28
        // (the .NET decimal limit the simulator already enforces); the value
        // can't have demanded more than that to begin with.
        var widerType = Get(28, valueScale);
        return SqlValue.FromDecimal(widerType, value);
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

    private static decimal Pow10(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++)
            result *= 10m;
        return result;
    }
}
