using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

// Where SQL Server's .NET Framework formatter and the .NET this simulator runs
// on answer the same call differently, independent of culture data — each
// rule below was read off FORMAT's own output (probed 2026-09-29 against
// SQL Server 2025).
internal sealed partial class Format
{
    /// <summary>
    /// An integer in its own width. The round-trip specifier belongs to the
    /// floating-point types on the Framework, so <c>'R'</c> of an integer is a
    /// <see cref="FormatException"/> — NULL.
    /// </summary>
    private static string FormatInteger<T>(T value, string? format, CultureInfo culture)
        where T : IFormattable =>
        IsStandard(format, out var specifier, out _) && specifier is 'R' or 'r'
            ? throw new FormatException()
            : value.ToString(format, culture);

    /// <summary>
    /// A <c>decimal</c>, <c>money</c> or <c>smallmoney</c>. Besides refusing
    /// <c>'R'</c>, the Framework reads two shapes its own way: a zero keeps a
    /// digit position its scale implies (see <see cref="ZeroDecimal"/>), and a
    /// custom pattern stops formatting at its first quoted literal (see
    /// <see cref="QuotedCustomDecimal"/>).
    /// </summary>
    private static string FormatDecimal(decimal value, string? format, CultureInfo culture)
    {
        var standard = IsStandard(format, out var specifier, out _);
        if (standard && specifier is 'R' or 'r')
            throw new FormatException();
        if (!standard && format is not null && format.AsSpan().IndexOfAny('\'', '"') >= 0 && QuotedCustomDecimal(value, format, culture) is { } quoted)
            return quoted;
        return value == 0 ? ZeroDecimal(value.Scale, format ?? "G", standard ? specifier : '\0', culture) : value.ToString(format, culture);
    }

    /// <summary>
    /// A decimal zero, whose Framework digit buffer sits one position to the
    /// left of where its scale puts a digit: <c>'P'</c> of a <c>decimal(5, 0)</c>
    /// zero is <c>000.00%</c> and of a <c>decimal(5, 2)</c> one <c>0.00%</c>,
    /// <c>'#'</c> writes <c>0</c> for the first and nothing for the second, and
    /// <c>'0.00‰'</c> of the first is <c>0000.00‰</c>. That is exactly how the
    /// Framework lays out the value one unit in the last place, with the digit
    /// written as <c>0</c> — so the layout is taken from formatting that value
    /// and its double, which differ only at that digit.
    /// </summary>
    private static string ZeroDecimal(int scale, string format, char specifier, CultureInfo culture)
    {
        var zero = new decimal(0, 0, 0, false, (byte)scale);
        // A general specifier with a precision writes an empty buffer as a bare 0.
        if (specifier is 'G' or 'g' && format.Length > 1)
            return 0m.ToString(format, culture);
        // Scientific notation writes a zero exponent for an empty buffer, and a
        // pattern's zero section takes over from the others.
        if (specifier is 'E' or 'e' || (specifier == '\0' && (HasScientificMarker(format) || SplitSections(format).Count >= 3)))
            return zero.ToString(format, culture);
        var one = new decimal(1, 0, 0, false, (byte)scale).ToString(format, culture);
        var two = new decimal(2, 0, 0, false, (byte)scale).ToString(format, culture);
        if (one.Length != two.Length)
            return zero.ToString(format, culture);
        var at = one.AsSpan().CommonPrefixLength(two);
        if (at == one.Length)
            return one;
        // Past the decimal separator the phantom digit is a trailing zero, which
        // a '#' or a general specifier drops like any other.
        var separator = specifier switch
        {
            'C' or 'c' => culture.NumberFormat.CurrencyDecimalSeparator,
            'P' or 'p' => culture.NumberFormat.PercentDecimalSeparator,
            _ => culture.NumberFormat.NumberDecimalSeparator,
        };
        var point = one.IndexOf(separator, StringComparison.Ordinal);
        if (point >= 0 && point < at)
            return zero.ToString(format, culture);
        return string.Create(one.Length, (one, at), static (span, state) =>
        {
            state.one.AsSpan().CopyTo(span);
            span[state.at] = '0';
        });
    }

