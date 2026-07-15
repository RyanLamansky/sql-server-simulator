using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Expression-nesting limits. Expression parsing recurses per operator and
/// grouping level, and a .NET stack overflow is uncatchable and
/// process-fatal — so pathological depth must surface as SQL Server's own
/// graceful errors instead. Probed against SQL Server 2025 (2026-07-15):
/// a 6000-term <c>1 + 1 + …</c> chain raises Msg 8631 Class 17 ("Server
/// stack limit"), a stack-dependent threshold the simulator mirrors via the
/// runtime's remaining-stack probe (so its threshold likewise scales with
/// the calling thread's stack size, firing near 750 levels on a default
/// 1 MB thread); 2000 nested parens raise Msg 191 Class 15, a structural
/// limit the simulator enforces at 512 so the paren shape reports Msg 191
/// before the stack probe would claim it.
/// </summary>
[TestClass]
public sealed class ExpressionNestingLimitTests
{
    private static string AdditionChain(int terms) =>
        $"select ({string.Join(" + ", Enumerable.Repeat("1", terms))})";

    [TestMethod]
    public void AdditionChain_ModerateDepth_Evaluates()
    {
        AreEqual(200, new Simulation().ExecuteScalar(AdditionChain(200)));
    }

    [TestMethod]
    [Timeout(60000)]
    public void AdditionChain_ExtremeDepth_RaisesMsg8631NotStackOverflow()
    {
        var ex = new Simulation().AssertSqlError(AdditionChain(50000), 8631);
        AreEqual(17, ex.Class);
        AreEqual("Internal error: Server stack limit has been reached. Please look for potentially deep nesting in your query, and try to simplify it.", ex.Message);
    }

    [TestMethod]
    public void NestedParens_WithinLimit_Evaluates()
    {
        AreEqual(1, new Simulation().ExecuteScalar($"select {new string('(', 200)}1{new string(')', 200)}"));
    }

    [TestMethod]
    [Timeout(60000)]
    public void NestedParens_OverLimit_RaisesMsg191()
    {
        var ex = new Simulation().AssertSqlError($"select {new string('(', 513)}1{new string(')', 513)}", 191);
        AreEqual(15, ex.Class);
        AreEqual("Some part of your SQL statement is nested too deeply. Rewrite the query or break it up into smaller queries.", ex.Message);
    }
}
