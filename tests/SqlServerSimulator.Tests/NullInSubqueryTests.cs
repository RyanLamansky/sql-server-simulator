using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a NULL left side answers against <c>[NOT] IN (SELECT …)</c>. The answer
/// turns on the body's <b>emptiness</b> alone, not on its values: <c>x IN (S)</c>
/// is an OR of <c>x = s</c> over S, an OR over no elements is FALSE whatever x
/// is, and one over a non-empty S is UNKNOWN because every comparison against
/// NULL is.
/// <para>
/// Probe matrix <c>N7.nn</c> / <c>N7b.nn</c>, run against SQL Server 2025 on
/// 2026-08-05. Each three-valued case is read through a CASE that reports
/// <c>T</c> / <c>F</c> / <c>U</c> distinctly, so FALSE and UNKNOWN can't be
/// confused with each other.
/// </para>
/// </summary>
[TestClass]
public sealed class NullInSubqueryTests
{
    private static Simulation WithValues()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table v (k int not null primary key, x int null);
            insert v values (1, 10), (2, null);
            """);
        return sim;
    }

    private static string ThreeValued(Simulation sim, string predicate)
        => (string)sim.ExecuteScalar($"select case when {predicate} then 'T' when not ({predicate}) then 'F' else 'U' end")!;

    /// <summary>N7.01 / N7.02 — an empty body settles it either way.</summary>
    [TestMethod]
    public void NullLeftSide_AgainstAnEmptyBody_IsFalse()
    {
        var sim = WithValues();
        AreEqual("F", ThreeValued(sim, "null in (select x from v where 1 = 0)"));
        AreEqual("T", ThreeValued(sim, "null not in (select x from v where 1 = 0)"));
    }

    /// <summary>N7.03 / N7.04 — a non-empty body is UNKNOWN in both directions.</summary>
    [TestMethod]
    public void NullLeftSide_AgainstANonEmptyBody_IsUnknown()
    {
        var sim = WithValues();
        AreEqual("U", ThreeValued(sim, "null in (select x from v where k = 1)"));
        AreEqual("U", ThreeValued(sim, "null not in (select x from v where k = 1)"));
    }

    /// <summary>N7.05 — a body whose only row is NULL is still non-empty.</summary>
    [TestMethod]
    public void NullLeftSide_AgainstABodyOfOnlyNulls_IsUnknown()
        => AreEqual("U", ThreeValued(WithValues(), "null in (select x from v where k = 2)"));

    /// <summary>N7.06 — a typed NULL variable reads the same as the literal.</summary>
    [TestMethod]
    public void TypedNullVariable_AgainstAnEmptyBody_IsFalse()
        => AreEqual("F", (string)WithValues().ExecuteScalar("""
            declare @n int = null;
            select case when @n in (select x from v where 1 = 0) then 'T'
                        when not (@n in (select x from v where 1 = 0)) then 'F' else 'U' end
            """)!);

    /// <summary>N7.12 — a VALUES-backed body behaves identically.</summary>
    [TestMethod]
    public void NullLeftSide_AgainstAnEmptyValuesBody_IsFalse()
        => AreEqual("F", ThreeValued(WithValues(), "null in (select y from (values (1)) t(y) where 1 = 0)"));

    /// <summary>N7b.06 — <c>TOP 0</c> is one more way to be empty.</summary>
    [TestMethod]
    public void NullLeftSide_AgainstATopZeroBody_IsFalse()
        => AreEqual("F", ThreeValued(WithValues(), "null in (select top 0 x from v)"));

    /// <summary>
    /// N7.08 — the WHERE-predicate reading: both rows survive <c>NOT IN</c>
    /// against an empty body, the NULL-valued one included.
    /// </summary>
    [TestMethod]
    public void NotInAgainstAnEmptyBody_KeepsEveryRow()
        => AreEqual(2, WithValues().ExecuteScalar("select count(*) from v where x not in (select x from v where 1 = 0)"));

    /// <summary>N7.07 — and none survives <c>IN</c>.</summary>
    [TestMethod]
    public void InAgainstAnEmptyBody_KeepsNoRow()
        => AreEqual(0, WithValues().ExecuteScalar("select count(*) from v where x in (select x from v where 1 = 0)"));

    /// <summary>
    /// N7.09 row 7 — per correlation key: an outer row whose key no inner row
    /// carries has an empty group, so its NULL left side reads FALSE (and
    /// <c>NOT IN</c> TRUE) rather than UNKNOWN. The other nine rows of the
    /// matrix are unchanged and live in <c>SemiJoinDecorrelationTests</c>.
    /// </summary>
    [TestMethod]
    public void NullLeftSide_OverAnEmptyCorrelationGroup_IsFalse()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table i (k int null, v int null);
            create table o (id int not null primary key, k int null, v int null);
            insert i values (1, 10), (1, 20), (2, 30), (2, null), (null, 50), (5, null);
            insert o values (7, null, null), (3, 1, null), (10, 5, null);
            """);
        AreEqual("F", (string)sim.ExecuteScalar("""
            select case when o.v in (select i.v from i where i.k = o.k) then 'T'
                        when not (o.v in (select i.v from i where i.k = o.k)) then 'F' else 'U' end
            from o where o.id = 7
            """)!);
        // A NULL left side over a *non-empty* group stays UNKNOWN (ids 3 and 10).
        AreEqual("U,U", sim.ExecuteScalar("""
            select string_agg(r, ',') within group (order by id) from (
                select o.id as id,
                       case when o.v in (select i.v from i where i.k = o.k) then 'T'
                            when not (o.v in (select i.v from i where i.k = o.k)) then 'F' else 'U' end as r
                from o where o.id in (3, 10)) x
            """));
    }
}