    /// <summary>
    /// A custom pattern over a decimal whose chosen section holds a quoted
    /// literal: the Framework formats up to the quote as the whole section
    /// would lay the digits out, then writes the rest of the format string —
    /// later sections included — verbatim with its quote characters dropped.
    /// So <c>'0''x''0.00'</c> of <c>123.456</c> is <c>12x0.00</c> and
    /// <c>'''x''#,##0.00'</c> is <c>x#,##0.00</c>, where the integer and
    /// floating-point types format the pattern normally. <see langword="null"/>
    /// when the chosen section holds no quote.
    /// </summary>
    private static string? QuotedCustomDecimal(decimal value, string format, CultureInfo culture)
    {
        var sections = SplitSections(format);
        var index = value < 0 && sections.Count >= 2 ? 1 : value == 0 && sections.Count >= 3 ? 2 : 0;
        var (start, length) = sections[index];
        var section = format.AsSpan(start, length);
        var quote = FirstQuote(section);
        if (quote < 0)
            return null;

        // Lay the digits out with the rest of the section's placeholders in
        // place, and keep only what lands before the quote.
        var layout = new StringBuilder().Append(section[..quote]).Append('');
        var inQuote = '\0';
        for (var i = quote; i < section.Length; i++)
        {
            var c = section[i];
            if (inQuote != '\0')
            {
                if (c == inQuote)
                    inQuote = '\0';
            }
            else if (c is '\'' or '"')
            {
                inQuote = c;
            }
            else
            {
                _ = layout.Append(c);
                if (c == '\\' && i + 1 < section.Length)
                    _ = layout.Append(section[++i]);
            }
        }

        var formatted = (index == 0 ? value : Math.Abs(value)).ToString(layout.ToString(), culture);
        var cut = formatted.IndexOf('', StringComparison.Ordinal);
        var rest = format[(start + quote)..].Replace("'", "", StringComparison.Ordinal).Replace("\"", "", StringComparison.Ordinal);
        return string.Concat(cut < 0 ? formatted : formatted[..cut], rest);
    }

    private static int FirstQuote(ReadOnlySpan<char> section)
    {
        for (var i = 0; i < section.Length; i++)
        {
            switch (section[i])
            {
                case '\\':
                    i++;
                    break;
                case '\'' or '"':
                    return i;
            }
        }

        return -1;
    }

