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
/// Specifier grammar: <c>%[flags][width][.precision][length]type</c>.
/// </para>
/// <list type="bullet">
/// <item><c>type</c>: one of <c>s d i u o x X</c>. <c>%c</c> and <c>%p</c> (and
/// any other type letter) raise Msg 2787, whose text runs from the <c>%</c>
/// to the end of the format string. <c>%%</c> emits a literal <c>%</c>.</item>
/// <item><c>length</c>: <c>l</c> or <c>L</c> (long; same as bare on 32-bit-int SQL
/// platforms), <c>h</c> (short — a smallint or tinyint argument, refused for
/// <c>%s</c>) or <c>I64</c> (int64 — takes a bigint argument and nothing
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
/// <item>flags, each at most once (a repeat is Msg 2787): <c>-</c> left-aligns
/// (default is right-align), <c>0</c> zero-pads, <c>+</c> and a space sign a
/// non-negative <c>%d</c> / <c>%i</c>, and <c>#</c> prefixes a nonzero
/// <c>%o</c> with 0 and a nonzero <c>%x</c> / <c>%X</c> with <c>0x</c> /
/// <c>0X</c>.</item>
/// </list>
/// <para>
/// The <c>*</c>, precision and 2787-text rules were probed 2026-09-24
/// against SQL Server 2025.
/// </para>
/// <para>
/// Argument-type matching: <c>%s</c> requires a string-category SqlValue;
/// <c>%d / %i / %ld / %li / %u / %o / %x / %X</c> require tinyint / smallint /
/// int, or a binary value read as an integer (bigint specifically requires the <c>%I64d</c>/<c>%I64i</c> length
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

            // Flags, each at most once — a repeated one is Msg 2787: `-`
            // left-aligns, `0` zero-pads, `+` signs a non-negative signed
            // value, a space prefixes one, and `#` prefixes octal with 0 and
            // hex with 0x (probed 2026-10-02 against SQL Server 2025).
            var flags = FormatFlags.None;
            while (i < format.Length && FlagOf(format[i]) is var flag and not FormatFlags.None)
            {
                if ((flags & flag) != 0)
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
                flags |= flag;
                i++;
            }
            var leftAlign = (flags & FormatFlags.Left) != 0;
            var zeroPad = (flags & FormatFlags.Zero) != 0;
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

            // Length modifier: `l` / `L` (long, the same as none), `h` (short —
            // a smallint or tinyint argument, and not for `%s`) or `I64` (int64).
            var length = IntegerLength.Int;
            if (format[i] is 'l' or 'L' or 'h')
            {
                if (format[i] == 'h')
                    length = IntegerLength.Short;
                i++;
                if (i >= format.Length)
                    throw SimulatedSqlException.RaiserrorInvalidFormatSpec(format[specStart..]);
            }
            else if (format[i] == 'I' && i + 2 < format.Length && format[i + 1] == '6' && format[i + 2] == '4')
            {
                length = IntegerLength.Int64;
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
                        if (length != IntegerLength.Int)
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
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, length);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var s = n.ToString(CultureInfo.InvariantCulture);
                        var sign = n < 0 ? "-" : (flags & FormatFlags.Plus) != 0 ? "+" : (flags & FormatFlags.Space) != 0 ? " " : "";
                        rendered = PadNumber(sign, n < 0 ? s[1..] : s, width, precision, leftAlign, zeroPad);
                        break;
                    }
                case 'u':
                    {
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, length);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var s = Unsigned(n, length).ToString(CultureInfo.InvariantCulture);
                        rendered = PadNumber("", s, width, precision, leftAlign, zeroPad);
                        break;
                    }
                case 'o':
                    {
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, length);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var s = Convert.ToString((long)Unsigned(n, length), 8);
                        // `#` makes the first digit a 0, which a 0 value already is.
                        if ((flags & FormatFlags.Alternate) != 0 && n != 0 && s.Length >= precision)
                            precision = s.Length + 1;
                        rendered = PadNumber("", s, width, precision, leftAlign, zeroPad);
                        break;
                    }
                case 'x':
                case 'X':
                    {
                        var (n, isNullArg) = TakeIntArg(arguments, ref argIndex, oneBasedArgIndex, length);
                        if (isNullArg)
                        {
                            rendered = PadString("(null)", width, leftAlign);
                            break;
                        }
                        var hex = Unsigned(n, length).ToString(typeChar == 'X' ? "X" : "x", CultureInfo.InvariantCulture);
                        var prefix = (flags & FormatFlags.Alternate) != 0 && n != 0 ? (typeChar == 'X' ? "0X" : "0x") : "";
                        rendered = PadNumber(prefix, hex, width, precision, leftAlign, zeroPad);
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
    /// The specifier texts of a us_english format string, in order — each from
    /// its <c>%</c> through its type letter, <c>%%</c> skipped. Reading stops
    /// at a malformed specifier, which <see cref="Format"/> raises for.
    /// </summary>
    public static List<string> SplitSpecifiers(string format)
    {
        var specs = new List<string>();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
                continue;
            var start = i++;
            if (i >= format.Length)
                break;
            if (format[i] == '%')
                continue;
            while (i < format.Length && format[i] is '-' or '0' or '+' or ' ' or '#')
                i++;
            while (i < format.Length && (format[i] is (>= '0' and <= '9') or '*'))
                i++;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                while (i < format.Length && (format[i] is (>= '0' and <= '9') or '*'))
                    i++;
            }
            if (i < format.Length && format[i] is 'l' or 'L' or 'h')
                i++;
            else if (i + 2 < format.Length && format[i] == 'I' && format[i + 1] == '6' && format[i + 2] == '4')
                i += 3;
            if (i >= format.Length)
                break;
            specs.Add(format[start..(i + 1)]);
        }
        return specs;
    }

    /// <summary>
    /// Reads a localized message's text against its us_english version's
    /// specifiers (<paramref name="specifiers"/>): the localized text names
    /// each one by position — <c>%1!</c> or <c>%1</c> — in any order, each at
    /// most once, with <c>%%</c> a literal percent, and anything else after a
    /// <c>%</c> is invalid (probed 2026-09-30 against SQL Server 2025). On
    /// success <paramref name="format"/> is an ordinary format string that
    /// carries those specifiers in the localized order and
    /// <paramref name="order"/> the zero-based argument each consumes; on
    /// failure <paramref name="invalidSpecification"/> is the text from after
    /// the offending <c>%</c>.
    /// </summary>
    public static bool TryLocalize(List<string> specifiers, string localized, out string format, out List<int> order, out string invalidSpecification)
    {
        var output = new StringBuilder(localized.Length);
        order = [];
        invalidSpecification = string.Empty;
        for (var i = 0; i < localized.Length; i++)
        {
            if (localized[i] != '%')
            {
                _ = output.Append(localized[i]);
                continue;
            }
            var afterPercent = i + 1;
            if (afterPercent >= localized.Length)
            {
                format = string.Empty;
                return false;
            }
            if (localized[afterPercent] == '%')
            {
                _ = output.Append("%%");
                i = afterPercent;
                continue;
            }
            var end = afterPercent;
            var position = 0;
            while (end < localized.Length && localized[end] is >= '0' and <= '9' && position < 1000)
                position = (position * 10) + (localized[end++] - '0');
            if (end == afterPercent || (end < localized.Length && localized[end] != '!') || position < 1 || position > specifiers.Count || order.Contains(position - 1))
            {
                format = string.Empty;
                invalidSpecification = localized[afterPercent..];
                return false;
            }
            order.Add(position - 1);
            _ = output.Append(specifiers[position - 1]);
            i = end < localized.Length && localized[end] == '!' ? end : end - 1;
        }
        format = output.ToString();
        return true;
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
    /// Reads the next substitution argument as an integer of the specifier's
    /// <paramref name="length"/>. <c>%I64d</c> takes a bigint and nothing else;
    /// <c>%hd</c> a smallint or tinyint; the plain and <c>l</c> forms a tinyint,
    /// smallint or int, or a binary value read as the big-endian integer its
    /// last four bytes spell (probed 2026-10-02 against SQL Server 2025:
    /// <c>%x</c> of <c>0x0102030405</c> prints <c>2030405</c>). Any other
    /// type raises Msg 2786. Returns <c>(0, isNullArg: true)</c> on
    /// NULL/missing so the caller renders <c>(null)</c>.
    /// </summary>
    private static (long value, bool isNullArg) TakeIntArg(List<SqlValue> arguments, ref int argIndex, int oneBasedIndex, IntegerLength length)
    {
        if (argIndex >= arguments.Count)
        {
            argIndex++;
            return (0, true);
        }
        var arg = RejectDecimal(arguments[argIndex++], oneBasedIndex);
        if (arg.IsNull)
            return (0, true);
        return length switch
        {
            IntegerLength.Int64 when arg.Type == SqlType.BigInt => (arg.AsInt64, false),
            IntegerLength.Short when arg.Type == SqlType.SmallInt => (arg.AsInt16, false),
            IntegerLength.Short or IntegerLength.Int when arg.Type == SqlType.TinyInt => (arg.AsByte, false),
            IntegerLength.Int when arg.Type == SqlType.SmallInt => (arg.AsInt16, false),
            IntegerLength.Int when arg.Type == SqlType.Int32 => (arg.AsInt32, false),
            IntegerLength.Int when arg.Type is VarbinarySqlType or BinarySqlType => (BinaryAsInt32(arg.AsBytes), false),
            _ => throw SimulatedSqlException.RaiserrorTypeMismatch(oneBasedIndex),
        };
    }

    private static int BinaryAsInt32(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        foreach (var b in bytes.Length > 4 ? bytes[^4..] : bytes)
            value = (value << 8) | b;
        return value;
    }

    /// <summary>The unsigned reading of <paramref name="n"/> at the specifier's width.</summary>
    private static ulong Unsigned(long n, IntegerLength length) => length switch
    {
        IntegerLength.Int64 => (ulong)n,
        IntegerLength.Short => (ushort)n,
        _ => (uint)n,
    };

    private static FormatFlags FlagOf(char c) => c switch
    {
        ' ' => FormatFlags.Space,
        '#' => FormatFlags.Alternate,
        '+' => FormatFlags.Plus,
        '-' => FormatFlags.Left,
        '0' => FormatFlags.Zero,
        _ => FormatFlags.None,
    };

    [Flags]
    private enum FormatFlags
    {
        None = 0,
        Left = 1,
        Zero = 2,
        Plus = 4,
        Space = 8,
        Alternate = 16,
    }

    private enum IntegerLength
    {
        Int,
        Short,
        Int64,
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
        return (int)TakeIntArg(arguments, ref argIndex, oneBasedIndex, IntegerLength.Int).value;
    }

    private static string PadString(string s, int width, bool leftAlign) =>
        width <= 0 || s.Length >= width
            ? s
            : leftAlign ? s.PadRight(width) : s.PadLeft(width);

    /// <summary>
    /// Lays out a number: <paramref name="prefix"/> (its sign or <c>0x</c>),
    /// then <paramref name="digits"/> filled to the precision — which turns the
    /// 0 flag off — then padded to the width, zeros going between the prefix
    /// and the digits.
    /// </summary>
    private static string PadNumber(string prefix, string digits, int width, int precision, bool leftAlign, bool zeroPad)
    {
        if (precision >= 0)
        {
            digits = precision == 0 && digits == "0" ? "" : digits.PadLeft(precision, '0');
            zeroPad = false;
        }
        var length = prefix.Length + digits.Length;
        if (width <= length)
            return prefix + digits;
        return leftAlign ? (prefix + digits).PadRight(width)
            : zeroPad ? prefix + digits.PadLeft(width - prefix.Length, '0')
            : (prefix + digits).PadLeft(width);
    }
}
