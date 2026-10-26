using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Expression depth limits. Two failure modes must never reach the host: a
/// .NET stack overflow (uncatchable, process-fatal) from deep recursion, and
/// a silently-wrong result. So flat left-associative chains parse and evaluate
/// iteratively (no per-term recursion), as deep as real's compile takes them,
/// while genuinely-nested shapes are bounded by SQL Server's own graceful
/// structural errors:
/// <list type="bullet">
/// <item>Msg 191 (Class 15) — parens, function calls, scalar subqueries,
/// unary signs and derived tables draw on one budget, each where real's own
/// limit is (probed 2026-10-24 against SQL Server 2025): 1,015 parens or
/// stacked signs, 1,013 function calls, 168 subqueries, 100 derived tables
/// and 339 chained signed factors pass, one more fails.</item>
/// <item>Msg 125 (Class 15) — CASE / IIF nested past ten levels; State 4 for
/// CASE, State 2 for IIF (the construct entered at the eleventh level).</item>
/// <item>Msg 8631 (Class 17) — a flat chain past the depth real's compile
/// takes, and the runtime stack probe, for a thread whose stack runs out before
/// real's limit: the depths real accepts need more stack than a default thread
/// has, so those run on a 64 MB one here.</item>
/// </list>
/// </summary>
[TestClass]
public sealed class ExpressionNestingLimitTests
{
    private const int DeepStack = 64 * 1024 * 1024;

    private const int SmallStack = 512 * 1024;

    private static string AdditionChain(int terms) =>
        $"select ({string.Join(" + ", Enumerable.Repeat("1", terms))})";

    // --- Flat left-associative chains: an iterative parse and evaluation, as
    // deep as real's compile takes them. ---

    [TestMethod]
    public void AdditionChain_ModerateDepth_Evaluates()
        => AreEqual(200, new Simulation().ExecuteScalar(AdditionChain(200)));

    /// <summary>
    /// A chain runs as deep as real's compile takes it, past which real's
    /// stack gives out with Msg 8631: 3,030 terms of <c>+</c> at the top,
    /// parentheses counting nothing, one fewer in a function call or a
    /// comparison, two fewer in a scalar subquery, and a shift chain two fewer
    /// (probed 2026-10-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    [DataRow("select {0}", " + ", "1", 3030)]
    [DataRow("select ((((({0})))))", " + ", "1", 3030)]
    [DataRow("select abs({0})", " + ", "1", 3029)]
    [DataRow("select 1 where {0} > 0", " + ", "1", 3029)]
    [DataRow("select (select {0})", " + ", "1", 3028)]
    [DataRow("select len({0})", " || ", "'a'", 3029)]
    [DataRow("select {0}", " << ", "1", 3028)]
    public void FlatChain_RunsAsDeepAsRealsCompile(string shape, string op, string term, int terms)
    {
        string Chain(int count) => string.Format(CultureInfo.InvariantCulture, shape, string.Join(op, Enumerable.Repeat(term, count)));
        IsNull(Extensions.ExecuteScalarOnStack(Chain(terms), DeepStack).Failure);
        AreEqual(8631, Extensions.ExecuteScalarOnStack(Chain(terms + 1), DeepStack).Failure.ErrorNumber());
    }

    /// <summary>
    /// A chain of postfix operations runs as deep as real's compile takes it
    /// too (probed 2026-10-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    [DataRow("select cast(cast('2020-01-01' as datetimeoffset){0} as varchar(40))", " at time zone 'UTC'", 3026)]
    [DataRow("select cast(cast('/1/2/3/' as hierarchyid){0} as varchar(40))", ".GetAncestor(0)", 3026)]
    public void PostfixChain_RunsAsDeepAsRealsCompile(string shape, string postfix, int count)
    {
        string Chain(int length) => string.Format(CultureInfo.InvariantCulture, shape, string.Concat(Enumerable.Repeat(postfix, length)));
        IsNull(Extensions.ExecuteScalarOnStack(Chain(count), DeepStack).Failure);
        AreEqual(8631, Extensions.ExecuteScalarOnStack(Chain(count + 1), DeepStack).Failure.ErrorNumber());
    }

