using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The inline join-algorithm hint — <c>MERGE</c> / <c>HASH</c> / <c>LOOP</c> /
/// <c>REMOTE</c> between the join type and <c>JOIN</c>. Accept-and-discard: it
/// names the physical operator real should use, and the simulator picks its
/// own, so it can never change an answer. The statement-level
/// <c>OPTION (MERGE JOIN)</c> spelling is separate and was already accepted.
/// Grammar probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class InlineJoinHintTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table jh1 (id int not null primary key, v int null);
            create table jh2 (id int not null primary key, w int null);
            insert jh1 values (1, 10), (2, 20), (3, 30);
            insert jh2 values (2, 200), (3, 300), (4, 400);
            """);
        return sim;
    }

    [TestMethod]
    // Every hint against every join type — real accepts all of them, including
    // the combinations that look implausible.
    [DataRow("inner merge join", 2)]
    [DataRow("inner hash join", 2)]
    [DataRow("inner loop join", 2)]
    [DataRow("inner remote join", 2)]
    [DataRow("left merge join", 3)]
    [DataRow("left outer merge join", 3)]
    [DataRow("left hash join", 3)]
    [DataRow("left loop join", 3)]
    [DataRow("right hash join", 3)]
    [DataRow("right outer hash join", 3)]
    [DataRow("right loop join", 3)]
    [DataRow("full merge join", 4)]
    [DataRow("full outer merge join", 4)]
    [DataRow("full loop join", 4)]
    public void TheHintIsAcceptedAndDoesNotChangeTheAnswer(string join, int expected)
    {
        var sim = Seeded();
        AreEqual(expected, sim.ExecuteScalar($"select count(*) from jh1 a {join} jh2 b on b.id = a.id"));
    }

    [TestMethod]
    public void AHintedJoinMatchesItsUnhintedForm()
    {
        var sim = Seeded();
        AreEqual(
            sim.ExecuteScalar("select sum(a.v) from jh1 a left join jh2 b on b.id = a.id"),
            sim.ExecuteScalar("select sum(a.v) from jh1 a left merge join jh2 b on b.id = a.id"));
    }

    [TestMethod]
    public void TheHintComposesWithATableHintAndWithAChain()
    {
        var sim = Seeded();
        AreEqual(2, sim.ExecuteScalar(
            "select count(*) from jh1 a with (nolock) inner hash join jh2 b with (nolock) on b.id = a.id"));
        AreEqual(2, sim.ExecuteScalar(
            "select count(*) from jh1 a inner merge join jh2 b on b.id = a.id inner loop join jh1 c on c.id = b.id"));
    }

    [TestMethod]
    public void AnUnrecognizedWordIsMsg155()
    {
        // Real's own error for this position, distinct from the generic
        // syntax error.
        var sim = Seeded();
        var ex = sim.AssertSqlError("select count(*) from jh1 a inner nonsense join jh2 b on b.id = a.id", 155);
        Assert.Contains("'nonsense' is not a recognized join option.", ex.Message);
    }

    [TestMethod]
    public void TwoHintsAreRefused()
        => _ = Seeded().AssertSqlError(
            "select count(*) from jh1 a inner merge hash join jh2 b on b.id = a.id", 102);

    [TestMethod]
    public void CrossJoinTakesNoHint()
        // Real refuses `CROSS MERGE JOIN`, naming MERGE as a keyword.
        => _ = Seeded().AssertSqlError("select count(*) from jh1 a cross merge join jh2 b", 156);

    [TestMethod]
    [DataRow("merge join")]
    [DataRow("hash join")]
    [DataRow("loop join")]
    public void TheJoinTypeKeywordIsRequired(string join)
        // A bare hint with no INNER / LEFT / RIGHT / FULL is refused. Both
        // engines refuse; real names the hint word (and, for the reserved
        // MERGE, reports Msg 156) where this names `join` — recorded in
        // backlog.md rather than reproduced, since only the naming differs.
        => _ = Seeded().AssertSqlError($"select count(*) from jh1 a {join} jh2 b on b.id = a.id", 102);
}
