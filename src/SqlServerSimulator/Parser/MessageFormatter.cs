using SqlServerSimulator.Storage;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Renders a <c>RAISERROR</c> printf-style format string against a list of
/// <see cref="SqlValue"/> substitution arguments. Mirrors SQL Server's
/// RAISERROR formatter (a C runtime <c>printf</c>-subset, not .NET
/// <c>string.Format</c>) — the supported specifier set is fixed by SQL Server
/// and validated at render time. Probe-confirmed against SQL Server 2025
/// (2026-05-12).
/// </summary>
/// <remarks>
/// <para>
/// Specifier grammar: <c>%[-][0][width][.precision][length]type</c>.
/// </para>
/// <list type="bullet">
/// <item><c>type</c>: one of <c>s d i u o x X</c>. <c>%c</c> and <c>%p</c> (and
/// any other type letter) raise Msg 2787 — probe-confirmed verbatim.
/// <c>%%</c> emits a literal <c>%</c>.</item>
/// <item><c>length</c>: <c>l</c> (long; same as bare on 32-bit-int SQL
/// platforms) or <c>I64</c> (int64 — required for bigint args; bare <c>%d</c>
/// with a bigint arg raises Msg 2786).</item>
/// <item><c>width</c>: minimum field width (pad with spaces, or zeros when
/// the <c>0</c> flag is present).</item>
/// <item><c>.precision</c>: for <c>%s</c>, max chars from the source; for
/// numeric specifiers it's accepted but currently not honored
/// (zero-pad-via-width is the common case and works).</item>
/// <item><c>-</c> flag: left-align (default is right-align).</item>
/// </list>
/// <para>
/// Argument-type matching: <c>%s</c> requires a string-category SqlValue;
/// <c>%d / %i / %ld / %li / %u / %o / %x / %X</c> require tinyint / smallint /
/// int (bigint specifically requires the <c>%I64d</c>/<c>%I64i</c> length
/// modifier — probe-confirmed: bare <c>%d</c> with a bigint arg raises Msg
/// 2786). Mismatches raise Msg 2786 with the 1-based parameter index.
/// </para>
/// <para>
/// NULL handling: a NULL substitution arg renders as the literal text
/// <c>(null)</c> regardless of specifier type (probe-confirmed for <c>%s</c>;
/// real SQL Server emits the same for <c>%d</c> with NULL). A format string
/// with more specifiers than supplied args substitutes <c>(null)</c> for the
/// missing slots (probe-confirmed). Extra args beyond the specifier count are
/// silently ignored. NULL message string itself renders as a single space
/// (matches real SQL Server's NULL-as-message handling — the caller passes
/// the empty/space-converted result to the raise path).
/// </para>
/// </remarks>
internal static class MessageFormatter
{
    /// <summary>
    /// Renders <paramref name="format"/> against <paramref name="arguments"/>
    /// and returns the resulting message. Throws <see cref="SimulatedSqlException"/>
    /// (Msg 2786 / 2787) on invalid specifiers or arg-type mismatches.
    /// </summary>
    public static string Format(string format, IReadOnlyList<SqlValue> arguments)
    {
        var output = new StringBuilder(format.Length);
        var argIndex = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%')
            {
                _ = output.Append(c);
                continue;
            }

            // Found a `%` — parse the specifier following it.
            var specStart = i;
            i++; // step past `%`
            if (i >= format.Length)
                throw SimulatedSqlException.RaiserrorInvalidFormatSpec("%");

            // `%%` → literal `%`.
            if (format[i] == '%')
            {
                _ = output.Append('%');
                continue;
            }

            // Flags: `-` (left-align), `0` (zero-pad). SQL Server accepts only
            // these two; other printf flags (` `, `+`, `#`) are not recognized.
            var leftAlign = false;
            var zeroPad = false;
            while (i < format.Length && (format[i] == '-' || format[i] == '0'))
            {
                if (format[i] == '-') leftAlign = true;
                else zeroPad = true;
                i++;
            }
            if (i >= format.Length)
                throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);

