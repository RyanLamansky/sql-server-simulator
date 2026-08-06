using System.Globalization;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DATENAME(&lt;datepart&gt;, &lt;date-expr&gt;)</c>: returns the
/// localized string form of the date/time part identified by the bare
/// <c>datepart</c> keyword. Result is always <see cref="SqlType.NVarchar"/>.
/// Two parts surface as month/weekday names rather than digit strings —
/// <c>month</c> → <c>January</c>...<c>December</c>; <c>weekday</c> →
/// <c>Sunday</c>...<c>Saturday</c> (en-US, matching SQL Server's default
/// language). Every other part is the same integer
/// <see cref="DatePart.Run"/> would compute, formatted via
/// <see cref="CultureInfo.InvariantCulture"/>. The projected result type is
/// a fixed <c>nvarchar(30)</c> — probe-confirmed against SQL Server 2025
/// (2026-07-22): <c>DATENAME(month, …)</c> describes as <c>nvarchar(30)</c>
/// regardless of the part.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-22):
/// <c>DATENAME(month, '2024-01-15')</c> → <c>'January'</c>;
/// <c>DATENAME(weekday, '2024-01-15')</c> → <c>'Monday'</c>;
/// <c>DATENAME(year, '2024-01-15')</c> → <c>'2024'</c>;
/// <c>DATENAME(part, NULL)</c> → NULL.
/// </remarks>
internal sealed class DateName : Expression
{
    private static readonly NVarcharSqlType ResultType = NVarcharSqlType.Get(30, Collation.Baseline, Coercibility.CoercibleDefault);

    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static readonly string[] DayOfWeekNames =
    [
        "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
    ];

    private readonly DatePartKind kind;
    private readonly string keywordText;
    private readonly Expression source;

    public DateName(ParserContext context)
    {
        this.keywordText = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        this.kind = DatePartKinds.ResolveOrThrow(this.keywordText, "datename");
        if (context.GetNextRequired() is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.source = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = DatePartKinds.CoerceDateArgumentImplicit(this.source.Run(runtime));
        if (value.IsNull)
            return SqlValue.Null(ResultType);
        DatePartKinds.RequireCompatible(this.kind, this.keywordText, value.Type, "datename");
        return this.kind switch
        {
            DatePartKind.Month => SqlValue.FromNVarchar(ResultType, MonthNames[DatePartKinds.Extract(this.kind, value) - 1]),
            // The weekday NAME is the calendar day's own, so it ignores
            // DATEFIRST — probe-confirmed that DATENAME(dw, <a Friday>) reads
            // 'Friday' under DATEFIRST 5, where DATEPART(dw, …) reads 1. Every
            // other unit reads the number DATEPART would, DATEFIRST included
            // (week moves with it).
            DatePartKind.Weekday => SqlValue.FromNVarchar(ResultType, DayOfWeekNames[DatePartKinds.Extract(this.kind, value) - 1]),
            _ => SqlValue.FromNVarchar(ResultType, DatePartKinds.Extract(this.kind, value, runtime.Batch.Connection.DateFirst).ToString(CultureInfo.InvariantCulture)),
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => $"DATENAME({this.keywordText}, {this.source.DebugDisplay()})";
}
