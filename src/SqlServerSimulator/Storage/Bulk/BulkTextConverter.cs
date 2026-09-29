using System.Globalization;
using System.Numerics;

namespace SqlServerSimulator.Storage.Bulk;

/// <summary>How a bulk field's text failed to become its column's value.</summary>
internal enum BulkConversionFailure : byte
{
    None,

    /// <summary>Msg 4864.</summary>
    TypeMismatch,

    /// <summary>Msg 4863.</summary>
    Truncation,

    /// <summary>Msg 4867.</summary>
    Overflow,
}

/// <summary>
/// Converts a character-mode bulk field to its column's type by the bulk
/// provider's own rules, which are not <c>CAST</c>'s: an integer takes
/// surrounding whitespace and a sign but no decimal point, <c>bit</c> only
/// <c>0</c> or <c>1</c>, <c>money</c> thousands separators but no currency
/// symbol, a binary field its hex digits without <c>0x</c>, and a string too
/// long for its column fails rather than truncating — Msg 4863 for the
/// <c>char</c> family and Msg 4864 for the <c>nchar</c> one (probed
/// 2026-09-29 against SQL Server 2025, one candidate string per row).
/// </summary>
internal static class BulkTextConverter
{
    /// <summary>
    /// Converts <paramref name="text"/> (never empty — an empty field is NULL
    /// before it gets here) to <paramref name="target"/>, or reports why not.
    /// The <c>xml</c>, <c>json</c> and <c>vector</c> targets convert as
    /// <c>CAST</c> does and raise its errors, which end the load.
    /// </summary>
    public static BulkConversionFailure TryConvert(string text, SqlType target, out SqlValue value)
    {
        value = default;
        switch (target)
        {
            case TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType:
                return TryInteger(text, target, out value);
            case BitSqlType:
                if (text is "0" or "1")
                {
                    value = SqlValue.FromBoolean(text == "1");
                    return BulkConversionFailure.None;
                }
                return BulkConversionFailure.TypeMismatch;
            case DecimalSqlType decimalType:
                return TryDecimal(text, decimalType, out value);
            case MoneySqlType or SmallMoneySqlType:
                return TryMoney(text, target, out value);
            case FloatSqlType or RealSqlType:
                return TryFloat(text, target, out value);
            case DateSqlType or DateTimeSqlType or SmallDateTimeSqlType or DateTime2SqlType or TimeSqlType or DateTimeOffsetSqlType:
                return TryTemporal(text, target, out value);
            case UniqueIdentifierSqlType:
                return TryCast(text, target, out value);
            case BinarySqlType or VarbinarySqlType or ImageSqlType:
                return TryBinary(text, target, out value);
            case HierarchyIdSqlType or SpatialSqlType or ClrUdtSqlType:
                {
                    var failure = TryBinary(text, SqlType.VarbinaryMax, out var bytes);
                    if (failure != BulkConversionFailure.None)
                        return failure;
                    try
                    {
                        value = bytes.CoerceTo(target);
                        return BulkConversionFailure.None;
                    }
                    catch (Exception ex) when (ex is SimulatedSqlException or FormatException or OverflowException or NotSupportedException or ArgumentException)
                    {
                        return BulkConversionFailure.TypeMismatch;
                    }
                }
            case CharSqlType or VarcharSqlType or TextSqlType:
                {
                    var maxBytes = target switch
                    {
                        CharSqlType c => c.length,
                        VarcharSqlType { length: > 0 } v => v.length,
                        _ => int.MaxValue,
                    };
                    if (target.Collation!.StorageEncoding.GetByteCount(text) > maxBytes)
                        return BulkConversionFailure.Truncation;
                    value = SqlValue.FromVarchar(text).CoerceTo(target);
                    return BulkConversionFailure.None;
                }
            case NCharSqlType or NVarcharSqlType or NTextSqlType or SystemNameSqlType:
                {
                    var maxChars = target switch
                    {
                        NCharSqlType c => c.length,
                        NVarcharSqlType { length: > 0 } v => v.length,
                        SystemNameSqlType => 128,
                        _ => int.MaxValue,
                    };
                    if (text.Length > maxChars)
                        return BulkConversionFailure.TypeMismatch;
                    value = SqlValue.FromNVarchar(text).CoerceTo(target);
                    return BulkConversionFailure.None;
                }
            case SqlVariantSqlType:
                value = SqlValue.FromVarchar(text).CoerceTo(target);
                return BulkConversionFailure.None;
            default:
                // xml, json and vector parse as CAST does, and their errors
                // end the load (probed 2026-09-29: Msg 9400 and Msg 13609).
                value = SqlValue.FromNVarchar(text).CoerceTo(target);
                return BulkConversionFailure.None;
        }
    }

