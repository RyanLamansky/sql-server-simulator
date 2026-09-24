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
/// any other type letter) raise Msg 2787, whose text runs from the <c>%</c>
/// to the end of the format string. <c>%%</c> emits a literal <c>%</c>.</item>
/// <item><c>length</c>: <c>l</c> (long; same as bare on 32-bit-int SQL
/// platforms) or <c>I64</c> (int64 — takes a bigint argument and nothing
/// else; bare <c>%d</c> with a bigint arg raises Msg 2786).</item>
/// <item><c>width</c>: minimum field width (pad with spaces, or zeros when
/// the <c>0</c> flag is present).</item>
/// <item><c>.precision</c>: for <c>%s</c>, max chars from the source; for the
/// integer types the minimum digit count, zero-filled, which overrides the
/// <c>0</c> flag (so <c>%.0d</c> prints 0 as nothing). A <c>.</c> with neither
/// digits nor <c>*</c> is Msg 2787.</item>
/// <item><c>*</c> in place of the width or precision digits takes it from the
/// next argument, which must be a tinyint / smallint / int (else Msg 2786,
/// NULL included). A negative one is ignored.</item>
/// <item><c>-</c> flag: left-align (default is right-align).</item>
/// </list>
/// <para>
/// The <c>*</c>, precision and 2787-text rules were probed 2026-09-24
/// against SQL Server 2025.
/// </para>
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
    public static string Format(string format, List<SqlValue> arguments)
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

            // Width: optional run of digits, or `*` for the next argument.
            var width = 0;
            if (format[i] == '*')
            {
                width = Math.Max(0, TakeStarArg(arguments, ref argIndex));
                i++;
            }
            else
            {
                while (i < format.Length && format[i] >= '0' && format[i] <= '9')
                {
                    width = (width * 10) + (format[i] - '0');
                    i++;
                }
            }
            if (i >= format.Length)
                throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);

            // Precision: optional `.digits` or `.*`.
            var precision = -1;
            if (format[i] == '.')
            {
                i++;
                if (i < format.Length && format[i] == '*')
                {
                    var starPrecision = TakeStarArg(arguments, ref argIndex);
                    precision = starPrecision < 0 ? -1 : starPrecision;
                    i++;
                }
                else if (i < format.Length && format[i] >= '0' && format[i] <= '9')
                {
                    precision = 0;
                    while (i < format.Length && format[i] >= '0' && format[i] <= '9')
                    {
                        precision = (precision * 10) + (format[i] - '0');
                        i++;
                    }
                }
                else
                {
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
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
                            throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
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
                        rendered = PadNumber(s, width, precision, leftAlign, zeroPad);
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
                        rendered = PadNumber(s, width, precision, leftAlign, zeroPad);
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
                        rendered = PadNumber(s, width, precision, leftAlign, zeroPad);
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
                        rendered = PadNumber(hex, width, precision, leftAlign, zeroPad);
                        break;
                    }
                default:
                    // Unsupported type letter (%c, %p, %f, etc.). Real SQL
                    // Server reports the rest of the format string from the `%`.
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
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
    private static (string text, bool isNullArg) TakeStringArg(List<SqlValue> arguments, ref int argIndex, int oneBasedIndex)
    {
        if (argIndex >= arguments.Count)
        {
            argIndex++;
            return ("(null)", true);
        }
        var arg = RejectDecimal(arguments[argIndex++], oneBasedIndex);
        if (arg.IsNull)
            return ("(null)", true);
        return arg.Type.Category != SqlTypeCategory.String
            ? throw SimulatedSqlException.RaiserrorTypeMismatch(oneBasedIndex)
            : (arg.AsString, false);
    }

    /// <summary>
    /// Reads the next substitution argument as an integer. <paramref name="isInt64"/>
    /// is the bigint specifier (<c>%I64d</c>), which takes a bigint and
    /// nothing else; without it a tinyint / smallint / int is required. Either
    /// mismatch raises Msg 2786 (probe-confirmed against SQL Server 2025,
    /// 2026-09-24: <c>%I64d</c> refuses an int, <c>%d</c> a bigint). Returns
    /// <c>(0, isNullArg: true)</c> on NULL/missing so the caller renders
    /// <c>(null)</c>.
    /// </summary>
    private static (long value, bool isNullArg) TakeIntArg(List<SqlValue> arguments, ref int argIndex, int oneBasedIndex, bool isInt64)
    {
        if (argIndex >= arguments.Count)
        {
            argIndex++;
            return (0, true);
        }
        var arg = RejectDecimal(arguments[argIndex++], oneBasedIndex);
        if (arg.IsNull)
            return (0, true);
        return isInt64 ? (arg.Type == SqlType.BigInt ? (arg.AsInt64, false) : throw SimulatedSqlException.RaiserrorTypeMismatch(oneBasedIndex))
            : arg.Type == SqlType.Int32 ? (arg.AsInt32, false)
            : arg.Type == SqlType.SmallInt ? (arg.AsInt16, false)
            : arg.Type == SqlType.TinyInt ? (arg.AsByte, false)
            : throw SimulatedSqlException.RaiserrorTypeMismatch(oneBasedIndex);
    }

    /// <summary>
    /// Refuses a decimal substitution a specifier consumes with Msg 2748,
    /// whose position counts RAISERROR's message, severity and state. The
    /// statement refuses the other disallowed types whether consumed or not.
    /// </summary>
    private static SqlValue RejectDecimal(SqlValue arg, int oneBasedIndex) =>
        arg.Type is DecimalSqlType
            ? throw SimulatedSqlException.SubstitutionParameterTypeNotAllowed(arg.Type.ToString()!, oneBasedIndex + 3)
            : arg;

    /// <summary>
    /// Reads the next argument as a <c>*</c> width or precision: a tinyint /
    /// smallint / int, where a NULL, missing or other-typed one is Msg 2786.
    /// </summary>
    private static int TakeStarArg(List<SqlValue> arguments, ref int argIndex)
    {
        var oneBasedIndex = argIndex + 1;
        if (argIndex >= arguments.Count || arguments[argIndex].IsNull)
            throw SimulatedSqlException.RaiserrorTypeMismatch(oneBasedIndex);
        return (int)TakeIntArg(arguments, ref argIndex, oneBasedIndex, isInt64: false).value;
    }

    private static string PadString(string s, int width, bool leftAlign) =>
        width <= 0 || s.Length >= width
            ? s
            : leftAlign ? s.PadRight(width) : s.PadLeft(width);

    private static string PadNumber(string s, int width, int precision, bool leftAlign, bool zeroPad)
    {
        // A precision is the minimum digit count, and it turns the 0 flag off.
        if (precision >= 0)
        {
            var negative = s.Length > 0 && s[0] == '-';
            var digits = negative ? s[1..] : s;
            digits = precision == 0 && digits == "0" ? "" : digits.PadLeft(precision, '0');
            s = negative ? "-" + digits : digits;
            zeroPad = false;
        }
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
