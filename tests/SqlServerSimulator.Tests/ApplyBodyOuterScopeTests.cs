using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A subquery nested inside a FROM-less query body reads the enclosing scope.
/// A body carrying its own FROM installs the scope chain while parsing it, so
/// those shapes always worked; a body with no FROM installed nothing, and the
/// nested subquery's parse then chained through whatever the <em>enclosing</em>
/// parse had left behind — which is why <c>CROSS APPLY (SELECT c.x WHERE EXISTS
/// (… c.k …))</c> reported Msg 207 where live answers.
/// <para>
/// Every expectation below is the live SQL Server 2025 answer (probe matrix
/// <c>N3.nn</c>, run 2026-08-05).
/// </para>
/// </summary>
[TestClass]
public sealed class ApplyBodyOuterScopeTests
{
    /// <summary>Three keys, two of which have order rows (1 has two, 3 has one).</summary>
    private static Simulation WithKeys()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table c (k int not null primary key, x int not null);
            create table o (k int not null, v int not null);
            insert c values (1, 10), (2, 20), (3, 30);
            insert o values (1, 100), (1, 101), (3, 300);
            """);
        return sim;
    }

    /// <summary>N3.01 — the reported shape: EXISTS in a FROM-less APPLY body's WHERE.</summary>
    [TestMethod]
    public void ExistsInAFromlessApplyBody_ReadsTheApplyLeft()
        => AreEqual("1,3", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), c.k), ',') within group (order by c.k)
            from c cross apply (select c.x as x where exists (select 1 from o where o.k = c.k)) a
            """));

    /// <summary>N3.02 — OUTER APPLY keeps the unmatched key, NULL-extended.</summary>
    [TestMethod]
    public void ExistsInAFromlessOuterApplyBody_KeepsTheUnmatchedLeftRow()
        => AreEqual(3, WithKeys().ExecuteScalar("""
            select count(*) from c outer apply (select c.x as x where exists (select 1 from o where o.k = c.k)) a
            """));

    /// <summary>N3.03 — a scalar subquery in the FROM-less body's select list.</summary>
    [TestMethod]
    public void ScalarSubqueryInAFromlessApplyBody_ReadsTheApplyLeft()
        => AreEqual("2,0,1", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), a.n), ',') within group (order by c.k)
            from c cross apply (select (select count(*) from o where o.k = c.k) as n) a
            """));

    /// <summary>N3.04 — an <c>IN (SELECT …)</c> correlating to the APPLY's left side.</summary>
    [TestMethod]
    public void InSubqueryInAFromlessApplyBody_ReadsTheApplyLeft()
        => AreEqual("1,3", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), c.k), ',') within group (order by c.k)
            from c cross apply (select c.x as x where c.k in (select o.k from o where o.v > c.k)) a
            """));

    /// <summary>N3.12 — the NOT EXISTS complement.</summary>
    [TestMethod]
    public void NotExistsInAFromlessApplyBody_ReadsTheApplyLeft()
        => AreEqual("2", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), c.k), ',') within group (order by c.k)
            from c cross apply (select c.x as x where not exists (select 1 from o where o.k = c.k)) a
            """));

    /// <summary>N3.06 — APPLY inside APPLY: the innermost body reads the outermost source.</summary>
    [TestMethod]
    public void ApplyInsideApply_InnerBodySubqueryReadsTheOutermost()
        => AreEqual("2,0,1", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), b.n), ',') within group (order by c.k)
            from c cross apply (select 1 as one) a
                   cross apply (select (select count(*) from o where o.k = c.k) as n) b
            """));

    /// <summary>N3.07 — a subquery inside an APPLY inside a subquery.</summary>
    [TestMethod]
    public void SubqueryInsideApplyInsideSubquery_ReadsThroughEveryLevel()
        => AreEqual("6,0,3", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), t.v), ',') within group (order by t.k)
            from (select c.k as k,
                         (select sum(a.n) from c c2 cross apply (select (select count(*) from o where o.k = c.k) as n) a) as v
                  from c) t
            """));

    /// <summary>N3.09 — two levels of EXISTS inside the body.</summary>
    [TestMethod]
    public void NestedExistsInAFromlessApplyBody_ReadsTheApplyLeft()
        => AreEqual("1,3", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), c.k), ',') within group (order by c.k)
            from c cross apply (
                select c.x as x
                where exists (select 1 from o where exists (select 1 from o o2 where o2.k = c.k and o2.k = o.k))) a
            """));

    /// <summary>N3.11 — the quantified comparison form.</summary>
    [TestMethod]
    public void QuantifiedComparisonInAFromlessApplyBody_ReadsTheApplyLeft()
        => AreEqual("1,3", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), c.k), ',') within group (order by c.k)
            from c cross apply (select c.x as x where c.k = any (select o.k from o)) a
            """));

    /// <summary>
    /// N3.05 / N3.08 / N3.10 — the shapes whose body carries its own FROM,
    /// which worked before and must keep working.
    /// </summary>
    [TestMethod]
    public void BodiesCarryingTheirOwnFrom_StillRead()
    {
        var sim = WithKeys();
        AreEqual(3, sim.ExecuteScalar("""
            select count(*) from c cross apply (
                select o2.v from o o2 where o2.k = c.k and exists (select 1 from o o3 where o3.k = c.k)) a
            """));
        AreEqual(3, sim.ExecuteScalar("""
            select count(*) from c cross apply (
                select o2.v from o o2 where exists (select 1 from o o3 where o3.k = o2.k and o3.k = c.k)) a
            """));
        AreEqual(2, sim.ExecuteScalar("""
            select count(*) from c cross apply (
                select d.x from (select c.x as x) d where exists (select 1 from o where o.k = c.k)) a
            """));
    }

    /// <summary>
    /// The same chain with no APPLY at all: a FROM-less scalar subquery wrapping
    /// a correlated one. It failed for the same reason and is fixed by the same
    /// change, so it pins the seam rather than the APPLY form.
    /// </summary>
    [TestMethod]
    public void FromlessSubqueryWrappingACorrelatedOne_ReadsTheEnclosingRow()
        => AreEqual("2,0,1", WithKeys().ExecuteScalar("""
            select string_agg(convert(varchar(10), n), ',') within group (order by k) from (
                select c.k as k, (select (select count(*) from o where o.k = c.k)) as n from c) t
            """));
}
