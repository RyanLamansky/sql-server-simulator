using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The answers an equi-correlated <c>EXISTS</c> / <c>NOT EXISTS</c> /
/// <c>[NOT] IN (SELECT …)</c> keeps once its outer side outgrows the per-row
/// execution and the site answers from the hash semi / anti-join its
/// decorrelated plan built (<c>Parser/SemiJoinIndex.cs</c>). Every fixture here
/// drives a 300-row outer over the same ten-case NULL matrix — outer key NULL,
/// inner key NULL, inner projection NULL, empty group, group whose only value
/// is NULL — so both sides of the switch evaluate the same rows, and each
/// expected value below was probed against SQL Server 2025 (see
/// <c>docs/claude/subqueries.md</c>).
/// <para>
/// The strategy the switch actually took is asserted in
/// <c>SqlServerSimulator.Tests.Internal.SemiJoinStrategyTests</c>, where the
/// trace and the per-row execution counter are reachable.
/// </para>
/// </summary>
[TestClass]
public sealed class SemiJoinDecorrelationTests
{
    /// <summary>
    /// Ten outer cases replicated 30 times (300 rows, well past the switch's
    /// 128-row threshold) against a six-row inner:
    /// <list type="bullet">
    /// <item>1 (k=1, v=10) — matching value.</item>
    /// <item>2 (k=1, v=99) — group of non-NULL values, no match.</item>
    /// <item>3 (k=1, v=NULL) — NULL left side.</item>
    /// <item>4 (k=2, v=30) — matching value in a group that also holds a NULL.</item>
    /// <item>5 (k=2, v=99) — no match in a group that holds a NULL.</item>
    /// <item>6 (k=NULL, v=10) — NULL outer key.</item>
    /// <item>7 (k=NULL, v=NULL) — NULL outer key and NULL left side.</item>
    /// <item>8 (k=4, …) — key no inner row carries.</item>
    /// <item>9 / 10 (k=5, …) — group whose only value is NULL.</item>
    /// </list>
    /// The inner's own <c>(NULL, 50)</c> row is the NULL inner key, which
    /// equi-matches nothing — including the NULL outer keys.
    /// </summary>
    private static Simulation NullMatrix()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table outer_rows (id int not null, k int null, v int null);
            create table inner_rows (k int null, v int null);
            insert inner_rows values (1, 10), (1, 20), (2, 30), (2, null), (null, 50), (5, null);
            declare @n int = 0;
            while @n < 30
            begin
                insert outer_rows values
                    (1, 1, 10), (2, 1, 99), (3, 1, null), (4, 2, 30), (5, 2, 99),
                    (6, null, 10), (7, null, null), (8, 4, 10), (9, 5, 10), (10, 5, null);
                set @n += 1;
            end
            """);
        return sim;
    }

    private const string ExistsWhere = "exists (select 1 from inner_rows i where i.k = outer_rows.k)";
    private const string InWhere = "outer_rows.v in (select i.v from inner_rows i where i.k = outer_rows.k)";

    // ---- the four forms over the NULL matrix (each count probed on real) ----

    /// <summary>Ids 1-5, 9, 10 have a group; the two NULL outer keys and the absent key 4 don't.</summary>
    [TestMethod]
    public void Exists_NullMatrix_KeepsTheSevenMatchingCases()
        => AreEqual(210, NullMatrix().ExecuteScalar($"select count(*) from outer_rows where {ExistsWhere}"));

    /// <summary>The complement — the NULL outer keys (6, 7) and the unmatched key (8).</summary>
    [TestMethod]
    public void NotExists_NullMatrix_KeepsTheThreeEmptyCases()
        => AreEqual(90, NullMatrix().ExecuteScalar($"select count(*) from outer_rows where not {ExistsWhere}"));

    /// <summary>Only the two rows whose value is in their own key's group (1 and 4).</summary>
    [TestMethod]
    public void In_NullMatrix_KeepsOnlyTheMatchingValues()
        => AreEqual(60, NullMatrix().ExecuteScalar($"select count(*) from outer_rows where {InWhere}"));

    /// <summary>
    /// The definite misses only: 2 (a group of non-NULL values with no match),
    /// 6 and 8 (an empty group — a miss however many NULLs other groups hold),
    /// and 7, whose NULL left side meets an empty group: an OR over no elements
    /// is FALSE whatever the left side, so <c>NOT IN</c> is TRUE (probed).
    /// A NULL left side over a non-empty group (3, 10) and a group holding a
    /// NULL (5, 9) are UNKNOWN, which WHERE excludes.
    /// </summary>
    [TestMethod]
    public void NotIn_NullMatrix_KeepsOnlyTheDefiniteMisses()
        => AreEqual("2,6,7,8", NullMatrix().ExecuteScalar($"""
            select string_agg(convert(varchar(10), id), ',') within group (order by id)
            from (select distinct id from outer_rows where outer_rows.v not in
                  (select i.v from inner_rows i where i.k = outer_rows.k)) x
            """));

    /// <summary>
    /// The NULL flag is per correlation key, not global: id 2's key holds two
    /// non-NULL values and answers a definite FALSE for <c>IN</c>, while id 9's
    /// key holds only a NULL and answers UNKNOWN — even though both are probed
    /// against one shared structure.
    /// </summary>
    [TestMethod]
    public void NotIn_SawNullIsPerKey()
    {
        var sim = NullMatrix();
        AreEqual(30, sim.ExecuteScalar($"select count(*) from outer_rows where id = 2 and outer_rows.v not in (select i.v from inner_rows i where i.k = outer_rows.k)"));
        AreEqual(0, sim.ExecuteScalar($"select count(*) from outer_rows where id = 9 and outer_rows.v not in (select i.v from inner_rows i where i.k = outer_rows.k)"));
    }

    // ---- the switch is answer-preserving across its own threshold ----

    /// <summary>
    /// Every one of an id's 30 copies has to answer identically whether it was
    /// evaluated per row (the first 128) or against the built structure — so
    /// the distinct (id, answer) pairs stay at ten, one per case. A mismatch
    /// across the threshold would double some case's pair count.
    /// </summary>
    private static void AssertStableAcrossThreshold(string predicate)
        => AreEqual(10, NullMatrix().ExecuteScalar($"""
            select count(*) from (
                select distinct id, case when {predicate} then 1 else 0 end as answered
                from outer_rows) x
            """));

    [TestMethod]
    public void Exists_AnswersIdenticallyBeforeAndAfterTheSwitch()
        => AssertStableAcrossThreshold(ExistsWhere);

    [TestMethod]
    public void NotExists_AnswersIdenticallyBeforeAndAfterTheSwitch()
        => AssertStableAcrossThreshold($"not {ExistsWhere}");

    [TestMethod]
    public void In_AnswersIdenticallyBeforeAndAfterTheSwitch()
        => AssertStableAcrossThreshold(InWhere);

    [TestMethod]
    public void NotIn_AnswersIdenticallyBeforeAndAfterTheSwitch()
        => AssertStableAcrossThreshold($"outer_rows.v not in (select i.v from inner_rows i where i.k = outer_rows.k)");

    // ---- shapes the transform declines, answering identically ----

    /// <summary>A residual conjunct reading the outer row keeps the per-row path — and the same answer.</summary>
    [TestMethod]
    public void ResidualReadsOuter_StillAnswersPerRow()
        => AreEqual(60, NullMatrix().ExecuteScalar("""
            select count(*) from outer_rows
            where exists (select 1 from inner_rows i where i.k = outer_rows.k and i.v = outer_rows.v)
            """));

    /// <summary>
    /// A correlation hidden in a nested subquery declines at the build and
    /// re-answers per row: ids 1, 2, 4 and 5 keep a row under their own key's
    /// bound, while the NULL-valued ids (3, 7, 10) compare against a NULL bound
    /// and the rest have no group at all.
    /// </summary>
    [TestMethod]
    public void NestedSubqueryReadsOuter_StillAnswersPerRow()
        => AreEqual(120, NullMatrix().ExecuteScalar("""
            select count(*) from outer_rows
            where exists (select 1 from inner_rows i
                          where i.k = outer_rows.k
                            and i.v < (select max(x.v) from outer_rows x where x.id = outer_rows.id) + 100)
            """));

    // ---- key promotion and collation ----

    /// <summary>
    /// A correlation pair spanning two integer widths hashes at the type the
    /// <c>=</c> would have promoted to, so every row still matches.
    /// </summary>
    [TestMethod]
    public void CrossWidthIntegerKey_MatchesUnderPromotion()
        => AreEqual(200, new Simulation().ExecuteScalar("""
            create table outer_rows (id int not null identity primary key, k bigint not null);
            create table inner_rows (k smallint not null);
            insert inner_rows values (1), (2), (3);
            declare @n int = 0;
            while @n < 200 begin insert outer_rows (k) values (@n % 3 + 1); set @n += 1; end;
            select count(*) from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k)
            """));

    /// <summary>
    /// The hash keys on the database's own collation, so a case-insensitive
    /// column matches across case exactly as the per-row comparison did.
    /// </summary>
    [TestMethod]
    public void StringKey_FoldsCaseWithTheColumnCollation()
        => AreEqual(200, new Simulation().ExecuteScalar("""
            create table outer_rows (id int not null identity primary key, k varchar(10) not null);
            create table inner_rows (k varchar(10) not null);
            insert inner_rows values ('ALPHA'), ('BETA');
            declare @n int = 0;
            while @n < 200 begin insert outer_rows (k) values (case when @n % 2 = 0 then 'alpha' else 'Beta' end); set @n += 1; end;
            select count(*) from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k)
            """));

    /// <summary>ANSI trailing-space padding folds in the hash too — <c>'ab' = 'ab   '</c>.</summary>
    [TestMethod]
    public void StringKey_FoldsTrailingSpaces()
        => AreEqual(200, new Simulation().ExecuteScalar("""
            create table outer_rows (id int not null identity primary key, k varchar(10) not null);
            create table inner_rows (k varchar(10) not null);
            insert inner_rows values ('ab');
            declare @n int = 0;
            while @n < 200 begin insert outer_rows (k) values ('ab   '); set @n += 1; end;
            select count(*) from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k)
            """));

    /// <summary>Two correlation columns key the structure as a tuple.</summary>
    [TestMethod]
    public void CompositeCorrelationKey_MatchesOnBothColumns()
        => AreEqual(100, new Simulation().ExecuteScalar("""
            create table outer_rows (id int not null identity primary key, a int not null, b int not null);
            create table inner_rows (a int not null, b int not null);
            insert inner_rows values (0, 0), (1, 1);
            declare @n int = 0;
            while @n < 200 begin insert outer_rows (a, b) values (@n % 2, 0); set @n += 1; end;
            select count(*) from outer_rows o where exists (select 1 from inner_rows i where i.a = o.a and i.b = o.b)
            """));

    // ---- the mutation forms ----

    [TestMethod]
    public void DeleteWhereExists_RemovesExactlyTheMatchedRows()
    {
        var sim = NullMatrix();
        _ = sim.ExecuteNonQuery($"delete from outer_rows where {ExistsWhere}");
        AreEqual(90, sim.ExecuteScalar("select count(*) from outer_rows"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from outer_rows where id in (1, 2, 3, 4, 5, 9, 10)"));
    }

    [TestMethod]
    public void UpdateWhereNotExists_TouchesExactlyTheUnmatchedRows()
    {
        var sim = NullMatrix();
        _ = sim.ExecuteNonQuery($"update outer_rows set v = -1 where not {ExistsWhere}");
        AreEqual(90, sim.ExecuteScalar("select count(*) from outer_rows where v = -1"));
        AreEqual(90, sim.ExecuteScalar("select count(*) from outer_rows where v = -1 and id in (6, 7, 8)"));
    }

    // ---- the drive-side transform for a small uncorrelated IN subquery ----

    /// <summary>
    /// 200 rows against a three-value set the read drives from: the answer is
    /// the two rows whose id is in it, the NULL value matching nothing.
    /// </summary>
    private static Simulation DriveSide()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, label varchar(10) null);
            create table vals (v int null);
            insert vals values (2), (5), (null);
            declare @n int = 1;
            while @n <= 200 begin insert t values (@n, 'row'); set @n += 1; end
            """);
        return sim;
    }

    [TestMethod]
    public void SmallUncorrelatedInSubquery_MatchesTheNonNullValues()
        => AreEqual(2, DriveSide().ExecuteScalar("select count(*) from t where t.id in (select v from vals)"));

    /// <summary>
    /// The <c>IN</c> conjunct stays in the residual WHERE, so a NULL among the
    /// values keeps the negated form's three-valued answer: every row is
    /// UNKNOWN, and none survives.
    /// </summary>
    [TestMethod]
    public void NotInSubqueryWithANullValue_MatchesNothing()
        => AreEqual(0, DriveSide().ExecuteScalar("select count(*) from t where t.id not in (select v from vals)"));

    [TestMethod]
    public void NotInSubqueryWithoutANullValue_MatchesTheComplement()
        => AreEqual(198, DriveSide().ExecuteScalar("select count(*) from t where t.id not in (select v from vals where v is not null)"));

    /// <summary>An empty value set matches nothing (and its negation everything).</summary>
    [TestMethod]
    public void EmptyUncorrelatedInSubquery_MatchesNothing()
        => AreEqual(0, DriveSide().ExecuteScalar("select count(*) from t where t.id in (select v from vals where v > 1000)"));

    /// <summary>Past the 64-value cap the read keeps its scan — and the same answer.</summary>
    [TestMethod]
    public void UncorrelatedInSubqueryPastTheCap_KeepsTheSameAnswer()
        => AreEqual(100, DriveSide().ExecuteScalar("select count(*) from t where t.id in (select x.id from t x where x.id <= 100)"));

    /// <summary>A correlated <c>IN</c> can't drive the read, and answers as it always did.</summary>
    [TestMethod]
    public void CorrelatedInSubquery_KeepsTheSameAnswer()
        => AreEqual(2, DriveSide().ExecuteScalar("select count(*) from t where t.id in (select v from vals where v = t.id)"));

    /// <summary>
    /// The driven read still applies the rest of the WHERE: the value set
    /// selects the candidates, the residual decides.
    /// </summary>
    [TestMethod]
    public void DrivenReadStillAppliesTheRestOfTheWhere()
        => AreEqual(1, DriveSide().ExecuteScalar("select count(*) from t where t.id in (select v from vals) and t.id > 3"));
}
