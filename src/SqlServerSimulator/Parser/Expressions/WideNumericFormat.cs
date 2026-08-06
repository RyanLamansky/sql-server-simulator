using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>FORMAT</c>'s renderer for an exact-numeric value wider than a .NET
/// <see cref="decimal"/> holds — real formats all 38 digits
/// (<c>FORMAT(CAST(12345678901234567890123456789012345678 AS decimal(38, 0)),
/// 'N0')</c> groups every one of them), so the digits are laid out from
/// <see cref="Decimal38"/>'s own magnitude rather than crossing to a narrower
/// type first.
/// </summary>
/// <remarks>
/// The culture's separators, group sizes and default digit counts are read
/// straight off its <see cref="NumberFormatInfo"/>, and the decoration around
/// the digits — currency symbol, percent sign, the sign patterns and their
/// parenthesized forms — comes from asking .NET to format <c>1</c> and
/// <c>-1</c> with the same specifier, so a value's rendering carries whatever
/// the narrow path would have produced around it.
/// </remarks>
internal static class WideNumericFormat
{
    /// <summary>
    /// Renders <paramref name="value"/> under a .NET numeric format string.
    /// Throws <see cref="FormatException"/> for the specifiers .NET refuses on
    /// a fractional type (<c>D</c> / <c>X</c> / <c>R</c>), which
    /// <see cref="Format"/> answers with NULL as real does.
    /// </summary>
    public static string Render(in Decimal38 value, string format, CultureInfo culture)
    {
        var info = culture.NumberFormat;
        if (format.Length == 0)
            return Standard(value, 'G', precision: -1, format, culture, info);

        var specifier = format[0];
        if (format.Length <= 3 && IsStandardSpecifier(specifier)
            && (format.Length == 1
                || int.TryParse(format.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            var precision = format.Length == 1
                ? -1
                : int.Parse(format.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
            return Standard(value, specifier, precision, format, culture, info);
        }

        return CustomPattern(value, format, info);
    }

    private static bool IsStandardSpecifier(char specifier) =>
        char.ToUpperInvariant(specifier) is 'C' or 'D' or 'E' or 'F' or 'G' or 'N' or 'P' or 'R' or 'X';

    private static string Standard(in Decimal38 value, char specifier, int precision, string format, CultureInfo culture, NumberFormatInfo info) =>
        char.ToUpperInvariant(specifier) switch
        {
            'C' => Decorated(value, format, culture,
                precision < 0 ? info.CurrencyDecimalDigits : precision,
                info.CurrencyGroupSizes, info.CurrencyGroupSeparator, info.CurrencyDecimalSeparator, powerOfTen: 0),
            'N' => Decorated(value, format, culture,
                precision < 0 ? info.NumberDecimalDigits : precision,
                info.NumberGroupSizes, info.NumberGroupSeparator, info.NumberDecimalSeparator, powerOfTen: 0),
            'F' => Decorated(value, format, culture,
                precision < 0 ? info.NumberDecimalDigits : precision,
                groupSizes: [], groupSeparator: "", info.NumberDecimalSeparator, powerOfTen: 0),
            'P' => Decorated(value, format, culture,
                precision < 0 ? info.PercentDecimalDigits : precision,
                info.PercentGroupSizes, info.PercentGroupSeparator, info.PercentDecimalSeparator, powerOfTen: 2),
            'E' => Scientific(value, precision < 0 ? 6 : precision, char.IsUpper(specifier) ? 'E' : 'e', info, exponentDigits: 3, trimTrailingZeros: false),
            'G' => General(value, precision, char.IsUpper(specifier) ? 'E' : 'e', info),
            // D / X / R have no meaning for a fractional type; .NET raises, and
            // real answers NULL.
            _ => throw new FormatException($"Format specifier '{specifier}' is not supported for a decimal value."),
        };

    /// <summary>
    /// The fixed-point specifiers: round, group, then wrap in whatever .NET
    /// puts around the digits for this specifier, culture and sign.
    /// </summary>
    private static string Decorated(
        in Decimal38 value, string format, CultureInfo culture,
        int fractionDigits, int[] groupSizes, string groupSeparator, string decimalSeparator, int powerOfTen)
    {
        var (negative, integerDigits, fractionText) = Rounded(value, fractionDigits, powerOfTen);
        var body = Group(integerDigits, groupSizes, groupSeparator);
        if (fractionText.Length > 0)
            body = body + decimalSeparator + fractionText;

        var (prefix, suffix) = Decoration(negative, format, culture);
        return prefix + body + suffix;
    }

    /// <summary>
    /// What .NET writes around the digits of <c>1</c> / <c>-1</c> under this
    /// format string — the currency symbol, the percent sign, the sign and the
    /// parentheses a negative pattern may use, each in its culture's own place.
    /// </summary>
    private static (string Prefix, string Suffix) Decoration(bool negative, string format, CultureInfo culture)
    {
        var sample = (negative ? -1m : 1m).ToString(format, culture);
        var first = -1;
        var last = -1;
        for (var i = 0; i < sample.Length; i++)
        {
            if (!char.IsAsciiDigit(sample[i]))
                continue;
            if (first < 0)
                first = i;
            last = i;
        }

        return first < 0 ? ("", "") : (sample[..first], sample[(last + 1)..]);
    }

    /// <summary>
    /// The scientific layout: one digit, the fraction, and a signed exponent.
    /// <c>E</c> pads the exponent to three digits and keeps every asked-for
    /// fractional digit; <c>G</c> pads to two and drops the trailing zeros.
    /// </summary>
    private static string Scientific(in Decimal38 value, int fractionDigits, char marker, NumberFormatInfo info, int exponentDigits, bool trimTrailingZeros)
    {
        var digits = value.Magnitude.ToString(CultureInfo.InvariantCulture);
        var exponent = 0;
        var mantissa = "0";
        if (!value.IsZero)
        {
            // The first digit's power of ten: its position in the magnitude,
            // less the fractional digits the scale claims, and one more where
            // rounding carried a place.
            exponent = digits.Length - 1 - value.Scale;
            var (carried, rounded) = RoundDigits(digits, fractionDigits + 1);
            if (carried)
                exponent++;
            mantissa = rounded;
        }

        mantissa = mantissa.PadRight(fractionDigits + 1, '0');
        var fraction = mantissa[1..];
        if (trimTrailingZeros)
            fraction = fraction.TrimEnd('0');
        return (value.IsNegative ? info.NegativeSign : "")
            + (fraction.Length == 0 ? mantissa[..1] : mantissa[..1] + info.NumberDecimalSeparator + fraction)
            + marker
            + (exponent < 0 ? info.NegativeSign : info.PositiveSign)
            + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).PadLeft(exponentDigits, '0');
    }

    /// <summary>
    /// <c>G</c>: the value's own digits when no precision is asked for — which
    /// is what real writes, trailing zeros of the declared scale included —
    /// and a significant-digit rounding that falls to scientific at .NET's own
    /// thresholds when one is.
    /// </summary>
    private static string General(in Decimal38 value, int precision, char marker, NumberFormatInfo info)
    {
        if (precision <= 0)
            return Plain(value, info);

        var digits = value.Magnitude.ToString(CultureInfo.InvariantCulture);
        var exponent = value.IsZero ? 0 : digits.Length - 1 - value.Scale;
        var (carried, rounded) = RoundDigits(digits, precision);
        if (carried)
            exponent++;
        if (exponent < -5 || exponent >= precision)
            return Scientific(value, Math.Max(precision - 1, 0), marker, info, exponentDigits: 2, trimTrailingZeros: true);

        var trimmed = rounded.TrimEnd('0');
        if (trimmed.Length == 0)
            return value.IsNegative ? info.NegativeSign + "0" : "0";

        // The exponent is the first kept digit's power of ten, so the point
        // sits that many places to its right.
        var whole = exponent < 0 ? "0" : trimmed.PadRight(exponent + 1, '0')[..(exponent + 1)];
        var fraction = exponent < 0
            ? new string('0', -exponent - 1) + trimmed
            : trimmed.Length > exponent + 1 ? trimmed[(exponent + 1)..] : "";
        var text = fraction.Length == 0 ? whole : whole + info.NumberDecimalSeparator + fraction;
        return value.IsNegative ? info.NegativeSign + text : text;
    }

    private static string Plain(in Decimal38 value, NumberFormatInfo info)
    {
        var (negative, integerDigits, fractionText) = Rounded(value, value.Scale, powerOfTen: 0);
        var body = fractionText.Length == 0 ? integerDigits : integerDigits + info.NumberDecimalSeparator + fractionText;
        return negative ? info.NegativeSign + body : body;
    }

    /// <summary>
    /// The value at <paramref name="fractionDigits"/> fractional digits after a
    /// shift of <paramref name="powerOfTen"/> places — rounding half away from
    /// zero where that drops digits and padding zeros where it doesn't, with no
    /// 38-digit ceiling on either side (<c>N40</c> of a
    /// <c>decimal(38, 38)</c> writes forty fractional digits, and <c>N2</c> of a
    /// 38-digit integer writes forty).
    /// </summary>
    private static (bool Negative, string IntegerDigits, string FractionDigits) Rounded(in Decimal38 value, int fractionDigits, int powerOfTen)
    {
        var digits = value.Magnitude.ToString(CultureInfo.InvariantCulture);
        var scale = value.Scale - powerOfTen;
        if (scale < 0)
        {
            digits = digits == "0" ? digits : digits.PadRight(digits.Length - scale, '0');
            scale = 0;
        }

        // Read the magnitude as an integer part and a fractional part, then
        // settle the fractional part at the asked-for width.
        var whole = digits.Length > scale ? digits[..^scale] : "0";
        var fraction = digits.Length > scale ? digits[^scale..] : digits.PadLeft(scale, '0');
        if (fractionDigits < fraction.Length)
        {
            var roundUp = fraction[fractionDigits] >= '5';
            fraction = fraction[..fractionDigits];
            if (roundUp)
            {
                var carried = Increment(whole + fraction);
                whole = carried[..^fractionDigits];
                fraction = carried[^fractionDigits..];
            }
        }
        else
        {
            fraction = fraction.PadRight(fractionDigits, '0');
        }

        whole = whole.TrimStart('0');
        return (value.IsNegative, whole.Length == 0 ? "0" : whole, fraction);
    }

    /// <summary>
    /// <paramref name="digits"/> plus one at its last place, growing by a
    /// leading digit where the carry runs off the front.
    /// </summary>
    private static string Increment(string digits)
    {
        var buffer = digits.ToCharArray();
        for (var i = buffer.Length - 1; i >= 0; i--)
        {
            if (buffer[i] != '9')
            {
                buffer[i]++;
                return new(buffer);
            }

            buffer[i] = '0';
        }

        return "1" + new string(buffer);
    }

    /// <summary>
    /// <paramref name="digits"/> kept to <paramref name="keep"/> significant
    /// digits, rounded half away from zero. The flag reports a carry that added
    /// a place, which moves the exponent.
    /// </summary>
    private static (bool Carried, string Digits) RoundDigits(string digits, int keep)
    {
        if (keep >= digits.Length)
            return (false, digits);
        var head = digits[..keep];
        if (digits[keep] < '5')
            return (false, head);
        var incremented = Increment(head);
        return incremented.Length > head.Length ? (true, incremented[..keep]) : (false, incremented);
    }

    /// <summary>
    /// The digits split by the culture's group sizes, which apply right to
    /// left with the last one repeating; a size of zero stops the grouping and
    /// leaves the rest of the digits running together.
    /// </summary>
    private static string Group(string integerDigits, int[] groupSizes, string separator)
    {
        if (groupSizes.Length == 0 || separator.Length == 0)
            return integerDigits;

        var groups = new List<string>();
        var remaining = integerDigits.Length;
        var index = 0;
        while (remaining > 0)
        {
            var size = groupSizes[Math.Min(index, groupSizes.Length - 1)];
            if (size <= 0 || size >= remaining)
                break;
            groups.Add(integerDigits.Substring(remaining - size, size));
            remaining -= size;
            index++;
        }

        groups.Add(integerDigits[..remaining]);
        groups.Reverse();
        return string.Join(separator, groups);
    }

    /// <summary>
    /// The custom-pattern subset a wide value reaches: digit placeholders
    /// (<c>0</c> / <c>#</c>), the decimal point, grouping and trailing-comma
    /// scaling, the percent and per-mille multipliers, escapes, quoted
    /// literals, and the semicolon sections. A pattern carrying scientific
    /// notation (<c>E+0</c>) isn't built for a wide value.
    /// </summary>
    private static string CustomPattern(in Decimal38 value, string format, NumberFormatInfo info)
    {
        var section = SelectSection(format, value.IsZero, value.IsNegative, out var negatedBySection);
        var parsed = ParsePattern(section);
        if (parsed.HasExponent)
            throw new NotSupportedException("FORMAT with a scientific custom pattern over a value wider than a decimal isn't modeled.");

        // A pattern with no digit placeholder writes its literal text and
        // nothing of the value — FORMAT(…, 'qq qq') is 'qq qq'.
        if (!parsed.HasPlaceholder)
            return parsed.Prefix + parsed.Suffix;

        var (negative, integerDigits, fractionText) = Rounded(value, parsed.MaxFractionDigits, parsed.PowerOfTen);
        while (fractionText.Length > parsed.MinFractionDigits && fractionText.EndsWith('0'))
            fractionText = fractionText[..^1];
        if (integerDigits == "0" && parsed.MinIntegerDigits == 0)
            integerDigits = "";
        integerDigits = integerDigits.PadLeft(parsed.MinIntegerDigits, '0');

        var body = parsed.Grouped ? Group(integerDigits, info.NumberGroupSizes, info.NumberGroupSeparator) : integerDigits;
        if (fractionText.Length > 0)
            body = body + info.NumberDecimalSeparator + fractionText;

        var text = parsed.Prefix + body + parsed.Suffix;
        return negative && !negatedBySection ? info.NegativeSign + text : text;
    }

    /// <summary>
    /// The section of a <c>;</c>-separated pattern this value uses. Two
    /// sections split positive-or-zero from negative; three split zero out as
    /// well. A negative value answered by its own section writes no sign of its
    /// own, which the flag reports.
    /// </summary>
    private static string SelectSection(string format, bool isZero, bool isNegative, out bool negatedBySection)
    {
        negatedBySection = false;
        var sections = SplitSections(format);
        if (sections.Count <= 1)
            return format;
        if (isZero && sections.Count >= 3)
            return sections[2];
        if (!isNegative)
            return sections[0];

        negatedBySection = true;
        return sections[1];
    }

    private static List<string> SplitSections(string format)
    {
        var sections = new List<string>();
        var start = 0;
        var quote = '\0';
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '\\')
            {
                i++;
            }
            else if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == ';')
            {
                sections.Add(format[start..i]);
                start = i + 1;
            }
        }

