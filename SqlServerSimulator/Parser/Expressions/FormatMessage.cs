using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>FORMATMESSAGE(msg_number_or_string, [param, ...])</c>: renders a
/// printf-style format string against substitution arguments. The
/// <c>msg_id</c> overload (first argument numeric) resolves a
/// <c>sys.messages</c> entry on real SQL Server; the simulator doesn't model
/// <c>sys.messages</c>, so every numeric id — user (≥50000) or system —
/// returns NULL, matching real SQL Server's behavior for an unknown id
/// (probe-confirmed 2026-07-10: <c>FORMATMESSAGE(50000, 'x')</c> and
/// <c>FORMATMESSAGE(99999999)</c> both yield NULL). Result type is
/// <c>nvarchar</c>; the value truncates to 2047 characters (probe-confirmed).
/// </summary>
/// <remarks>
/// <para>
/// The formatter mirrors SQL Server's C-runtime <c>printf</c> subset — not
/// .NET <c>string.Format</c>. Specifier grammar:
/// <c>%[flags][width][.precision][length]type</c> where flags are any of
/// <c>- + 0 # (space)</c>, width is a digit run or <c>*</c> (consumes an int
/// argument), precision is <c>.digits</c>, length is <c>l</c> (ignored) or
/// <c>I64</c> (bigint), and type is one of <c>s d i u o x X</c>. <c>%%</c>
/// emits a literal <c>%</c>. All specifier forms were probe-confirmed against
/// SQL Server 2025 (2026-07-10): <c>%5d</c>→right-pad, <c>%-5d</c>→left-align,
/// <c>%05d</c>→zero-pad, <c>%+d</c>→forced sign, <c>%#x</c>→<c>0x</c> prefix,
/// <c>%.3d</c>→min-digit zero-pad, <c>%*d</c>→argument-driven width,
/// <c>%u</c>→unsigned (<c>-1</c>→<c>4294967295</c>), <c>%I64d</c>→bigint.
/// </para>
/// <para>
/// Argument handling (probe-confirmed): a NULL argument, or a specifier with
/// no corresponding argument, renders the literal text <c>(null)</c>; extra
/// arguments beyond the specifier count are ignored; a NULL format string
/// yields NULL.
/// </para>
/// <para>
/// Error handling diverges from <see cref="MessageFormatter"/> (the RAISERROR
/// path, which throws): FORMATMESSAGE only throws Msg 2748 when a *consumed*
/// substitution argument's data type is fundamentally disallowed
/// (anything other than the integer family excluding <c>bit</c>, the string
/// family, or the binary family). Every other failure — a supported-but-
/// mismatched argument (int into <c>%s</c>, string/binary into <c>%d</c>,
/// bigint into a 32-bit specifier, int into <c>%I64d</c>), a malformed
/// specifier, or an empty format string — does NOT throw; instead the whole
/// result becomes SQL Server's terse in-server formatting-error diagnostic
/// string (probe-captured byte-exact, including the trailing CRLF).
/// </para>
/// <para>
/// Divergence: real SQL Server treats a scale-0 <c>numeric</c>/<c>decimal</c>
/// argument as a valid substitution type (then fails formatting with the
/// terse string); the simulator raises Msg 2748 for any decimal/numeric
/// regardless of scale. Known system-message ids aren't modeled (no
/// <c>sys.messages</c>).
/// </para>
/// <para>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/formatmessage-transact-sql
/// </para>
/// </remarks>
internal sealed class FormatMessage : Expression
{
    /// <summary>
    /// Maximum character length of the rendered result (probe-confirmed:
    /// <c>DATALENGTH</c> of a 5000-character <c>%s</c> substitution is 4094
    /// bytes = 2047 nchars).
    /// </summary>
    private const int MaxResultChars = 2047;

    /// <summary>
    /// SQL Server's terse in-server formatting-error diagnostic, returned as
    /// the whole result whenever formatting fails without a hard type error.
    /// Byte-exact capture from SQL Server 2025 (2026-07-10), trailing CRLF
    /// included.
    /// </summary>
    private const string TerseFormattingError =
        "Error: 50000, Severity: -1, State: 1. (Params:). The error is printed in terse mode because there was error during formatting. Tracing, ETW, notifications etc are skipped.\r\n";

    private readonly Expression formatArg;
    private readonly Expression[] substitutionArgs;