    /// <summary>
    /// Surrounding whitespace, a sign followed by any whitespace, then ASCII
    /// digits only (<c>- 3</c> and <c>0012</c> load, <c>1.0</c> and <c>1e2</c>
    /// don't); out of range is overflow, a negative <c>tinyint</c> included.
    /// </summary>
    private static BulkConversionFailure TryInteger(string text, SqlType target, out SqlValue value)
    {
        value = default;
        var span = text.AsSpan().Trim(" \t\n\r\v\f");
        var negative = false;
        if (span.Length > 0 && span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            span = span[1..].TrimStart(" \t\n\r\v\f");
        }
        if (span.IsEmpty)
            return BulkConversionFailure.TypeMismatch;
        BigInteger magnitude = 0;
        foreach (var c in span)
        {
            if (c is < '0' or > '9')
                return BulkConversionFailure.TypeMismatch;
            magnitude = (magnitude * 10) + (c - '0');
        }
        var number = negative ? -magnitude : magnitude;
        var (min, max) = target switch
        {
            TinyIntSqlType => (0L, byte.MaxValue),
            SmallIntSqlType => (short.MinValue, short.MaxValue),
            Int32SqlType => (int.MinValue, int.MaxValue),
            _ => (long.MinValue, long.MaxValue),
        };
        if (number < min || number > max)
            return BulkConversionFailure.Overflow;
        var n = (long)number;
        value = target switch
        {
            TinyIntSqlType => SqlValue.FromByte((byte)n),
            SmallIntSqlType => SqlValue.FromInt16((short)n),
            Int32SqlType => SqlValue.FromInt32((int)n),
            _ => SqlValue.FromInt64(n),
        };
        return BulkConversionFailure.None;
    }

    /// <summary>
    /// Reads <c>[sign] digits [. digits]</c> (either side may be empty, not
    /// both) with surrounding whitespace, allowing <c>,</c> in the integer
    /// part when <paramref name="allowGrouping"/>; no exponent. Returns the
    /// digits as a magnitude at <paramref name="scale"/>, rounded half away
    /// from zero, or false when the text isn't that shape.
    /// </summary>
    private static bool TryReadFixedPoint(string text, int scale, bool allowGrouping, out BigInteger magnitude, out bool negative, out int integerDigits)
    {
        magnitude = 0;
        negative = false;
        integerDigits = 0;
        var span = text.AsSpan().Trim(" \t\n\r\v\f");
        if (span.Length > 0 && span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            span = span[1..];
        }
        var sawDigit = false;
        var i = 0;
        for (; i < span.Length && (span[i] is >= '0' and <= '9' || (allowGrouping && span[i] == ',')); i++)
        {
            if (span[i] == ',')
                continue;
            magnitude = (magnitude * 10) + (span[i] - '0');
            if (magnitude != 0)
                integerDigits++;
            sawDigit = true;
        }
        var fractionDigits = 0;
        var roundUp = false;
        if (i < span.Length && span[i] == '.')
        {
            for (i++; i < span.Length && span[i] is >= '0' and <= '9'; i++)
            {
                sawDigit = true;
                if (fractionDigits < scale)
                {
                    magnitude = (magnitude * 10) + (span[i] - '0');
                    fractionDigits++;
                }
                else if (fractionDigits == scale)
                {
                    roundUp = span[i] >= '5';
                    fractionDigits++;
                }
            }
        }
        if (i != span.Length || !sawDigit)
            return false;
        for (var f = Math.Min(fractionDigits, scale); f < scale; f++)
            magnitude *= 10;
        if (roundUp)
            magnitude++;
        return true;
    }

    /// <summary>
    /// Fixed-point text, rounded to the scale; more integer digits than the
    /// precision leaves room for is truncation (<c>123456</c> into
    /// <c>decimal(5, 2)</c>, and <c>999.995</c>, which rounds past it).
    /// </summary>
    private static BulkConversionFailure TryDecimal(string text, DecimalSqlType target, out SqlValue value)
    {
        value = default;
        if (!TryReadFixedPoint(text, target.scale, allowGrouping: false, out var magnitude, out var negative, out _))
            return BulkConversionFailure.TypeMismatch;
        if (magnitude >= BigInteger.Pow(10, target.precision))
            return BulkConversionFailure.Truncation;
        value = SqlValue.FromDecimal(target, Decimal38.FromParts((UInt128)magnitude, negative && !magnitude.IsZero, target.scale));
        return BulkConversionFailure.None;
    }

