using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>WHILE cond stmt</c>, <c>BREAK</c>, and <c>CONTINUE</c>.
/// Covers iteration with mutated cond, the one-statement-body footgun,
/// nested loops (BREAK exits innermost), BREAK / CONTINUE scope check
/// (Msg 135 / Msg 136 fire even from un-taken IF branches — real SQL
/// Server's compile-time check), @@ROWCOUNT=0 at every exit path,
/// non-boolean cond (Msg 4145), WHILE in un-taken IF (skip-mode), and
/// the simulator's iteration cap. Behavior probed against SQL Server
/// 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class WhileLoopTests
{
    [TestMethod]
    public void BasicCounter_RunsToCompletion()
        => AreEqual(3, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while @i < 3 set @i = @i + 1;
            select @i
            """));

    /// <summary>
    /// Body is exactly one statement — same footgun as IF. The second
    /// statement after the body runs once *after* the loop, not per
    /// iteration. Probe-confirmed.
    /// </summary>
    [TestMethod]
    public void BodyOneStatement_SecondStatementEscapesLoop()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while @i < 2 set @i = @i + 1 select @i
            """));

    [TestMethod]
    public void CondInitiallyFalse_NoIteration()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while 1 = 0 set @i = 99;
            select @i
            """));

    [TestMethod]
    public void NonBooleanCond_Msg4145()
        => new Simulation().AssertSqlError("while 1 select 1", 4145);

    [TestMethod]
    public void NullCond_Msg4145()
        => new Simulation().AssertSqlError("while null select 1", 4145);

    [TestMethod]
    public void EmptyBeginEnd_Msg102()
        => new Simulation().AssertSqlError("while 1=0 begin end", 102);

    // ---- BREAK ----

    [TestMethod]
    public void Break_ExitsLoop()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while 1 = 1
            begin
                set @i = @i + 1;
                if @i >= 2 break;
            end;
            select @i
            """));

    /// <summary>
    /// BREAK skips remaining statements in the same block — subsequent
    /// statements after BREAK in the body never execute. Probe-confirmed.
    /// </summary>
    [TestMethod]
    public void Break_SkipsRemainingStatementsInBlock()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("""
            declare @sum int = 0;
            while 1 = 1
            begin
                set @sum = @sum + 1;
                break;
                set @sum = @sum + 100;
            end;
            select @sum
            """));

    // ---- CONTINUE ----

    /// <summary>
    /// CONTINUE skips the remainder of the current iteration and re-
    /// evaluates the cond. @hits counts only odd iterations because
    /// even iterations continue before incrementing @hits.
    /// </summary>
    [TestMethod]
    public void Continue_SkipsRemainderAndReevaluates()
        => AreEqual(3, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0, @hits int = 0;
            while @i < 5
            begin
                set @i = @i + 1;
                if @i % 2 = 0 continue;
                set @hits = @hits + 1;
            end;
            select @hits
            """));

    // ---- Scope check (Msg 135 / 136) ----

    [TestMethod]
    public void BreakOutsideLoop_Msg135()
        => new Simulation().AssertSqlError("break", 135, "Cannot use a BREAK statement outside the scope of a WHILE statement.");

    [TestMethod]
    public void ContinueOutsideLoop_Msg136()
        => new Simulation().AssertSqlError("continue", 136, "Cannot use a CONTINUE statement outside the scope of a WHILE statement.");

    /// <summary>
    /// BREAK inside an IF body (not in a WHILE) → Msg 135. The check is
    /// compile-time in real SQL Server, so it fires even though the IF
    /// might be un-taken at runtime. The simulator parses the IF body
    /// statement-by-statement; BREAK's scope check fires at parse time
    /// regardless of skip mode (probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void BreakInsideIfNoLoop_StillMsg135()
        => new Simulation().AssertSqlError("if 1=1 break", 135);

    [TestMethod]
    public void BreakInsideUntakenIf_StillMsg135()
        => new Simulation().AssertSqlError("if 1=0 break", 135);

    [TestMethod]
    public void BreakInsideBlock_NoLoop_StillMsg135()
        => new Simulation().AssertSqlError("begin break end", 135);

    /// <summary>
    /// BREAK inside an IF inside a WHILE: the LoopDepth check passes
    /// (we're in a WHILE), so no Msg 135. Iteration completes; outer
    /// WHILE keeps iterating.
    /// </summary>
    [TestMethod]
    public void BreakInsideIfInsideLoop_NoError()
        => AreEqual(5, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while @i < 5
            begin
                set @i = @i + 1;
                if 1 = 0 break;
            end;
            select @i
            """));

    // ---- Nested loops ----

    /// <summary>
    /// Inner BREAK exits inner WHILE only. Outer continues iterating.
    /// Reset <c>@inner</c> inside the outer body via SET (not DECLARE —
    /// re-declaring inside a loop body would fire Msg 134 on iteration 2).
    /// </summary>
    [TestMethod]
    public void NestedBreak_ExitsInnerOnly()
        => AreEqual(3, new Simulation().ExecuteScalar<int>("""
            declare @outer int = 0, @inner int = 0;
            while @outer < 3
            begin
                set @outer = @outer + 1;
                set @inner = 0;
                while 1 = 1
                begin
                    set @inner = @inner + 1;
                    if @inner >= 2 break;
                end;
            end;
            select @outer
            """));

    /// <summary>
    /// Nested CONTINUE only re-iterates the inner loop. Outer iteration
    /// continues normally after inner WHILE finishes.
    /// </summary>
    [TestMethod]
    public void NestedContinue_ReiteratesInnerOnly()
        => AreEqual(10, new Simulation().ExecuteScalar<int>("""
            declare @outer int = 0, @inner int = 0, @total int = 0;
            while @outer < 2
            begin
                set @outer = @outer + 1;
                set @inner = 0;
                while @inner < 4
                begin
                    set @inner = @inner + 1;
                    if @inner % 2 = 0 continue;
                    set @total = @total + 1;
                end;
            end;
            select @total + (@outer * 3)
            """));

    // ---- WHILE in skipped IF ----

    /// <summary>
    /// WHILE inside an un-taken IF branch: the WHILE never iterates.
    /// Critical because the body is <c>WHILE 1=1</c> (infinite loop) —
    /// if skip-mode failed, the test would hit the iteration cap.
    /// </summary>
    [TestMethod]
    public void WhileInsideSkippedIf_NeverIterates()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            if 1 = 0 while 1 = 1 set @i = 99;
            select @i
            """));

    // ---- @@ROWCOUNT ----

    [TestMethod]
    public void RowCount_AfterWhileNoIter_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            declare @prime int = 1;
            while 1 = 0 set @prime = 99;
            select @@rowcount
            """));

    [TestMethod]
    public void RowCount_AfterWhileWithIters_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while @i < 2 begin set @i = @i + 1 end;
            select @@rowcount
            """));

    [TestMethod]
    public void RowCount_AfterBreakExit_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while 1 = 1
            begin
                set @i = @i + 1;
                if @i >= 1 break;
            end;
            select @@rowcount
            """));

    // ---- Cond mutation across iterations ----

    [TestMethod]
    public void CondReferencesVariable_MutationVisibleAcrossIters()
        => AreEqual(5, new Simulation().ExecuteScalar<int>("""
            declare @i int = 0;
            while @i < 5 set @i = @i + 1;
            select @i
            """));

    // ---- Iteration cap ----

    /// <summary>
    /// Simulator-only safety: a runaway WHILE throws after the per-batch
    /// iteration cap is exceeded. Real SQL Server has no such cap — query
    /// timeouts handle this in production — but the simulator surfaces an
    /// explicit error so a buggy test doesn't hang CI.
    /// </summary>
    [TestMethod]
    public void IterationCap_ThrowsAfterLimit()
    {
        var ex = Throws<InvalidOperationException>(() => new Simulation().ExecuteNonQuery("""
            declare @i int = 0;
            while 1 = 1 set @i = @i + 1
            """));
        Contains("iteration cap exceeded", ex.Message, StringComparison.Ordinal);
    }
}