    [TestMethod]
    [Timeout(60000)]
    public void ConcatChain_AtRealsDepth_StopsGrowingAtVarcharsLimit()
        => AreEqual(new string('a', 8000), new Simulation().ExecuteScalar(
            "select " + string.Join(" + ", Enumerable.Repeat("'aaa'", 3030))));

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
        var (result, failure) = Extensions.ExecuteScalarOnStack(
            "select 1 where " + string.Join(" and ", Enumerable.Repeat("1=1", 50000)), SmallStack);
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
        var (result, failure) = Extensions.ExecuteScalarOnStack(
            "select 1 where " + string.Concat(Enumerable.Repeat("not ", 50000)) + "1=1", SmallStack);
        IsNull(failure, "deep NOT stack should evaluate, not raise");
        AreEqual(1, result);
    }

    // A flat chain nests a level per term down its left side, so whatever
    // reads the whole chain walks it without recursing — a `+` checked for
    // constants inside a folded call, a `||` evaluated — or probes the stack as
    // it recurses (`<<`): on a small thread a chain as deep as real's allows
    // answers or raises Msg 8631, never ends the process.
    [TestMethod]
    [Timeout(60000)]
    [DataRow("select len({0})", " + ", "'a'", 3029)]
    [DataRow("select len({0})", " || ", "'a'", 3029)]
    [DataRow("select {0}", " << ", "1", 3028)]
    public void OperatorChain_AtRealsDepth_OnSmallStackThread_IsGracefulNeverProcessDeath(string shape, string op, string term, int terms)
    {
        var sql = string.Format(CultureInfo.InvariantCulture, shape, string.Join(op, Enumerable.Repeat(term, terms)));
        var failure = Extensions.ExecuteScalarOnStack(sql, SmallStack).Failure;
        IsTrue(failure is null || failure.Number == 8631, $"raised {failure?.Number}");
    }

    // --- Stacked / chained unary signs: genuinely nested. ---
    // A sign parses the following multiplicative chain as its operand (SQL
    // Server's additive binding for `+` / `-`), so both a stack of signs and a
    // chain of signed factors recurse once per sign. Real takes 1,015 stacked
    // signs, as many as parens, but only 339 signed factors in a chain.

    [TestMethod]
    public void SignedMultiplicativeChain_ModerateDepth_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar(
            "select " + string.Join(" * ", Enumerable.Repeat("-1", 300))));

    [TestMethod]
    [Timeout(60000)]
    [DataRow(339, null)]
    [DataRow(340, 191)]
    public void SignedMultiplicativeChain_MeetsRealsLimit(int factors, int? error)
    {
        var (result, failure) = Extensions.ExecuteScalarOnStack("select " + string.Join(" * ", Enumerable.Repeat("-1", factors)), DeepStack);
        AreEqual(error, failure.ErrorNumber());
        if (error is null)
            AreEqual(-1, result);
    }

    [TestMethod]
    [Timeout(60000)]
    [DataRow(1015, null)]
    [DataRow(1016, 191)]
    public void StackedSigns_MeetRealsLimit(int signs, int? error)
    {
        var (result, failure) = Extensions.ExecuteScalarOnStack("select " + string.Concat(Enumerable.Repeat("- ", signs)) + "1", DeepStack);
        AreEqual(error, failure.ErrorNumber());
        if (error is null)
            AreEqual(-1, result);
    }

    // Past real's limit on a thread too small to reach it, whichever comes
    // first raises — the budget's Msg 191 or the stack probe's Msg 8631 —
    // and the process survives.
    [TestMethod]
    [Timeout(60000)]
    [DataRow("stacked")]
    [DataRow("chained")]
    public void Signs_OnSmallStackThread_AreGracefulNeverProcessDeath(string shape)
    {
        var sql = shape == "stacked"
            ? "select " + string.Concat(Enumerable.Repeat("- ", 20000)) + "1"
            : "select " + string.Join(" * ", Enumerable.Repeat("-1", 20000));
        var failure = Extensions.ExecuteScalarOnStack(sql, SmallStack).Failure;
        IsTrue(failure.ErrorNumber() is 191 or 8631, $"raised {failure.ErrorNumber()}");
    }

    // --- Nested parens: shared budget, Msg 191. ---

    [TestMethod]
    public void NestedParens_WithinLimit_Evaluates()
        => AreEqual(1, new Simulation().ExecuteScalar($"select {new string('(', 200)}1{new string(')', 200)}"));

    [TestMethod]
    [Timeout(60000)]
    public void NestedParens_AtRealsLimit_Evaluates()
    {
        var (result, failure) = Extensions.ExecuteScalarOnStack($"select {new string('(', 1015)}1{new string(')', 1015)}", DeepStack);
        IsNull(failure);
        AreEqual(1, result);
    }

    [TestMethod]
    [Timeout(60000)]
    public void NestedParens_PastRealsLimit_RaisesMsg191()
    {
        var (_, failure) = Extensions.ExecuteScalarOnStack($"select {new string('(', 1016)}1{new string(')', 1016)}", DeepStack);
        var ex = IsInstanceOfType<SimulatedSqlException>(failure);
        AreEqual(191, ex.Number);
        AreEqual(15, ex.Class);
        AreEqual("Some part of your SQL statement is nested too deeply. Rewrite the query or break it up into smaller queries.", ex.Message);
    }

    // --- Nested scalar subqueries, function calls and derived tables. ---

    private static string NestedSubqueries(int depth)
    {
        var q = "select 1";
        for (var i = 0; i < depth; i++)
            q = $"select ({q})";
        return q;
    }

    private static string NestedDerivedTables(int depth, string innermost = "select 1 as x")
    {
        var q = innermost;
        for (var i = 0; i < depth; i++)
            q = $"select * from ({q}) as d{i}";
        return q;
    }

    [TestMethod]
    [Timeout(60000)]
    [DataRow("subqueries", 168, null)]
    [DataRow("subqueries", 169, 191)]
    [DataRow("functions", 1013, null)]
    [DataRow("functions", 1014, 191)]
    [DataRow("derived tables", 100, null)]
    [DataRow("derived tables", 101, 191)]
    public void NestedQueriesAndCalls_MeetRealsLimit(string construct, int depth, int? error)
    {
        var sql = construct switch
        {
            "subqueries" => NestedSubqueries(depth),
            "functions" => "select " + string.Concat(Enumerable.Repeat("abs(", depth)) + "1" + new string(')', depth),
            _ => NestedDerivedTables(depth),
        };
        var (result, failure) = Extensions.ExecuteScalarOnStack(sql, DeepStack);
        AreEqual(error, failure.ErrorNumber());
        if (error is null)
            AreEqual(1, result);
    }

    [TestMethod]
    [Timeout(60000)]
    [DataRow("functions", SmallStack)]
    [DataRow("derived tables", SmallStack / 2)]
    public void NestedQueriesAndCalls_OnSmallStackThread_RaiseMsg8631NotProcessDeath(string construct, int stack)
    {
        // The frames run the stack out below real's limit, so the probe
        // converts the overflow into Msg 8631 rather than faulting the process.
        var sql = construct == "functions"
            ? "select " + string.Concat(Enumerable.Repeat("abs(", 5000)) + "1" + new string(')', 5000)
            : NestedDerivedTables(100);
        AreEqual(8631, Extensions.ExecuteScalarOnStack(sql, stack).Failure.ErrorNumber());
    }