    /// <summary>
    /// Fixed-point text with thousands separators allowed, no currency symbol
    /// or parentheses; beyond the type's range is overflow.
    /// </summary>
    private static BulkConversionFailure TryMoney(string text, SqlType target, out SqlValue value)
    {
        value = default;
        if (!TryReadFixedPoint(text, 4, allowGrouping: true, out var magnitude, out var negative, out _))
            return BulkConversionFailure.TypeMismatch;
        var units = negative ? -magnitude : magnitude;
        var (min, max) = target is SmallMoneySqlType ? ((BigInteger)int.MinValue, (BigInteger)int.MaxValue) : (long.MinValue, long.MaxValue);
        if (units < min || units > max)
            return BulkConversionFailure.Overflow;
        value = SqlValue.FromMoneyScaledUnits(target, (long)units);
        return BulkConversionFailure.None;
    }

    /// <summary>
    /// Decimal or exponent notation; <c>inf</c>, <c>NaN</c>, a comma and a
    /// value beyond the type's range are all a type mismatch.
    /// </summary>
    private static BulkConversionFailure TryFloat(string text, SqlType target, out SqlValue value)
    {
        value = default;
        var span = text.AsSpan().Trim(" \t\n\r\v\f");
        foreach (var c in span)
        {
            if (c is not ((>= '0' and <= '9') or '.' or '+' or '-' or 'e' or 'E'))
                return BulkConversionFailure.TypeMismatch;
        }
        if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            return BulkConversionFailure.TypeMismatch;
        if (target is RealSqlType)
        {
            var single = (float)number;
            if (!float.IsFinite(single))
                return BulkConversionFailure.TypeMismatch;
            value = SqlValue.FromSingle(single);
        }
        else
        {
            value = SqlValue.FromDouble(number);
        }
        return BulkConversionFailure.None;
    }

    /// <summary>
    /// The string forms <c>CAST</c> reads, with the fraction's precision
    /// unbounded: a <c>datetime</c> takes seven fractional digits, which
    /// <c>CAST</c> refuses. The text is read at full precision, then
    /// narrowed to the column.
    /// </summary>
    private static BulkConversionFailure TryTemporal(string text, SqlType target, out SqlValue value)
    {
        var wide = target is DateTimeOffsetSqlType ? SqlType.GetDateTimeOffset(7) : SqlType.GetDateTime2(7);
        if (TryCast(text, wide, out var read) == BulkConversionFailure.None)
        {
            try
            {
                value = read.CoerceTo(target);
                return BulkConversionFailure.None;
            }
            catch (Exception ex) when (ex is SimulatedSqlException or OverflowException or ArgumentException)
            {
                value = default;
                return BulkConversionFailure.TypeMismatch;
            }
        }
        return TryCast(text, target, out value);
    }

    private static BulkConversionFailure TryCast(string text, SqlType target, out SqlValue value)
    {
        try
        {
            value = SqlValue.FromVarchar(text).CoerceTo(target);
            return BulkConversionFailure.None;
        }
        catch (Exception ex) when (ex is SimulatedSqlException or FormatException or OverflowException or ArgumentException)
        {
            value = default;
            return BulkConversionFailure.TypeMismatch;
        }
    }

    /// <summary>
    /// Hex digits without a <c>0x</c> prefix, either case: an odd length is
    /// truncation whatever the characters, then any non-hex character a type
    /// mismatch, then more bytes than the column holds truncation again.
    /// </summary>
    private static BulkConversionFailure TryBinary(string text, SqlType target, out SqlValue value)
    {
        value = default;
        if (text.Length % 2 != 0)
            return BulkConversionFailure.Truncation;
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
                return BulkConversionFailure.TypeMismatch;
        }
        var bytes = Convert.FromHexString(text);
        var max = target switch
        {
            BinarySqlType b => b.length,
            VarbinarySqlType { length: > 0 } v => v.length,
            _ => int.MaxValue,
        };
        if (bytes.Length > max)
            return BulkConversionFailure.Truncation;
        value = SqlValue.FromVarbinary(bytes).CoerceTo(target);
        return BulkConversionFailure.None;
    }
}
