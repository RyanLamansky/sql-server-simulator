using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Data.Common;
using System.Globalization;

namespace SqlServerSimulator;

#pragma warning disable CA1032 // Implement standard exception constructors
// This is only thrown internally so standard constructors aren't needed.

/// <summary>
/// Describes a simulated SQL exception.
/// </summary>
internal sealed class SimulatedSqlException : DbException
{
    private SimulatedSqlException(string message, int number, byte @class, byte state)
        : this(message, new SimulatedSqlError(message, number, @class, state))
    {
    }

    private SimulatedSqlException(string message, params ReadOnlySpan<SimulatedSqlError> errors)
        : base(message)
    {
        base.HResult = unchecked((int)0x80131904);
        base.Source = "Core Microsoft SqlClient Data Provider";

        if (errors.Length == 0)
        {
            this.Errors = [new SimulatedSqlError(base.Message, 0, 0, 0)];

            return;
        }

        this.Errors = [.. errors];

        var firstError = errors[0];

        this.Number = firstError.Number;
        this.Class = firstError.Class;
        this.State = firstError.State;

        var data = this.Data;

        data.Add("HelpLink.ProdName", "Microsoft SQL Server");
        data.Add("HelpLink.ProdVer", "99.00.1000");
        data.Add("HelpLink.EvtSrc", "MSSQLServer");
        data.Add("HelpLink.EvtID", firstError.Number.ToString(CultureInfo.InvariantCulture));
        data.Add("HelpLink.BaseHelpUrl", "https://go.microsoft.com/fwlink");
        data.Add("HelpLink.LinkId", "20476");
    }

    /// <inheritdoc/>
    public sealed override int ErrorCode => this.HResult;

    /// <inheritdoc/>
    public sealed override bool IsTransient => false;

    /// <summary>
    /// An error number as described by https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors .
    /// </summary>
    public readonly int Number;

    /// <summary>
    /// A value from 1 to 25 that indicates the severity level of the error. The default is 0.
    /// </summary>
    /// <remarks>
    /// The severity indicates how serious the error is.
    /// Errors that have a low severity, such as 1 or 2, are information messages or low-level warnings.
    /// Errors that have a high severity indicate problems that should be addressed as soon as possible.
    /// </remarks>
    public readonly byte Class;

    /// <summary>
    /// Some error messages can be raised at multiple points in the code for the Database Engine.
    /// For example, an 1105 error can be raised for several different conditions.
    /// Each specific condition that raises an error assigns a unique state code.
    /// </summary>
    public readonly byte State;

    public readonly SimulatedSqlError[] Errors;

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
    /// Mimics SQL Server error 131 in CAST form for
    /// <c>varchar</c> / <c>varbinary</c>. Class 15 / State 3.
    /// </summary>
    internal static SimulatedSqlException SizeExceedsMaximumCast(SqlType type, int requested, int max) =>
        new($"The size ({requested}) given to the type '{type}' exceeds the maximum allowed for any data type ({max}).", 131, 15, 3);

