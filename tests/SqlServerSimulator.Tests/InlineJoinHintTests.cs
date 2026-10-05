using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The inline join-algorithm hint — <c>MERGE</c> / <c>HASH</c> / <c>LOOP</c> /
/// <c>REMOTE</c> between the join type and <c>JOIN</c> — and the statement's
/// <c>OPTION (… JOIN)</c> hints. The simulator picks its own operator, so a
/// hint never changes an answer; what it does reproduce is real's refusals:
/// a plan the hinted algorithms can't build (Msg 8622), an inline hint the
/// OPTION hints exclude (Msg 1042), and the REMOTE restrictions. Probed
/// against SQL Server 2025.
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
    [DataRow("merge join", "Incorrect syntax near the keyword 'join'.")]
    [DataRow("hash join", "Incorrect syntax near 'hash'.")]
    [DataRow("loop join", "Incorrect syntax near 'loop'.")]
    public void TheJoinTypeKeywordIsRequired(string join, string message)
        // A bare hint with no INNER / LEFT / RIGHT / FULL is refused: the
        // unreserved hint word is left over after the alias, and the reserved
        // MERGE opens a statement whose next token is JOIN (probed 2026-09-24).
        => Seeded().AssertSqlError($"select count(*) from jh1 a {join} jh2 b on b.id = a.id", message.Contains("keyword", StringComparison.Ordinal) ? 156 : 102, message);

    /// <summary>
    /// A local hint fixes the join order, which real reports with the
    /// informational Msg 8625 as the statement compiles (probed 2026-10-01
    /// against SQL Server 2025); an unhinted join sends nothing.
    /// </summary>
    [TestMethod]
    [DataRow("inner hash join", 1)]
    [DataRow("full merge join", 1)]
    [DataRow("join", 0)]
    public void TheHintSendsMsg8625(string join, int expected)
    {
        using var connection = (SimulatedDbConnection)Seeded().CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand($"select count(*) from jh1 a {join} jh2 b on b.id = a.id").ExecuteScalar();
        AreEqual(expected, messages.Count(number => number == 8625));
    }

    [TestMethod]
    [DataRow("select count(*) from jh1 a inner hash join jh2 b on b.id > a.id")]
    [DataRow("select count(*) from jh1 a left merge join jh2 b on b.id > a.id")]
    [DataRow("select count(*) from jh1 a inner hash join jh2 b on b.id = 1")]
    [DataRow("select count(*) from jh1 a inner hash join jh2 b on b.id = a.id or b.w = a.v")]
    [DataRow("select count(*) from jh1 a join jh2 b on b.id > a.id option (hash join)")]
    [DataRow("select count(*) from jh1 a cross join jh2 b option (merge join)")]
    [DataRow("select count(*) from jh1 a, jh2 b where b.id > a.id option (hash join)")]
    [DataRow("select count(*) from jh1 a full join jh2 b on b.id > a.id option (hash join)")]
    [DataRow("select count(*) from jh1 a inner merge join jh1 b with (forceseek) on b.id = a.id")]
    public void AnAlgorithmThePredicatesCantBuild_IsMsg8622(string query)
        => _ = Seeded().AssertSqlError(query, 8622);

    [TestMethod]
    [DataRow("select count(*) from jh1 a inner hash join jh2 b on b.id = a.id + 1")]
    [DataRow("select count(*) from jh1 a inner hash join jh2 b on isnull(b.id, 0) = a.id")]
    [DataRow("select count(*) from jh1 a inner merge join jh2 b on b.id > a.id and b.w = a.v")]
    [DataRow("select count(*) from jh1 a left hash join jh2 b on b.id > a.id where b.w = a.v")]
    [DataRow("select count(*) from jh1 a full merge join jh2 b on b.id > a.id")]
    [DataRow("select count(*) from jh1 a, jh2 b where b.id = a.id option (hash join)")]
    [DataRow("select count(*) from jh1 a cross join jh2 b option (loop join)")]
    [DataRow("select count(*) from jh1 a join jh2 b on b.id > a.id option (hash join, loop join)")]
    public void AnAlgorithmThePredicatesBuild_IsTaken(string query)
        => _ = Seeded().ExecuteScalar(query);

    [TestMethod]
    [DataRow("select count(*) from jh1 a inner loop join jh2 b on b.id = a.id option (merge join)", 1042)]
    [DataRow("select count(*) from jh1 a left loop join jh2 b on b.id = a.id option (hash join)", 1042)]
    [DataRow("select count(*) from jh1 a inner remote join jh2 b on b.id = a.id option (hash join)", 1071)]
    [DataRow("select count(*) from jh1 a left remote join jh2 b on b.id = a.id", 1072)]
    public void InlineHintsMeetTheOptionClausesRules(string query, int number)
        => _ = Seeded().AssertSqlError(query, number);

    [TestMethod]
    public void AnInlineHintTheOptionClauseIncludes_IsTaken()
        => AreEqual(2, Seeded().ExecuteScalar("select count(*) from jh1 a inner loop join jh2 b on b.id = a.id option (hash join, loop join)"));

    /// <summary>
    /// Msg 8625 goes once per statement however many joins carry a hint, not
    /// at all under <c>OPTION (FORCE ORDER)</c>, and for a query an
    /// <c>IF</c> tests too (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select count(*) from jh1 a inner hash join jh2 b on b.id = a.id inner loop join jh1 c on c.id = b.id", 1)]
    [DataRow("select count(*) from jh1 a inner hash join jh2 b on b.id = a.id option (force order)", 0)]
    [DataRow("if exists (select 1 from jh1 a inner hash join jh2 b on b.id = a.id) select 1", 1)]
    public void Msg8625_PerStatement(string batch, int expected)
    {
        using var connection = (SimulatedDbConnection)Seeded().CreateOpenConnection();
        var messages = new List<int>();
        connection.InfoMessage += (_, e) => messages.AddRange(e.Errors.Select(error => error.Number));
        _ = connection.CreateCommand(batch).ExecuteScalar();
        AreEqual(expected, messages.Count(number => number == 8625));
    }
}
