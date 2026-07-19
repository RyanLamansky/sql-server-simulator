using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ISDATE(expression)</c>: returns <c>int</c> <c>1</c> when the
/// argument is a recognized legacy <c>datetime</c> / <c>smalldatetime</c>
/// value (string parseable as one, integer in the valid days-since-1900
/// range, or already a <c>datetime</c>/<c>smalldatetime</c>); <c>0</c>
/// otherwise. Modern date / time / datetimeoffset types raise <strong>Msg
/// 8116</strong> rather than coercing (probe-confirmed).
/// </summary>
/// <remarks>
/// Probe-confirmed range gate: parsed value's year must be in <c>[1753,
/// 9999]</c>. Integer input is implicitly converted to its decimal-string
/// form, then parsed (so <c>ISDATE(20260512)</c> = 1 via <c>'20260512'</c>
/// matching <c>yyyyMMdd</c>; <c>ISDATE(1)</c> = 0 because <c>'1'</c> parses
/// to year 1, below the 1753 floor). Float / decimal inputs always return
/// <c>0</c> (no implicit string conversion path).
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/isdate-transact-sql</remarks>
internal sealed class IsDate(ParserContext context) : Expression
{
    private readonly Expression operand = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.operand.Run(runtime);
        if (v.IsNull)
            return SqlValue.FromInt32(0);

        // Modern (post-2008) date/time/datetimeoffset surface Msg 8116 — SQL
        // Server's ISDATE intentionally rejects these to avoid suggesting
        // that they live in the same value space as the legacy datetime.
        if (v.Type == SqlType.Date || v.Type is TimeSqlType or DateTimeOffsetSqlType)
            throw SimulatedSqlException.InvalidArgumentDataType(v.Type.SqlServerName, argumentIndex: 1, "isdate");

        // Already a legacy datetime / smalldatetime: trivially in range.
        if (v.Type == SqlType.DateTime || v.Type == SqlType.SmallDateTime)
            return SqlValue.FromInt32(1);

        if (SqlType.IsStringCategory(v.Type))
            return SqlValue.FromInt32(TryParseLegacyDateTimeInRange(v.AsString) ? 1 : 0);

        // Integer category gets the implicit string-coercion route.
        if (v.Type.Category == SqlTypeCategory.Integer && v.Type != SqlType.Bit)
        {
            var asString = v.CoerceTo(SqlType.BigInt).AsInt64.ToString(CultureInfo.InvariantCulture);
            return SqlValue.FromInt32(TryParseLegacyDateTimeInRange(asString) ? 1 : 0);
        }

        return SqlValue.FromInt32(0);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    /// <summary>
    /// Bridges to <see cref="SqlValue.TryParseLegacyDateTime"/> with the
    /// legacy-datetime year-range gate applied. The shared parser accepts
    /// year ranges broader than legacy datetime (e.g. <c>datetime2</c> down
    /// to year 1); ISDATE specifically rejects pre-1753 values to match the
    /// legacy <c>datetime</c> domain. Empty string short-circuits to false:
    /// the shared parser treats <c>""</c> as datetime base-date for CAST
    /// support, but ISDATE specifically rejects it (probe-confirmed).
    /// </summary>
    internal static bool TryParseLegacyDateTimeInRange(string value) =>
        !string.IsNullOrEmpty(value)
        && SqlValue.TryParseLegacyDateTime(value, out var dt)
        && dt.Year is >= 1753 and <= 9999;

    internal override string DebugDisplay() => $"ISDATE({this.operand.DebugDisplay()})";
}
