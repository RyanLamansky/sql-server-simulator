using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The rows a <c>WHERE</c> keeps when its disjuncts name <em>different</em>
/// seekable columns of one table (<c>a = 1 OR b = 2</c>) — the shape a
/// single-column equality family can't express, and which the reader now answers
/// as a union of seeks deduplicated by row address rather than a full scan. The
/// narrowing is result-transparent (the whole original WHERE stays as the
/// residual filter), so every expectation here is the value **SQL Server 2025
/// returned for the identical table**, probed before the pass was written: the
/// NULL-bearing columns, the mixed same/cross-column disjunction, the AND group
/// inside the OR, the join, the correlated inner, the view, and the two DML
/// verbs. Which access path each shape resolves to is asserted in
/// <c>SqlServerSimulator.Tests.Internal</c>'s <c>IndexSeekTests</c>.
/// </summary>
[TestClass]
public sealed class CrossColumnOrSeekTests
{
    /// <summary>
    /// Eight rows over three indexed nullable columns, carrying every case the
    /// disjunction has to separate: a row matching only the first disjunct, only
    /// the second, both, and neither — with NULLs in each column, including one
    /// row NULL in both (which matches no probe and reads UNKNOWN in the
    /// residual, so it must not come back).
    /// </summary>
    private static Simulation Eight()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, a int null, b int null, c int null, v int not null);
            create index ix_a on t (a);
            create index ix_b on t (b);
            create index ix_c on t (c);
            insert t values
              (1, 1, 9, 3, 100), (2, 9, 2, 3, 200), (3, 1, 2, 3, 300), (4, null, 2, 3, 400),
              (5, 1, null, 3, 500), (6, null, null, 3, 600), (7, 2, 9, 9, 700), (8, 9, 9, 9, 800)
            """);
        return sim;
    }

    /// <summary>The first column of every row, comma-joined in the order read.</summary>
    private static string Ids(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var ids = new List<string>();
        foreach (var record in reader.EnumerateRecords())
            ids.Add(record.GetValue(0).ToString()!);
        return string.Join(",", ids);
    }

    // ---- the disjunction itself -----------------------------------------------

    /// <summary>
    /// The shape the union serves. Rows 1 / 3 / 5 match on <c>a</c>, rows 2 / 3 /
    /// 4 on <c>b</c>; row 3 matches both and comes back once; row 6 is NULL in
    /// both and doesn't.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_WithNullsInBothColumns_AnswersEveryMatchOnce() =>
        AreEqual("1,2,3,4,5", Ids(Eight(), "select id from t where a = 1 or b = 2 order by id"));

    /// <summary>
    /// Two disjuncts on one column and a third on another: not a family a single
    /// column's multi-value probe can hold, so it is the union's, and the answer
    /// is the union of all three.
    /// </summary>
    [TestMethod]
    public void MixedSameAndCrossColumnOr_AnswersTheUnionOfAllThree() =>
        AreEqual("1,2,3,4,5,7", Ids(Eight(), "select id from t where a = 1 or a = 2 or b = 2 order by id"));

    /// <summary>
    /// A disjunct that is itself an <c>AND</c>: only rows satisfying the whole
    /// group join the answer, so row 2 (<c>b = 2</c> and <c>c = 3</c>) is in and
    /// row 8 is not — the group's terms that the probe didn't key on are still
    /// enforced by the residual WHERE.
    /// </summary>
    [TestMethod]
    public void AndGroupInsideOr_EnforcesTheWholeGroup() =>
        AreEqual("2,3,4,7", Ids(Eight(), "select id from t where a = 2 or (b = 2 and c = 3) order by id"));

    /// <summary>Heavily overlapping disjuncts: every <c>a = 1</c> row also carries <c>c = 3</c>.</summary>
    [TestMethod]
    public void OverlappingDisjuncts_ReturnNoDuplicateRow() =>
        AreEqual("1,2,3,4,5,6", Ids(Eight(), "select id from t where a = 1 or c = 3 order by id"));

    /// <summary>A disjunction nothing matches is an empty result, not every row.</summary>
    [TestMethod]
    public void CrossColumnOr_MatchingNothing_AnswersEmpty() =>
        AreEqual("", Ids(Eight(), "select id from t where a = 77 or b = 88"));

    /// <summary>Variables on the value sides read exactly as literals do.</summary>
    [TestMethod]
    public void CrossColumnOr_WithVariableValueSides_AnswersTheSameRows() =>
        AreEqual("1,2,3,4,5", Ids(
            Eight(), "declare @x int = 1, @y int = 2; select id from t where a = @x or b = @y order by id"));

    /// <summary>
    /// A NULL value side is never equal to anything, so that disjunct
    /// contributes nothing and the other one answers alone.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_WithANullValueSide_AnswersTheOtherDisjunctAlone() =>
        AreEqual("2,3,4", Ids(
            Eight(), "declare @n int = null; select id from t where a = @n or b = 2 order by id"));

    /// <summary>The aggregate projector narrows through the same path.</summary>
    [TestMethod]
    public void CrossColumnOr_UnderAnAggregate_CountsTheSameRows() =>
        AreEqual(5, Eight().ExecuteScalar<int>("select count(*) from t where a = 1 or b = 2"));

    /// <summary>
    /// A disjunct on an unindexed column can't be probed, so the whole
    /// disjunction stays a scan — and answers identically.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_WithAnUnseekableDisjunct_AnswersTheSameRows() =>
        AreEqual("1,3,5,7", Ids(Eight(), "select id from t where a = 1 or v = 700 order by id"));

    /// <summary>
    /// A table with no index at all offers no probe anywhere; the answer is the
    /// same one the indexed table gives.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_OverAnUnindexedHeap_AnswersTheSameRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table h (id int not null, a int null, b int null);
            insert h values (1, 1, 9), (2, 9, 2), (3, 1, 2), (4, null, 2), (5, 1, null), (6, null, null)
            """);
        AreEqual("1,2,3,4,5", Ids(sim, "select id from h where a = 1 or b = 2 order by id"));
    }

    /// <summary>
    /// The narrowing is per execution, not baked into a cached plan: the same
    /// statement text run three times probes each execution's own pair of
    /// values.
    /// </summary>
    [TestMethod]
    public void CachedPlanRunThreeTimes_ProbesEachExecutionsOwnValues()
    {
        var sim = Eight();
        using var connection = sim.CreateOpenConnection();
        var answers = new List<string>();
        foreach (var (a, b) in ((int, int)[])[(1, 2), (2, 9), (1, 2)])
        {
            using var command = connection.CreateCommand(
                "select id from t where a = @a or b = @b order by id", ("@a", a), ("@b", b));
            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            foreach (var record in reader.EnumerateRecords())
                ids.Add(record.GetInt32(0).ToString());
            answers.Add(string.Join(",", ids));
        }

        CollectionAssert.AreEqual((string[])["1,2,3,4,5", "1,7,8", "1,2,3,4,5"], answers);
    }

    // ---- string columns: the probe carries the column's own comparison rules ----

    private static Simulation Named()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table s (id int not null primary key, nm varchar(20) null, tag varchar(20) null);
            create index ix_s_nm on s (nm);
            create index ix_s_tag on s (tag);
            insert s values (1, 'ann', 'x'), (2, 'bob', 'y'), (3, 'ann', 'y'), (4, null, 'x'), (5, 'cid', null)
            """);
        return sim;
    }

    [TestMethod]
    public void CrossColumnOr_OverStringColumns_AnswersTheSameRows() =>
        AreEqual("1,3,4", Ids(Named(), "select id from s where nm = 'ann' or tag = 'x' order by id"));

    /// <summary>
    /// ANSI trailing-space padding still decides the compare: <c>'ann   '</c>
    /// matches <c>'ann'</c>, so a probe keyed on the padded literal must find the
    /// unpadded rows — the seek promotes and compares exactly as <c>=</c> does.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_WithATrailingSpacePaddedLiteral_MatchesTheUnpaddedRows() =>
        AreEqual("1,2,3", Ids(Named(), "select id from s where nm = 'ann   ' or tag = 'y' order by id"));

    // ---- the disjunction under a join, a correlated inner, and a body ----------

    private static Simulation Joined()
    {
        var sim = Eight();
        _ = sim.ExecuteNonQuery("""
            create table j (jid int not null primary key, tid int not null);
            insert j values (10, 1), (11, 2), (12, 3), (13, 7), (14, 8)
            """);
        return sim;
    }

    /// <summary>
    /// The narrowed source is the <em>second</em> written one, which is the case
    /// the join reorder reads a candidate count for. Whichever order the chain
    /// folds in, the rows are the same.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_OnANonLeftmostJoinedSource_AnswersTheSameRows() =>
        AreEqual("10,11,12", Ids(
            Joined(), "select j.jid from j join t on t.id = j.tid where t.a = 1 or t.b = 2 order by j.jid"));

    /// <summary>
    /// A disjunct naming the <em>other</em> source can't be probed against this
    /// one; the join still answers what real answers.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_NamingBothJoinedSources_AnswersTheSameRows() =>
        AreEqual("10,11,12", Ids(
            Joined(), "select j.jid from j join t on t.id = j.tid where t.a = 1 or j.jid = 11 order by j.jid"));

    /// <summary>
    /// A value side reading the sibling source varies row by row, so it anchors
    /// nothing — the rows are unchanged either way.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_WithASiblingValueSide_AnswersTheSameRows() =>
        AreEqual("10,12", Ids(
            Joined(), "select j.jid from j join t on t.id = j.tid where t.a = 1 or t.b = j.jid order by j.jid"));

    /// <summary>
    /// Both disjuncts read the enclosing row, so the inner re-probes per outer
    /// row — the correlated shape the seek exists for, now reachable through an
    /// OR.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_InACorrelatedInner_AnswersPerOuterRow() =>
        AreEqual("10,11", Ids(
            Joined(), "select jid from j where exists (select 1 from t where t.a = j.tid or t.b = j.tid) order by jid"));

    /// <summary>
    /// Written above a view, the disjunction is pushed into the body as the
    /// same OR and answers there.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_AboveAView_AnswersTheSameRows()
    {
        var sim = Eight();
        _ = sim.ExecuteNonQuery("create view vt as select id, a, b, c, v from t");
        AreEqual("1,2,3,4,5", Ids(sim, "select id from vt where a = 1 or b = 2 order by id"));
    }

    /// <summary>The derived-table spelling of the same read.</summary>
    [TestMethod]
    public void CrossColumnOr_AboveADerivedTable_AnswersTheSameRows() =>
        AreEqual("1,2,3,4,5", Ids(Eight(), "select id from (select * from t) d where a = 1 or b = 2 order by id"));

    // ---- the mutation verbs ---------------------------------------------------

    /// <summary>
    /// A <c>DELETE</c> whose WHERE is a cross-column OR removes exactly the rows
    /// the equivalent SELECT returns, and no others.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_InADeleteWhere_RemovesExactlyThoseRows()
    {
        var sim = Eight();
        AreEqual(5, sim.ExecuteNonQuery("delete t where a = 1 or b = 2"));
        AreEqual("6,7,8", Ids(sim, "select id from t order by id"));
    }

    /// <summary>
    /// An <c>UPDATE</c> across three columns' worth of disjunction touches each
    /// matched row once — a row matched by two disjuncts must not be updated
    /// twice, which the value here would show.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_InAnUpdateWhere_WritesEachMatchedRowOnce()
    {
        var sim = Eight();
        AreEqual(5, sim.ExecuteNonQuery("update t set v = v * 2 where a = 1 or b = 2"));
        AreEqual("200,400,600,800,1000,600,700,800", Ids(sim, "select v from t order by id"));
    }

    /// <summary>
    /// The <c>OUTPUT</c> clause reports the same matched set. Sorted here
    /// because an OUTPUT stream carries no order (there is no ORDER BY to hang
    /// one on) and a union emits each disjunct's matches in turn, so the rows
    /// arrive in probe order rather than the heap order a scan happened to give.
    /// </summary>
    [TestMethod]
    public void CrossColumnOr_InAnUpdateWithOutput_ReportsEveryMatchedRow() =>
        AreEqual("1,2,3,4,5", string.Join(",", Ids(
            Eight(), "update t set v = v * 2 output deleted.id where a = 1 or b = 2").Split(',').Order()));
}