    public FormatMessage(ParserContext context)
    {
        if (context.Token is Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionArgumentCount("formatmessage", 1);

        this.formatArg = Parse(context);
        List<Expression> args = [];
        while (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            args.Add(Parse(context));
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.substitutionArgs = [.. args];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var formatValue = this.formatArg.Run(runtime);
        if (formatValue.IsNull)
            return SqlValue.Null(SqlType.NVarchar);

        // Numeric first argument → the msg_id overload. sys.messages isn't
        // modeled, so every id resolves as "unknown" → NULL.
        if (formatValue.Type.Category == SqlTypeCategory.Integer)
            return SqlValue.Null(SqlType.NVarchar);

        var format = formatValue.CoerceTo(SqlType.NVarchar).AsString;

        var args = new SqlValue[this.substitutionArgs.Length];
        for (var i = 0; i < args.Length; i++)
            args[i] = this.substitutionArgs[i].Run(runtime);

        var rendered = TryRender(format, args, out var text) ? text : TerseFormattingError;
        if (rendered.Length > MaxResultChars)
            rendered = rendered[..MaxResultChars];
        return SqlValue.FromNVarchar(rendered);
    }

    /// <summary>
    /// Renders <paramref name="format"/> into <paramref name="result"/>.
    /// Returns <c>false</c> (leaving <paramref name="result"/> empty) when a
    /// recoverable formatting failure — malformed specifier, empty format, or
    /// a supported-but-mismatched argument — should surface as the terse
    /// diagnostic. Throws Msg 2748 when a consumed argument's type is
    /// fundamentally disallowed as a substitution parameter.
    /// </summary>
    private static bool TryRender(string format, IReadOnlyList<SqlValue> args, out string result)
    {
        result = string.Empty;

        // An empty format string is itself a formatting error (probe-confirmed:
        // FORMATMESSAGE('') → terse diagnostic).
        if (format.Length == 0)
            return false;

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

            i++;
            if (i >= format.Length)
                return false; // lone trailing '%'
            if (format[i] == '%')
            {
                _ = output.Append('%');
                continue;
            }

            // Flags: any run of '- + 0 # (space)'.
            var leftAlign = false;
            var zeroPad = false;
            var forceSign = false;
            var spaceSign = false;
            var altForm = false;
            for (; i < format.Length; i++)
            {
                switch (format[i])
                {
                    case '-': leftAlign = true; continue;
                    case '0': zeroPad = true; continue;
                    case '+': forceSign = true; continue;
                    case ' ': spaceSign = true; continue;
                    case '#': altForm = true; continue;
                }
                break;
            }
            if (i >= format.Length)
                return false;

            // Width: digit run, or '*' to consume an int argument.
            var width = 0;
            if (format[i] == '*')
            {
                if (!TryTakeInt(args, ref argIndex, acceptInt64: false, out var starWidth))
                    return false;
                if (starWidth < 0)
                {
                    leftAlign = true;
                    starWidth = -starWidth;
                }
                width = (int)starWidth;
                i++;
            }
            else
            {
                while (i < format.Length && format[i] is >= '0' and <= '9')
                {
                    width = (width * 10) + (format[i] - '0');
                    i++;
                }
            }
            if (i >= format.Length)
                return false;

            // Precision: '.digits'.
            var precision = -1;
            if (format[i] == '.')
            {
                i++;
                precision = 0;
                while (i < format.Length && format[i] is >= '0' and <= '9')
                {
                    precision = (precision * 10) + (format[i] - '0');
                    i++;
                }
                if (i >= format.Length)
                    return false;
            }

            // Length modifier: 'l'/'ll'/'h'/'hh' (ignored) or 'I64' (bigint).
            var isInt64 = false;
            if (format[i] is 'l' or 'h')
            {
                i++;
                if (i < format.Length && format[i] == format[i - 1])
                    i++;
                if (i >= format.Length)
                    return false;
            }
            else if (format[i] == 'I' && i + 2 < format.Length && format[i + 1] == '6' && format[i + 2] == '4')
            {
                isInt64 = true;
                i += 3;
                if (i >= format.Length)
                    return false;
            }

            if (!TryRenderSpecifier(format[i], args, ref argIndex, leftAlign, zeroPad, forceSign, spaceSign, altForm, width, precision, isInt64, out var piece))
                return false;
            _ = output.Append(piece);
        }

        result = output.ToString();
        return true;
    }

    private static bool TryRenderSpecifier(char type, IReadOnlyList<SqlValue> args, ref int argIndex, bool leftAlign, bool zeroPad, bool forceSign, bool spaceSign, bool altForm, int width, int precision, bool isInt64, out string piece)
    {
        piece = string.Empty;
        switch (type)
        {
            case 's':
                {
                    if (isInt64)
                        return false;
                    if (!TryTakeString(args, ref argIndex, out var text, out var isNull))
                        return false;
                    if (isNull)
                    {
                        piece = "(null)";
                        return true;
                    }
                    if (precision >= 0 && text.Length > precision)
                        text = text[..precision];
                    piece = Pad(text, width, leftAlign, zeroPad: false);
                    return true;
                }
            case 'd':
            case 'i':
                {
                    if (!TryTakeInt(args, ref argIndex, isInt64, out var n, out var isNull))
                        return false;
                    if (isNull)
                    {
                        piece = "(null)";
                        return true;
                    }
                    var digits = Math.Abs(n).ToString(CultureInfo.InvariantCulture);
                    if (precision >= 0)
                        digits = digits.PadLeft(precision, '0');
                    var sign = n < 0 ? "-" : forceSign ? "+" : spaceSign ? " " : string.Empty;
                    piece = PadSigned(sign, digits, width, leftAlign, zeroPad && precision < 0);
                    return true;
                }
            case 'u':
            case 'o':
            case 'x':
            case 'X':
                {
                    if (!TryTakeInt(args, ref argIndex, isInt64, out var n, out var isNull))
                        return false;
                    if (isNull)
                    {
                        piece = "(null)";
                        return true;
                    }
                    var unsigned = isInt64 ? (ulong)n : (uint)n;
                    var digits = type switch
                    {
                        'o' => Convert.ToString((long)unsigned, 8),
                        'x' => unsigned.ToString("x", CultureInfo.InvariantCulture),
                        'X' => unsigned.ToString("X", CultureInfo.InvariantCulture),
                        _ => unsigned.ToString(CultureInfo.InvariantCulture),
                    };
                    if (precision >= 0)
                        digits = digits.PadLeft(precision, '0');
                    var prefix = altForm && unsigned != 0
                        ? type switch { 'x' => "0x", 'X' => "0X", 'o' => "0", _ => string.Empty }
                        : string.Empty;
                    piece = PadSigned(prefix, digits, width, leftAlign, zeroPad && precision < 0);
                    return true;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// Consumes the next argument as a string. Missing / NULL → <c>(null)</c>.
    /// A supported-but-non-string argument yields <c>false</c> (terse
    /// diagnostic); a disallowed type throws Msg 2748.
    /// </summary>
    private static bool TryTakeString(IReadOnlyList<SqlValue> args, ref int argIndex, out string text, out bool isNull)
    {
        text = string.Empty;
        isNull = false;
        if (argIndex >= args.Count)
        {
            argIndex++;
            isNull = true;
            return true;
        }
        var arg = args[argIndex++];
        RejectDisallowedType(arg, argIndex);
        if (arg.IsNull)
        {
            isNull = true;
            return true;
        }
        if (arg.Type.Category != SqlTypeCategory.String)
            return false;
        text = arg.AsString;
        return true;
    }

    private static bool TryTakeInt(IReadOnlyList<SqlValue> args, ref int argIndex, bool acceptInt64, out long value, out bool isNull)
    {
        value = 0;
        isNull = false;
        if (argIndex >= args.Count)
        {
            argIndex++;
            isNull = true;
            return true;
        }
        var arg = args[argIndex++];
        RejectDisallowedType(arg, argIndex);
        if (arg.IsNull)
        {
            isNull = true;
            return true;
        }
        // A 32-bit specifier accepts tinyint/smallint/int; a bigint requires
        // %I64. %I64 requires a bigint specifically (probe-confirmed: int into
        // %I64d and bigint into %d both fail with the terse diagnostic).
        if (acceptInt64)
        {
            if (arg.Type != SqlType.BigInt)
                return false;
            value = arg.AsInt64;
            return true;
        }
        if (arg.Type == SqlType.Int32) { value = arg.AsInt32; return true; }
        if (arg.Type == SqlType.SmallInt) { value = arg.AsInt16; return true; }
        if (arg.Type == SqlType.TinyInt) { value = arg.AsByte; return true; }
        return false;
    }

    /// <summary>
    /// The <c>*</c> width consumer — no NULL slot semantics (a NULL/missing
    /// width just surfaces the terse diagnostic).
    /// </summary>
    private static bool TryTakeInt(IReadOnlyList<SqlValue> args, ref int argIndex, bool acceptInt64, out long value)
    {
        if (!TryTakeInt(args, ref argIndex, acceptInt64, out value, out var isNull) || isNull)
        {
            value = 0;
            return false;
        }
        return true;
    }

    private static void RejectDisallowedType(SqlValue arg, int oneBasedIndex)
    {
        if (arg.IsNull)
            return;
        var type = arg.Type;
        var allowed = (type.Category == SqlTypeCategory.Integer && type is not BitSqlType)
            || type.Category == SqlTypeCategory.String
            || type is VarbinarySqlType or BinarySqlType or ImageSqlType;
        if (!allowed)
            throw SimulatedSqlException.SubstitutionParameterTypeNotAllowed(type.SqlServerName, oneBasedIndex);
    }

    private static string Pad(string s, int width, bool leftAlign, bool zeroPad) =>
        width <= 0 || s.Length >= width ? s
        : leftAlign ? s.PadRight(width)
        : s.PadLeft(width, zeroPad ? '0' : ' ');

    /// <summary>
    /// Pads a numeric field whose leading <paramref name="prefix"/> (sign or
    /// alt-form prefix) must stay ahead of any zero-fill.
    /// </summary>
    private static string PadSigned(string prefix, string digits, int width, bool leftAlign, bool zeroPad)
    {
        var body = prefix + digits;
        return width <= 0 || body.Length >= width ? body
            : leftAlign ? body.PadRight(width)
            : zeroPad ? prefix + digits.PadLeft(width - prefix.Length, '0')
            : body.PadLeft(width);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.NVarchar;

    internal override string DebugDisplay() => $"FORMATMESSAGE({this.formatArg.DebugDisplay()}, ...)";
}
