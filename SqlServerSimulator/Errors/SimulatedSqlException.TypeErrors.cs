using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 8115: arithmetic overflow converting an
    /// expression into a narrower numeric data type.
    /// </summary>
    /// <param name="targetType">The destination type's display name (e.g. <c>tinyint</c>).</param>
    internal static SimulatedSqlException ArithmeticOverflow(string targetType) => new($"Arithmetic overflow error converting expression to data type {targetType}.", 8115, 16, 8);

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
    internal static SimulatedSqlException ArithmeticOverflowToTarget(string targetTypeName) =>
        new($"Arithmetic overflow error converting numeric to data type {targetTypeName}.", 8115, 16, 8);

    /// <summary>
    /// Mimics SQL Server error 235: a string couldn't be parsed as money /
    /// smallmoney (e.g. <c>'5.5e2'</c>, <c>'abc'</c>). Distinct from
    /// Msg 8114 (decimal/float source error); the money-specific text
    /// emphasizes the "incorrect syntax" angle.
    /// </summary>
    internal static SimulatedSqlException CannotConvertCharToMoney() =>
        new("Cannot convert a char value to money. The char value has incorrect syntax.", 235, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8134: division by zero in decimal / float /
    /// money arithmetic. Integer division currently falls through .NET's
    /// <see cref="DivideByZeroException"/> path; future integer-division
    /// alignment can reuse this factory.
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
    /// can't be coerced back to the input's integer type. Distinct from
    /// Msg 8115 (generic arithmetic overflow) by message wording: this one
    /// embeds the source numeric value, while 8115 embeds only the target
    /// type. The simulator formats <paramref name="formattedValue"/> as
    /// SQL Server does — six fractional digits via <c>F6</c>.
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowForType(string typeName, string formattedValue) =>
        new($"Arithmetic overflow error for type {typeName}, value = {formattedValue}.", 232, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 220: integer-family narrowing overflow during
    /// ALTER COLUMN per-row coercion (e.g. <c>int</c> → <c>tinyint</c> on a
    /// value &gt; 255). Probe-confirmed verbatim wording: <c>"Arithmetic
    /// overflow error for data type tinyint, value = 500."</c>; embeds the
    /// target type's SqlServer name (lowercase) and the offending value's
    /// invariant-culture string form.
    /// </summary>
    internal static SimulatedSqlException ArithmeticOverflowForDataType(string targetTypeName, string formattedValue) =>
        new($"Arithmetic overflow error for data type {targetTypeName}, value = {formattedValue}.", 220, 16, 2);

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
        new($"Conversion failed when converting the {sourceType} value '{sourceValue}' to data type {targetType}.", 245, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 244: parsing a string succeeded but the
    /// resulting integer value exceeded the destination column's range —
    /// limited to <c>tinyint</c> (<c>INT1</c>) and <c>smallint</c>
    /// (<c>INT2</c>). Distinct from the int-overflow variant
    /// (<see cref="OverflowConvertingToInt"/>, Msg 248) and the generic
    /// arithmetic-overflow (<see cref="ArithmeticOverflow"/>, Msg 8115)
    /// used for <c>bigint</c> overflows.
    /// </summary>
    internal static SimulatedSqlException OverflowConvertingNarrowInt(SqlType sourceType, string sourceValue, string targetTypeAlias) =>
        new($"The conversion of the {sourceType} value '{sourceValue}' overflowed an {targetTypeAlias} column. Use a larger integer column.", 244, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 248: the int-target counterpart of Msg 244.
    /// Note the lowercase "int" wording and the missing "Use a larger
    /// integer column" sentence — both verified against real SQL Server.
    /// </summary>
    internal static SimulatedSqlException OverflowConvertingToInt(SqlType sourceType, string sourceValue) =>
        new($"The conversion of the {sourceType} value '{sourceValue}' overflowed an int column.", 248, 16, 1);

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
    /// Mimics SQL Server error 537: a length / count argument to a string
    /// function (LEFT, RIGHT, SUBSTRING) was negative.
    /// </summary>
    internal static SimulatedSqlException NegativeLengthNotAllowed(string function) =>
        new($"Invalid length parameter passed to the {function} function.", 537, 16, 3);

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
    /// runtime because <see cref="Storage.SqlType"/> isn't fully bound
    /// during the parse pass (column refs without a resolver are typed
    /// lazily). Same Msg + same wording; only the firing point differs.
    /// </summary>
    internal static SimulatedSqlException CollateClauseRequiresString(SqlType operandType) =>
        new($"Expression type {operandType} is invalid for COLLATE clause.", 447, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 448: an explicit <c>COLLATE</c> clause names
    /// a collation the engine doesn't know about. Probe-confirmed against
    /// SQL Server 2025: Class 16 State 1, verbatim wording
    /// <c>"Invalid collation '{name}'."</c>.
    /// </summary>
    internal static SimulatedSqlException InvalidCollation(string name) =>
        new($"Invalid collation '{name}'.", 448, 16, 1);

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
        _ => type.ToString()!,
    };
}
