using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A postfix <c>COLLATE</c> on a <b>parenthesized value expression</b> sitting
/// where a predicate belongs — <c>WHERE (a + b) COLLATE X LIKE 'ab%'</c>. The
/// leading <c>(</c> is ambiguous between a grouped sub-predicate and a
/// parens-wrapped value on a comparison's left, and only <c>COLLATE</c> settles
/// it: the comparison it belongs to sits past the collation name, out of the
/// disambiguating peek's one-token reach.
/// </summary>
/// <remarks>
/// Every row below was run on both engines against SQL Server 2025 on
/// 2026-08-05. Real accepts the shape in every predicate position and against
/// every comparison form, and refuses the postfix on a parenthesized
/// <em>boolean</em> — which is what makes routing that spelling to the value
/// path cost nothing.
/// </remarks>
[TestClass]
public sealed class ParenthesizedCollateTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a varchar(10), b varchar(10), n int);
            insert t values ('a', 'b', 1);
            """);
        return sim;
    }

    private static object? Count(string predicateOrClause) =>
        Seeded().ExecuteScalar($"select count(*) from t where {predicateOrClause}");

    /// <summary>Each comparison form the collated group can sit on the left of.</summary>
    [TestMethod]
    public void EveryComparisonFormAcceptsACollatedGroupOnTheLeft()
    {
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS like 'ab%'"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS not like 'zz%'"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS = 'ab'"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS in ('ab')"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS not in ('zz')"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS between 'a' and 'z'"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS not between 'y' and 'z'"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS is not null"));
    }

    /// <summary>Any parenthesized value expression, however deep, and a scalar subquery too.</summary>
    [TestMethod]
    public void TheGroupMayBeAnyValueExpression()
    {
        AreEqual(1, Count("(a) collate Latin1_General_CI_AS = 'a'"));
        AreEqual(1, Count("((a)) collate Latin1_General_CI_AS like 'a%'"));
        AreEqual(1, Count("(case when 1 = 1 then a else b end) collate Latin1_General_CI_AS like 'a%'"));
        AreEqual(1, Count("(select max(a) from t) collate Latin1_General_CI_AS like 'a%'"));
        AreEqual(1, Count("not (a + b) collate Latin1_General_CI_AS like 'zz%'"));
        AreEqual(1, Count("((a) collate Latin1_General_CI_AS like 'a%')"));
        AreEqual(1, Count("(a) collate Latin1_General_CI_AS like 'a%' and b = 'b'"));
    }

    /// <summary>The collation actually applies, so a CS variant of the same shape excludes the row.</summary>
    [TestMethod]
    public void TheCollationIsTheOneThatDecidesTheMatch()
    {
        AreEqual(1, Count("(a) collate Latin1_General_CI_AS in ('A')"));
        AreEqual(0, Count("(a) collate Latin1_General_CS_AS in ('A')"));
        AreEqual(1, Count("(a + b) collate Latin1_General_CI_AS like 'AB%'"));
        AreEqual(0, Count("(a + b) collate Latin1_General_CS_AS like 'AB%'"));
    }

    /// <summary>Every other clause that takes a predicate or an expression takes it too.</summary>
    [TestMethod]
    public void TheShapeWorksInEveryClauseThatTakesIt()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from t group by a having (min(a) + max(b)) collate Latin1_General_CI_AS like 'a%'"));
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from t x join t y on (x.a + x.b) collate Latin1_General_CI_AS like y.a + '%'"));
        AreEqual("ab", sim.ExecuteScalar("select (a + b) collate Latin1_General_CI_AS from t"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from (select * from t order by (a + b) collate Latin1_General_CI_AS offset 0 rows) z"));
    }

    /// <summary>
    /// The DACFx / WWI CHECK-constraint spelling this disambiguation was built
    /// for keeps working, collated or not, and still enforces.
    /// </summary>
    [TestMethod]
    public void ACollatedGroupIsALegalCheckConstraint()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table ck (a varchar(10), constraint c1 check ((a) collate Latin1_General_CI_AS = 'x'));
            insert ck values ('X');
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from ck"));
        _ = sim.AssertSqlError("insert ck values ('y')", 547);
        _ = sim.ExecuteNonQuery("create table ck2 (a int, b int, constraint c2 check ((a) = (1)))");
        AreEqual(1, sim.ExecuteNonQuery("insert ck2 values (1, 1)"));
    }

    /// <summary>
    /// What real refuses, and the shapes the disambiguation must leave alone: a
    /// second <c>COLLATE</c> is Msg 156 on the keyword, a bogus collation name
    /// is Msg 448, and a top-level row constructor still reports Msg 4145 at its
    /// own comma.
    /// </summary>
    [TestMethod]
    public void TheRefusalsRealGivesAreUnchanged()
    {
        var sim = Seeded();
        _ = sim.AssertSqlError(
            "select * from t where (a + b) collate Latin1_General_CI_AS collate Latin1_General_CS_AS = 'ab'", 156);
        _ = sim.AssertSqlError("select * from t where (a) collate Bogus_Collation_Name = 'a'", 448);
        _ = sim.AssertSqlError("select * from t where (a, b) in (select a, b from t)", 4145);
        _ = sim.AssertSqlError("select * from t where (a) collate Latin1_General_CI_AS", 4145);
        AreEqual(1, Count("(a = 'a') and b = 'b'"));
        AreEqual(1, Count("(a = 'a') or (b = 'b')"));
    }
}
