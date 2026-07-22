using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

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
        batch.BlockDepth++;
        try
        {
            batch.SkipModeFlag = thenSkip;
            foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false, atBatchStart: false))
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
                foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false, atBatchStart: false))
                    yield return o;
            }
        }
        finally
        {
            batch.SkipModeFlag = wasSkipModeFlag;
            batch.BlockDepth--;
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
        batch.BlockDepth++;
        try
        {
            if (outerSkipping)
            {
                // WHILE itself in skip mode — never iterate. Skip-dispatch the
                // body once to advance the cursor.
                batch.SkipModeFlag = true;
                foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false, atBatchStart: false))
                    yield return o;
            }
            else
            {
                while (true)
                {
                    // A cancelled command (TDS attention / CommandTimeout /
                    // in-process Cancel()) breaks the loop at the iteration
                    // boundary — the classic cancel target of an otherwise
                    // unbounded WHILE. Body-internal statements observe the
                    // same signal through DispatchStatementsUntil.
                    if (batch.Connection.ExecutionCancellationToken.IsCancellationRequested)
                        goto ExitLoop;

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
                            foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false, atBatchStart: false))
                                yield return o;
                        }
                        finally
                        {
                            batch.SkipModeFlag = wasSkipModeFlag;
                        }
                        break;
                    }

                    foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false, atBatchStart: false))
                        yield return o;

                    // RETURN propagates through WHILE (unlike BREAK / CONTINUE
                    // which we catch). Exit the loop without clearing — the
                    // outer DispatchStatementsUntil also stops on ReturnSignaled.
                    if (batch.ReturnSignaled)
                        goto ExitLoop;

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
            batch.BlockDepth--;
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
    /// Dispatches a <c>RETURN</c> statement. In batch context (the only
    /// context the simulator currently supports — stored procs and scalar
    /// functions still aren't modeled), only the bare form is legal: a
    /// value-form <c>RETURN &lt;expr&gt;</c> raises <c>Msg 178</c> at parse
    /// time regardless of skip mode (compile-time check, same pattern as
    /// <c>BREAK</c>'s Msg 135 from an un-taken IF). Bare RETURN sets
    /// <see cref="BatchContext.ReturnSignaled"/>; <see cref="BatchContext.IsSkipping"/>
    /// then OR's the flag into the skip predicate so any remaining statements
    /// in the same block no-op, and the dispatch loop's early-exit check
    /// (in <c>DispatchStatementsUntil</c>) terminates the batch as soon as
    /// the next statement boundary is reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RETURN propagates through every enclosing construct — IF, BEGIN…END,
    /// nested WHILE — exiting the batch entirely (unlike BREAK / CONTINUE
    /// which the innermost WHILE catches). WHILE checks the flag after each
    /// body dispatch and exits its iteration loop; BEGIN…END's
    /// <c>ParseBeginBlock</c> short-circuits the "expect END" check when
    /// the flag is set (the cursor may not have reached END if RETURN fired
    /// mid-block).
    /// </para>
    /// <para>
    /// The expression-presence check uses <see cref="IsStatementBoundary"/>
    /// to decide bare vs value-form: any non-boundary token following RETURN
    /// (operators, variables, literals, parens, non-statement-start keywords)
    /// triggers Msg 178. Statement-boundary tokens (<c>;</c>, EOB, statement-
    /// starting keywords) leave RETURN bare.
    /// </para>
    /// </remarks>
    private static void ParseReturnStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextOptional(); // consume RETURN

        // Value form is legal inside a scalar UDF body (UdfFrame non-null)
        // or a stored procedure body (ProcFrame non-null). Outside either,
        // it raises Msg 178 at parse time, even from un-taken IF branches
        // (compile-time check, same pattern as BREAK's Msg 135).
        if (!IsStatementBoundary(context.Token))
        {
            if (batch.UdfFrame is null && batch.ProcFrame is null)
                throw SimulatedSqlException.ReturnWithValueNotAllowed();

            var valueExpr = Expression.Parse(context);
            if (!batch.IsSkipping)
            {
                var raw = valueExpr.Run(new RuntimeContext(
                    name => throw SimulatedSqlException.MustDeclareScalarVariable(name.Leaf),
                    batch));
                if (batch.UdfFrame is { } udfFrame)
                {
                    udfFrame.ReturnedValue = raw.CoerceTo(udfFrame.ReturnType);
                }
                else
                {
                    // Procedure RETURN: coerce to int with NULL → 0 (probe-
                    // confirmed against SQL Server 2025: `RETURN NULL` lands
                    // 0 in the caller's @rc, not NULL). Msg 245 surfaces here
                    // for non-coercible types like `RETURN 'abc'`.
                    var coerced = raw.CoerceTo(SqlType.Int32);
                    batch.ProcFrame!.ReturnCode = coerced.IsNull ? SqlValue.FromInt32(0) : coerced;
                }
                batch.ReturnSignaled = true;
            }
            return;
        }

        if (!batch.IsSkipping)
            batch.ReturnSignaled = true;
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

        // RETURN inside the block exits early without reaching END — abandon
        // the block; outer DispatchStatementsUntil also stops on ReturnSignaled.
        // A batch-aborting error (e.g. a Msg 207 on a resolvable table inside a
        // skipped block) likewise leaves the cursor mid-statement with no
        // recovery scan, so short-circuit the "expect END" check to let the one
        // error surface instead of a spurious Msg 102 near the abandoned token.
        if (batch.ReturnSignaled || batch.BatchAborted)
            yield break;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional(); // consume END
    }

    /// <summary>
    /// Parses a <c>BEGIN ATOMIC [WITH (option [, option ...])] body END</c>
    /// block — the body shape natively-compiled procedures use. Cursor on
    /// entry: the <c>BEGIN</c> keyword. Cursor on exit: the first token
    /// after <c>END</c>.
    /// </summary>
    /// <remarks>
    /// The WITH options (TRANSACTION ISOLATION LEVEL, LANGUAGE, DATEFORMAT,
    /// DATEFIRST, DELAYED_DURABILITY) are parse-and-discard. The simulator
    /// doesn't enforce per-block isolation overrides or language-specific
    /// date parsing inside the block, and DELAYED_DURABILITY has no
    /// performance meaning in an in-process emulator. The body dispatches
    /// statements like a regular BEGIN…END block — the atomic-transaction
    /// boundary that real SQL Server enforces (the block is its own
    /// transaction) is approximated by the simulator's implicit
    /// per-statement undo plus any outer explicit transaction; explicit
    /// COMMIT / ROLLBACK inside the body would surprise a caller but isn't
    /// rejected.
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseBeginAtomicBlock(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume BEGIN
        context.MoveNextRequired(); // consume ATOMIC

        // Optional WITH (...) options block. Real SQL Server requires this
        // for natively-compiled procs but the grammar allows omission for
        // future ATOMIC use cases. Skip token-by-token with paren balancing —
        // the options have no semantic effect in the simulator, so loose
        // consumption avoids per-option dispatch.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var depth = 1;
            context.MoveNextRequired();
            while (depth > 0)
            {
                if (context.Token is null)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.Token is Operator op)
                {
                    if (op.Character == '(')
                        depth++;
                    else if (op.Character == ')')
                        depth--;
                }
                context.MoveNextRequired();
            }
        }

        // Body dispatch mirrors ParseBeginBlock — leading separators drained,
        // empty body rejected, statements dispatched until END.
        while (context.Token is Operator { Character: ';' })
            context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        foreach (var o in DispatchStatementsUntil(batch, endKeyword: Keyword.End))
            yield return o;

        if (batch.ReturnSignaled || batch.BatchAborted)
            yield break;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional(); // consume END
    }
}