#if !DEBUG
    // A default-size thread (1 MB on Windows) holds real's limit for nested
    // derived tables and subqueries, whose levels keep their frames small for
    // it; a Debug build's frames are unoptimized and run out sooner.
    [TestMethod]
    [Timeout(60000)]
    [DataRow("subqueries")]
    [DataRow("derived tables")]
    public void NestedQueries_ReachRealsLimit_OnADefaultSizedStack(string construct)
    {
        var (result, failure) = Extensions.ExecuteScalarOnStack(construct == "subqueries" ? NestedSubqueries(168) : NestedDerivedTables(100), 1024 * 1024);
        IsNull(failure);
        AreEqual(1, result);
    }
#endif

    // A value-selecting form types each of its arms once, so one nested in its
    // own first argument compiles in time linear in the depth, where typing the
    // arms again to find the one naming the result made it exponential.
    [TestMethod]
    [Timeout(20000)]
    [DataRow("isnull({0}, 0)")]
    [DataRow("coalesce({0}, 0)")]
    [DataRow("choose(1, {0}, 0)")]
    [DataRow("greatest({0}, 0)")]
    public void ValueSelectingForm_NestedInItsFirstArm_CompilesInLinearTime(string form)
    {
        var expression = "1";
        for (var level = 0; level < 60; level++)
            expression = string.Format(CultureInfo.InvariantCulture, form, expression);
        AreEqual(1, new Simulation().ExecuteScalar($"select {expression}"));
    }

    // A built-in types each argument once, so one nested through its own
    // argument compiles in time linear in the depth, where typing an argument
    // again for a slot or result rule doubled the work at every level.
    [TestMethod]
    [Timeout(20000)]
    [DataRow("trim({0})", "' a '", "a")]
    [DataRow("json_modify({0}, '$.a', 1)", "'{}'", """{"a":1}""")]
    [DataRow("json_query({0})", """'{"a":1}'""", """{"a":1}""")]
    [DataRow("""json_value(concat('{"a":', {0}, '}'), '$.a')""", "'1'", "1")]
    [DataRow("""json_value('{"a":"$.a"}', {0})""", "'$.a'", "$.a")]
    [DataRow("stuff('ab', len({0}), 1, '')", "'a'", "b")]
    [DataRow("datediff(day, 0, dateadd(day, {0}, 0))", "1", 1)]
    public void BuiltIn_NestedThroughItsOwnArgument_CompilesInLinearTime(string form, string seed, object expected)
    {
        var expression = seed;
        for (var level = 0; level < 60; level++)
            expression = form.Replace("{0}", expression, StringComparison.Ordinal);
        AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));
    }

    // A speculative read of a query's FROM ahead of its select list, retried in
    // order when it failed, read every nested level's FROM twice — an error at
    // the bottom of 30 nested derived tables raised 2^31 times.
    [TestMethod]
    [Timeout(20000)]
    public void AnErrorUnderNestedDerivedTables_RaisesOnce()
        => _ = new Simulation().AssertSqlError(NestedDerivedTables(30, "select 1 as x from nosuchtable"), 208);

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
        AreEqual("Case expressions may only be nested to level 10.", ex.Errors[0].Message);
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

    // --- Shared budget: the constructs draw from one pool, as real's mixtures
    // probed 2026-10-24 against SQL Server 2025 land. ---

    [TestMethod]
    [Timeout(60000)]
    [DataRow("derived", 50, 500, null)]
    [DataRow("derived", 50, 600, 191)]
    [DataRow("derived", 90, 100, null)]
    [DataRow("signs", 300, 100, null)]
    [DataRow("signs", 330, 50, 191)]
    public void SharedBudget_MixturesLandWhereRealsDo(string outer, int outerDepth, int parens, int? error)
    {
        var innermost = $"select {new string('(', parens)}1{new string(')', parens)} as x";
        var sql = outer == "derived"
            ? NestedDerivedTables(outerDepth, innermost)
            : $"select {new string('(', parens)}" + string.Join(" * ", Enumerable.Repeat("-1", outerDepth)) + new string(')', parens);
        AreEqual(error, Extensions.ExecuteScalarOnStack(sql, DeepStack).Failure.ErrorNumber());
    }

    // --- A chain of UNION ALLs is as long as it is deep, which real doesn't
    // count as nesting: it runs 10,000 branches. ---

    [TestMethod]
    [Timeout(60000)]
    public void UnionAllChain_OfThousands_RunsOnADefaultSizedStack()
    {
        var (result, failure) = Extensions.ExecuteScalarOnStack(
            "select count(*) from (" + string.Join(" union all ", Enumerable.Range(0, 3000).Select(i => $"select {i.ToInvariantString()} as x")) + ") u",
            1024 * 1024);
        IsNull(failure);
        AreEqual(3000, result);
    }

    /// <summary>
    /// Parsing a chain is linear in its branches, where each operator copying
    /// every branch to its left made 20,000 take seconds; real runs them
    /// (probed 2026-10-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    public void UnionAllChain_OfTwentyThousand_ParsesInLinearTime()
        => AreEqual(20000, new Simulation().ExecuteScalar(
            "select count(*) from (" + string.Join(" union all ", Enumerable.Range(0, 20000).Select(i => $"select {i.ToInvariantString()} as x")) + ") u"));

    /// <summary>
    /// A chain of CTEs, each reading the last, hands its projection demand
    /// down the whole chain: real reads 850 of them (probed 2026-10-25 against
    /// SQL Server 2025), and past the thread's stack the hand-down is Msg 8631
    /// rather than a process abort.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public void CteChain_ReadsRealsLength_AndRaisesMsg8631PastTheStack()
    {
        var sql = "with c0 as (select 1 as x), "
            + string.Join(", ", Enumerable.Range(1, 849).Select(i => $"c{i.ToInvariantString()} as (select x from c{(i - 1).ToInvariantString()})"))
            + " select x from c849";
        AreEqual(1, Extensions.ExecuteScalarOnStack(sql, DeepStack).Result);
        AreEqual(8631, Extensions.ExecuteScalarOnStack(sql, SmallStack).Failure.ErrorNumber());
    }

    // --- Nesting inside another language's text: XQuery, full-text conditions. ---

    private static string Nested(string open, string inner, string close, int depth) =>
        string.Concat(Enumerable.Repeat(open, depth)) + inner + string.Concat(Enumerable.Repeat(close, depth));

    /// <summary>
    /// An XQuery expression nests as deep as real's — 3,000 parentheses read
    /// (probed 2026-10-25 against SQL Server 2025) — and past the thread's
    /// stack is Msg 8631, real's own answer at 10,000, never a process abort:
    /// as it parses, and as it evaluates a tree the parse took in, which
    /// needs more stack per level (a 3,000-sign chain on a 1 MB thread).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public void XQueryNesting_ReadsRealsDepth_AndRaisesMsg8631PastTheStack()
    {
        AreEqual("<r/>", Extensions.ExecuteScalarOnStack($"declare @x xml = N'<r/>'; select cast(@x.query('{Nested("(", "/r", ")", 3000)}') as nvarchar(max))", DeepStack).Result);
        AreEqual(8631, Extensions.ExecuteScalarOnStack($"declare @x xml = N'<r/>'; select cast(@x.query('{Nested("(", "/r", ")", 10000)}') as nvarchar(max))", SmallStack).Failure.ErrorNumber());
        AreEqual(8631, Extensions.ExecuteScalarOnStack($"declare @x xml = N'<r/>'; select @x.value('{Nested("- ", "1", "", 10000)}', 'int')", SmallStack).Failure.ErrorNumber());
        AreEqual(8631, Extensions.ExecuteScalarOnStack($"declare @x xml = N'<r/>'; select @x.value('{Nested("- ", "1", "", 3000)}', 'int')", 1024 * 1024).Failure.ErrorNumber());
    }

    /// <summary>
    /// A full-text condition's parentheses nest through the stack probe too:
    /// real's 497 levels read, and past the thread's stack is Msg 8631 rather
    /// than a process abort (real refuses a condition nested past its own
    /// limit as too complex, Msg 9937, which isn't modeled yet).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public void ContainsNesting_RaisesMsg8631PastTheStack()
    {
        const string Setup = "create table t (id int not null constraint pk_t primary key, c nvarchar(100)); insert t values (1, N'alpha beta'); create fulltext catalog cat as default; create fulltext index on t (c) key index pk_t; ";
        AreEqual(1, Extensions.ExecuteScalarOnStack(Setup + $"select count(*) from t where contains(c, '{Nested("(", "alpha", ")", 497)}')", DeepStack).Result);
        AreEqual(8631, Extensions.ExecuteScalarOnStack(Setup + $"select count(*) from t where contains(c, '{Nested("(", "alpha", ")", 1900)}')", SmallStack).Failure.ErrorNumber());
    }
}
