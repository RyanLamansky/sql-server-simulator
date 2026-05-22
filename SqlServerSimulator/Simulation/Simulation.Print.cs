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
    /// probe-confirmed batch-coalescing semantic). Probed against SQL Server
    /// 2025 (2026-05-11): NULL operand emits a single-space message; long
    /// strings truncate at 8000 / 4000 chars depending on collation (not
    /// modeled — simulator delivers whatever the expression evaluates to).
    /// </summary>
    /// <remarks>
    /// Type validity follows from normal expression evaluation: <c>PRINT 'val=' + 5</c>
    /// raises Msg 245 from the <c>+</c> operator (matches probe). One known
    /// fidelity gap: real SQL Server raises Msg 1046 ("Subqueries are not
    /// allowed in this context") when a scalar subquery appears in the PRINT
    /// operand; the simulator silently evaluates it. The non-string-value
    /// formatting routes through <see cref="SqlValue.CoerceTo"/> to varchar,
    /// which differs from SQL Server's PRINT-specific style 0 conventions
    /// for datetime / money (acceptable divergence — string-based PRINT is
    /// the dominant pattern; non-string emitters are typically wrapped in
    /// CAST or CONVERT at the call site).
    /// </remarks>
    private static void ParsePrintStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume PRINT
        var expression = Expression.Parse(context);
        if (batch.IsSkipping)
            return;
        var value = expression.Run(new RuntimeContext(NoColumnResolver, batch));
        batch.AppendPrintMessage(FormatPrintValue(value));
    }

    /// <summary>
    /// Renders a <see cref="SqlValue"/> for <c>PRINT</c> delivery. NULL → a
    /// single-space string (probe-confirmed surprise); string-typed values
    /// pass through verbatim (preserving any embedded CR / LF); other types
    /// coerce to <c>varchar(8000)</c> for display. The 8000-byte cap matches
    /// SQL Server's PRINT-output truncation point for ANSI strings, though
    /// the simulator doesn't enforce the cap at the expression level —
    /// upstream <c>+</c> concatenation may produce longer strings which
    /// pass through here unchanged.
    /// </summary>
    private static string FormatPrintValue(SqlValue value) =>
        value.IsNull ? " "
        : SqlType.IsStringCategory(value.Type) ? value.AsString
        : value.CoerceTo(VarcharSqlType.Get(8000, Collation.Default, Coercibility.CoercibleDefault)).AsString;
}
