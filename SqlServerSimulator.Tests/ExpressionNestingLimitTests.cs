using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Expression depth limits. Two failure modes must never reach the host: a
/// .NET stack overflow (uncatchable, process-fatal) from deep recursion, and
/// a silently-wrong result. So flat left-associative chains parse and evaluate
/// iteratively (no per-term recursion, no artificial cap — matching real SQL
/// Server's tolerance of thousands of terms), while genuinely-nested shapes are
/// bounded by SQL Server's own graceful structural errors:
/// <list type="bullet">
/// <item>Msg 191 (Class 15) — parens / subquery / function-argument nesting,
/// via the shared weighted budget (probe-confirmed 2026-07-18: real pools
/// these into one budget where a subquery level costs ≈ 6 paren levels). The
/// simulator's parse frames are fatter than real's, so the caps are lower than
/// real's stack-dependent thresholds (500 parens vs 1015, 83 subqueries vs
/// 168) but keep the probed ratio — a documented divergence.</item>
/// <item>Msg 125 (Class 15) — CASE / IIF nested past ten levels; State 4 for
/// CASE, State 2 for IIF (the construct entered at the eleventh level).</item>
/// <item>Msg 8631 (Class 17) — the runtime stack probe, the backstop for any
/// deep recursive shape whose frames outrun a deterministic cap (deep function
/// nesting reaches it near ~80 levels on a 1 MB thread; real raises Msg 191 at
/// 1013 — a documented divergence).</item>
/// </list>
/// </summary>
[TestClass]
public sealed class ExpressionNestingLimitTests
{
    private static string AdditionChain(int terms) =>
        $"select ({string.Join(" + ", Enumerable.Repeat("1", terms))})";

    // --- Flat left-associative chains: no cap, iterative parse + evaluation. ---

    [TestMethod]
    public void AdditionChain_ModerateDepth_Evaluates()
        => AreEqual(200, new Simulation().ExecuteScalar(AdditionChain(200)));

    [TestMethod]
    [Timeout(60000)]
    public void AdditionChain_ExtremeDepth_Evaluates()
        => AreEqual(50000, new Simulation().ExecuteScalar(AdditionChain(50000)));

    [TestMethod]
    [Timeout(60000)]
    public void ConcatChain_ExtremeDepth_Evaluates()
        => AreEqual(new string('a', 20000), new Simulation().ExecuteScalar(
            "select " + string.Join(" + ", Enumerable.Repeat("'a'", 20000))));

    [TestMethod]
    [Timeout(60000)]
    public void AndChain_ExtremeDepth_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select 1 where " + string.Join(" and ", Enumerable.Repeat("1=1", 50000))));

    [TestMethod]
    [Timeout(60000)]
    public void OrChain_ExtremeDepth_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select 1 where " + string.Join(" or ", Enumerable.Repeat("1=0", 49999)) + " or 1=1"));

    // --- Hardening: a small-stack thread must never process-death. ---

    [TestMethod]
    [Timeout(60000)]
    public void AndChain_ExtremeDepth_OnSmallStackThread_IsGracefulNeverProcessDeath()
    {
        // A 50,000-term flat AND chain used to overflow the host at Run time
        // (the boolean spine recursed once per term). Evaluation is now
        // iterative; running it on a deliberately small (512 KB) thread proves
        // the whole parse + run path is recursion-bounded. A stack overflow
        // would fault the process rather than fail the test, so reaching the
        // assertion at all is the guarantee.
        var sql = "select 1 where " + string.Join(" and ", Enumerable.Repeat("1=1", 50000));
        RunOnSmallStack(sql, out var result, out var failure);
        IsNull(failure, "deep AND chain should evaluate, not raise");
        AreEqual(1, result);
    }

    [TestMethod]
    [Timeout(60000)]
    public void NotChain_ExtremeDepth_OnSmallStackThread_IsGracefulNeverProcessDeath()
    {
        // `NOT NOT … p` used to recurse per NOT at Run time. NOT-runs now
        // collapse at parse (three-valued NOT is an involution), so an
        // even-length stack is the identity — here 50,000 NOTs over 1=1.
        var sql = "select 1 where " + string.Concat(Enumerable.Repeat("not ", 50000)) + "1=1";
        RunOnSmallStack(sql, out var result, out var failure);
        IsNull(failure, "deep NOT stack should evaluate, not raise");
        AreEqual(1, result);
    }

