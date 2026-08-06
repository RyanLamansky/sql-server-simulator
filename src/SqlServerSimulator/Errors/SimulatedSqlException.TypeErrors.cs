using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 8115: arithmetic overflow converting an
    /// expression into a narrower numeric data type.
    /// </summary>
    /// <param name="targetType">The destination type's display name (e.g. <c>tinyint</c>).</param>
    /// <remarks>
    /// State 2, probe-confirmed 2026-07-24 against both reachable paths: integer
    /// arithmetic overflow (<c>cast(2147483647 as int) + cast(1 as int)</c>) and
    /// conversion overflow (<c>cast(@i as datetime)</c> past the range).
    /// </remarks>
    internal static SimulatedSqlException ArithmeticOverflow(string targetType) => new($"Arithmetic overflow error converting expression to data type {targetType}.", 8115, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 8117: an arithmetic operator was used with a
    /// data type that doesn't support it at all (e.g. <c>date + date</c>,
    /// <c>time + time</c>). Distinct from Msg 206 (cross-type clash) and
    /// Msg 402 (incompatible date-family pair); fires when both operands are
    /// the same date-family type that has no arithmetic implementation.
    /// </summary>
    internal static SimulatedSqlException OperandDataTypeInvalid(SqlType operand, string operatorName) =>
        new($"Operand data type {FamilyRootName(operand)} is invalid for {operatorName} operator.", 8117, 16, 1);

    /// <summary>
    /// The untyped-<c>NULL</c> variant of <see cref="OperandDataTypeInvalid"/>:
    /// an aggregate whose operand is the bare <c>NULL</c> keyword reports the
    /// literal type name <c>NULL</c>, which no <see cref="SqlType"/> models (the
    /// simulator resolves a bare NULL to a placeholder <see cref="SqlType.Int32"/>).
    /// Probe-confirmed 2026-07-24 for count / count_big / sum / avg / max / min /
    /// stdev / checksum_agg.
    /// </summary>
    internal static SimulatedSqlException OperandDataTypeNullInvalid(string operatorName) =>
        new($"Operand data type NULL is invalid for {operatorName} operator.", 8117, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2715: an unknown type name appears in a column
    /// declaration (CREATE TABLE / DECLARE / parameter list). The CAST/CONVERT
    /// path uses a separate error (Msg 243); see <see cref="CannotFindDataTypeInCast"/>.
    /// </summary>
    /// <param name="name">The name of the type.</param>
    /// <param name="index">The 1-based index of the reference.</param>
    internal static SimulatedSqlException CannotFindDataType(ReadOnlySpan<char> name, int index) => new($"Column, parameter, or variable #{index}: Cannot find data type {name}.", 2715, 16, 6);

    /// <summary>
    /// Mimics SQL Server error 243: an unknown type name appears inside a
    /// CAST or CONVERT expression. (Column declarations use Msg 2715 instead.)
    /// </summary>
    internal static SimulatedSqlException CannotFindDataTypeInCast(ReadOnlySpan<char> name) =>
        new($"Type {name} is not a defined system type.", 243, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2716: a length was specified for a data type that
    /// doesn't accept one (e.g. <c>int(4)</c>) in a column declaration. The
    /// CAST/CONVERT path uses Msg 291 instead — see <see cref="CannotSpecifyColumnWidthInCast"/>.
    /// </summary>
    internal static SimulatedSqlException CannotSpecifyColumnWidth(SqlType type, int index) =>
        new($"Column, parameter, or variable #{index}: Cannot specify a column width on data type {type}.", 2716, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 291: a length specifier appears on a fixed-length
    /// type inside a CAST or CONVERT expression (e.g. <c>cast(x as int(4))</c>).
    /// </summary>
    internal static SimulatedSqlException CannotSpecifyColumnWidthInCast(SqlType type) =>
        new($"CAST or CONVERT: invalid attributes specified for type '{type}'", 291, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 131 in column-declaration form: a
    /// <c>varchar</c> / <c>varbinary</c> column exceeds the 8000-byte cap.
    /// SQL Server uses the column name (unquoted) in the message and reports
    /// Class 15 / State 2.
    /// </summary>
    /// <remarks>
    /// <c>nvarchar</c> columns take a separate error path entirely
    /// (<see cref="NVarcharSizeExceedsMaximumColumn"/>, Msg 2717). CAST
    /// contexts use a different message form (<see cref="SizeExceedsMaximumCast"/>).
    /// </remarks>
    internal static SimulatedSqlException SizeExceedsMaximumColumn(string columnName, int requested, int max) =>
        new($"The size ({requested}) given to the column '{columnName}' exceeds the maximum allowed for any data type ({max}).", 131, 15, 2);

    /// <summary>
    /// Mimics SQL Server error 131 in CAST form for <c>varchar</c> /
    /// <c>varbinary</c> / <c>char</c> / <c>binary</c>. Class 15 / State 3.
    /// The message always names the family root (no <c>(N)</c> suffix); callers
    /// of parameterized types must pass the bare name explicitly because the
    /// resolved <see cref="SqlType"/>'s <see cref="object.ToString"/> renders
    /// the suffix for debug contexts.
    /// </summary>
    internal static SimulatedSqlException SizeExceedsMaximumCast(string typeName, int requested, int max) =>
        new($"The size ({requested}) given to the type '{typeName}' exceeds the maximum allowed for any data type ({max}).", 131, 15, 3);

    /// <summary>
    /// Mimics SQL Server error 2717: an <c>nvarchar</c> column exceeds the
    /// 4000-character cap. Distinct error code from the
    /// <c>varchar</c> / <c>varbinary</c> path; uses "parameter" wording even
    /// for column declarations and omits the "for any data type" suffix.
    /// </summary>
    internal static SimulatedSqlException NVarcharSizeExceedsMaximumColumn(string columnName, int requested) =>
        new($"The size ({requested}) given to the parameter '{columnName}' exceeds the maximum allowed (4000).", 2717, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 131 in CAST form for <c>nvarchar</c> /
    /// <c>nchar</c>. Class 16 / State 1; uses "convert specification" wording.
    /// The type name is parameterized so <c>nchar(N)</c> casts produce the
    /// matching <c>'nchar'</c> wording (verified against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException NVarcharSizeExceedsMaximumCast(string typeName, int requested) =>
        new($"The size ({requested}) given to the convert specification '{typeName}' exceeds the maximum allowed for any data type (4000).", 131, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1001: a length or precision specification of
    /// 0 is invalid for the target type. SQL Server fires this generic check
    /// before the more-specific "cannot specify column width" (Msg 2716) and
    /// "size exceeds maximum" (Msg 131) errors, so <c>varchar(0)</c> and
    /// <c>datetime(0)</c> both land here even though the underlying problem
    /// (zero-length string vs unsupported precision parameter) differs.
    /// </summary>
    internal static SimulatedSqlException LengthOrPrecisionSpecificationInvalid(int requested, int line) =>
        new($"Line {line}: Length or precision specification {requested} is invalid.", 1001, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1002: a precision/scale parameter on a
    /// fractional-second type (<c>datetime2(N)</c>, <c>time(N)</c>,
    /// <c>datetimeoffset(N)</c>) is out of the 0-7 range. The
    /// <paramref name="line"/> tracks the line of the offending type token
    /// and is rendered into the message as the <c>"Line N: "</c> prefix
    /// SQL Server emits for parse-time errors of this class.
    /// </summary>
    internal static SimulatedSqlException InvalidScale(int requested, int line) =>
        new($"Line {line}: Specified scale {requested} is invalid.", 1002, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 289: a <c>*FROMPARTS</c> builder received
    /// argument values outside the legal range for the constructed type
    /// (e.g. <c>DATEFROMPARTS(2025, 2, 30)</c>, <c>TIMEFROMPARTS(24, ...)</c>).
    /// State numbers vary by target type: 1=date, 2=time, 3=datetime,
    /// 5=datetime2, 6=datetimeoffset (probe-confirmed against SQL Server 2025,
    /// 2026-05-09).
    /// </summary>
    internal static SimulatedSqlException CannotConstructFromParts(string typeName, byte state) =>
        new($"Cannot construct data type {typeName}, some of the arguments have values which are not valid.", 289, 16, state);

    /// <summary>
    /// Mimics SQL Server error 10760: the scale (precision) argument of a
    /// <c>*FROMPARTS</c> builder for <c>datetime2</c> / <c>time</c> /
    /// <c>datetimeoffset</c> isn't a valid integer constant — typically
    /// triggered by passing <c>NULL</c> in that slot. Probe-confirmed against
    /// SQL Server 2025 (2026-05-09).
    /// </summary>
    internal static SimulatedSqlException ScaleArgumentNotValid(string typeName) =>
        new($"Scale argument is not valid. Valid expressions for data type {typeName} scale argument are integer constants and integer constant expressions.", 10760, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8116: the LHS of an <c>AT TIME ZONE</c>
    /// expression is a type SQL Server doesn't accept (<c>date</c> or
    /// <c>time</c>). Probe-confirmed against SQL Server 2025 (2026-05-09);
    /// the wording uses the family-root name (<c>date</c> / <c>time</c>),
    /// not a parameterized form.
    /// </summary>
    internal static SimulatedSqlException AtTimeZoneInvalidArgument(string typeName) =>
        new($"Argument data type {typeName} is invalid for argument 1 of AT TIME ZONE function.", 8116, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 9820: the time-zone string supplied to
    /// <c>AT TIME ZONE</c> isn't recognized. Includes the offending name
    /// verbatim (empty string renders as <c>''</c>). Probe-confirmed against
    /// SQL Server 2025 (2026-05-09).
    /// </summary>
    internal static SimulatedSqlException InvalidTimeZoneParameter(string name) =>
        new($"The time zone parameter '{name}' provided to AT TIME ZONE clause is invalid.", 9820, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 241: a string value could not be parsed as a
    /// date or time value (e.g. <c>CAST('not-a-date' AS date)</c>). Covers
    /// the <c>date</c> / <c>datetime</c> / <c>datetime2</c> / <c>time</c> /
    /// <c>datetimeoffset</c> targets; <c>smalldatetime</c> uses its own
    /// distinct Msg 295 path (see <see cref="ConversionFailedSmallDateTimeFromString"/>).
    /// </summary>
    internal static SimulatedSqlException ConversionFailedDateTimeFromString() =>
        new("Conversion failed when converting date and/or time from character string.", 241, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 9807: <c>CONVERT(date-like, '...', N)</c>
    /// received a string that parses as a valid date but doesn't follow the
    /// specific style number's format. Distinct from Msg 241 (general bad
    /// format) — apps that explicitly check for style-specific input shape
    /// can distinguish "wrong format" from "not a date at all". Probe-
    /// confirmed verbatim against SQL Server 2025 (2026-05-13).
    /// </summary>
    internal static SimulatedSqlException InputCharacterStringStyleMismatch(int style) =>
        new($"The input character string does not follow style {style}, either change the input character string or use a different style.", 9807, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 295: the <c>smalldatetime</c>-specific
    /// counterpart of <see cref="ConversionFailedDateTimeFromString"/>. SQL
    /// Server uses a distinct Msg number and a target-named message text
    /// for this type — verified against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ConversionFailedSmallDateTimeFromString() =>
        new("Conversion failed when converting character string to smalldatetime data type.", 295, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 8169: a string couldn't be parsed as a
    /// <c>uniqueidentifier</c>. SQL Server uses a single fixed message
    /// regardless of why (bad format, wrong length, leading whitespace,
    /// parens-instead-of-braces) — verified against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ConversionFailedFromStringToUniqueIdentifier() =>
        new("Conversion failed when converting from a character string to uniqueidentifier.", 8169, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 8114: a non-numeric value was passed to a
    /// data-type conversion that demands a numeric form (typically
    /// <c>CAST('abc' AS decimal/numeric/float)</c>). The fixed text uses
    /// <c>"numeric"</c> for both decimal and numeric targets — verified
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ConvertingDataTypeError(SqlType source, string targetWord) =>
        new($"Error converting data type {FamilyRootName(source)} to {targetWord}.", 8114, 16, 5);

    /// <summary>
    /// Variant of <see cref="ConvertingDataTypeError(SqlType, string)"/>
    /// taking the source-family wording directly — for callers that don't
    /// have a <see cref="SqlType"/> instance handy (e.g. the time-source
    /// arm of CONVERT-style dispatch where no <c>time</c> singleton exists
    /// because the type carries precision).
    /// </summary>
    internal static SimulatedSqlException ConvertingDataTypeError(string sourceFamilyName, string targetWord) =>
        new($"Error converting data type {sourceFamilyName} to {targetWord}.", 8114, 16, 5);

    /// <summary>
    /// Mimics SQL Server error 8115 in its decimal/numeric variant: scale-
    /// truncated rounding produced a value whose integer part exceeds the
    /// destination precision. Real-server text uses <c>"numeric"</c> for
    /// both decimal and numeric targets.
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowToNumeric() =>
        new("Arithmetic overflow error converting expression to data type numeric.", 8115, 16, 8);

    /// <summary>
    /// Mimics SQL Server error 8115 with a target-name slot — used by
    /// money / smallmoney range overflows where the message text reads
    /// <c>"... converting numeric to data type smallmoney"</c>. The numeric/
    /// expression source-word matches what real SQL Server emits when the
    /// overflow happens in the integer-to-target widening path.
    /// </summary>
    /// <summary>
    /// Mimics SQL Server error 8115 naming both ends — the shape a conversion
    /// out of a character source reports when the number it read is wider than
    /// the destination's precision (probed 2026-08-05:
    /// <c>CAST('123456789012345678901234567890' AS decimal(20, 0))</c> reads
    /// "Arithmetic overflow error converting varchar to data type numeric.").
    /// The state splits the two ways a number can be too wide: 6 when it
    /// exceeds <c>numeric</c>'s own 38-digit maximum, 8 when it merely exceeds
    /// the declared target's.
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowConverting(SqlType source, string targetWord, byte state) =>
        new($"Arithmetic overflow error converting {FamilyRootName(source)} to data type {targetWord}.", 8115, 16, state);

    /// <summary>
    /// Mimics SQL Server error 8115 out of an exact-numeric source, whose
    /// message names <c>numeric</c> on the source side whatever the declared
    /// precision. The state is the target's: 4 for <c>money</c> /
    /// <c>smallmoney</c>, 5 for a <c>varchar</c> / <c>char</c> too short to
    /// hold the rendering, and 6 or 8 for a narrower <c>numeric</c> depending
    /// on whether the value overran 38 digits or only the declared precision —
    /// each probed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowToTarget(string targetTypeName, byte state) =>
        new($"Arithmetic overflow error converting numeric to data type {targetTypeName}.", 8115, 16, state);

    /// <summary>
    /// Mimics SQL Server error 235: a string couldn't be parsed as money /
    /// smallmoney (e.g. <c>'5.5e2'</c>, <c>'abc'</c>). Distinct from
    /// Msg 8114 (decimal/float source error); the money-specific text
    /// emphasizes the "incorrect syntax" angle.
    /// </summary>
    internal static SimulatedSqlException CannotConvertCharToMoney() =>
        new("Cannot convert a char value to money. The char value has incorrect syntax.", 235, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8134: division by zero in integer, decimal,
    /// float, or money arithmetic.
    /// </summary>
    internal static SimulatedSqlException DivideByZero() =>
        new("Divide by zero error encountered.", 8134, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3623: a math function received an input that
    /// has no real-valued result (negative <c>SQRT</c>, non-positive
    /// <c>LOG</c> / <c>LOG10</c> input, base-1 <c>LOG</c>, or
    /// <c>POWER(negative, fractional)</c>). Probe-confirmed against
    /// SQL Server 2025 (2026-05-09): same message text and class for all
    /// triggers; no slot for the function name or value.
    /// </summary>
    internal static SimulatedSqlException InvalidFloatingPointOperation() =>
        new("An invalid floating point operation occurred.", 3623, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 232: a numeric result overflowed the target
    /// integer type — fired by <c>POWER</c> when the float-internal result
    /// can't be coerced back to the input's integer type, and by float /
    /// real / money conversion overflows via
    /// <see cref="TryConversionOverflow"/>. Distinct from Msg 8115 (generic
    /// arithmetic overflow) by message wording: this one embeds the source
    /// numeric value, while 8115 embeds only the target type. The simulator
    /// formats <paramref name="formattedValue"/> as SQL Server does — six
    /// fractional digits via <c>F6</c>. The state is target-keyed for the
    /// conversion path (tinyint 1, smallint 2, int 3, money source 11);
    /// the default 3 is <c>POWER</c>'s int-result state.
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowForType(string typeName, string formattedValue, byte state = 3) =>
        new($"Arithmetic overflow error for type {typeName}, value = {formattedValue}.", 232, 16, state);

    /// <summary>
    /// <see cref="ArithmeticOverflowForType(string, string, byte)"/> for an
    /// approximate source, which real renders at <b>seventeen significant
    /// digits</b> before padding to six fractional ones — so a magnitude past
    /// seventeen digits shows trailing zeros rather than the double's own
    /// exact binary tail (<c>CAST(CAST(1e30 AS float) AS int)</c> names
    /// <c>1000000000000000000000000000000.000000</c>, not the double's
    /// <c>…19884624838656</c>).
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowForType(string typeName, double value, byte state) =>
        ArithmeticOverflowForType(typeName, FormatApproximateAtSeventeenDigits(value), state);

    private static string FormatApproximateAtSeventeenDigits(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) < 1e17)
            return value.ToString("F6", CultureInfo.InvariantCulture);

        // "E16" is the seventeen-digit form; past 1e17 every one of those
        // digits is an integer digit, so the layout is the digits, the zeros
        // the exponent adds beyond them, and an all-zero fraction.
        var text = value.ToString("E16", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        var body = negative ? text[1..] : text;
        var marker = body.IndexOf('E', StringComparison.Ordinal);
        var digits = string.Concat(body[..1], body[2..marker]);
        var exponent = int.Parse(body[(marker + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture);
        return string.Concat(negative ? "-" : "", digits, new string('0', exponent - 16), ".000000");
    }

    /// <summary>
    /// Mimics SQL Server error 220: integer-family narrowing overflow during
    /// column assignment, CAST, or ALTER COLUMN per-row coercion (e.g.
    /// <c>int</c> → <c>tinyint</c> on a value &gt; 255). Probe-confirmed
    /// verbatim wording: <c>"Arithmetic overflow error for data type tinyint,
    /// value = 500."</c>; embeds the target type's SqlServer name (lowercase)
    /// and the offending value's invariant-culture string form. The state is
    /// target-keyed: tinyint 2, smallint 1, and 7 for a money source
    /// reporting its ×10000 tick value.
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowForDataType(string targetTypeName, string formattedValue, byte state = 2) =>
        new($"Arithmetic overflow error for data type {targetTypeName}, value = {formattedValue}.", 220, 16, state);

    /// <summary>
    /// Mimics SQL Server error 237: a <c>money</c> source overflowed an
    /// <c>int</c> conversion target. The money-to-integer overflow surface is
    /// splintered per target — see <see cref="TryConversionOverflow"/> —
    /// and this is the int cell. Same text as Msg 234's string-target
    /// variant, different error number (probe-confirmed 2026-07-31).
    /// </summary>
    internal static SimulatedSqlException InsufficientResultSpaceForMoneyToInt() =>
        new("There is insufficient result space to convert a money value to int.", 237, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 237's <c>smallmoney</c> cell: a <c>money</c>
    /// value outside <c>smallmoney</c>'s range. Same number as the <c>int</c>
    /// cell at a different state (probe-confirmed).
    /// </summary>
    internal static SimulatedSqlException InsufficientResultSpaceForMoneyToSmallMoney() =>
        new("There is insufficient result space to convert a money value to smallmoney.", 237, 16, 3);

    /// <summary>
    /// Chooses the SQL Server error for a numeric source value that overflowed
    /// a narrower conversion target, or <see langword="null"/> when no special
    /// case applies and the caller's own generic Msg 8115 stands. The source
    /// type picks the error family and the target picks the state —
    /// probe-confirmed against SQL Server 2025 (2026-07-31), identical across
    /// CAST/CONVERT, INSERT/UPDATE column assignment, <c>SET @v</c>, and
    /// ALTER COLUMN:
    /// <list type="bullet">
    /// <item><c>tinyint</c> / <c>smallint</c> / <c>int</c> source → the
    /// value-bearing Msg 220 (tinyint state 2, smallint state 1); a
    /// <c>bigint</c> source stays Msg 8115.</item>
    /// <item><c>float</c> / <c>real</c> source → the value-bearing Msg 232
    /// with six fractional digits (state 1/2/3 for tinyint/smallint/int); a
    /// <c>bigint</c> target stays Msg 8115.</item>
    /// <item><c>money</c> source → Msg 232 state 11 for tinyint, Msg 220
    /// state 7 for smallint with the value in money's ×10000 tick
    /// representation, Msg 237 for int; <c>smallmoney</c> takes none of
    /// these and stays Msg 8115.</item>
    /// </list>
    /// </summary>
    internal static SimulatedSqlException? TryConversionOverflow(SqlValue source, SqlType targetType)
    {
        if (targetType is TinyIntSqlType or SmallIntSqlType
            && SqlType.IsIntegerCategory(source.Type) && source.Type != SqlType.BigInt)
        {
            return ArithmeticOverflowForDataType(
                targetType.SqlServerName,
                source.CoerceTo(SqlType.BigInt).AsInt64.ToString(CultureInfo.InvariantCulture),
                state: targetType is TinyIntSqlType ? (byte)2 : (byte)1);
        }

        if (source.Type == SqlType.Float || source.Type == SqlType.Real)
        {
            var state = targetType is TinyIntSqlType ? (byte)1
                : targetType is SmallIntSqlType ? (byte)2
                : targetType == SqlType.Int32 ? (byte)3
                : (byte)0;
            return state == 0 ? null : ArithmeticOverflowForType(
                targetType.SqlServerName, source.CoerceTo(SqlType.Float).AsDouble, state);
        }

        if (source.Type == SqlType.Money)
        {
            if (targetType is TinyIntSqlType)
                return ArithmeticOverflowForType("tinyint", source.AsMoney.ToString("F6", CultureInfo.InvariantCulture), state: 11);
            if (targetType is SmallIntSqlType)
                return ArithmeticOverflowForDataType("smallint", (source.AsMoney * 10000m).ToString("F0", CultureInfo.InvariantCulture), state: 7);
            if (targetType == SqlType.Int32)
                return InsufficientResultSpaceForMoneyToInt();
        }

        return null;
    }

    /// <summary>
    /// Mimics SQL Server error 8170: a non-NULL <c>uniqueidentifier</c> was
    /// cast to a <c>char</c> / <c>varchar</c> destination too short to hold
    /// the 36-character formatted GUID. The <c>nchar</c> / <c>nvarchar</c>
    /// counterpart raises Msg 8115 (the generic arithmetic-overflow path)
    /// instead — verified against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException InsufficientResultSpaceForUniqueIdentifier() =>
        new("Insufficient result space to convert uniqueidentifier value to char.", 8170, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 234: a <c>money</c> / <c>smallmoney</c> source
    /// was cast to a <c>varchar</c> / <c>nvarchar</c> destination too narrow
    /// to hold the formatted value. Distinct from the generic Msg 8115 path —
    /// money picks its own dedicated message rather than reusing the
    /// arithmetic-overflow surface. Probe-confirmed against SQL Server 2025
    /// (2026-05-09); the message says "money" regardless of whether the
    /// source was money or smallmoney.
    /// </summary>
    internal static SimulatedSqlException InsufficientResultSpaceForMoney(string targetType) =>
        new($"There is insufficient result space to convert a money value to {targetType}.", 234, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 281: a non-zero, non-120/121 style number
    /// passed to <c>CONVERT</c> when targeting a character string from a
    /// date-like type. The <paramref name="sourceTypeWord"/> is the bare
    /// family name SQL Server uses in the message — e.g. <c>"datetime"</c>,
    /// <c>"datetime2"</c>, <c>"time"</c>, <c>"datetimeoffset"</c> — never
    /// with precision suffix.
    /// </summary>
    internal static SimulatedSqlException InvalidStyleForCharacterString(int style, string sourceTypeWord) =>
        new($"{style} is not a valid style number when converting from {sourceTypeWord} to a character string.", 281, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2739: a local variable, parameter or the return
    /// of a scalar function was declared as one of the legacy LOB types, which
    /// only a column may be. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException LegacyLobTypeInvalidForLocals() =>
        new("The text, ntext, and image data types are invalid for local variables.", 2739, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8116: an argument to a function has the wrong
    /// data type — currently surfaced for <c>CONVERT</c>'s third (style)
    /// argument when it isn't an integer.
    /// </summary>
    internal static SimulatedSqlException InvalidArgumentDataType(string sourceTypeWord, int argumentIndex, string functionName) =>
        new($"Argument data type {sourceTypeWord} is invalid for argument {argumentIndex} of {functionName} function.", 8116, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 206: the binary expression's two operands
    /// belong to types that have no implicit conversion between them
    /// (e.g. <c>date = 0</c>, <c>time + 1</c>). Distinct from Msg 402
    /// (time-vs-non-time-date) and Msg 529 (explicit-CAST rejection).
    /// </summary>
    internal static SimulatedSqlException OperandTypeClash(SqlType left, SqlType right) =>
        new($"Operand type clash: {FamilyRootName(left)} is incompatible with {FamilyRootName(right)}", 206, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 206 against a user-defined table type: the
    /// scalar-typed EXEC argument is incompatible with the TVP parameter's
    /// table type. Probe-confirmed wording — the table type renders as its
    /// bare leaf name (no schema qualifier).
    /// </summary>
    internal static SimulatedSqlException OperandTypeClashScalarVsTableType(SqlType scalar, string tableTypeLeaf) =>
        new($"Operand type clash: {FamilyRootName(scalar)} is incompatible with {tableTypeLeaf}", 206, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 529: an explicit <c>CAST</c> requested a
    /// conversion that SQL Server doesn't allow even with the explicit
    /// keyword (e.g. <c>cast(0 as date)</c>, <c>cast(d as int)</c> when
    /// <c>d</c> is <c>date</c>). Implicit-conversion attempts on the same
    /// pair surface as Msg 206 from the comparison/arithmetic path.
    /// </summary>
    internal static SimulatedSqlException ExplicitConversionNotAllowed(SqlType source, SqlType target) =>
        new($"Explicit conversion from data type {FamilyRootName(source)} to {FamilyRootName(target)} is not allowed.", 529, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 245: a CAST from a string to a non-string
    /// type couldn't parse the string into the target type's value space
    /// (e.g. <c>cast('abc' as int)</c>, <c>cast('42.5' as int)</c>). The
    /// source-type word in the message reflects the actual source
    /// (<c>varchar</c>, <c>nvarchar</c>, etc.).
    /// </summary>
    internal static SimulatedSqlException ConversionFailedFromString(SqlType sourceType, string sourceValue, SqlType targetType) =>
        new($"Conversion failed when converting the {sourceType.SqlServerName} value '{sourceValue}' to data type {targetType.SqlServerName}.", 245, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 244: parsing a string succeeded but the
    /// resulting integer value exceeded the destination column's range —
    /// limited to <c>tinyint</c> (<c>INT1</c>) and <c>smallint</c>
    /// (<c>INT2</c>). Distinct from the int-overflow variant
    /// (<see cref="OverflowConvertingToInt"/>, Msg 248) and the generic
    /// arithmetic-overflow (<see cref="ArithmeticOverflow"/>, Msg 8115)
    /// used for <c>bigint</c> overflows. The state is target-keyed:
    /// <c>INT1</c> 1, <c>INT2</c> 2 (probe-confirmed 2026-07-31).
    /// </summary>
    internal static SimulatedSqlException OverflowConvertingNarrowInt(SqlType sourceType, string sourceValue, string targetTypeAlias, byte state) =>
        new($"The conversion of the {sourceType.SqlServerName} value '{sourceValue}' overflowed an {targetTypeAlias} column. Use a larger integer column.", 244, 16, state);

    /// <summary>
    /// Mimics SQL Server error 248: the int-target counterpart of Msg 244.
    /// Note the lowercase "int" wording and the missing "Use a larger
    /// integer column" sentence — both verified against real SQL Server.
    /// </summary>
    internal static SimulatedSqlException OverflowConvertingToInt(SqlType sourceType, string sourceValue) =>
        new($"The conversion of the {sourceType.SqlServerName} value '{sourceValue}' overflowed an int column.", 248, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 242, state 3: a date/time conversion
    /// (parameter binding, CAST from string, or rounding-induced overflow)
    /// produced a value outside the target type's representable range. The
    /// canonical case for legacy <c>datetime</c> is <c>'9999-12-31 23:59:59.999'</c>:
    /// rounding pushes the tick past the type's max, so SQL Server raises
    /// this rather than silently clamping. The <paramref name="target"/>
    /// parameter slots into the message text — Msg 242 is shared by
    /// <c>datetime</c> and <c>smalldatetime</c>, distinguished only by the
    /// type name.
    /// </summary>
    internal static SimulatedSqlException OutOfRangeDateTimeConversion(SqlType target) =>
        new($"The conversion of a varchar data type to a {target} data type resulted in an out-of-range value.", 242, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 402: two operand types can't be combined in
    /// a particular operator. The canonical comparison case is <c>time</c>
    /// vs any non-<c>time</c> date/time type — SQL Server allows the
    /// underlying types to convert via <c>COALESCE</c> but explicitly
    /// forbids the pair in equality/ordering operators (passes
    /// <c>"equal to"</c>). The arithmetic case is a legacy date type
    /// (<c>datetime</c>, <c>smalldatetime</c>) paired with a non-legacy
    /// date type (<c>date</c>, <c>datetime2</c>, <c>datetimeoffset</c>) —
    /// callers pass <c>"add"</c> or <c>"subtract"</c>.
    /// </summary>
    internal static SimulatedSqlException IncompatibleDataTypesInOperator(SqlType a, SqlType b, string operatorName) =>
        new($"The data types {FamilyRootName(a)} and {FamilyRootName(b)} are incompatible in the {operatorName} operator.", 402, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 257: an <c>sql_variant</c> operand meets a
    /// non-variant type in an arithmetic operator, which requires an implicit
    /// conversion the server forbids. Probe-confirmed State 3 and the verbatim
    /// "Use the CONVERT function to run this query." tail (SQL Server 2025);
    /// <paramref name="target"/> is the non-variant operand's type.
    /// </summary>
    internal static SimulatedSqlException ImplicitConversionFromSqlVariantNotAllowed(SqlType target) =>
        new($"Implicit conversion from data type sql_variant to {FamilyRootName(target)} is not allowed. Use the CONVERT function to run this query.", 257, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 536: a length / count argument to a string
    /// function (left, right, substring) was negative. The function name is
    /// lowercase in the message and the state varies by function (6 for
    /// left / right, 8 for substring), verified against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException NegativeLengthNotAllowed(string function, byte state) =>
        new($"Invalid length parameter passed to the {function} function.", 536, 16, state);

    /// <summary>
    /// Mimics SQL Server error 1007: a numeric literal carries more than 38
    /// significant digits, exceeding the maximum precision of the numeric
    /// representation. Class 15 — raised while reading the literal.
    /// </summary>
    internal static SimulatedSqlException NumberOutOfRangeForNumeric(string literal) =>
        new($"The number '{literal}' is out of the range for numeric representation (maximum precision 38).", 1007, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 9812: the offset passed to SWITCHOFFSET /
    /// TODATETIMEOFFSET falls outside the legal ±14:00 range. The builtin
    /// function name appears lowercase in the message.
    /// </summary>
    internal static SimulatedSqlException InvalidTimeZone(string function) =>
        new($"The timezone provided to builtin function {function} is invalid.", 9812, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6522: an input to a hierarchyid method
    /// (<c>Parse</c>, <c>GetAncestor</c> with out-of-range depth,
    /// <c>GetDescendant</c> with mismatched children, etc.) violates the
    /// hierarchyid contract. Real SQL Server wraps these as ".NET Framework
    /// error … during execution of user-defined routine or aggregate
    /// 'hierarchyid'"; the simulator surfaces a concise actionable message
    /// with the same number so apps doing <c>TRY/CATCH</c> on Msg 6522 still
    /// see the same code.
    /// </summary>
    internal static SimulatedSqlException InvalidHierarchyIdInput(string detail) =>
        new($"A .NET Framework error occurred during execution of user-defined routine or aggregate \"hierarchyid\": Microsoft.SqlServer.Types.HierarchyIdException: 24001: SqlHierarchyId operation failed because input '{detail}' was not valid.", 6522, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 447: an explicit <c>COLLATE</c> clause was
    /// attached to a non-string expression (probe-confirmed wording:
    /// <c>"Expression type int is invalid for COLLATE clause."</c>).
    /// Real SQL Server raises this at bind time; the simulator raises at
    /// runtime because <see cref="SqlType"/> isn't fully bound
    /// during the parse pass (column refs without a resolver are typed
    /// lazily). Same Msg + same wording; only the firing point differs.
    /// </summary>
    internal static SimulatedSqlException CollateClauseRequiresString(SqlType operandType) =>
        new($"Expression type {FamilyRootName(operandType)} is invalid for COLLATE clause.", 447, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 448: an explicit <c>COLLATE</c> clause names
    /// a collation the engine doesn't know about. Probe-confirmed against
    /// SQL Server 2025: Class 16 State 1, verbatim wording
    /// <c>"Invalid collation '{name}'."</c>.
    /// </summary>
    internal static SimulatedSqlException InvalidCollation(string name) =>
        new($"Invalid collation '{name}'.", 448, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 459: a collation with no ANSI code page — the
    /// Windows collations real SQL Server supports on Unicode data types only
    /// (Assamese, Bengali, Divehi, Indic_General, Khmer, Lao, Maltese, Maori,
    /// Nepali, Pashto, Syriac, Tibetan) — was applied to <c>char</c>,
    /// <c>varchar</c>, or <c>text</c>. Probe-confirmed against SQL Server
    /// 2025: Class 16 State 2, and batch-aborting (<c>TRY</c>/<c>CATCH</c>
    /// does not intercept it), which the simulator gets for free because the
    /// rejection fires while the column type is being constructed.
    /// </summary>
    internal static SimulatedSqlException CollationIsUnicodeOnly(string name) =>
        new($"Collation '{name}' is supported on Unicode data types only and cannot be applied to char, varchar or text data types.", 459, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 468: both operands of an operator (currently
    /// <c>LIKE</c>) carry explicit <c>COLLATE</c> postfixes that don't agree.
    /// Probe-confirmed against SQL Server 2025: Class 16 State 9, with the
    /// operand collation names quoted by <c>"</c> and the operator name
    /// lower-cased. The simulator surfaces the same shape for the LIKE
    /// site; other comparison operators don't honor the override yet.
    /// </summary>
    internal static SimulatedSqlException CollationConflict(string leftCollation, string rightCollation, string operatorName) =>
        new($"Cannot resolve the collation conflict between \"{leftCollation}\" and \"{rightCollation}\" in the {operatorName} operation.", 468, 16, 9);

    /// <summary>
    /// Mimics SQL Server error 457: a string-producing operation (concat with
    /// <c>+</c>, <c>UNION ALL</c>, <c>DISTINCT</c> over a unioned column,
    /// <c>ORDER BY</c> on a concat result) couldn't pick a single output
    /// collation because two same-rank operands had different collations.
    /// Probe-confirmed against SQL Server 2025: Class 16 State 1, wording
    /// reads "Implicit conversion of {srcType} value to {dstType} ...". The
    /// source/destination type names are both the same bare keyword (e.g.
    /// <c>varchar</c>) — SQL Server's wording uses the same word twice in
    /// the unresolved-collation case.
    /// <para>The message names the conflicting pair and the operator that
    /// couldn't resolve it — <c>add</c> for string <c>+</c>,
    /// <c>UNION ALL</c> for the set operator (probe-confirmed; note real
    /// spells the set operator upper-case and the arithmetic one lower-case,
    /// and says "operator" where Msg 468 says "operation"). Collation names
    /// follow the same right-then-left order Msg 468 uses.</para>
    /// </summary>
    internal static SimulatedSqlException UnresolvedCollationInImplicitConversion(
        SqlType type,
        string rightCollation,
        string leftCollation,
        string operatorName) =>
        new($"Implicit conversion of {type.SqlServerName} value to {type.SqlServerName} cannot be performed because the collation of the value is unresolved due to a collation conflict between \"{rightCollation}\" and \"{leftCollation}\" in {operatorName} operator.", 457, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 451: an operator couldn't pick one collation for
    /// an output column that has to name one. Probe-confirmed against SQL
    /// Server 2025: Class 16 State 1, the conflicting pair in the same
    /// right-then-left order Msg 457 / 468 use, and a tail naming the clause
    /// and the 1-based ordinal of the slot being settled —
    /// <c>SELECT</c> / <c>ORDER BY</c> / <c>GROUP BY</c>, each spelled
    /// "&lt;clause&gt; statement". Note the wording carries no leading
    /// <i>the</i> where Msg 468's does.
    /// </summary>
    internal static SimulatedSqlException UnresolvedCollationInOutputColumn(
        string rightCollation,
        string leftCollation,
        string operatorName,
        string clause,
        int ordinal) =>
        new($"Cannot resolve collation conflict between \"{rightCollation}\" and \"{leftCollation}\" in {operatorName} operator occurring in {clause} statement column {ordinal}.", 451, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 456: an <em>already unresolved</em> collation
    /// reached an implicit conversion that has to name one — a <c>varchar</c>
    /// result can't be materialized without a code page. Probe-confirmed
    /// against SQL Server 2025: Class 16 State 1. Distinct from
    /// <see cref="UnresolvedCollationInImplicitConversion"/> (Msg 457, the
    /// operator's <em>own</em> operands conflicting) both in number and in
    /// wording — "the <b>resulting</b> collation is unresolved due to collation
    /// conflict" where Msg 457 says "the collation of the value is unresolved
    /// due to <b>a</b> collation conflict" — and the operator it names is the
    /// one that produced the conflict, not the one consuming it.
    /// <para>Which family raises is the <em>source</em>'s, not the
    /// destination's (probe-confirmed against SQL Server 2025): an unresolved
    /// <c>nvarchar</c> assigns into a <c>varchar</c> column silently, where an
    /// unresolved <c>varchar</c> raises even assigning into
    /// <c>nvarchar</c>.</para>
    /// </summary>
    internal static SimulatedSqlException UnresolvedCollationReachedImplicitConversion(
        SqlType sourceType,
        SqlType destinationType,
        string rightCollation,
        string leftCollation,
        string operatorName) =>
        new($"Implicit conversion of {sourceType.SqlServerName} value to {destinationType.SqlServerName} cannot be performed because the resulting collation is unresolved due to collation conflict between \"{rightCollation}\" and \"{leftCollation}\" in {operatorName} operator.", 456, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 446: an unresolved collation reached an
    /// operation that names both the producing operator and itself. Real uses
    /// one message with a per-operation State (probe-confirmed against SQL
    /// Server 2025): <c>DISTINCT</c> is State 11, <c>CONVERT</c> — which
    /// <c>CAST</c> reports as too — is State 20, and <c>COLLATE</c> is State 6.
    /// </summary>
    /// <remarks>
    /// Only the <c>varchar</c> family reaches the CONVERT / COLLATE forms: an
    /// <c>nvarchar</c> conversion propagates the conflict instead (a
    /// <c>CAST</c> inherits the source's collation, so the marker rides along)
    /// and an <c>nvarchar</c> <c>COLLATE</c> settles it outright. DISTINCT
    /// raises for both families.
    /// </remarks>
    internal static SimulatedSqlException UnresolvedCollationInOperation(
        string rightCollation,
        string leftCollation,
        string producingOperator,
        string operation,
        byte state) =>
        new($"Cannot resolve collation conflict between \"{rightCollation}\" and \"{leftCollation}\" in {producingOperator} operator for {operation} operation.", 446, 16, state);

    /// <summary>
    /// Mimics SQL Server error 4191: an operation that needs a definite
    /// collation to do its work met a value whose collation is unresolved.
    /// Probe-confirmed against SQL Server 2025: Class 16 State 9, and the
    /// message names only the consuming operation — not the conflicting pair,
    /// and not the operator that produced the conflict.
    /// </summary>
    /// <remarks>
    /// The operation name is the built-in's own name lower-cased
    /// (<c>len</c> / <c>upper</c> / <c>substring</c> / <c>charindex</c> /
    /// <c>max</c> / <c>string_agg</c> / <c>like</c> …) or a comparison's
    /// spelled-out name (<c>equal to</c>, <c>less than</c>, …), matching the
    /// vocabulary Msg 468 uses. <c>TRIM</c> is real's own odd one out: it
    /// reports <c>Trim</c> capitalized where every sibling is lower-case.
    /// </remarks>
    internal static SimulatedSqlException UnresolvedCollationForOperation(string operationName) =>
        new($"Cannot resolve collation conflict for {operationName} operation.", 4191, 16, 9);

    /// <summary>
    /// Mimics SQL Server error 5335: a <c>UNION</c> / <c>INTERSECT</c> /
    /// <c>EXCEPT</c> branch is a type those operators can't compare. Real
    /// reaches it for a string whose collation is unresolved — those operators
    /// dedup, and a value with no collation has no comparison to dedup by
    /// (probe-confirmed against SQL Server 2025: Class 16 State 1).
    /// </summary>
    internal static SimulatedSqlException SetOpOperandNotComparable(SqlType type) =>
        new($"The data type {type.SqlServerName} cannot be used as an operand to the UNION, INTERSECT or EXCEPT operators because it is not comparable.", 5335, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 8116 — the bit-manipulation family
    /// (<c>BIT_COUNT</c> / <c>GET_BIT</c> / <c>SET_BIT</c> / <c>LEFT_SHIFT</c> /
    /// <c>RIGHT_SHIFT</c>) raises this when argument 1 isn't an integer
    /// or binary type. Verified wording against SQL Server 2025
    /// (2026-05-22).
    /// </summary>
    internal static SimulatedSqlException ArgumentDataTypeInvalidForBitFunction(string typeName, int argumentIndex, string functionName) =>
        new($"Argument data type {typeName} is invalid for argument {argumentIndex} of {functionName} function.", 8116, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 9838 — <c>GET_BIT</c> / <c>SET_BIT</c> raise
    /// this when the position argument falls outside the first operand's bit
    /// width (<c>GET_BIT(int, 32)</c> exceeds int's 0-to-31 range). The check
    /// runs against the argument widened to <c>bigint</c>, so a position past
    /// <c>int</c> range lands here rather than in a conversion overflow.
    /// State is function-keyed: 1 for <c>get_bit</c>, 2 for <c>set_bit</c>
    /// (probe-confirmed 2026-07-31).
    /// </summary>
    internal static SimulatedSqlException BitFunctionPositionOutOfRange(string functionName, int maxPosition, byte state) =>
        new($"Parameter 2 in function '{functionName}' is out of range 0 to {maxPosition}.", 9838, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 9839 — <c>SET_BIT</c>'s third argument carries
    /// the bit to write and must be exactly 0 or 1; any other value, at any
    /// magnitude, reports this rather than a range or conversion error
    /// (probe-confirmed 2026-07-31).
    /// </summary>
    internal static SimulatedSqlException BitFunctionValueNotZeroOrOne() =>
        new("Parameter 3 in function 'set_bit' must be 0 or 1.", 9839, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 9819 — <c>TRANSLATE(input, chars, translations)</c>
    /// raises this when <c>chars</c> and <c>translations</c> have unequal
    /// length. Verbatim wording verified against SQL Server 2025 (2026-05-22).
    /// </summary>
    internal static SimulatedSqlException TranslateUnequalChars() =>
        new("The second and third arguments of the TRANSLATE built-in function must contain an equal number of characters.", 9819, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 9819 (variant used by <c>PARSE</c>) — fires
    /// when the source string can't be converted into the target type
    /// under the given culture.
    /// </summary>
    internal static SimulatedSqlException ParseConversionFailed(string value, string targetTypeName, string cultureName) =>
        new($"Error converting string value '{value}' into data type {targetTypeName} using culture '{cultureName}'.", 9819, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8064: an RPC parameter declares a CLR-UDT type
    /// name (TDS type token <c>0xF0</c>) that resolves to no known type. Real
    /// substitutes the current database for the client's empty db segment and
    /// leaves the schema segment empty. Probe-confirmed against SQL Server 2025
    /// (2026-07-19).
    /// </summary>
    internal static SimulatedSqlException RpcClrTypeDoesNotExist(int ordinal, string database, string typeName) =>
        new($"Parameter {ordinal} ([{database}].[].[{typeName}]): The CLR type does not exist or you do not have permissions to access it.", 8064, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8023: an RPC CLR-UDT parameter's serialized bytes
    /// are not a valid (modeled) instance of the declared spatial type — the
    /// simulator's WKB decoder covers a 2D shape subset, so bytes outside it (or
    /// genuinely corrupt bytes) surface here as real's bind-time rejection.
    /// Probe-confirmed against SQL Server 2025 (2026-07-19).
    /// </summary>
    internal static SimulatedSqlException RpcInvalidUdtInstance(int ordinal, string parameterName, string typeName) =>
        new($"The incoming tabular data stream (TDS) remote procedure call (RPC) protocol stream is incorrect. Parameter {ordinal} (\"{parameterName}\"): The supplied value is not a valid instance of data type {typeName}. Check the source data for invalid values. An example of an invalid value is data of numeric type with scale greater than precision.", 8023, 16, 4);

    /// <summary>
    /// Returns the type name SQL Server uses in Msg 402 / 206 / 529 for a
    /// date/time type: the family root (e.g. <c>datetime2</c>,
    /// <c>datetimeoffset</c>) without a precision suffix, matching
    /// real-server output.
    /// </summary>
    private static string FamilyRootName(SqlType type) => type switch
    {
        DateTime2SqlType => "datetime2",
        TimeSqlType => "time",
        DateTimeOffsetSqlType => "datetimeoffset",
        // A MAX-form var type renders with its "(max)" suffix in these messages
        // (e.g. Msg 529 "Explicit conversion from data type image to
        // nvarchar(max) is not allowed") while a bounded length is dropped to
        // the root name — probe-confirmed against SQL Server 2025 across
        // nvarchar(10)/(max), varchar, varbinary.
        VarcharSqlType { length: SqlType.MaxLengthSentinel } => "varchar(max)",
        NVarcharSqlType { length: SqlType.MaxLengthSentinel } => "nvarchar(max)",
        VarbinarySqlType { length: SqlType.MaxLengthSentinel } => "varbinary(max)",
        VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType
            or VarbinarySqlType or BinarySqlType or DecimalSqlType => type.SqlServerName,
        _ => type.ToString()!,
    };
}