        sections.Add(format[start..]);
        return sections;
    }

    private sealed class PatternShape
    {
        public string Prefix = "";
        public string Suffix = "";
        public int MinIntegerDigits;
        public int MinFractionDigits;
        public int MaxFractionDigits;
        public int PowerOfTen;
        public bool Grouped;
        public bool HasExponent;
        public bool HasPlaceholder;
    }

    private static PatternShape ParsePattern(string pattern)
    {
        var shape = new PatternShape();
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        var seenPlaceholder = false;
        var afterPoint = false;
        var integerPlaceholders = 0;
        var firstZeroPlaceholder = -1;
        var trailingCommas = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                _ = (seenPlaceholder ? suffix : prefix).Append(pattern[++i]);
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = pattern.IndexOf(c, i + 1);
                var literal = end < 0 ? pattern[(i + 1)..] : pattern[(i + 1)..end];
                _ = (seenPlaceholder ? suffix : prefix).Append(literal);
                i = end < 0 ? pattern.Length : end;
                continue;
            }

            switch (c)
            {
                case '0':
                case '#':
                    seenPlaceholder = true;
                    trailingCommas = 0;
                    if (afterPoint)
                    {
                        shape.MaxFractionDigits++;
                        if (c == '0')
                            shape.MinFractionDigits = shape.MaxFractionDigits;
                    }
                    else
                    {
                        integerPlaceholders++;
                        if (c == '0' && firstZeroPlaceholder < 0)
                            firstZeroPlaceholder = integerPlaceholders;
                    }

                    break;
                case '.':
                    afterPoint = true;
                    break;
                case ',':
                    if (seenPlaceholder && !afterPoint)
                    {
                        shape.Grouped = true;
                        trailingCommas++;
                    }

                    break;
                case '%':
                    shape.PowerOfTen += 2;
                    _ = (seenPlaceholder ? suffix : prefix).Append(pattern[i]);
                    break;
                case '‰':
                    shape.PowerOfTen += 3;
                    _ = (seenPlaceholder ? suffix : prefix).Append(pattern[i]);
                    break;
                case 'E':
                case 'e':
                    shape.HasExponent = true;
                    break;
                default:
                    _ = (seenPlaceholder ? suffix : prefix).Append(c);
                    break;
            }
        }

        // A comma sitting between the last digit placeholder and the decimal
        // point divides rather than groups.
        shape.PowerOfTen -= trailingCommas * 3;
        if (trailingCommas > 0 && integerPlaceholders <= 1)
            shape.Grouped = false;
        shape.Prefix = prefix.ToString();
        shape.Suffix = suffix.ToString();
        // Every integer placeholder at or right of the first '0' is forced, so
        // "#,##0" writes one leading digit and "0000" writes four.
        shape.MinIntegerDigits = firstZeroPlaceholder < 0 ? 0 : integerPlaceholders - firstZeroPlaceholder + 1;
        shape.HasPlaceholder = seenPlaceholder;
        return shape;
    }
}
