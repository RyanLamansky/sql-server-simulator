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

        // Capture both pieces of state we need to restore independently:
        // the raw IF-skip flag (restored in the finally) and the combined
        // initial skip state (drives the cond-skip and rowcount-reset
        // decisions). LoopControl propagates through unchanged.
        var wasSkipModeFlag = batch.SkipModeFlag;
        var outerSkipping = batch.IsSkipping;
        var condResult = !outerSkipping
            && cond.Run(new RuntimeContext(NoColumnResolver, batch)) == true;
        var thenSkip = !condResult;

        var hadElse = false;
        try
        {
            batch.SkipModeFlag = thenSkip;
            foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false))
                yield return o;

            hadElse = context.Token is ReservedKeyword { Keyword: Keyword.Else };
            if (hadElse)
            {
                context.MoveNextRequired(); // consume ELSE
                // ELSE skips iff: outer was IF-skipping initially, cond was
                // true (THEN ran), or THEN's body set a LoopControl signal.
                // Don't read batch.IsSkipping here — the SkipModeFlag we set
                // above for the THEN dispatch is sticky and would conflate
                // with the outer-initial state.
                batch.SkipModeFlag = wasSkipModeFlag
                    || condResult
                    || batch.LoopControl != LoopControl.None;
                foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false))
                    yield return o;
            }
        }
        finally
        {
            batch.SkipModeFlag = wasSkipModeFlag;
        }

        // Probe-confirmed: an IF whose cond was false and which has no ELSE
        // resets @@ROWCOUNT to 0 (the IF "completes" without dispatching
        // anything that would update the counter). When a branch ran, that
        // branch's last statement already set @@ROWCOUNT.
        if (!outerSkipping && thenSkip && !hadElse)
            connection.LastStatementRowCount = 0;
    }

    /// <summary>
    /// Parses and runs a <c>WHILE &lt;boolean&gt; &lt;stmt&gt;</c> loop. The
    /// body is exactly one statement (or a <c>BEGIN…END</c> block); the same
    /// one-statement footgun as IF applies — <c>WHILE cond stmt1 stmt2</c>
    /// runs <c>stmt2</c> once after the loop. Cond must be a Boolean predicate
    /// (Msg 4145 via the shared <see cref="BooleanExpression"/> path).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-iteration: re-evaluate cond; if true, dispatch the body once.
    /// <c>BREAK</c> / <c>CONTINUE</c> inside the body signal via
    /// <see cref="BatchContext.LoopControl"/> — flag-based rather than
    /// exception-based, so the iterator-based dispatch composes cleanly.
    /// WHILE consumes and clears the flag; nested WHILE only sees its own
    /// signals because each WHILE clears before returning to its caller.
    /// </para>
    /// <para>
    /// Cursor: <see cref="ParserContext.SaveCheckpoint"/> at the body's first
    /// token; <see cref="ParserContext.RestoreCheckpoint"/> before each
    /// iteration so the body re-parses from scratch (variable references hold
    /// live slot references, so mutations between iterations are visible).
    /// On exit the cursor must sit past the body; for the "cond initially
    /// false" and "WHILE-in-skip-mode" cases the simulator skip-dispatches
    /// the body once with <see cref="BatchContext.SkipModeFlag"/> set to
    /// advance the cursor without effects. For BREAK-exit and natural-cond-
    /// false exit, the body's last dispatch already left the cursor past
    /// the body.
    /// </para>
    /// <para>
    /// <c>@@ROWCOUNT</c> resets to 0 at every exit path — probe-confirmed
    /// against SQL Server 2025 (2026-05-11) across no-iter, multi-iter with
    /// body SELECT, and BREAK-exit scenarios.
    /// </para>
    /// <para>
    /// Iteration cap: <see cref="BatchContext.LoopIterationLimit"/> caps the
    /// total iterations across the batch (not per-loop) so a buggy test
    /// doesn't hang CI. Real SQL Server has no such cap (server timeouts
    /// handle runaway loops); the simulator surfaces an explicit error
    /// instead. Document this in CLAUDE.md as a simulator-only limit.
    /// </para>
    /// <para>
    /// <see cref="BatchContext.LoopDepth"/> is bumped unconditionally (even
    /// when this WHILE is itself in skip mode) so <c>BREAK</c> / <c>CONTINUE</c>
    /// inside the body — including inside un-taken IF branches — never see
    /// the parse-time loop-scope check incorrectly fire Msg 135 / 136.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseWhileStatement(BatchContext batch)
    {
        var context = batch.Parser;
        var connection = context.Connection;

        context.MoveNextRequired(); // consume WHILE
        var cond = BooleanExpression.Parse(context);

        var bodyStart = context.SaveCheckpoint();
        var wasSkipModeFlag = batch.SkipModeFlag;
        var outerSkipping = batch.IsSkipping;

        batch.LoopDepth++;
        try
        {
            if (outerSkipping)
            {
                // WHILE itself in skip mode — never iterate. Skip-dispatch the
                // body once to advance the cursor.
                batch.SkipModeFlag = true;
                foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false))
                    yield return o;
            }
            else
            {
                while (true)
                {
                    if (++batch.LoopIterations > BatchContext.LoopIterationLimit)
                    {
                        throw new InvalidOperationException(
                            $"WHILE iteration cap exceeded ({BatchContext.LoopIterationLimit} iterations). "
                            + "Real SQL Server has no such cap; the simulator enforces one so a buggy test doesn't hang. "
                            + "If this is a legitimate use case, restructure the loop or bump LoopIterationLimit.");
                    }

                    context.RestoreCheckpoint(bodyStart);
                    var condResult = cond.Run(new RuntimeContext(NoColumnResolver, batch)) == true;

                    if (!condResult)
                    {
                        // Final pass: advance cursor past body in skip mode.
                        batch.SkipModeFlag = true;
                        try
                        {
                            foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false))
                                yield return o;
                        }
                        finally
                        {
                            batch.SkipModeFlag = wasSkipModeFlag;
                        }
                        break;
                    }

                    foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false))
                        yield return o;

                    switch (batch.LoopControl)
                    {
                        case LoopControl.Break:
                            batch.LoopControl = LoopControl.None;
                            goto ExitLoop;
                        case LoopControl.Continue:
                            batch.LoopControl = LoopControl.None;
                            continue;
                    }
                }
            ExitLoop:;
            }
        }
        finally
        {
            batch.SkipModeFlag = wasSkipModeFlag;
            batch.LoopDepth--;
        }

        // Probe-confirmed: every WHILE exit path (cond initially false,
        // cond becomes false mid-loop, BREAK) resets @@ROWCOUNT to 0,
        // regardless of what the body's last statement produced. Skip-mode
        // WHILEs leave @@ROWCOUNT untouched (the surrounding scope owns it).
        if (!outerSkipping)
            connection.LastStatementRowCount = 0;
    }

    /// <summary>
    /// Dispatches a <c>BREAK</c> statement: validates loop scope at parse
    /// time (Msg 135 fires unconditionally when <see cref="BatchContext.LoopDepth"/>
    /// is zero, matching real SQL Server's compile-time check — fires even
    /// inside un-taken IF branches), then sets
    /// <see cref="BatchContext.LoopControl"/> to <see cref="LoopControl.Break"/>
    /// when not in skip mode. The dispatch loop's <c>IsSkipping</c> property
    /// picks up the flag so subsequent statements in the same block naturally
    /// no-op; the innermost WHILE consumes the flag.
    /// </summary>
    private static void ParseBreakStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextOptional(); // consume BREAK
        if (batch.LoopDepth == 0)
            throw SimulatedSqlException.BreakOutsideLoop();
        if (!batch.IsSkipping)
            batch.LoopControl = LoopControl.Break;
    }

    /// <summary>
    /// Dispatches a <c>CONTINUE</c> statement: same compile-time loop-scope
    /// check as <c>BREAK</c> (Msg 136 instead of 135), and same skip-mode
    /// gate on the flag write.
    /// </summary>
    private static void ParseContinueStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextOptional(); // consume CONTINUE
        if (batch.LoopDepth == 0)
            throw SimulatedSqlException.ContinueOutsideLoop();
        if (!batch.IsSkipping)
            batch.LoopControl = LoopControl.Continue;
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
