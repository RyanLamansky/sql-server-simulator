using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>lhs op {ANY|SOME|ALL} (SELECT col FROM ...)</c> — quantified
/// subquery comparisons. Six comparison operators (<c>=</c>, <c>&lt;&gt;</c>,
/// <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) plus the T-SQL
/// synonyms <c>!=</c> / <c>!&lt;</c> / <c>!&gt;</c>; SOME is a pure synonym
/// of ANY. Semantics probed against SQL Server 2025 (2026-05-13).
/// </summary>
[TestClass]
public sealed class QuantifiedComparisonTests
{
    private static DbConnection SeededTwoTables()
    {
        var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table q1 (id int not null primary key, v int null);
            create table q2 (id int not null primary key, x int null);
            insert q1 values (1, 10), (2, 20), (3, 30), (4, null);
            insert q2 values (1, 15), (2, 25), (3, null)
            """).ExecuteNonQuery();
        return conn;
    }

    private static int[] Ids(DbDataReader reader)
    {
        var results = new List<int>();
        while (reader.Read())
            results.Add(reader.GetInt32(0));
        return [.. results];
    }

    [TestMethod]
    public void GreaterThanAll_NonEmptyInner_OnlyStrictlyGreaterRowsMatch()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > all (select x from q2 where x is not null)").ExecuteReader();
        // 15 and 25 in inner; only v=30 strictly exceeds both.
        CollectionAssert.AreEqual(new int[] { 3 }, Ids(reader));
    }

    [TestMethod]
    public void GreaterThanAny_NonEmptyInner_MatchesIfBeatsAnyOne()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > any (select x from q2 where x is not null)").ExecuteReader();
        // 15 is the min; v=20 beats 15, v=30 beats both.
        CollectionAssert.AreEqual(new int[] { 2, 3 }, Ids(reader));
    }

    [TestMethod]
    public void SomeIsAliasOfAny()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > some (select x from q2 where x is not null)").ExecuteReader();
        CollectionAssert.AreEqual(new int[] { 2, 3 }, Ids(reader));
    }

    [TestMethod]
    public void EmptyInner_AllIsVacuouslyTrue_EvenForNullLhs()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > all (select x from q2 where 1=0)").ExecuteReader();
        // Empty inner → ALL vacuously true. Probe-confirmed (2026-05-13)
        // that this also includes rows where LHS is NULL.
        CollectionAssert.AreEqual(new int[] { 1, 2, 3, 4 }, Ids(reader));
    }

    [TestMethod]
    public void EmptyInner_AnyIsVacuouslyFalse()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > any (select x from q2 where 1=0)").ExecuteReader();
        CollectionAssert.AreEqual(Array.Empty<int>(), Ids(reader));
    }

    [TestMethod]
    public void NullOnlyInner_AllReturnsUnknown_RowExcludedFromWhere()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > all (select x from q2 where x is null)").ExecuteReader();
        CollectionAssert.AreEqual(Array.Empty<int>(), Ids(reader));
    }

    [TestMethod]
    public void NullOnlyInner_AnyReturnsUnknown_RowExcludedFromWhere()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > any (select x from q2 where x is null)").ExecuteReader();
        CollectionAssert.AreEqual(Array.Empty<int>(), Ids(reader));
    }

    [TestMethod]
    public void NullLhs_NonEmptyInner_RowExcluded()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v > all (select x from q2 where x is not null) and v is null").ExecuteReader();
        CollectionAssert.AreEqual(Array.Empty<int>(), Ids(reader));
    }

    [TestMethod]
    public void EqualAny_BehavesLikeIn()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v = any (select x from q2 where x is not null)").ExecuteReader();
        // q1.v values 10/20/30 against q2.x in (15, 25): no equal matches.
        CollectionAssert.AreEqual(Array.Empty<int>(), Ids(reader));
    }

    [TestMethod]
    public void EqualAny_LiteralUnion_MatchesIn()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v = any (select 10 union select 20)").ExecuteReader();
        CollectionAssert.AreEqual(new int[] { 1, 2 }, Ids(reader));
    }

    [TestMethod]
    public void NotEqualAll_BehavesLikeNotIn()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v <> all (select 10 union select 20)").ExecuteReader();
        // v in (10, 20, 30, NULL) — only 30 differs from both 10 and 20 with
        // no NULL siblings (the union has no NULLs); v=NULL → UNKNOWN excluded.
        CollectionAssert.AreEqual(new int[] { 3 }, Ids(reader));
    }

    [TestMethod]
    public void NotEqualAll_NullInInner_PoisonsResult()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v <> all (select x from q2)").ExecuteReader();
        // q2.x has (15, 25, NULL). For each q1.v: v=10 → 10<>15 true, 10<>25
        // true, 10<>NULL UNKNOWN → ALL = UNKNOWN. Same for v=20/30. v=NULL
        // → UNKNOWN. No row should match.
        CollectionAssert.AreEqual(Array.Empty<int>(), Ids(reader));
    }

    [TestMethod]
    public void EqualAll_AllSameValue_True()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v = all (select 10 union all select 10)").ExecuteReader();
        CollectionAssert.AreEqual(new int[] { 1 }, Ids(reader));
    }

    [TestMethod]
    public void EqualAll_DifferentValues_NoneMatch()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v = all (select 10 union select 20)").ExecuteReader();
        CollectionAssert.AreEqual(Array.Empty<int>(), Ids(reader));
    }

    [TestMethod]
    public void LessThanOrEqualAll_RequiresLhsBelowMinimum()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v <= all (select x from q2 where x is not null)").ExecuteReader();
        // min(q2.x) = 15; only v=10 is <= all.
        CollectionAssert.AreEqual(new int[] { 1 }, Ids(reader));
    }

    [TestMethod]
    public void GreaterThanOrEqualAll_RequiresLhsAtOrAboveMax()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v >= all (select x from q2 where x is not null)").ExecuteReader();
        CollectionAssert.AreEqual(new int[] { 3 }, Ids(reader));
    }

    [TestMethod]
    public void LessThanAll_StrictlyBelowMin()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v < all (select x from q2 where x is not null)").ExecuteReader();
        CollectionAssert.AreEqual(new int[] { 1 }, Ids(reader));
    }

    [TestMethod]
    public void BangEqualAny_SynonymOfNotEqualAny()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v != any (select 10 union select 20)").ExecuteReader();
        // v=10 vs (10,20): 10<>10 false, 10<>20 true → ANY true. v=20 same.
        // v=30 vs both <>: true. v=NULL excluded.
        CollectionAssert.AreEqual(new int[] { 1, 2, 3 }, Ids(reader));
    }

    [TestMethod]
    public void BangLessAny_SynonymOfGreaterOrEqualAny()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v !< any (select 20)").ExecuteReader();
        // !< means >=. v=20 → 20>=20 true. v=30 → true. v=10 → 10>=20 false.
        CollectionAssert.AreEqual(new int[] { 2, 3 }, Ids(reader));
    }

    [TestMethod]
    public void BangGreaterAny_SynonymOfLessOrEqualAny()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 where v !> any (select 20)").ExecuteReader();
        // !> means <=. v=10 → true. v=20 → true. v=30 → false.
        CollectionAssert.AreEqual(new int[] { 1, 2 }, Ids(reader));
    }

    [TestMethod]
    public void MultiColumnInner_RaisesMsg116()
    {
        using var conn = SeededTwoTables();
        using var cmd = conn.CreateCommand("select id from q1 where v > all (select id, x from q2)");
        var ex = Throws<DbException>(cmd.ExecuteReader);
        AreEqual("116", ex.Data["HelpLink.EvtID"]);
        AreEqual(
            "Only one expression can be specified in the select list when the subquery is not introduced with EXISTS.",
            ex.Message);
    }

    [TestMethod]
    public void CorrelatedSubquery_PerOuterRowReevaluation()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select id from q1 o where v > all (select v from q1 i where i.id < o.id and v is not null)").ExecuteReader();
        // For each outer id, inner is rows with id < outer.id. id=1: empty
        // inner → vacuously true. id=2: inner = {10}; 20>10 → true. id=3:
        // inner = {10,20}; 30 beats both. id=4: v=null → UNKNOWN.
        CollectionAssert.AreEqual(new int[] { 1, 2, 3 }, Ids(reader));
    }

    [TestMethod]
    public void HavingContext_AnyWorks()
    {
        using var conn = SeededTwoTables();
        using var reader = conn.CreateCommand(
            "select v from q1 group by v having v > any (select x from q2 where x is not null)").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        values.Sort();
        CollectionAssert.AreEqual(new int[] { 20, 30 }, values);
    }

    [TestMethod]
    public void CaseWhenContext_AnyWorks()
    {
        using var conn = SeededTwoTables();
        using var cmd = conn.CreateCommand(
            "select case when 50 > any (select x from q2 where x is not null) then 'yes' else 'no' end");
        AreEqual("yes", cmd.ExecuteScalar());
    }

    [TestMethod]
    public void SelectListUsage_PredicateOnly_Rejected()
    {
        // Probe-confirmed: real SQL Server raises Msg 102 at the comparison
        // operator when a quantified comparison appears as a SELECT-list
        // value expression. The simulator's predicate-only grammar reaches
        // the same result because quantified parsing lives in
        // BooleanExpression.ParseComparison.
        new Simulation().ValidateSyntaxError("select 50 > all (select 1)", ">");
    }

    [TestMethod]
    public void TypePromotion_IntLhsAgainstDecimalInner()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = conn.CreateCommand("""
            create table tp (v int);
            insert tp values (1), (5), (10)
            """).ExecuteNonQuery();
        using var reader = conn.CreateCommand(
            "select v from tp where v > all (select cast(0.5 as decimal(5,2)) union select cast(2.5 as decimal(5,2)))").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        values.Sort();
        CollectionAssert.AreEqual(new int[] { 5, 10 }, values);
    }

    [TestMethod]
    public void TypeMismatch_StringLhs_IntInner_ConversionError()
    {
        using var conn = SeededTwoTables();
        using var cmd = conn.CreateCommand("select id from q1 where 'x' = any (select x from q2 where x is not null)");
        var ex = Throws<DbException>(cmd.ExecuteReader);
        AreEqual("245", ex.Data["HelpLink.EvtID"]);
    }

}