    /// <summary>The (start, length) of each <c>;</c>-separated section, quotes and escapes respected.</summary>
    private static List<(int Start, int Length)> SplitSections(string format)
    {
        var sections = new List<(int, int)>();
        var start = 0;
        var inQuote = '\0';
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (inQuote != '\0')
            {
                if (c == inQuote)
                    inQuote = '\0';
                continue;
            }

            switch (c)
            {
                case '\\':
                    i++;
                    break;
                case '\'' or '"':
                    inQuote = c;
                    break;
                case ';':
                    sections.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        sections.Add((start, format.Length - start));
        return sections;
    }

    private static bool HasScientificMarker(string format)
    {
        for (var i = 0; i + 1 < format.Length; i++)
        {
            if (format[i] is 'E' or 'e' && format[i + 1] is '+' or '-' or '0')
                return true;
        }

        return false;
    }

    /// <summary>
    /// A <c>float</c> or <c>real</c>. The Framework rounds the value to 15
    /// significant digits (7 for <c>real</c>) before any specifier sees it —
    /// 17 (9) for an <c>'E'</c> asking for more than 14 (6) fraction digits or a
    /// <c>'G'</c> asking for more than 15 (7) — and then rounds that decimal
    /// string half away from zero, so <c>'N2'</c> of <c>0.125</c> is
    /// <c>0.13</c>, <c>'F0'</c> of <c>2.5</c> is <c>3</c>, a value rounding to
    /// zero loses its sign, and a large one pads with zeros past the fifteenth
    /// digit. <c>'R'</c> tries 15 digits and falls back to 17 only when they
    /// don't read back. Past what a decimal holds the digits are laid out
    /// directly, and past 38 digits either way .NET formats the value itself.
    /// </summary>
    private static string FormatFloating(double value, bool single, string? format, CultureInfo culture)
    {
        var precision = single ? 7 : 15;
        var significant = precision;
        if (IsStandard(format, out var specifier, out var digits))
        {
            switch (specifier)
            {
                case 'R' or 'r':
                    Span<char> shortest = stackalloc char[32];
                    _ = value.TryFormat(shortest, out var written, single ? "G7" : "G15", CultureInfo.InvariantCulture);
                    var readsBack = single
                        ? float.Parse(shortest[..written], CultureInfo.InvariantCulture) == (float)value
                        : double.Parse(shortest[..written], CultureInfo.InvariantCulture) == value;
                    return value.ToString(readsBack ? (single ? "G7" : "G15") : (single ? "G9" : "G17"), culture);
                case 'G':
                    return digits is null or 0 ? value.ToString(single ? "G7" : "G15", culture)
                        : digits > precision ? value.ToString(single ? "G9" : "G17", culture)
                        : value.ToString(format, culture);
                case 'g':
                    return digits is null or 0 ? value.ToString(single ? "g7" : "g15", culture)
                        : digits > precision ? value.ToString(single ? "g9" : "g17", culture)
                        : value.ToString(format, culture);
                case 'E' or 'e':
                    if ((digits ?? 6) > precision - 1)
                        significant = single ? 9 : 17;
                    break;
                case 'D' or 'd' or 'X' or 'x':
                    throw new FormatException();
            }
        }

        if (value == 0)
            return 0m.ToString(format, culture);

        // The significant digits and exponent, read off .NET's correctly
        // rounded scientific form without allocating.
        Span<char> scientific = stackalloc char[32];
        _ = value.TryFormat(scientific, out var length, significant switch { 7 => "E6", 9 => "E8", 15 => "E14", _ => "E16" }, CultureInfo.InvariantCulture);
        var negative = scientific[0] == '-';
        UInt128 mantissa = 0;
        var mantissaDigits = 0;
        var trailingZeros = 0;
        var e = negative ? 1 : 0;
        for (; scientific[e] != 'E'; e++)
        {
            if (scientific[e] == '.')
                continue;
            mantissa = (mantissa * 10) + (uint)(scientific[e] - '0');
            mantissaDigits++;
            trailingZeros = scientific[e] == '0' ? trailingZeros + 1 : 0;
        }

        for (var i = 0; i < trailingZeros; i++)
            mantissa /= 10;
        mantissaDigits -= trailingZeros;
        var exponent = int.Parse(scientific[(e + 1)..length], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        // The value is mantissa × 10^power.
        var power = exponent - (mantissaDigits - 1);
        if (exponent < 28 && power >= -28)
        {
            var digitsValue = (ulong)mantissa;
            var result = power >= 0 ? digitsValue * Pow10(power) : new decimal((int)(uint)digitsValue, (int)(uint)(digitsValue >> 32), 0, false, (byte)-power);
            return (negative ? -result : result).ToString(format, culture);
        }

        if (exponent < 38 && power >= -38)
            return WideNumericFormat.Render(Decimal38.FromParts(power > 0 ? mantissa * UInt128Pow10(power) : mantissa, negative, Math.Max(0, -power)), format ?? "G", culture);

        return value.ToString(format, culture);
    }

    private static decimal Pow10(int power)
    {
        var result = 1m;
        for (var i = 0; i < power; i++)
            result *= 10;
        return result;
    }

    private static UInt128 UInt128Pow10(int power)
    {
        UInt128 result = 1;
        for (var i = 0; i < power; i++)
            result *= 10;
        return result;
    }

    /// <summary>
    /// Whether <paramref name="format"/> is a standard numeric specifier — a
    /// letter, then up to nine digits — reporting the letter and the digits.
    /// </summary>
    private static bool IsStandard(string? format, out char specifier, out int? digits)
    {
        specifier = '\0';
        digits = null;
        if (string.IsNullOrEmpty(format))
        {
            specifier = 'G';
            return true;
        }

        if (!char.IsAsciiLetter(format[0]) || format.Length > 10 || format.AsSpan(1).ContainsAnyExceptInRange('0', '9'))
            return false;
        specifier = format[0];
        if (format.Length > 1)
            digits = int.Parse(format.AsSpan(1), CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// A date and time. <c>'U'</c> writes the value as it stands — the
    /// Framework would convert an unzoned value from the server's local time,
    /// and real's server runs in UTC — through <see cref="FormatCulture.Universal"/>.
    /// </summary>
    private static string FormatDateTime(DateTime value, string? format, FormatCulture culture) =>
        format is "U"
            ? value.ToString(culture.Universal.DateTimeFormat.FullDateTimePattern, culture.Universal)
            : value.ToString(WithEra(format, culture, value), culture.Culture);

    /// <summary>
    /// A <c>time</c>, whose <c>'g'</c> and <c>'G'</c> fraction separator is the
    /// culture's decimal separator on the Framework (<c>13:45:56,789</c> under
    /// <c>fr-FR</c>) where .NET writes a period.
    /// </summary>
    private static string FormatTime(TimeSpan value, string? format, CultureInfo culture) =>
        format is "g" or "G"
            ? value.ToString(format, CultureInfo.InvariantCulture).Replace(".", culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal)
            : value.ToString(format, culture);

    /// <summary>
    /// A custom date pattern with its era written as the culture's own text:
    /// .NET offers no setter for era names, and real's are the Framework's
    /// (<c>A.D.</c> for <c>en-US</c> where .NET writes <c>AD</c>). A standard
    /// specifier stands as it is — the culture's own patterns already carry the
    /// era as text.
    /// </summary>
    private static string? WithEra(string? format, FormatCulture culture, DateTime value)
    {
        if (format is null || format.Length < 2 || !format.Contains('g', StringComparison.Ordinal))
            return format;
        // The era's text still has to come from a date the calendar holds.
        if (culture.Culture.DateTimeFormat.Calendar is not GregorianCalendar)
            _ = culture.Culture.DateTimeFormat.Calendar.GetEra(value);
        var builder = new StringBuilder(format.Length + culture.Era.Length);
        var inQuote = '\0';
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (inQuote != '\0')
            {
                _ = builder.Append(c);
                if (c == inQuote)
                    inQuote = '\0';
                continue;
            }

            switch (c)
            {
                case '\\' when i + 1 < format.Length:
                    _ = builder.Append(c).Append(format[++i]);
                    break;
                case '\'' or '"':
                    inQuote = c;
                    _ = builder.Append(c);
                    break;
                case 'g':
                    while (i + 1 < format.Length && format[i + 1] == 'g')
                        i++;
                    foreach (var eraChar in culture.Era)
                        _ = builder.Append('\\').Append(eraChar);
                    break;
                default:
                    _ = builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
