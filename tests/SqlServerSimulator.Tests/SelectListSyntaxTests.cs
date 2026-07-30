using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A select list has to project something. A keyword standing where the first
/// element belongs raises <strong>Msg 156</strong> naming that keyword, rather
/// than silently ending the projection and leaving a zero-column SELECT
/// behind. All wordings probe-confirmed against SQL Server 2025, which echoes
/// the keyword in the source's own casing.
/// </summary>
[TestClass]
public sealed class SelectListSyntaxTests
{
    private static void AssertBlocked(string commandText, string keyword)
        => new Simulation().AssertSqlError(commandText, 156, $"Incorrect syntax near the keyword '{keyword}'.");

    /// <summary>
    /// These read as a statement keyword opening an element. The projection
    /// parser treats such keywords as a statement boundary so back-to-back
    /// statements need no semicolon — which is right once an element exists,
    /// and wrong when the list is still empty.
    /// </summary>
    [TestMethod]
    public void StatementKeyword_AsTheFirstElement_RaisesMsg156()
    {
        AssertBlocked("select update(c1)", "update");
        AssertBlocked("select delete(c1)", "delete");
        AssertBlocked("select insert", "insert");
    }

    /// <summary>
    /// A clause keyword is the same shape. <c>SELECT FROM t</c> is the worst
    /// of them: it used to parse to a zero-column SELECT and was accepted
    /// outright whenever the table was empty.
    /// </summary>
    [TestMethod]
    public void ClauseKeyword_AsTheFirstElement_RaisesMsg156()
    {
        AssertBlocked("create table t (c1 int); select from t", "from");
        AssertBlocked("select where", "where");
        AssertBlocked("select order by c1", "order");
    }

    /// <summary>
    /// End-of-input isn't a keyword, so the bare form keeps its Msg 102 —
    /// probe-confirmed that real distinguishes the two.
    /// </summary>
    [TestMethod]
    public void BareSelect_KeepsMsg102()
        => new Simulation().AssertSqlError("select", 102, "Incorrect syntax near 'select'.");

    /// <summary>
    /// The reserved words that legitimately open an element — function-call
    /// heads, CASE, the niladic constants, NULL — must still parse.
    /// </summary>
    [TestMethod]
    public void ReservedWordsThatOpenAnElement_StillParse()
    {
        var sim = new Simulation();
        AreEqual("a", sim.ExecuteScalar("select left('ab', 1)"));
        AreEqual(1, sim.ExecuteScalar("select case when 1 = 1 then 1 end"));
        AreEqual(1, sim.ExecuteScalar("select convert(int, '1')"));
        AreEqual(1, sim.ExecuteScalar("select coalesce(1, 2)"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select null"));
    }

    /// <summary>
    /// The set quantifiers and TOP precede the first element rather than
    /// being one, so the guard has to let them through.
    /// </summary>
    [TestMethod]
    public void SetQuantifiersAndTop_StillParse()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (c1 int); insert t values (7)");
        AreEqual(7, sim.ExecuteScalar("select distinct c1 from t"));
        AreEqual(7, sim.ExecuteScalar("select top 1 c1 from t"));
        AreEqual(7, sim.ExecuteScalar("select all c1 from t"));
        AreEqual(7, sim.ExecuteScalar("select * from t"));
    }

    /// <summary>
    /// Once an element exists, a statement keyword still ends the projection
    /// and starts the next statement without a separating semicolon — the
    /// behavior the guard must not regress.
    /// </summary>
    [TestMethod]
    public void BackToBackStatements_WithoutSemicolon_StillParse()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (c1 int); insert t values (7)");
        _ = sim.ExecuteNonQuery("select 1 update t set c1 = 2");
        AreEqual(2, sim.ExecuteScalar("select c1 from t"));
        _ = sim.ExecuteNonQuery("select 1 delete t");
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>A FROM-less SELECT may still carry a trailing clause.</summary>
    [TestMethod]
    public void FromLessSelect_WithTrailingClause_StillParses()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select 1 as x order by x"));
        AreEqual(1, sim.ExecuteScalar("select 1 as x where 1 = 1"));
    }

    // === A keyword blocking a *later* element ===

    /// <summary>
    /// The same rule past the first element: a comma promises an element, so a
    /// keyword standing there is Msg 156 rather than an early end to the list.
    /// </summary>
    [TestMethod]
    [DataRow("select 1, update(c1) from t", "update")]
    [DataRow("select 1, delete(c1) from t", "delete")]
    [DataRow("select 1, from t", "from")]
    [DataRow("select 1, where", "where")]
    [DataRow("select 1, order by c1", "order")]
    public void KeywordAfterAComma_RaisesMsg156(string commandText, string keyword) =>
        new Simulation().AssertSqlError(
            $"create table t (c1 int); {commandText}", 156, $"Incorrect syntax near the keyword '{keyword}'.");

    /// <summary>A comma with nothing after it at all reports at the comma.</summary>
    [TestMethod]
    public void TrailingComma_RaisesMsg102AtTheComma() =>
        new Simulation().AssertSqlError("select 1,", 102, "Incorrect syntax near ','.");

    // === Alias swallow ===

    /// <summary>
    /// One bare token after an element is its alias; a second is one too many.
    /// The simulator used to read it as a further column, silently turning
    /// <c>SELECT 1 xyz 2</c> into a two-column result.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 xyz 2", "2")]
    [DataRow("select 1 xyz abc", "abc")]
    [DataRow("select 1 2", "2")]
    [DataRow("select 1 as x 2", "2")]
    [DataRow("select 1 x 2 y", "2")]          // reports the first offender
    public void ValueAfterACompleteElement_RaisesMsg102(string commandText, string near) =>
        new Simulation().AssertSqlError(commandText, 102, $"Incorrect syntax near '{near}'.");

    /// <summary>
    /// A string literal is a legal postfix alias, so the error lands on the
    /// one after it — and names it without the literal's own quotes, matching
    /// real's wording.
    /// </summary>
    [TestMethod]
    public void StringLiteralAfterAnAliasedElement_RaisesMsg102WithoutQuotes() =>
        new Simulation().AssertSqlError("select 'p' y 'q'", 102, "Incorrect syntax near 'q'.");

    [TestMethod]
    public void ThreeBareTokens_ReportTheThird()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b int)");
        sim.AssertSqlError("select a b c from t", 102, "Incorrect syntax near 'c'.");
    }

    /// <summary>The single postfix alias each element is allowed still works.</summary>
    [TestMethod]
    public void SinglePostfixAlias_StillParses()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int, b int); insert t values (7, 8)");
        AreEqual(7, sim.ExecuteScalar("select a b from t"));
        AreEqual(7, sim.ExecuteScalar("select a as x from t"));
        AreEqual(2, sim.ExecuteScalar("select s.y from (select 1 x, 2 y) s"));
        AreEqual("lit", sim.ExecuteScalar("select 'lit' alias, a from t"));
    }
}