    /// <summary>
    /// Mimics SQL Server error 2717: an <c>nvarchar</c> column exceeds the
    /// 4000-character cap. Distinct error code from the
    /// <c>varchar</c> / <c>varbinary</c> path; uses "parameter" wording even
    /// for column declarations and omits the "for any data type" suffix.
    /// </summary>
    internal static SimulatedSqlException NVarcharSizeExceedsMaximumColumn(string columnName, int requested) =>
        new($"The size ({requested}) given to the parameter '{columnName}' exceeds the maximum allowed (4000).", 2717, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 131 in CAST form for <c>nvarchar</c>. Class 16
    /// / State 1; uses "convert specification" wording.
    /// </summary>
    internal static SimulatedSqlException NVarcharSizeExceedsMaximumCast(int requested) =>
        new($"The size ({requested}) given to the convert specification 'nvarchar' exceeds the maximum allowed for any data type (4000).", 131, 16, 1);

    /// <summary>
    /// Mimics SQL Server's verbose truncation error (Msg 2628): a string value
    /// would not fit within the destination column's declared maximum length.
    /// The displayed "truncated value" is the prefix of the offending value
    /// clipped to the column's max length.
    /// </summary>
    /// <remarks>
    /// Introduced in SQL Server 2019 (compatibility level 150) behind trace
    /// flag 460 or <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON</c>;
    /// became the default in SQL Server 2022+ (compatibility level 160+),
    /// superseding the legacy <see cref="StringOrBinaryWouldBeTruncatedLegacy"/>
    /// (Msg 8152). The simulator selects between the two via
    /// <see cref="Simulation.IsVerboseTruncationActive"/>.
    /// </remarks>
    internal static SimulatedSqlException StringOrBinaryWouldBeTruncated(string tableName, string columnName, string value, int max)
    {
        var prefix = value.Length <= max ? value : value[..max];
        return new($"String or binary data would be truncated in table '{tableName}', column '{columnName}'. Truncated value: '{prefix}'.", 2628, 16, 1);
    }

    /// <summary>
    /// Binary overload of the verbose truncation factory: renders the
    /// truncated prefix as a SQL hex literal (<c>0xABCD…</c>), matching SQL
    /// Server's varbinary formatting in Msg 2628.
    /// </summary>
    internal static SimulatedSqlException StringOrBinaryWouldBeTruncated(string tableName, string columnName, byte[] value, int max)
    {
        var prefix = value.Length <= max ? value : value[..max];
        var hex = $"0x{Convert.ToHexString(prefix)}";
        return new($"String or binary data would be truncated in table '{tableName}', column '{columnName}'. Truncated value: '{hex}'.", 2628, 16, 1);
    }

    /// <summary>
    /// Mimics the legacy SQL Server truncation error (Msg 8152): same trigger
    /// as the verbose factory above but without the table, column, or value
    /// detail. Default behavior on compatibility levels before 160 (SQL Server
    /// 2022) and on older levels with the verbose option off.
    /// </summary>
    internal static SimulatedSqlException StringOrBinaryWouldBeTruncatedLegacy() =>
        new("String or binary data would be truncated.", 8152, 16, 14);

    /// <summary>
    /// Mimics SQL Server error 1701: the schema declares a fixed-length row
    /// that cannot ever fit within the per-row size limit (8060 bytes).
    /// </summary>
    internal static SimulatedSqlException RowSizeExceedsMaximum(string tableName, int requested, int max) =>
        new($"Cannot create the table '{tableName}' because the row size ({requested} bytes) exceeds the maximum allowable table row size ({max} bytes).", 1701, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15048: the integer supplied to
    /// <c>SET COMPATIBILITY_LEVEL</c> is not one of the supported values.
    /// </summary>
    /// <remarks>
    /// The valid-values list and exact phrasing are version-dependent: SQL
    /// Server periodically drops the oldest legacy modes as new releases
    /// add a higher level. The text here was validated against SQL Server
    /// 2025 (compatibility level 170); future releases may need the list
    /// updated to drop earlier values.
    /// </remarks>
    internal static SimulatedSqlException InvalidCompatibilityLevel() =>
        new($"Valid values of the database compatibility level are 100, 110, 120, 130, 140, 150, 160 or 170.", 15048, 16, 1);

    /// <summary>
    /// Mimics the SqlException that occurs then when a TOP/OFFSET/FETCH clause has an inappropriate column reference.
    /// </summary>
    /// <param name="name">The name of the column.</param>
    /// <returns>The exception.</returns>
    internal static SimulatedSqlException ColumnReferenceNotAllowed(IEnumerable<string> name)
        => new($"The reference to column \"{string.Join('.', name)}\" is not allowed in an argument to a TOP, OFFSET, or FETCH clause. Only references to columns at an outer scope or standalone expressions and subqueries are allowed here.", 4115, 15, 1);

    internal static SimulatedSqlException IdentifierTooLong(ReadOnlySpan<char> first128)
        => new($"The identifier that starts with '{first128}' is too long. Maximum length is 128.", 103, 15, 4);

    internal static SimulatedSqlException InvalidColumnName(string name) => new($"Invalid column name '{name}'.", 207, 16, 1);

    internal static SimulatedSqlException InvalidColumnName(IEnumerable<string> name) => InvalidColumnName(string.Join('.', name));

    internal static SimulatedSqlException InvalidObjectName(StringToken name) => new($"Invalid object name {name}.", 208, 16, 1);

    internal static SimulatedSqlException MissingEndCommentMark() => new("Missing end comment mark '*/'.", 113, 15, 1);

    internal static SimulatedSqlException MustDeclareScalarVariable(string name) => new($"Must declare the scalar variable \"@{name}\".", 137, 15, 2);

    internal static SimulatedSqlException SyntaxErrorNearKeyword(ReservedKeyword token) => new($"Incorrect syntax near the keyword '{token}'.", 156, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(ParserContext context) => new($"Incorrect syntax near '{context.Token}'.", 102, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(Token? token) => new($"Incorrect syntax near '{token}'.", 102, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(char c) => new($"Incorrect syntax near '{c}'.", 102, 15, 1);

    internal static SimulatedSqlException ThereIsAlreadyAnObject(string name) => new($"There is already an object named '{name}' in the database.", 2714, 16, 6);

    /// <summary>
    /// Mimics the SqlException that occurs then when a TOP or FETCH clause returns something other than an integer.
    /// </summary>
    /// <returns>The exception.</returns>
    internal static SimulatedSqlException TopFetchRequiresInteger() => new("The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.", 1060, 15, 1);

    internal static SimulatedSqlException UnrecognizedBuiltInFunction(string name) => new($"'{name}' is not a recognized built-in function name.", 195, 15, 10);

    /// <summary>
    /// Mimics SQL Server error 105: a string literal opened with <c>'</c> was
    /// never closed before end of input.
    /// </summary>
    internal static SimulatedSqlException UnclosedStringLiteral() =>
        new("Unclosed quotation mark after the character string.", 105, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 537: a length / count argument to a string
    /// function (LEFT, RIGHT, SUBSTRING) was negative.
    /// </summary>
    internal static SimulatedSqlException NegativeLengthNotAllowed(string function) =>
        new($"Invalid length parameter passed to the {function} function.", 537, 16, 3);

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
    /// Mimics SQL Server error 295: the <c>smalldatetime</c>-specific
    /// counterpart of <see cref="ConversionFailedDateTimeFromString"/>. SQL
    /// Server uses a distinct Msg number and a target-named message text
    /// for this type — verified against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ConversionFailedSmallDateTimeFromString() =>
        new("Conversion failed when converting character string to smalldatetime data type.", 295, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 206: the binary expression's two operands
    /// belong to types that have no implicit conversion between them
    /// (e.g. <c>date = 0</c>, <c>time + 1</c>). Distinct from Msg 402
    /// (time-vs-non-time-date) and Msg 529 (explicit-CAST rejection).
    /// </summary>
    internal static SimulatedSqlException OperandTypeClash(SqlType left, SqlType right) =>
        new($"Operand type clash: {FamilyRootName(left)} is incompatible with {FamilyRootName(right)}", 206, 16, 2);

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
    /// Returns the type name SQL Server uses in Msg 402 for a date/time type:
    /// the family root (e.g. <c>datetime2</c>, <c>datetimeoffset</c>) without
    /// a precision suffix, matching real-server output.
    /// </summary>
    private static string FamilyRootName(SqlType type) => type switch
    {
        DateTime2SqlType => "datetime2",
        TimeSqlType => "time",
        DateTimeOffsetSqlType => "datetimeoffset",
        _ => type.ToString()!,
    };

    /// <summary>
    /// Mimics SQL Server error 145: an <c>ORDER BY</c> item references a
    /// column or expression that isn't in the SELECT list when
    /// <c>SELECT DISTINCT</c> is specified. The post-DISTINCT row stream no
    /// longer carries the source column, so the reference would be
    /// ambiguous; SQL Server rejects at parse time with this fixed text.
    /// </summary>
    internal static SimulatedSqlException OrderByItemNotInSelectListWithDistinct() =>
        new("ORDER BY items must appear in the select list if SELECT DISTINCT is specified.", 145, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 108: a positional <c>ORDER BY</c> ordinal
    /// (e.g. <c>order by 0</c>, <c>order by 5</c> with only 3 columns) is
    /// outside the projection's column count. The validation is 1-based.
    /// </summary>
    internal static SimulatedSqlException OrderByPositionOutOfRange(int position) =>
        new($"The ORDER BY position number {position} is out of range of the number of items in the select list.", 108, 16, 1);

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
}
