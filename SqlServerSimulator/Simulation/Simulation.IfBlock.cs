using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs an <c>IF &lt;boolean&gt; &lt;stmt&gt; [ELSE &lt;stmt&gt;]</c>
    /// statement. The condition is a Boolean predicate (<see cref="BooleanExpression"/>);
    /// value-typed expressions in this slot raise Msg 4145 from <see cref="BooleanExpression"/>'s
    /// "atom without comparison op" path. Three-valued result: only an explicit
    /// <c>true</c> takes the THEN branch — both <c>false</c> and UNKNOWN
    /// (e.g. <c>IF 1 = NULL …</c>) fall through to ELSE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Body grammar: exactly one statement, optionally wrapped in
    /// <c>BEGIN…END</c> for a compound block. <c>IF 1=1 SELECT 'a' SELECT 'b'</c>
    /// runs both SELECTs (only the first is the IF body; the second is a
    /// subsequent batch-level statement) — probe-confirmed footgun.
    /// Dangling-else binds to the nearest unmatched IF (standard rule, probed
    /// across all three truth-table cases).
    /// </para>
    /// <para>
    /// Un-taken branches dispatch with <see cref="BatchContext.IsSkipping"/>
    /// set to true: every statement parser runs (advancing the cursor and
    /// resolving names) but skips its state mutations. The dispatch loop
    /// suppresses <c>yield return</c> and <c>@@ROWCOUNT</c> updates while
    /// the flag is true. When neither branch executes (cond false, no ELSE,
    /// outer scope not already skipping), <c>@@ROWCOUNT</c> resets to 0 —
    /// probe-confirmed.
    /// </para>
    /// <para>
    /// On entry the cursor is on the <c>IF</c> keyword. On return the cursor
    /// sits on the first token after the IF statement — typically <c>;</c>,
    /// the next statement keyword, an <c>END</c> closing an enclosing block,
    /// or end of batch.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseIfStatement(BatchContext batch)
    {
        var context = batch.Parser;
        var connection = context.Connection;

        context.MoveNextRequired(); // consume IF
        var cond = BooleanExpression.Parse(context);

        var wasSkipping = batch.IsSkipping;
        var thenSkip = wasSkipping
            || cond.Run(new RuntimeContext(NoColumnResolver, batch)) != true;

        var hadElse = false;
        try
        {
            batch.IsSkipping = thenSkip;
            foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false))
                yield return o;

            hadElse = context.Token is ReservedKeyword { Keyword: Keyword.Else };
            if (hadElse)
            {
                context.MoveNextRequired(); // consume ELSE
                batch.IsSkipping = wasSkipping || !thenSkip;
                foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false))
                    yield return o;
            }
        }
        finally
        {
            batch.IsSkipping = wasSkipping;
        }

        // Probe-confirmed: an IF whose cond was false and which has no ELSE
        // resets @@ROWCOUNT to 0 (the IF "completes" without dispatching
        // anything that would update the counter). When a branch ran, that
        // branch's last statement already set @@ROWCOUNT.
        if (!wasSkipping && thenSkip && !hadElse)
            connection.LastStatementRowCount = 0;
    }

    /// <summary>
    /// Parses and runs a <c>BEGIN … END</c> compound-statement block. Dispatches
    /// each contained statement through <see cref="DispatchStatementsUntil"/>
    /// until the matching <c>END</c>. Empty blocks (<c>BEGIN END</c> or
    /// <c>BEGIN ; END</c> with nothing but separators inside) raise Msg 102
    /// near <c>'end'</c> — probe-confirmed against SQL Server 2025.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Variable scope is batch-wide, not block-local — a <c>DECLARE</c> inside
    /// a block remains visible after the block ends. Probe-confirmed against
    /// SQL Server 2025 (2026-05-11) and matches the existing batch-scope
    /// model on <see cref="BatchContext.Variables"/>: blocks don't introduce
    /// a new scope.
    /// </para>
    /// <para>
    /// On entry the cursor is on the <c>BEGIN</c> keyword. On return the
    /// cursor sits on the first token after <c>END</c>. The caller has
    /// already disambiguated this as a block (vs <c>BEGIN TRAN</c> /
    /// <c>BEGIN TRY</c> / <c>BEGIN ATOMIC</c>) by peeking the token after
    /// <c>BEGIN</c>.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseBeginBlock(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume BEGIN

        // Drain leading separators. A block containing only `;`s (or none at
        // all) lands on END here and is rejected — real SQL Server enforces
        // a non-empty body.
        while (context.Token is Operator { Character: ';' })
            context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        foreach (var o in DispatchStatementsUntil(batch, endKeyword: Keyword.End))
            yield return o;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional(); // consume END
    }
}