    private void RunOnSmallStack(string sql, out object? result, out Exception? failure)
    {
        object? captured = null;
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                captured = new Simulation().ExecuteScalar(sql);
            }
            catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
            {
                caught = ex;
            }
        }, maxStackSize: 512 * 1024);
        thread.Start();
        IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "query did not complete on the small-stack thread");
        result = captured;
        failure = caught;
    }

    // --- Nested parens: shared budget, Msg 191. ---

    [TestMethod]
    public void NestedParens_WithinLimit_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar($"select {new string('(', 200)}1{new string(')', 200)}"));

    [TestMethod]
    public void NestedParens_AtLimit_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar($"select {new string('(', 500)}1{new string(')', 500)}"));

    [TestMethod]
    public void NestedParens_OverLimit_RaisesMsg191()
    {
        var ex = new Simulation().AssertSqlError($"select {new string('(', 501)}1{new string(')', 501)}", 191);
        AreEqual(15, ex.Class);
        AreEqual("Some part of your SQL statement is nested too deeply. Rewrite the query or break it up into smaller queries.", ex.Message);
    }

    // --- Nested scalar subqueries: shared budget (≈ 6× a paren), Msg 191. ---

    private static string NestedSubqueries(int depth)
    {
        var q = "select 1";
        for (var i = 0; i < depth; i++)
            q = $"select ({q})";
        return q;
    }

    [TestMethod]
    public void NestedSubqueries_AtLimit_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar(NestedSubqueries(83)));

    [TestMethod]
    public void NestedSubqueries_OverLimit_RaisesMsg191()
        => AreEqual(15, new Simulation().AssertSqlError(NestedSubqueries(84), 191).Class);

    // --- Nested functions: charged to the shared budget (Msg 191 at the cap
    // on an adequate stack) but with fatter frames, so on a tight stack the
    // runtime probe (Msg 8631) pre-empts — both graceful, never process death. ---

    [TestMethod]
    [Timeout(60000)]
    public void NestedFunctions_OverBudget_RaisesMsg191()
    {
        // 501 nested ABS = 501 budget units > 500. On a normal (large) test
        // thread the structural cap trips before the stack probe.
        var sql = "select " + string.Concat(Enumerable.Repeat("abs(", 501)) + "1" + new string(')', 501);
        AreEqual(15, new Simulation().AssertSqlError(sql, 191).Class);
    }

    [TestMethod]
    [Timeout(60000)]
    public void NestedFunctions_ExtremeDepth_OnSmallStackThread_RaisesMsg8631NotProcessDeath()
    {
        // On a 512 KB thread the fat function frames exhaust the stack around
        // ~40 levels — far below the 500-unit budget — so the runtime probe
        // converts the overflow into Msg 8631 rather than faulting the process.
        var sql = "select " + string.Concat(Enumerable.Repeat("abs(", 5000)) + "1" + new string(')', 5000);
        RunOnSmallStack(sql, out _, out var failure);
        var ex = IsInstanceOfType<SimulatedSqlException>(failure);
        AreEqual(8631, ex.Number);
    }

    // --- Nested CASE / IIF: lexical cap of ten, Msg 125. ---

    private static string NestedCase(int depth, string innermost = "1")
    {
        var q = innermost;
        for (var i = 0; i < depth; i++)
            q = $"case when 1=1 then {q} else 0 end";
        return q;
    }

    private static string NestedIif(int depth, string innermost = "1")
    {
        var q = innermost;
        for (var i = 0; i < depth; i++)
            q = $"iif(1=1, {q}, 0)";
        return q;
    }

    [TestMethod]
    public void NestedCase_AtLimit_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar($"select {NestedCase(10)}"));

    [TestMethod]
    public void NestedCase_OverLimit_RaisesMsg125_State4()
    {
        var ex = new Simulation().AssertSqlError($"select {NestedCase(11)}", 125);
        AreEqual(15, ex.Class);
        AreEqual(4, ex.State);
        AreEqual("Case expressions may only be nested to level 10.", ex.Message);
    }

    [TestMethod]
    public void NestedIif_OverLimit_RaisesMsg125_State2()
    {
        var ex = new Simulation().AssertSqlError($"select {NestedIif(11)}", 125);
        AreEqual(15, ex.Class);
        AreEqual(2, ex.State);
    }

    [TestMethod]
    public void NestedCase_InWhenCondition_CountsTowardLimit()
    {
        // Nesting in a WHEN condition counts identically to a THEN/ELSE result
        // (probe-confirmed). Build eleven CASEs each nested in the next WHEN.
        var predicate = "1=1";
        for (var i = 0; i < 11; i++)
            predicate = $"case when {predicate} then 1 else 0 end = 1";
        AreEqual(4, new Simulation().AssertSqlError($"select 1 where {predicate}", 125).State);
    }

    [TestMethod]
    public void NestedCase_AcrossSubqueryBoundary_DoesNotReset()
    {
        // The CASE-depth counter is not reset by a scalar-subquery boundary
        // (probe-confirmed): eight outer CASE levels + a subquery + five inner
        // = thirteen lexical levels, tripping Msg 125.
        var sql = $"select {NestedCase(8, $"(select {NestedCase(5)})")}";
        AreEqual(4, new Simulation().AssertSqlError(sql, 125).State);
    }

    [TestMethod]
    public void NestedCase_MixedWithIif_SharesCounter_StateFromInnermost()
    {
        // Ten IIF levels inside one CASE — the eleventh (innermost-entered)
        // construct is an IIF, so State is 2 despite the outermost being CASE.
        AreEqual(2, new Simulation().AssertSqlError($"select {NestedCase(1, NestedIif(10))}", 125).State);
    }

    // --- Shared budget: parens and subqueries draw from one pool. ---

    [TestMethod]
    public void SharedBudget_ParensPlusSubquery_RaisesMsg191()
    {
        // 480 parens (480 units) wrapping a 4-deep subquery (24 units) = 504 > 500.
        var sql = $"select {new string('(', 480)}{NestedSubqueries(4)}{new string(')', 480)}";
        _ = new Simulation().AssertSqlError(sql, 191);
    }
}
