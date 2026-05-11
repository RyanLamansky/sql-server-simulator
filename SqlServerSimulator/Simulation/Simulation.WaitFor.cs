using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs <c>WAITFOR DELAY '&lt;time&gt;'</c> or
    /// <c>WAITFOR DELAY @variable</c>. Probed against SQL Server 2025
    /// (2026-05-11): the operand grammar is strict — only a string literal
    /// or a <c>@variable</c> reference; <c>cast(...)</c>, integer literals,
    /// the bare <c>NULL</c> literal, and a <c>time</c>-typed variable are
    /// all rejected by real SQL Server (Msg 102 / Msg 156 / Msg 9815),
    /// and the simulator inherits that rejection by not parsing those
    /// shapes. <c>WAITFOR TIME</c> (the absolute-time form) raises
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Time format: <c>HH:MM:SS[.fff]</c> (or <c>HH:MM</c>) with hours 0-23
    /// and no sign or day component. Bad format → Msg 148. An empty string
    /// or a NULL-valued variable is silently accepted as a zero delay
    /// (probe-confirmed: <c>waitfor delay ''</c> and a NULL-valued varchar
    /// both succeed without sleeping).
    /// </para>
    /// <para>
    /// Sleep mechanism: <see cref="Thread.Sleep(TimeSpan)"/> on the calling
    /// thread, matching real SQL Server's "blocks the connection" semantics.
    /// The simulator's dispatch is synchronous; an <c>ExecuteReaderAsync</c>
    /// caller's <c>CancellationToken</c> isn't threaded into the sleep —
    /// documented gap in CLAUDE.md. <c>@@ROWCOUNT</c> resets to 0
    /// (probe-confirmed; applied by the dispatcher after this parser returns).
    /// Skip-mode (un-taken IF, after BREAK/CONTINUE/RETURN) suppresses the
    /// sleep entirely.
    /// </para>
    /// </remarks>
    private static void ParseWaitForStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume WAITFOR

        // DELAY and TIME are contextual keywords (not in the reserved list),
        // tokenized as UnquotedString. WAITFOR TIME isn't modeled — it's an
        // absolute-time wait whose primary use case is scheduling, which is
        // out of scope for the simulator.
        if (context.Token is not UnquotedString verb)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (verb.Span.Equals("TIME", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("WAITFOR TIME (absolute-time wait) isn't modeled — WAITFOR DELAY is.");
        if (!verb.Span.Equals("DELAY", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired(); // consume DELAY

        // Capture the raw string operand. The grammar is strict: literal or
        // @-prefixed variable reference only. Anything else (cast, integer
        // literal, bare NULL, paren-expr) falls through to Msg 102 / Msg 156
        // from real SQL Server; the simulator routes them all through the
        // existing SyntaxErrorNear path which produces Msg 102.
        string? operandText;
        switch (context.Token)
        {
            case Literal lit when lit.Value.Type is VarcharSqlType or NVarcharSqlType:
                operandText = lit.Value.IsNull ? null : lit.Value.AsString;
                break;

            case AtPrefixedString variableToken:
                {
                    var slot = batch.GetVariableSlot(variableToken.Value);
                    if (slot.DeclaredType is TimeSqlType)
                        throw SimulatedSqlException.WaitForCannotBeTimeType();
                    operandText = slot.Value.IsNull ? null : slot.Value.CoerceTo(SqlType.Varchar).AsString;
                    break;
                }

            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        context.MoveNextOptional(); // consume the operand token

        if (batch.IsSkipping)
            return;

        // Probe-confirmed: NULL via variable, and empty string, both succeed
        // silently with zero delay.
        if (string.IsNullOrEmpty(operandText))
            return;

        if (!TryParseWaitForTime(operandText, out var delay))
            throw SimulatedSqlException.IncorrectWaitForTimeSyntax(operandText);

        if (delay.Ticks > 0)
            Thread.Sleep(delay);
    }

    private static readonly string[] waitForTimeFormats =
    [
        @"hh\:mm\:ss",
        @"hh\:mm\:ss\.f",
        @"hh\:mm\:ss\.ff",
        @"hh\:mm\:ss\.fff",
        @"hh\:mm\:ss\.ffff",
        @"hh\:mm\:ss\.fffff",
        @"hh\:mm\:ss\.ffffff",
        @"hh\:mm\:ss\.fffffff",
        @"hh\:mm",
    ];

    private static bool TryParseWaitForTime(string value, out TimeSpan result) =>
        TimeSpan.TryParseExact(value, waitForTimeFormats, CultureInfo.InvariantCulture, out result)
            && result.Ticks is >= 0 and < TimeSpan.TicksPerDay;
}
