using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavior a subquery keeps once an outer-row-independent inner plan runs
/// once per statement instead of once per outer row: the answers, the NULL
/// rules, the collation and cross-type rules of the hashed <c>IN</c> probe,
/// the per-call-varying built-ins that decline the reuse, and the statement
/// boundary the reuse ends at. The execution counts themselves are asserted in
/// <c>SqlServerSimulator.Tests.Internal</c>, where the counter is reachable.
/// </summary>
[TestClass]
public sealed class UncorrelatedSubqueryTests
{
    private static Simulation WithNumbers()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table outer_rows (id int not null primary key, k int not null);
            create table inner_rows (v int null);
            insert outer_rows values (1, 10), (2, 20), (3, 30), (4, 40);
            insert inner_rows values (20), (40);
            """);
        return sim;
    }

    // ---- scalar (SELECT ...) --------------------------------------------

    /// <summary>
    /// The inner MIN is the same for every outer row, so hoisting it out of the
    /// per-row loop can't change which rows qualify — three of the four keys
    /// are at or above it.
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_InWhere_Uncorrelated_FiltersOnTheOneValue()
        => AreEqual(3, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where o.k >= (select min(v) from inner_rows)"));

    /// <summary>
    /// The select-list form projects the same value into every row (an
    /// aggregate can't wrap a subquery — Msg 130 — so the sum is taken over a
    /// derived table).
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_InSelectList_Uncorrelated_RepeatsPerRow()
        => AreEqual(160, WithNumbers().ExecuteScalar(
            "select sum(x.m) from (select (select max(v) from inner_rows) as m from outer_rows) x"));

    /// <summary>
    /// A correlated inner still sees each outer row: the projected count is the
    /// number of inner values at or below that row's own key, so weighting by
    /// the row's id gives 1×0 + 2×1 + 3×1 + 4×2 rather than four copies of one
    /// row's answer.
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_Correlated_StillVariesPerRow()
        => AreEqual(13, WithNumbers().ExecuteScalar("""
            select sum(x.id * x.c) from (
                select o.id as id, (select count(*) from inner_rows i where i.v <= o.k) as c
                from outer_rows o) x
            """));

    [TestMethod]
    public void ScalarSubquery_EmptyInner_YieldsNull()
        => AreEqual(4, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where (select max(v) from inner_rows where v > 1000) is null"));

    /// <summary>
    /// Msg 512 is per evaluation, so the reuse must not swallow it — the first
    /// outer row raises exactly as it did when every row re-executed.
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_MultipleRows_StillRaises512()
        => _ = WithNumbers().AssertSqlError(
            "select count(*) from outer_rows o where o.k > (select v from inner_rows)", 512);

    // ---- EXISTS ----------------------------------------------------------

    [TestMethod]
    public void Exists_Uncorrelated_AppliesToEveryRow()
        => AreEqual(4, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows where exists (select 1 from inner_rows where v = 20)"));

    [TestMethod]
    public void NotExists_Uncorrelated_AppliesToEveryRow()
        => AreEqual(4, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows where not exists (select 1 from inner_rows where v = 999)"));

    [TestMethod]
    public void Exists_Correlated_StillVariesPerRow()
        => AreEqual(2, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where exists (select 1 from inner_rows i where i.v = o.k)"));

    /// <summary>
    /// The inner's first row is produced without reading the outer row here —
    /// the <c>OR</c> short-circuits before the correlated half — yet the answer
    /// the reuse replays is still the answer every outer row would compute,
    /// because what the plan produced didn't depend on the outer row.
    /// </summary>
    [TestMethod]
    public void Exists_ShortCircuitingOr_MatchesPerRowEvaluation()
        => AreEqual(4, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where exists (select 1 from inner_rows i where 1 = 1 or i.v = o.k)"));

    // ---- IN (SELECT ...) -------------------------------------------------

    [TestMethod]
    public void InSubquery_Uncorrelated_MatchesMembership()
        => AreEqual(2, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where o.k in (select v from inner_rows)"));

    [TestMethod]
    public void NotInSubquery_Uncorrelated_MatchesComplement()
        => AreEqual(2, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where o.k not in (select v from inner_rows)"));

    [TestMethod]
    public void InSubquery_Correlated_StillVariesPerRow()
        => AreEqual(2, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where o.k in (select v from inner_rows i where i.v = o.k)"));

    /// <summary>
    /// A NULL among the inner values makes a miss UNKNOWN rather than false, so
    /// the hashed probe has to carry the <c>sawNull</c> flag beside the set:
    /// <c>IN</c> keeps only the two genuine matches while <c>NOT IN</c> keeps
    /// nothing at all.
    /// </summary>
    [TestMethod]
    public void InSubquery_NullAmongInnerValues_MissIsUnknown()
    {
        var sim = WithNumbers();
        _ = sim.ExecuteNonQuery("insert inner_rows values (null)");
        AreEqual(2, sim.ExecuteScalar("select count(*) from outer_rows o where o.k in (select v from inner_rows)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from outer_rows o where o.k not in (select v from inner_rows)"));
    }

    /// <summary>
    /// A NULL LHS is UNKNOWN whatever the inner holds, so it survives neither
    /// form: only the row that genuinely matches counts for <c>IN</c>, and
    /// nothing at all counts for <c>NOT IN</c>.
    /// </summary>
    [TestMethod]
    public void InSubquery_NullSource_IsUnknown()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table o (k int null);
            create table i (v int not null);
            insert o values (null), (7);
            insert i values (7);
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from o where o.k in (select v from i)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from o where o.k not in (select v from i)"));
    }

    [TestMethod]
    public void InSubquery_EmptyInner_IsFalseAndItsNegationTrue()
    {
        var sim = WithNumbers();
        AreEqual(0, sim.ExecuteScalar("select count(*) from outer_rows o where o.k in (select v from inner_rows where v > 1000)"));
        AreEqual(4, sim.ExecuteScalar("select count(*) from outer_rows o where o.k not in (select v from inner_rows where v > 1000)"));
    }

    /// <summary>
    /// The probe set keys on the promoted type, so a <c>smallint</c> LHS still
    /// finds its <c>bigint</c> partner.
    /// </summary>
    [TestMethod]
    public void InSubquery_PromotesAcrossIntegerWidths()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table s (v smallint not null);
            create table b (v bigint not null);
            insert s values (7), (9);
            insert b values (7), (11);
            select count(*) from s where s.v in (select v from b)
            """));

    /// <summary>
    /// Equality under the database collation is case-insensitive and ignores
    /// trailing spaces, so the hash has to fold both the same way the scan's
    /// <c>=</c> does.
    /// </summary>
    [TestMethod]
    public void InSubquery_HashHonorsCollationAndAnsiPadding()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table needles (s varchar(20) not null);
            create table haystack (s varchar(30) not null);
            insert needles values ('alpha'), ('BETA'), ('gamma  '), ('delta');
            insert haystack values ('ALPHA'), ('beta'), ('gamma');
            select count(*) from needles where needles.s in (select s from haystack)
            """));

    /// <summary>
    /// A case-sensitive column doesn't fold, and the hashed probe must not fold
    /// on its behalf.
    /// </summary>
    [TestMethod]
    public void InSubquery_CaseSensitiveCollation_DoesNotFold()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table needles (s varchar(20) collate SQL_Latin1_General_CP1_CS_AS not null);
            create table haystack (s varchar(20) collate SQL_Latin1_General_CP1_CS_AS not null);
            insert needles values ('alpha'), ('BETA');
            insert haystack values ('ALPHA'), ('BETA');
            select count(*) from needles where needles.s in (select s from haystack)
            """));

    /// <summary>
    /// A cross-family pair converts per value, which is what a bad value raises
    /// on — the hashed probe declines the pair rather than raising while it
    /// builds, so the error still comes from the comparison.
    /// </summary>
    [TestMethod]
    public void InSubquery_UnconvertibleStringAgainstInteger_StillRaises()
        => _ = new Simulation().AssertSqlError("""
            create table needles (v int not null);
            create table haystack (s varchar(20) not null);
            insert needles values (1), (2);
            insert haystack values ('nope');
            select count(*) from needles where needles.v in (select s from haystack)
            """, 245);

    // ---- quantified ANY / SOME / ALL -------------------------------------

    [TestMethod]
    public void Quantified_Any_Uncorrelated_MatchesMembership()
        => AreEqual(3, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where o.k >= any (select v from inner_rows)"));

    [TestMethod]
    public void Quantified_All_Uncorrelated_MatchesMembership()
        => AreEqual(1, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where o.k >= all (select v from inner_rows)"));

    [TestMethod]
    public void Quantified_EmptyInner_KeepsVacuousTruth()
    {
        var sim = WithNumbers();
        AreEqual(4, sim.ExecuteScalar("select count(*) from outer_rows o where o.k > all (select v from inner_rows where v > 1000)"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from outer_rows o where o.k > any (select v from inner_rows where v > 1000)"));
    }

    /// <summary>
    /// A NULL inner value taints <c>ALL</c> to UNKNOWN for the rows that would
    /// otherwise be true, so the materialized values have to keep their NULLs.
    /// </summary>
    [TestMethod]
    public void Quantified_All_NullInnerValue_Taints()
    {
        var sim = WithNumbers();
        _ = sim.ExecuteNonQuery("insert inner_rows values (null)");
        AreEqual(0, sim.ExecuteScalar("select count(*) from outer_rows o where o.k >= all (select v from inner_rows)"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from outer_rows o where o.k >= any (select v from inner_rows)"));
    }

    [TestMethod]
    public void Quantified_Correlated_StillVariesPerRow()
        => AreEqual(2, WithNumbers().ExecuteScalar(
            "select count(*) from outer_rows o where o.k = any (select v from inner_rows i where i.v = o.k)"));

    // ---- per-call-varying built-ins decline the reuse --------------------

    /// <summary>
    /// Probe-confirmed against SQL Server 2025: an uncorrelated
    /// <c>(SELECT TOP 1 NEWID() FROM …)</c> read once per row over a 100-row
    /// outer yields 100 distinct values, so a draw inside the inner plan has to
    /// keep the plan running per row.
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_NewIdInside_StillDrawsPerRow()
        => AreEqual(4, WithNumbers().ExecuteScalar("""
            select count(distinct g) from (
                select (select top 1 newid() from inner_rows) as g from outer_rows) x
            """));

    /// <summary>
    /// <c>RAND()</c> is frozen for the statement on both engines (the same probe
    /// reports one distinct value across the same 100 rows), so no gate applies
    /// and the reuse is invisible.
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_RandInside_StaysStatementFrozen()
        => AreEqual(1, WithNumbers().ExecuteScalar("""
            select count(distinct g) from (
                select (select top 1 rand() from inner_rows) as g from outer_rows) x
            """));

    /// <summary>
    /// The sequence half of the volatility gate is unreachable through a
    /// subquery, because real refuses <c>NEXT VALUE FOR</c> there outright:
    /// Msg 11719 at parse, so the batch is rejected and the sequence is left
    /// where it stood (probed 2026-08-05 — the derived table around it carries
    /// the same refusal). <c>NEWID()</c> is what covers the gate itself.
    /// </summary>
    [TestMethod]
    public void ScalarSubquery_NextValueForInside_IsRejectedWithoutAdvancing()
    {
        var sim = WithNumbers();
        _ = sim.ExecuteNonQuery("create sequence dbo.s as int start with 1 increment by 1");
        _ = sim.AssertSqlError("""
            select count(distinct g) from (
                select (select top 1 next value for dbo.s from inner_rows) as g from outer_rows) x
            """, 11719);
        AreEqual(1, sim.ExecuteScalar("select cast(current_value as int) from sys.sequences where name = 's'"));
    }

    // ---- the reuse ends at the statement ---------------------------------

    /// <summary>
    /// Each statement gets its own frame, so a table that grew between two
    /// statements is re-read by the second — including across the iterations of
    /// a <c>WHILE</c> body, which re-dispatches its statements.
    /// </summary>
    [TestMethod]
    public void ReuseEndsAtTheStatementBoundary()
        => AreEqual(6, new Simulation().ExecuteScalar("""
            create table t (v int not null);
            create table seen (n int not null);
            declare @i int = 0;
            while @i < 3
            begin
                set @i = @i + 1;
                insert t values (@i);
                insert seen select (select count(*) from t) from t where v = @i;
            end
            select sum(n) from seen
            """));

    // ---- DML self-reference (Halloween), probe-confirmed against real ----

    /// <summary>
    /// Probe-confirmed against SQL Server 2025: the inner <c>MAX</c> is the
    /// pre-statement one, so only the two rows below it are updated. A per-row
    /// re-read against the rows already written would raise the max and sweep
    /// the third row in too (<c>SUM</c> 300 rather than 230).
    /// </summary>
    [TestMethod]
    public void Update_ScalarSubqueryOverItsOwnTarget_ReadsPreStatementState()
        => AreEqual(230, new Simulation().ExecuteScalar("""
            create table h (id int not null primary key, v int not null);
            insert h values (1, 10), (2, 20), (3, 30);
            update h set v = 100 where v < (select max(v) from h);
            select sum(v) from h
            """));

    /// <summary>
    /// Probe-confirmed: every row is assigned the pre-statement <c>SUM</c> of 3,
    /// so the table totals 9. A per-row re-read would compound to 17.
    /// </summary>
    [TestMethod]
    public void Update_SetExpressionSubqueryOverItsOwnTarget_ReadsPreStatementState()
        => AreEqual(9, new Simulation().ExecuteScalar("""
            create table h (id int not null primary key, v int not null);
            insert h values (1, 1), (2, 1), (3, 1);
            update h set v = (select sum(v) from h);
            select sum(v) from h
            """));

    /// <summary>
    /// Probe-confirmed: the inner <c>COUNT</c> is the pre-statement 3, so the
    /// three inserted rows are 4 / 5 / 6 and the table totals 21. A per-row
    /// re-read against the growing table would give 24.
    /// </summary>
    [TestMethod]
    public void Insert_SelectSubqueryOverItsOwnTarget_ReadsPreStatementState()
        => AreEqual(21, new Simulation().ExecuteScalar("""
            create table h (v int not null);
            insert h values (1), (2), (3);
            insert h select v + (select count(*) from h) from h;
            select sum(v) from h
            """));

    /// <summary>
    /// The membership form over the statement's own target, locked in beside
    /// the scalar ones: the pre-statement maximum selects one row and updating
    /// it recruits no others.
    /// </summary>
    [TestMethod]
    public void Update_InSubqueryOverItsOwnTarget_ReadsPreStatementState()
        => AreEqual(70, new Simulation().ExecuteScalar("""
            create table h (id int not null primary key, v int not null);
            insert h values (1, 10), (2, 20), (3, 30);
            update h set v = 40 where v in (select max(v) from h);
            select sum(v) from h
            """));

    // ---- correlation through an enclosing lateral scope ------------------

    /// <summary>
    /// The inner plan sits under a <c>CROSS APPLY</c> that re-runs per outer
    /// row, but the subquery itself reads neither scope — the reuse spans the
    /// whole statement rather than one APPLY invocation, and the answer is the
    /// same either way.
    /// </summary>
    [TestMethod]
    public void InSubquery_InsideApply_Uncorrelated_MatchesPerRowEvaluation()
        => AreEqual(2, WithNumbers().ExecuteScalar("""
            select count(*) from outer_rows o
            cross apply (select o.k as k2 where o.k in (select v from inner_rows)) a
            """));

    /// <summary>
    /// The same shape with the subquery reading the APPLY's own output stays
    /// per-row: only the rows whose key is an inner value survive.
    /// </summary>
    [TestMethod]
    public void InSubquery_ReadingApplyOutput_StillVariesPerRow()
        => AreEqual(2, WithNumbers().ExecuteScalar("""
            select count(*) from outer_rows o
            cross apply (select o.k as k2) a
            where exists (select 1 from inner_rows i where i.v = a.k2)
            """));
}
