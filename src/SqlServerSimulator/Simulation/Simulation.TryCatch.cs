using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and runs a <c>BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH</c>
    /// block. The TRY body dispatches normally with
    /// <see cref="BatchContext.TryFrameDepth"/> bumped so the per-statement
    /// dispatch wrapper catches <see cref="SimulatedSqlException"/> and
    /// stores the error info into <see cref="BatchContext.InFlightError"/>
    /// instead of letting it propagate. When an error is caught the TRY body
    /// runs the remainder in skip-mode (via <see cref="BatchContext.ErrorSignaled"/>
    /// in <see cref="BatchContext.IsSkipping"/>), drains to <c>END TRY</c>,
    /// then the CATCH body dispatches with the error in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probed against SQL Server 2025 (2026-05-12). Catchable severities:
    /// 11-19 reach CATCH; severity 10 is informational and TRY keeps running;
    /// severity 20+ with WITH LOG is also caught when the simulator's
    /// existing factories raise it (the simulator's <c>SimulatedSqlException</c>
    /// carries a uniform severity class on each instance, so the
    /// per-statement wrapper catches them all uniformly).
    /// </para>
    /// <para>
    /// Statement-level atomicity preserved: a multi-row INSERT that fails
    /// on row 3 rolls back its partial heap writes via the existing undo log
    /// before the caught error materializes — probed and confirmed
    /// (CATCH sees zero rows in the destination).
    /// </para>
    /// <para>
    /// Nested TRY/CATCH: outer state (<see cref="BatchContext.InFlightError"/>,
    /// <see cref="BatchContext.ErrorSignaled"/>) is saved at entry and
    /// restored at exit so a re-throw from an inner CATCH (<c>THROW;</c>)
    /// surfaces to the outer CATCH via the normal exception path; the outer
    /// TRY's still-active <see cref="BatchContext.TryFrameDepth"/> catches it.
    /// </para>
    /// <para>
    /// Empty TRY body raises Msg 102 (probe-confirmed: real SQL Server
    /// reports "near 'try'"). Empty CATCH body is legal.
    /// </para>
    /// <para>
    /// Fidelity gaps:
    /// </para>
    /// <list type="bullet">
    /// <item>Parse-time name-resolution errors (Msg 208 / Msg 207) inside a
    /// TRY body propagate out of the batch — the simulator parses the whole
    /// batch eagerly while real SQL Server defers some name resolution.
    /// Same root cause as the un-taken-IF deferred-name-resolution gap.</item>
    /// <item><c>XACT_STATE()</c> and the XACT_ABORT / doomed-transaction
    /// semantics aren't modeled. <c>@@TRANCOUNT</c> behaves correctly
    /// (caught errors don't auto-rollback explicit transactions, matching
    /// real SQL Server's XACT_ABORT OFF default), so the standard
    /// <c>IF @@TRANCOUNT &gt; 0 ROLLBACK</c> idiom in CATCH works.</item>
    /// </list>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseTryCatch(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume BEGIN
        context.MoveNextRequired(); // consume TRY

        // Drain leading separators inside TRY body. An empty body (BEGIN TRY
        // ; END TRY or BEGIN TRY END TRY) raises Msg 102 — probe-confirmed.
        while (context.Token is Operator { Character: ';' })
            context.MoveNextOptional();
        if (IsEndTry(context))
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Save outer error state for nested TRY/CATCH: a re-throw from an
        // inner CATCH must surface to the outer CATCH with the re-thrown
        // error in flight, not the (possibly different) outer pre-state.
        var outerInFlight = batch.InFlightError;
        var outerErrorSignaled = batch.ErrorSignaled;
        batch.InFlightError = null;
        batch.ErrorSignaled = false;

        batch.TryFrameDepth++;
        // The session-wide companion: whether an XACT_ABORT-promoted error
        // rolls back or merely dooms depends on any frame on the stack holding
        // a TRY, including one in a caller a procedure body can't see.
        batch.Connection.OpenTryFrames++;
        try
        {
            // TRY body dispatches normally. The per-statement wrapper
            // (DispatchOneStatement) catches SimulatedSqlException when
            // TryFrameDepth > 0; after a catch IsSkipping is true (via
            // ErrorSignaled) so the remainder of the body skip-parses to
            // advance the cursor to END TRY.
            foreach (var o in DispatchStatementsUntil(batch, endKeyword: Keyword.End))
                yield return o;
        }
        finally
        {
            batch.TryFrameDepth--;
            batch.Connection.OpenTryFrames--;
        }

        // A GOTO out of the TRY body leaves the cursor mid-block with a jump
        // pending; the batch root does the jump, so abandon the rest of the
        // construct rather than demanding its END TRY. Jumping *into* a TRY or
        // CATCH is refused while the batch compiles (Msg 1026), so the CATCH
        // half never needs the same escape.
        if (batch.PendingGotoLabel is not null)
            yield break;

        // Consume END TRY.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Try })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        // Expect BEGIN CATCH.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Begin })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Catch })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var didCatch = batch.ErrorSignaled;
        if (didCatch)
        {
            // Clear ErrorSignaled so CATCH body statements actually run
            // (IsSkipping was true while still inside TRY skip-mode draining;
            // CATCH dispatch wants normal execution). Bump CatchDepth so
            // ERROR_*() and THROW; know they're inside a CATCH.
            batch.ErrorSignaled = false;
            batch.CatchDepth++;
            try
            {
                foreach (var o in DispatchStatementsUntil(batch, endKeyword: Keyword.End))
                    yield return o;
            }
            finally
            {
                batch.CatchDepth--;
            }
        }
        else
        {
            // No error caught — skip-dispatch CATCH body to advance the
            // cursor past END CATCH. Matches the "outer skipping" branch in
            // ParseWhileStatement. CatchDepth still bumps: it tracks LEXICAL
            // containment, and the bare-THROW rethrow check (Msg 10704) is a
            // compile-time structural rule that must accept a THROW inside a
            // skipped CATCH body — SSMS's Select-Top-1000 server-properties
            // batch has exactly that shape once its TRY body succeeds.
            var wasSkipModeFlag = batch.SkipModeFlag;
            batch.SkipModeFlag = true;
            batch.CatchDepth++;
            try
            {
                foreach (var o in DispatchStatementsUntil(batch, endKeyword: Keyword.End))
                    yield return o;
            }
            finally
            {
                batch.CatchDepth--;
                batch.SkipModeFlag = wasSkipModeFlag;
            }
        }

        // Restore outer error state. For nested TRY/CATCH: if we caught and
        // ran the CATCH, the inner is done — outer state takes over. If the
        // inner CATCH re-threw (via THROW;), that throw was caught at the
        // dispatch wrapper using the outer TRY's still-active frame, which
        // already updated InFlightError + ErrorSignaled to the re-thrown
        // values; we shouldn't blow those away. Detect: if the new state
        // looks like a fresh throw (ErrorSignaled set after CATCH ran),
        // leave it; otherwise restore to outer's pre-state.
        if (!batch.ErrorSignaled)
        {
            batch.InFlightError = outerInFlight;
            batch.ErrorSignaled = outerErrorSignaled;
        }

        // Consume END CATCH.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Catch })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
    }

    /// <summary>
    /// True when the current token is the start of an <c>END TRY</c> pair
    /// (used to detect an empty TRY body before dispatch begins).
    /// </summary>
    private static bool IsEndTry(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.End })
            return false;
        var checkpoint = context.SaveCheckpoint();
        try
        {
            return context.MoveNext()
                && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Try };
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }
}