            // Width: optional run of digits.
            var width = 0;
            while (i < format.Length && format[i] >= '0' && format[i] <= '9')
            {
                width = (width * 10) + (format[i] - '0');
                i++;
            }
            if (i >= format.Length)
                throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);

            // Precision: optional `.digits`.
            var precision = -1;
            if (format[i] == '.')
            {
                i++;
                precision = 0;
                while (i < format.Length && format[i] >= '0' && format[i] <= '9')
                {
                    precision = (precision * 10) + (format[i] - '0');
                    i++;
                }
                if (i >= format.Length)
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
            }

            // Length modifier: `l` (long) or `I64` (int64).
            var isInt64 = false;
            if (format[i] == 'l')
            {
                // %l<type> — same as bare for 32-bit-int SQL Server semantics.
                i++;
                if (i >= format.Length)
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
            }
            else if (format[i] == 'I' && i + 2 < format.Length && format[i + 1] == '6' && format[i + 2] == '4')
            {
                isInt64 = true;
                i += 3;
                if (i >= format.Length)
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
            }

            // Type letter. Bounds already validated above for each path.
            var typeChar = format[i];
            var oneBasedArgIndex = argIndex + 1;
            string rendered;
            switch (typeChar)
            {
                case 's':
                    {
                        if (isInt64)
                            throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..(i + 1)]);
                        var (text, isNullArg) = TakeStringArg(arguments, ref argIndex, oneBasedArgIndex);
                        if (!isNullArg && precision >= 0 && text.Length > precision)
                            text = text[..precision];
                        rendered = PadString(text, width, leftAlign);
                        break;
                    }
                case 'd':
                case 'i':
                    {
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, isInt64);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var s = n.ToString(CultureInfo.InvariantCulture);
                        rendered = PadNumber(s, width, leftAlign, zeroPad);
                        break;
                    }
                case 'u':
                    {
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, isInt64);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var s = isInt64
                            ? ((ulong)n).ToString(CultureInfo.InvariantCulture)
                            : ((uint)n).ToString(CultureInfo.InvariantCulture);
                        rendered = PadNumber(s, width, leftAlign, zeroPad);
                        break;
                    }
                case 'o':
                    {
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, isInt64);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var s = isInt64
                            ? Convert.ToString(n, 8)
                            : Convert.ToString((int)n, 8);
                        rendered = PadNumber(s, width, leftAlign, zeroPad);
                        break;
                    }
                case 'x':
                case 'X':
                    {
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, isInt64);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var hex = isInt64
                            ? ((ulong)n).ToString("x", CultureInfo.InvariantCulture)
                            : ((uint)n).ToString("x", CultureInfo.InvariantCulture);
                        if (typeChar == 'X')
                            hex = hex.ToUpperInvariant();
                        rendered = PadNumber(hex, width, leftAlign, zeroPad);
                        break;
                    }
                default:
                    // Unsupported type letter (%c, %p, %f, etc.). Real SQL
                    // Server reports the full spec text including the `%`.
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..(i + 1)]);
            }
            _ = output.Append(rendered);
        }

        return output.ToString();
    }

    /// <summary>
    /// Reads the next substitution argument as a string. Returns
    /// <c>("(null)", isNullArg: true)</c> for a NULL or missing argument so
    /// the caller can decide whether to skip precision/width truncation.
    /// Type mismatch raises Msg 2786.
    /// </summary>
    private static (string text, bool isNullArg) TakeStringArg(IReadOnlyList<SqlValue> arguments, ref int argIndex, int oneBasedIndex)
    {
        if (argIndex >= arguments.Count)
        {
            argIndex++;
            return ("(null)", true);
        }
        var arg = arguments[argIndex++];
        if (arg.IsNull)
            return ("(null)", true);
        return arg.Type.Category != SqlTypeCategory.String
            ? throw SimulatedSqlException.RaiserrorTypeMismatch(oneBasedIndex)
            : (arg.AsString, false);
    }

    /// <summary>
    /// Reads the next substitution argument as an integer. <paramref name="acceptInt64"/>
    /// gates the bigint specifier (<c>%I64d</c>): when true the bare 32-bit
    /// integer types still work but bigint is also accepted; when false a
    /// bigint arg raises Msg 2786 (matches real SQL Server: bare <c>%d</c> +
    /// bigint → 2786 St 1, probe-confirmed). Returns
    /// <c>(0, isNullArg: true)</c> on NULL/missing so the caller renders
    /// <c>(null)</c>.
    /// </summary>
    private static (long value, bool isNullArg) TakeIntArg(IReadOnlyList<SqlValue> arguments, ref int argIndex, int oneBasedIndex, bool acceptInt64)
    {
        if (argIndex >= arguments.Count)
        {
            argIndex++;
            return (0, true);
        }
        var arg = arguments[argIndex++];
        if (arg.IsNull)
            return (0, true);
        return arg.Type == SqlType.Int32 ? (arg.AsInt32, false)
            : arg.Type == SqlType.SmallInt ? (arg.AsInt16, false)
            : arg.Type == SqlType.TinyInt ? (arg.AsByte, false)
            : acceptInt64 && arg.Type == SqlType.BigInt ? (arg.AsInt64, false)
            : throw SimulatedSqlException.RaiserrorTypeMismatch(oneBasedIndex);
    }

    private static string PadString(string s, int width, bool leftAlign) =>
        width <= 0 || s.Length >= width
            ? s
            : leftAlign ? s.PadRight(width) : s.PadLeft(width);

    private static string PadNumber(string s, int width, bool leftAlign, bool zeroPad)
    {
        if (width <= 0 || s.Length >= width)
            return s;
        if (leftAlign)
            return s.PadRight(width);
        if (!zeroPad)
            return s.PadLeft(width);
        // Zero-pad goes between sign and digits; bare PadLeft over "-42"
        // would produce "00-42" instead of "-0042". Handle the negative case.
        return s.Length > 0 && s[0] == '-'
            ? "-" + s[1..].PadLeft(width - 1, '0')
            : s.PadLeft(width, '0');
    }
}
