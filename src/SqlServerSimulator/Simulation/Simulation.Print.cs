using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs a <c>PRINT &lt;expression&gt;</c> statement. The
    /// expression is parsed unconditionally (advancing the cursor) and
    /// evaluated only when not in skip mode so an un-taken <c>IF</c> branch
    /// doesn't surface runtime errors from the value computation. The
    /// formatted value buffers into the batch's pending-PRINT list, which
    /// fires as a single coalesced <see cref="SimulatedDbConnection.InfoMessage"/>
    /// event at end of dispatch (internal-only API — mirrors SqlClient's
    /// probe-confirmed batch-coalescing semantic).
    /// </summary>
    /// <remarks>
    /// The operand admits only scalar expressions and has no column scope, so
    /// the parse arms <see cref="ParserContext.ScalarOnlyOperand"/>: a name is
    /// Msg 128 and a subquery Msg 1046, whichever the left-to-right reading
    /// meets first, both settled while parsing so an un-taken <c>IF</c> branch
    /// and a module body at CREATE raise where real raises. Type validity
    /// otherwise follows from normal expression evaluation:
    /// <c>PRINT 'val=' + 5</c> raises Msg 245 from the <c>+</c> operator.
    /// </remarks>
    private static void ParsePrintStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume PRINT
        // PRINT is one of the statements real declines to define a sequence
        // draw in at all, and it says so with its own catch-all message
        // (Msg 11738, probe-confirmed) rather than any of the query-shaped
        // ones — though a CASE wrapped around the reference still reports
        // Msg 11741, which is what makes this a floor rather than a set.
        var savedRejection = context.EnterNextValueForScope(NextValueForScope.Unsupported);
        var savedScalarOnly = context.ScalarOnlyOperand;
        var savedScalarOnlyReference = context.ScalarOnlyColumnReference;
        context.ScalarOnlyOperand = true;
        context.ScalarOnlyColumnReference = null;
        Expression expression;
        try
        {
            expression = Expression.Parse(context);
            if (context.ScalarOnlyColumnReference is not null)
                throw Expression.ScalarOnlyOperandError(context);
        }
        finally
        {
            context.NextValueForRejection = savedRejection;
            context.ScalarOnlyOperand = savedScalarOnly;
            context.ScalarOnlyColumnReference = savedScalarOnlyReference;
        }

        if (batch.IsSkipping)
            return;
        var value = expression.Run(new RuntimeContext(NoColumnResolver, batch));
        batch.AppendPrintMessage(FormatPrintValue(value));
    }

    /// <summary>
    /// Renders a <see cref="SqlValue"/> for <c>PRINT</c> delivery, which is the
    /// implicit conversion to a character string real applies — the datetime
    /// family's own default layouts (<c>datetime</c> / <c>smalldatetime</c> at
    /// style 0's <c>Aug  6 2026  1:45PM</c>, the modern types at their ISO
    /// forms), <c>money</c> at two decimals, <c>float</c> / <c>real</c> at style
    /// 0's six significant digits with a three-digit exponent, and the binary
    /// family as <c>0x</c>-prefixed hex. NULL renders as a single space, which
    /// is also what an empty string renders as.
    /// </summary>
    /// <remarks>
    /// The message truncates at <b>8000</b> characters, or <b>4000</b> when the
    /// operand is one of the national string types — probe-confirmed against
    /// SQL Server 2025, which delivers exactly that many characters and drops
    /// the rest without a warning. A non-string operand's rendering is bounded
    /// by its own type well below either cap.
    /// </remarks>
    private static string FormatPrintValue(SqlValue value)
    {
        if (value.IsNull)
            return " ";
        var type = value.Type;
        if (SqlType.IsStringCategory(type))
            return TruncateForPrint(value.AsString, IsNationalString(type) ? 4000 : 8000);
        var target = VarcharSqlType.Get(8000, Collation.Baseline, Coercibility.CoercibleDefault);
        var rendered = type switch
        {
            // Style 0 is the implicit rendering only for the two legacy
            // datetime types; the modern date / time / datetime2 /
            // datetimeoffset family converts to its own ISO layout instead
            // (probe-confirmed: `PRINT CAST('2026-08-06' AS date)` is
            // `2026-08-06`, not style 0's `Aug  6 2026`).
            DateTimeSqlType or SmallDateTimeSqlType => value.CoerceDateTimeToStringWithStyle(target, 0),
            _ when SqlType.IsMoneyCategory(type) => value.CoerceMoneyToStringWithStyle(target, 0),
            _ when SqlType.IsApproximateNumericCategory(type) => value.CoerceFloatToStringWithStyle(target, 0),
            VarbinarySqlType or BinarySqlType or ImageSqlType => value.CoerceBinaryToStringWithStyle(target, 1),
            _ => value.CoerceTo(target),
        };
        return TruncateForPrint(rendered.AsString, 8000);
    }

    private static bool IsNationalString(SqlType type) =>
        type is NVarcharSqlType or NCharSqlType or NTextSqlType;

    private static string TruncateForPrint(string text, int limit) =>
        text.Length == 0 ? " "
        : text.Length <= limit ? text
        : text[..limit];
}
