using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>EOMONTH(start_date [, month_to_add])</c>: returns the date of the
/// last day of the month containing <c>start_date</c> (with an optional
/// integer month offset applied first). Result type is always <c>date</c>,
/// regardless of input type — date / datetime / datetime2 / datetimeoffset /
/// smalldatetime / string-literal inputs all surface as date in the output
/// (probe-confirmed against SQL Server 2025, 2026-05-09).
/// </summary>
/// <remarks>
/// Quirk: a NULL <c>month_to_add</c> is silently treated as zero rather than
/// propagating to NULL — `EOMONTH(@d, NULL)` returns the last day of `@d`'s
/// month (probe-confirmed). NULL <c>start_date</c> propagates normally.
/// </remarks>
internal sealed class EOMonth : Expression
{
    private readonly Expression startDate;
    private readonly Expression? monthOffset;

    public EOMonth(ParserContext context)
    {
        this.startDate = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            this.monthOffset = Parse(context.MoveNextRequiredReturnSelf());
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Date;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var input = this.startDate.Run(runtime);
        if (input.IsNull)
            return SqlValue.Null(SqlType.Date);

        // Coerce the input to date — handles date / datetime / datetime2 /
        // datetimeoffset / smalldatetime / string-literal inputs through the
        // existing CAST machinery.
        var asDate = input.CoerceTo(SqlType.Date).AsDate;

        var offset = 0;
        if (this.monthOffset is { } offsetExpr)
        {
            var offsetValue = offsetExpr.Run(runtime);
            // Probe-confirmed quirk: NULL month offset is silently treated as
            // zero (no shift), unlike start_date's NULL which propagates.
            if (!offsetValue.IsNull)
                offset = offsetValue.CoerceTo(SqlType.Int32).AsInt32;
        }

        var shifted = asDate.AddMonths(offset);
        var lastDay = DateTime.DaysInMonth(shifted.Year, shifted.Month);
        return SqlValue.FromDate(new DateOnly(shifted.Year, shifted.Month, lastDay));
    }

    internal override string DebugDisplay() =>
        this.monthOffset is null
            ? $"EOMONTH({this.startDate.DebugDisplay()})"
            : $"EOMONTH({this.startDate.DebugDisplay()}, {this.monthOffset.DebugDisplay()})";
}
