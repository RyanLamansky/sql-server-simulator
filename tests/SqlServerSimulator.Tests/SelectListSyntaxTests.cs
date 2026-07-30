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
}
