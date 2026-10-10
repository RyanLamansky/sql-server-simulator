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

        var ifStart = batch.CurrentStatement.StartIndex;
        context.MoveNextRequired(); // consume IF
        var cond = ParseCondition(batch);
        var conditionEnd = context.PreviousTokenEnd;

        var bindError = BindCondition(cond, batch);

        // Capture both pieces of state we need to restore independently:
        // the raw IF-skip flag (restored in the finally) and the combined
        // initial skip state (drives the cond-skip and rowcount-reset
        // decisions). LoopControl propagates through unchanged. A condition
        // that failed to bind runs neither branch.
        var wasSkipModeFlag = batch.SkipModeFlag;
        var outerSkipping = batch.IsSkipping || bindError is not null;
        SimulatedSqlException? conditionError = null;
        StatementClock? conditionClock = connection.StatisticsTime ? StatementClock.Start(connection) : null;
        IoStatistics? conditionIo = null;
        var conditionCapture = outerSkipping ? null : BeginConditionQueryStoreCapture(batch, out conditionIo);
        var condResult = !outerSkipping && RunCondition(cond, batch, out conditionError);
        if (conditionCapture is { } capture)
            EndConditionQueryStoreCapture(batch, capture, conditionIo, ifStart, conditionEnd, conditionError);
        batch.QueueNullEliminatedWarning();
        if (!outerSkipping && conditionError is null)
            QueueConditionStatistics(batch, conditionClock, batch.CurrentStatement.StartLine);
        // The condition is a statement of its own for @@ERROR: the branch it
        // chose reads 0 (probed 2026-09-24 against SQL Server 2025).
        if (conditionError is not null)
        {
            foreach (var o in ConditionErrorOutcomes(batch, conditionError))
                yield return o;
        }
        else if (!outerSkipping)
        {
            connection.LastErrorNumber = 0;
            // So is it for @@ROWCOUNT, which the branch reads as 0 whatever
            // the condition queried (probed 2026-10-01 against SQL Server 2025).
            connection.LastStatementRowCount = 0;
            // The condition closes with a DONE of its own, ahead of the branch
            // it chose (probed 2026-09-28 against SQL Server 2025).
            if (connection.FramesEveryStatement)
                yield return StatementDone(batch, StatementDoneKind.Condition);
        }
        else if (batch.FramesUnderNoExec)
        {
            yield return StatementDone(batch, StatementDoneKind.Condition);
        }
        var thenSkip = !condResult;

        var hadElse = false;
        batch.BlockDepth++;
        try
        {
            batch.SkipModeFlag = thenSkip;
            foreach (var o in DispatchOneStatement(batch, requireSemicolonBeforeCte: false, atBatchStart: false))
                yield return o;

            hadElse = TryConsumeSeparatorsBeforeElse(context);
            if (hadElse)
            {
                context.MoveNextRequired(); // consume ELSE
                // ELSE skips iff: outer was IF-skipping initially, cond was
                // true (THEN ran), or THEN's body set a LoopControl signal.
                // Don't read batch.IsSkipping here — the SkipModeFlag we set
                // above for the THEN dispatch is sticky and would conflate
                // with the outer-initial state.
                batch.SkipModeFlag = wasSkipModeFlag
                    || bindError is not null
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

        if (bindError is not null)
            throw bindError;
    }

    /// <summary>
    /// Parses an <c>IF</c> / <c>WHILE</c> condition, which opens no implicit
    /// transaction whatever it reads (probed 2026-09-28 against SQL Server
    /// 2025).
    /// </summary>
    private static BooleanExpression ParseCondition(BatchContext batch)
    {
        batch.Parser.ConditionDepth++;
        try
        {
            return BooleanExpression.Parse(batch.Parser);
        }
        finally
        {
            batch.Parser.ConditionDepth--;
        }
    }

    /// <summary>
    /// Runs an <c>IF</c> / <c>WHILE</c> condition. A run-time error the batch
    /// would carry on past reads as a condition that isn't true — real
    /// reports it, then takes the <c>ELSE</c> or leaves the loop (probed
    /// 2026-09-26 against SQL Server 2025) — and lands on
    /// <paramref name="reported"/> for the caller to emit; one a TRY frame
    /// catches, or that ends the batch, propagates as the statement's own.
    /// </summary>
    /// <remarks>
    /// The condition is a statement of its own for locking too: the
    /// statement-scoped locks it took — the Sch-S of an object it names, a
    /// <c>READ COMMITTED</c> read's IS — go as it ends, before the branch or
    /// loop body runs (probed 2026-10-10 against SQL Server 2025:
    /// <c>sys.dm_tran_locks</c> lists no object lock inside the body of
    /// <c>IF OBJECT_ID(N'dbo.f') IS NOT NULL</c>, <c>IF dbo.f() = 0</c>,
    /// <c>IF EXISTS (SELECT * FROM dbo.t)</c> or a <c>WHILE</c> naming the
    /// function, and another transaction's <c>DROP FUNCTION</c> goes ahead
    /// beside such a body). Held into the body, the Sch-S deadlocked two
    /// transactions each running <c>IF EXISTS (… OBJECT_ID(N'dbo.f') …) DROP
    /// FUNCTION dbo.f</c> at once, each one's drop waiting on the other's
    /// Sch-S.
    /// </remarks>
    private static bool RunCondition(BooleanExpression condition, BatchContext batch, out SimulatedSqlException? reported)
    {
        reported = null;
        try
        {
            return condition.Run(new RuntimeContext(NoColumnResolver, batch)) == true;
        }
        catch (SimulatedSqlException ex)
        {
            ApplyXactAbortPromotion(batch.Connection, ex);
            if (ex.Class == 13 || ex.AbortsTransaction || CaughtByTryFrame(batch, ex)
                || !batch.ContinueOnError || EndsBatch(ex) || !IsStatementTerminating(ex))
            {
                throw;
            }

            if (!batch.SuppressDiagnosticsResolution)
                ex.ResolveDiagnostics(batch.CurrentStatement.StartLine, batch.LineOffset, batch.ErrorProcedureName);
            reported = ex;
            return false;
        }
        finally
        {
            batch.ReleaseStatementSchemaLocks();
        }
    }

    /// <summary>What a condition's reported error puts in the stream, as a failed statement's would.</summary>
    private static IEnumerable<SimulatedStatementOutcome> ConditionErrorOutcomes(BatchContext batch, SimulatedSqlException error)
    {
        batch.Connection.LastErrorNumber = error.Number;
        yield return new SimulatedErrorOutcome(error)
        {
            DoneKind = StatementDoneKind.Condition,
            InModule = batch.ProcFrame is not null || batch.TriggerFrame is not null,
            TransactionEventMark = batch.Connection.TransactionEventsRecorded,
        };
        if (IsStatementTerminationNoticed(batch, error))
            yield return new SimulatedInfoOutcome(SimulatedSqlException.StatementTerminatedMessage(batch), followsRows: true);
    }

    /// <summary>
    /// Binds an <c>IF</c> / <c>WHILE</c> condition the way real compiles it
    /// with the batch, so a comparison it refuses (<c>IF CAST(NULL AS int) =
    /// CAST(NULL AS date)</c>) raises whether or not a branch would run. The
    /// error is handed back rather than thrown so the caller can still step
    /// its cursor past the body — which never runs — before raising it.
    /// </summary>
    private static SimulatedSqlException? BindCondition(BooleanExpression condition, BatchContext batch)
    {
        try
        {
            condition.Bind(batch, NoColumnTypeResolver);
            return null;
        }
        catch (SimulatedSqlException error)
        {
            return error;
        }
    }

    /// <summary>
    /// Reports whether an <c>ELSE</c> follows the THEN branch, stepping over
    /// the statement separators real allows between the two — <c>IF … PRINT 'a';
    /// ELSE PRINT 'b'</c> and any run of <c>;</c> in that slot, the shape
    /// AdventureWorks' <c>ddlDatabaseTriggerLog</c> writes. The separators are
    /// consumed only when an <c>ELSE</c> actually follows, so an IF with no ELSE
    /// leaves its terminator for the dispatch loop.
    /// </summary>
    private static bool TryConsumeSeparatorsBeforeElse(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.Else })
            return true;
        if (context.Token is not Operator { Character: ';' })
            return false;

        var beforeSeparators = context.SaveCheckpoint();
        while (context.Token is Operator { Character: ';' })
            context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.Else })
            return true;

        context.RestoreCheckpoint(beforeSeparators);
        return false;
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
    /// A WHILE has no iteration limit, as real's has none: a runaway loop ends
    /// at the command timeout or a cancel, which the between-statement check
    /// above observes.
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

        var whileLine = batch.CurrentStatement.StartLine;
        var whileStart = batch.CurrentStatement.StartIndex;
        context.MoveNextRequired(); // consume WHILE
        var cond = ParseCondition(batch);
        var conditionEnd = context.PreviousTokenEnd;
        var bindError = BindCondition(cond, batch);

        var bodyStart = context.SaveCheckpoint();
        var wasSkipModeFlag = batch.SkipModeFlag;
        var outerSkipping = batch.IsSkipping || bindError is not null;

        batch.LoopDepth++;
        batch.BlockDepth++;
        try
        {
            if (outerSkipping)
            {
                // WHILE itself in skip mode — never iterate. Skip-dispatch the
                // body once to advance the cursor.
                if (batch.FramesUnderNoExec)
                    yield return StatementDone(batch, StatementDoneKind.Condition);
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
                    // unbounded WHILE — and, inside a function, procedure or
                    // trigger body, ends the calling statement. Body-internal
                    // statements observe the same signal through
                    // DispatchStatementsUntil.
                    if (batch.CancelledAtStatementBoundary())
                        goto ExitLoop;


                    context.RestoreCheckpoint(bodyStart);
                    StatementClock? conditionClock = connection.StatisticsTime ? StatementClock.Start(connection) : null;
                    var conditionCapture = BeginConditionQueryStoreCapture(batch, out var conditionIo);
                    var condResult = RunCondition(cond, batch, out var conditionError);
                    if (conditionCapture is { } capture)
                        EndConditionQueryStoreCapture(batch, capture, conditionIo, whileStart, conditionEnd, conditionError);
                    batch.QueueNullEliminatedWarning();
                    if (conditionError is null)
                        QueueConditionStatistics(batch, conditionClock, whileLine);
                    // As for IF, the body reads @@ERROR 0 after its condition.
                    if (conditionError is not null)
                    {
                        foreach (var o in ConditionErrorOutcomes(batch, conditionError))
                            yield return o;
                    }
                    else
                    {
                        connection.LastErrorNumber = 0;
                        if (connection.FramesEveryStatement)
                            yield return StatementDone(batch, StatementDoneKind.Condition);
                    }

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

                    // RETURN, GOTO, a batch-ending error and an error a TRY
                    // caught propagate through WHILE (unlike BREAK / CONTINUE
                    // which we catch). Exit the loop without clearing — the
                    // outer DispatchStatementsUntil stops on the first three,
                    // and the TRY skips to its CATCH on the last; iterating on
                    // would run the condition over a body that only skips, so
                    // a loop whose body advances its own condition never ends.
                    if (batch.ReturnSignaled || batch.BatchAborted || batch.PendingGotoLabel is not null || batch.ErrorSignaled)
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

        if (bindError is not null)
            throw bindError;
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
    /// Dispatches a <c>RETURN</c> statement. Outside a procedure or scalar
    /// function body only the bare form is legal: a
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
    /// <returns>Whether the statement carried a value.</returns>
    private static bool ParseReturnStatement(BatchContext batch)
    {
        // Real's parser reads the value before its grammar action refuses it,
        // so the value's own parse errors (Msg 102, 137, 195) come first,
        // while its binder errors never do (probed 2026-10-06 against SQL
        // Server 2025).
        static SimulatedSqlException RefusedReturnValue(ParserContext context)
        {
            var start = context.SaveCheckpoint();
            try
            {
                using (context.EnterNextValueForScope(NextValueForScope.Nested))
                    _ = Expression.Parse(context);
            }
            catch (SimulatedSqlException bindError) when (bindError.Class != 15)
            {
                // Past the value, where real's refusal leaves its parser.
                context.RestoreCheckpoint(start);
                var depth = 0;
                while (depth > 0 || !IsStatementBoundary(context.Token))
                {
                    switch (context.Token)
                    {
                        case Operator { Character: '(' }:
                            depth++;
                            break;
                        case Operator { Character: ')' }:
                            depth--;
                            break;
                    }
                    context.MoveNextOptional();
                }
            }
            return SimulatedSqlException.ReturnWithValueNotAllowed();
        }

        var context = batch.Parser;
        context.MoveNextOptional(); // consume RETURN

        // Value form is legal inside a scalar UDF body (UdfFrame non-null)
        // or a stored procedure body (ProcFrame non-null). Outside either,
        // it raises Msg 178 at parse time, even from un-taken IF branches
        // (compile-time check, same pattern as BREAK's Msg 135).
        if (!IsStatementBoundary(context.Token))
        {
            // Dynamic SQL runs under a procedure frame but refuses the value
            // form as a batch does (probed 2026-09-24).
            if (batch.UdfFrame is null && batch.ProcFrame is null or { IsDynamicSql: true })
                throw RefusedReturnValue(context);

            // A RETURN's value is one of the places real refuses a sequence
            // draw as nested (probed 2026-10-04 against SQL Server 2025).
            Expression valueExpr;
            using (context.EnterNextValueForScope(NextValueForScope.Nested))
                valueExpr = Expression.Parse(context);
            if (batch.UdfFrame is { AnalyzesReturnMask: true } analyzed)
                analyzed.ReturnMask = DataMask.Merge(analyzed.ReturnMask, DataMask.Of(valueExpr, static _ => null, typeOf: null));
            // A scalar function's result takes its value as an assignment
            // does, settled while the body binds at CREATE among the body's
            // other binder errors — and not at all once the body broke a shape
            // rule, whose Msg 443 real reports alone (probed 2026-09-24).
            if (batch.UdfFrame is { } returnFrame && batch.FunctionBodyShape is not { Violations.Count: > 0 })
            {
                try
                {
                    AssignmentRules.RequireAssignable(valueExpr, valueExpr.GetSqlType(batch, NoColumnTypeResolver), returnFrame.ReturnType);
                }
                catch (SimulatedSqlException refused) when (refused.Number is 206 or 257 && batch.CreateTimeBindErrors is { } bindErrors)
                {
                    refused.ResolveDiagnostics(batch.CurrentStatement.StartLine, batch.LineOffset, batch.ErrorProcedureName);
                    bindErrors.Add(refused);
                }
            }
            if (!batch.IsSkipping)
            {
                if (batch.UdfFrame is { } udfFrame)
                {
                    var raw = valueExpr.Run(new RuntimeContext(
                        name => throw SimulatedSqlException.MustDeclareScalarVariable(name.Leaf),
                        batch));
                    // Converted as an assignment converts, so a value past the
                    // return type is its overflow error (Msg 220 for an int
                    // into tinyint), never a raw .NET overflow.
                    udfFrame.ReturnedValue = Parser.Expressions.Cast.ApplyCoercion(raw, udfFrame.ReturnType, targetMaxLength: null);
                }
                else
                {
                    // Procedure RETURN: coerce to int, a NULL landing 0 in the
                    // caller's @rc with Msg 282 saying so (probe-confirmed
                    // against SQL Server 2025). Msg 245 surfaces here for
                    // non-coercible types like `RETURN 'abc'`, and ends the
                    // batch; an error that ends only its statement — a divide
                    // by zero, a value past int — returns NULL instead, Msg
                    // 282 following the error (probed 2026-10-02).
                    var procFrame = batch.ProcFrame!;
                    SqlValue coerced;
                    try
                    {
                        coerced = valueExpr.Run(new RuntimeContext(
                            name => throw SimulatedSqlException.MustDeclareScalarVariable(name.Leaf),
                            batch)).CoerceTo(SqlType.Int32);
                    }
                    catch (Exception failure) when (failure is OverflowException || (failure is SimulatedSqlException { AbortsAsUnderXactAbort: false, TerminatesBatch: false } sql && sql.Class == 16))
                    {
                        var error = failure as SimulatedSqlException ?? SimulatedSqlException.ArithmeticOverflow("int");
                        error.FollowingMessage = SimulatedSqlException.NullReturnStatusMessage(batch, procFrame.ProcedureName);
                        procFrame.ReturnCode = 0;
                        batch.ReturnSignaled = true;
                        throw error;
                    }
                    if (coerced.IsNull)
                        batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.NullReturnStatusMessage(batch, procFrame.ProcedureName));
                    procFrame.ReturnCode = coerced.IsNull ? 0 : coerced.AsInt32;
                    // A RETURN that completes is a statement that succeeded,
                    // which the caller's @@ERROR reads (probed 2026-10-04
                    // against SQL Server 2025).
                    batch.Connection.LastErrorNumber = 0;
                }
                batch.ReturnSignaled = true;
            }
            return true;
        }

        // The bare form is the one a scalar function may never use: every
        // RETURN in one carries the value it returns, so real raises Msg 1075
        // (probe-confirmed against SQL Server 2025, mid-body and trailing
        // alike). A multi-statement TVF (no frame) and a procedure (ProcFrame)
        // both accept it. Parse-time like the Msg 178 check above, so an
        // un-taken branch reports it too.
        if (batch.UdfFrame is not null)
            throw SimulatedSqlException.ScalarFunctionReturnNeedsArgument();

        if (!batch.IsSkipping)
            batch.ReturnSignaled = true;
        return false;
    }

    /// <summary>
    /// Parses and runs a <c>BEGIN … END</c> compound-statement block. Dispatches
    /// each contained statement through <see cref="DispatchStatementsUntil"/>
    /// until the matching <c>END</c>. Empty blocks (<c>BEGIN END</c> or
    /// <c>BEGIN ; END</c> with nothing but separators inside) raise a syntax
    /// error near the token after <c>END</c> — probe-confirmed against SQL
    /// Server 2025.
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
        // Real reads past the END before refusing the empty block, so it
        // names the token after it — a keyword as Msg 156 — and the END
        // itself, as a plain token, only where the batch ends there (probed
        // 2026-09-24 and 2026-09-26 against SQL Server 2025: `BEGIN END;` is
        // near ';', `BEGIN END SELECT 1` near the keyword 'select').
        if (context.Token is ReservedKeyword { Keyword: Keyword.End } end)
        {
            context.MoveNextOptional();
            throw context.Token is null ? SimulatedSqlException.SyntaxErrorNear(end) : SimulatedSqlException.SyntaxErrorNear(context);
        }

        foreach (var o in DispatchStatementsUntil(batch, endKeyword: Keyword.End))
            yield return o;

        // RETURN inside the block exits early without reaching END — abandon
        // the block; outer DispatchStatementsUntil also stops on ReturnSignaled.
        // A batch-aborting error (e.g. a Msg 207 on a resolvable table inside a
        // skipped block) likewise leaves the cursor mid-statement with no
        // recovery scan, so short-circuit the "expect END" check to let the one
        // error surface instead of a spurious Msg 102 near the abandoned token.
        if (batch.ReturnSignaled || batch.BatchAborted || batch.PendingGotoLabel is not null)
            yield break;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional(); // consume END
    }

    /// <summary>
    /// Parses a <c>BEGIN ATOMIC WITH (option [, option ...]) body END</c>
    /// block — the body of a natively compiled module. Cursor on entry: the
    /// <c>BEGIN</c> keyword. Cursor on exit: the first token after
    /// <c>END</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The block's <c>WITH</c> list must name a <c>TRANSACTION ISOLATION
    /// LEVEL</c> — SNAPSHOT, REPEATABLE READ or SERIALIZABLE, READ COMMITTED
    /// being Msg 10794 — and a <c>LANGUAGE</c> (Msg 10784 for either missing),
    /// checked as the module binds at <c>CREATE</c> (probed 2026-10-02 against
    /// SQL Server 2025); <c>DATEFORMAT</c>, <c>DATEFIRST</c> and
    /// <c>DELAYED_DURABILITY</c> parse and are discarded.
    /// </para>
    /// <para>
    /// Running, the block is one transaction: its own when none is open,
    /// committed when the body ends, else a savepoint in the caller's. An
    /// error inside it rolls the block back and, uncaught, ends the batch,
    /// leaving the caller's transaction committable (probed 2026-10-02 against
    /// SQL Server 2025). A memory-optimized table read
    /// inside runs at the block's level, so the session's isolation rules
    /// don't reach it (<see cref="SimulatedDbConnection.AtomicBlockDepth"/>).
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseBeginAtomicBlock(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume BEGIN
        // Only a module body may hold one: at batch level, dynamic SQL
        // included, ATOMIC is a syntax error, and a module that isn't natively
        // compiled refuses it as it binds at CREATE (Msg 10782) — probed
        // 2026-09-25 against SQL Server 2025. A body that runs has passed that,
        // and so has one compiling as its call is about to run it.
        if (batch.UdfFrame is null && batch.TriggerFrame is null && batch.ProcFrame is not { IsDynamicSql: false })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (batch.CreateTimeBinding && !batch.CompilingForRun && !batch.NativelyCompiledBody)
            throw SimulatedSqlException.BeginAtomicOutsideNativeModule();
        context.MoveNextRequired(); // consume ATOMIC
        ParseAtomicBlockOptions(context, validate: batch.CreateTimeBinding && !batch.CompilingForRun);

        // Body dispatch mirrors ParseBeginBlock — leading separators drained,
        // empty body rejected, statements dispatched until END.
        while (context.Token is Operator { Character: ';' })
            context.MoveNextOptional();
        // A statement-position END is named as a plain token (Msg 102),
        // not as a keyword (probed 2026-09-24 against SQL Server 2025).
        if (context.Token is ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context.Token);

        var connection = batch.Connection;
        var runs = !batch.IsSkipping && !batch.CreateTimeBinding;
        var outerTransaction = runs ? connection.CurrentTransaction : null;
        var ownTransaction = runs && outerTransaction is null ? connection.StartTransaction(System.Data.IsolationLevel.Unspecified) : null;
        // Inside the caller's transaction the block is a savepoint, under a
        // name no SAVE TRANSACTION can write.
        var savepoint = $"\u0001atomic{connection.AtomicBlockDepth}";
        outerTransaction?.SetSavepoint(savepoint);
        if (runs)
            connection.AtomicBlockDepth++;
        var completed = false;
        try
        {
            foreach (var o in DispatchStatementsUntil(batch, endKeyword: Keyword.End))
                yield return o;
            completed = true;
        }
        finally
        {
            if (runs)
                connection.AtomicBlockDepth--;
            // The block settles its own transaction, and an error rolls the
            // caller's back to where the block began.
            if (ownTransaction is not null && ReferenceEquals(connection.CurrentTransaction, ownTransaction))
            {
                if (completed && !ownTransaction.Doomed)
                    ownTransaction.EndCommit();
                else
                    ownTransaction.EndRollback();
            }
            else if (!completed && outerTransaction is { Doomed: false } && ReferenceEquals(connection.CurrentTransaction, outerTransaction))
            {
                _ = outerTransaction.TryRollbackToSavepoint(savepoint);
            }
        }

        if (batch.ReturnSignaled || batch.BatchAborted || batch.PendingGotoLabel is not null)
            yield break;

        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional(); // consume END
    }

    /// <summary>
    /// Reads a <c>BEGIN ATOMIC</c> block's <c>WITH (option = value, …)</c>
    /// list, entered on the token after <c>ATOMIC</c> and leaving the cursor
    /// past the list's <c>)</c>. With <paramref name="validate"/> — the module
    /// binding at <c>CREATE</c> — a missing <c>TRANSACTION ISOLATION LEVEL</c>
    /// or <c>LANGUAGE</c> is Msg 10784 and READ COMMITTED Msg 10794.
    /// </summary>
    internal static void ParseAtomicBlockOptions(ParserContext context, bool validate)
    {
        var isolation = false;
        var language = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            while (true)
            {
                context.MoveNextRequired();
                if (context.Token is ReservedKeyword { Keyword: Keyword.Transaction })
                {
                    if (context.GetNextRequired() is not UnquotedString { Value: var isolationWord } || !BuiltInToken.Equals(isolationWord, "ISOLATION")
                        || context.GetNextRequired() is not UnquotedString { Value: var levelWord } || !BuiltInToken.Equals(levelWord, "LEVEL")
                        || context.GetNextRequired() is not Operator { Character: '=' })
                    {
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    var first = context.GetNextRequired().Source;
                    var readCommitted = first.Equals("READ", StringComparison.OrdinalIgnoreCase);
                    if (readCommitted || first.Equals("REPEATABLE", StringComparison.OrdinalIgnoreCase))
                        context.MoveNextRequired();
                    if (validate && readCommitted)
                        throw SimulatedSqlException.ReadCommittedNativeModule();
                    isolation = true;
                }
                else
                {
                    if (context.Token is not (Name or UnquotedString))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    language |= context.Token is UnquotedString { Value: var option } && BuiltInToken.Equals(option, "LANGUAGE");
                    if (context.GetNextRequired() is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                }
                if (context.GetNextRequired() is not Operator { Character: ',' })
                    break;
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
        if (validate && !isolation)
            throw SimulatedSqlException.AtomicBlockOptionRequired("transaction isolation level");
        if (validate && !language)
            throw SimulatedSqlException.AtomicBlockOptionRequired("language");
    }
}
