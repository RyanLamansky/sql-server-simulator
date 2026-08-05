using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a syntax error names when the batch simply stops. Real reports the last
/// token it consumed rather than an empty slot, and it refuses an argument list
/// or a parenthesized expression the input never closed — probed against
/// SQL Server 2025 (2026-08-05) across the whole family. The token is named as
/// it was written, so the lowercase SQL below reports lowercase names.
/// </summary>
[TestClass]
public sealed class EndOfBatchTokenTests
{
    private static void AssertNear(string commandText, string expectedName)
        => new Simulation().AssertSqlError(commandText, 102, $"Incorrect syntax near '{expectedName}'.");

    private static void AssertNonBooleanNear(string commandText, string expectedName)
        => new Simulation().AssertSqlError(
            commandText,
            4145,
            $"An expression of non-boolean type specified in a context where a condition is expected, near '{expectedName}'.");

    // --- An argument list or paren the batch never closed ---

    [TestMethod]
    public void UnclosedFunctionCall_NamesItsLastArgumentToken()
        => AssertNear("select abs(-1", "1");

    [TestMethod]
    public void UnclosedNestedFunctionCall_NamesTheInnermostLastToken()
        => AssertNear("select abs(abs(-1", "1");

    [TestMethod]
    public void FunctionCallMissingItsCloser_NamesTheStrayToken()
        => AssertNear("select abs(-1 x", "x");

    [TestMethod]
    public void UnclosedParenthesizedExpression_NamesItsLastToken()
        => AssertNear("select (1", "1");

    [TestMethod]
    public void UnclosedInList_NamesItsLastToken()
        => AssertNear("select 1 where 1 in (1", "1");

    [TestMethod]
    public void UnclosedSubqueryInInList_NamesItsLastToken()
        => AssertNear("select 1 from (values (1)) v(a) where a in (select 1", "1");

    [TestMethod]
    public void UnclosedDerivedTable_NamesItsLastToken()
        => AssertNear("select * from (select 1 as a", "a");

    [TestMethod]
    public void ClosedCall_StillRuns()
        => AreEqual(1, new Simulation().ExecuteScalar("select abs(-1)"));

    // --- Msg 4145's own near-token ---

    [TestMethod]
    public void NonBooleanAtEndOfBatch_NamesTheExpressionItself()
        => AssertNonBooleanNear("if 1", "1");

    [TestMethod]
    public void NonBooleanStringAtEndOfBatch_NamesTheLiteralBody()
        => AssertNonBooleanNear("if 'abc'", "abc");

    [TestMethod]
    public void NonBooleanConstantAtEndOfBatch_NamesTheConstant()
        => AssertNonBooleanNear("if @@rowcount", "@@rowcount");

    [TestMethod]
    public void NonBooleanWhereAtEndOfBatch_NamesTheExpression()
        => AssertNonBooleanNear("select 1 where 1", "1");

    /// <summary>
    /// The parens of a paren-wrapped value are consumed on the way in, but real
    /// names what follows them.
    /// </summary>
    [TestMethod]
    public void ParenthesizedNonBoolean_NamesTheTokenAfterTheClosingParen()
        => AssertNonBooleanNear("if (1) print 'x'", "print");

    [TestMethod]
    public void DoublyParenthesizedNonBoolean_NamesTheTokenAfterBothParens()
        => AssertNonBooleanNear("if ((1)) print 'x'", "print");

    [TestMethod]
    public void ParenthesizedNonBooleanFollowedByBegin_NamesTheBlockKeyword()
        => AssertNonBooleanNear("if (1) begin print 'a' end", "begin");

    [TestMethod]
    public void ParenthesizedNonBooleanFollowedByAnd_NamesTheOperator()
        => AssertNonBooleanNear("select 1 where (1) and 1 = 1", "and");

    /// <summary>With nothing after the group, the closing paren is the last token.</summary>
    [TestMethod]
    public void ParenthesizedNonBooleanAtEndOfBatch_NamesTheClosingParen()
        => AssertNonBooleanNear("select 1 where (1)", ")");

    [TestMethod]
    public void DoublyParenthesizedNonBooleanAtEndOfBatch_NamesTheClosingParen()
        => AssertNonBooleanNear("select 1 where ((1))", ")");
}
