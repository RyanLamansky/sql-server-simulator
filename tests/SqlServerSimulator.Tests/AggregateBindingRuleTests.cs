using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The parse-time binding rules SQL Server enforces around aggregates and
/// GROUP BY: Msg 130 (aggregate over an aggregate or subquery), Msg 8117
/// (aggregate over the bare untyped NULL), Msg 144 (aggregate or subquery in a
/// GROUP BY item) and Msg 164 (GROUP BY item with no column of its own).
/// All expectations probe-confirmed against SQL Server 2025 (17.0.1125.2) on
/// 2026-07-24. Each rule is the over-permissive direction — the simulator used
/// to accept these, so an app query would work here and break on real.
/// </summary>
[TestClass]
public sealed class AggregateBindingRuleTests
{
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, d datetime);
            insert t values (1, 10, '2024-01-01'), (2, 20, '2024-06-01');
            create table u (x int);
            insert u values (1)
            """);
        return simulation;
    }

    private const string Msg130 = "Cannot perform an aggregate function on an expression containing an aggregate or a subquery.";

    [TestMethod]
    [DataRow("select sum(max(a)) from t")]
    [DataRow("select sum(a + max(b)) from t")]
    [DataRow("select max(case when 1=1 then sum(b) end) from t")]
    [DataRow("select sum((select count(*) from u)) from t")]
    [DataRow("select max((select 1)) from t")]
    [DataRow("select max(case when exists(select 1 from u) then a end) from t")]
    [DataRow("select max(case when a in (select x from u) then b end) from t")]
    [DataRow("select max((select count(*) from u where x = t.a)) from t")]
    public void AggregateOverAggregateOrSubquery_RaisesMsg130(string sql) =>
        Seeded().AssertSqlError(sql, 130, Msg130);

    [TestMethod]
    public void AggregateOverAggregate_InHaving_RaisesMsg130() =>
        Seeded().AssertSqlError("select a from t group by a having max(sum(b)) > 0", 130, Msg130);

    [TestMethod]
    [DataRow("count_big", "select count_big(null) from t")]
    [DataRow("count", "select count(null) from t")]
    [DataRow("sum", "select sum(null) from t")]
    [DataRow("max", "select max(null) from t")]
    [DataRow("min", "select min(null) from t")]
    [DataRow("avg", "select avg(null) from t")]
    [DataRow("stdev", "select stdev(null) from t")]
    [DataRow("checksum_agg", "select checksum_agg(null) from t")]
    public void AggregateOverUntypedNull_RaisesMsg8117(string aggregate, string sql) =>
        // mssql-django's empty `filter=` aggregate degrades to COUNT_BIG(NULL),
        // which is what surfaced this.
        Seeded().AssertSqlError(sql, 8117, $"Operand data type NULL is invalid for {aggregate} operator.");

    [TestMethod]
    [DataRow("select count_big(cast(null as int)) from t", 0L)]
    [DataRow("select count(cast(null as int)) from t", 0)]
    public void AggregateOverTypedNull_IsAccepted(string sql, object expected) =>
        // Only the *untyped* NULL keyword is rejected; a typed NULL counts zero
        // non-NULL rows like any other all-NULL operand.
        AreEqual(expected, Seeded().ExecuteScalar(sql));

    [TestMethod]
    [DataRow("select count(*) from t group by max(a)")]
    [DataRow("select count(*) from t group by (select max(x) from u)")]
    [DataRow("select count(*) from t group by (select max(x) from u where x = t.a)")]
    public void AggregateOrSubqueryInGroupBy_RaisesMsg144(string sql) =>
        // Takes precedence over Msg 164 — the correlated form references a local
        // column yet still reports 144.
        Seeded().AssertSqlError(sql, 144, "Cannot use an aggregate or a subquery in an expression used for the group by list of a GROUP BY clause.");

    [TestMethod]
    [DataRow("select count(*) from t group by 1")]
    [DataRow("select count(*) from t group by 1+1")]
    [DataRow("select count(*) from t group by 'x'")]
    [DataRow("select count(*) from t group by getdate()")]
    [DataRow("select count(*) from t group by newid()")]
    [DataRow("select count(*) from t group by rand()")]
    [DataRow("select count(*) from t group by cast(sysdatetime() as date)")]
    [DataRow("select count(*) from t group by a, getdate()")]
    public void GroupByItemWithoutOwnColumn_RaisesMsg164(string sql) =>
        // Note the last row: the rule is per item, so a valid `a` beside an
        // offending expression doesn't rescue the statement. `GROUP BY 1` is a
        // constant here, not an ordinal — SQL Server has no ordinal GROUP BY.
        Seeded().AssertSqlError(sql, 164, "Each GROUP BY expression must contain at least one column that is not an outer reference.");

    [TestMethod]
    [DataRow("select count(*) from t group by 'a' 'b'")]
    [DataRow("select count(*) from t group by (select max(x) from u) 'b'")]
    [DataRow("select count(*) from t group by a 'b'")]
    public void StrayTokenAfterGroupByClause_RaisesMsg102AheadOfTheBindingRule(string sql) =>
        // Real parses a batch before binding any of it, so the trailing-token
        // syntax error outranks the clause's own Msg 164 / Msg 144 — the same
        // `GROUP BY 'a'` that reports 164 on its own reports 102 once a stray
        // token follows it (probe-confirmed for all three shapes).
        Seeded().AssertSqlError(sql, 102, "Incorrect syntax near 'b'.");

    [TestMethod]
    [DataRow("select count(*) from t group by a + datepart(year, getdate())")]
    [DataRow("select count(*) from t group by dateadd(year, 1, d)")]
    [DataRow("select count(*) from t group by left(cast(a as varchar(9)), 1)")]
    [DataRow("select count(*) from t group by isnull(a, 0)")]
    [DataRow("select count(*) from t group by case when a > 1 then 1 else 0 end")]
    [DataRow("select count(*) from t group by cast(a as varchar(10))")]
    [DataRow("select count(*) from t group by t.a")]
    public void GroupByItemWithColumnBuriedInACall_IsAccepted(string sql) =>
        // The reason Msg 164 counts references at parse time rather than walking
        // the finished expression: only a minority of Expression subclasses
        // override VisitColumnReferences, so a walk can't see the column inside
        // DATEADD / LEFT / ISNULL and would reject every one of these.
        // Non-determinism is irrelevant — GETDATE() beside a column is fine.
        _ = Seeded().ExecuteScalar(sql);

    [TestMethod]
    [DataRow("select count(*) from t group by ()")]
    [DataRow("select count(*) from t group by grouping sets (())")]
    [DataRow("select count(*) from t group by grouping sets ((a), ())")]
    [DataRow("select count(*) from t group by (), a")]
    public void EmptyGroupingSet_IsExemptFromMsg164(string sql) =>
        // The grand-total grouping set contributes no expression, so there's
        // nothing for the rule to require a column of.
        _ = Seeded().ExecuteScalar(sql);

    [TestMethod]
    [DataRow("select a from t group by a having count(*) > (select count(*) from u)")]
    [DataRow("select (select count(*) from u) from t")]
    [DataRow("select count(*) from t where exists (select 1 from u)")]
    [DataRow("select string_agg(cast(a as varchar(10)), ',') from t")]
    public void SubqueryOutsideAnAggregateArgument_IsUnaffected(string sql) =>
        // Msg 130 is scoped to an aggregate's own argument: a subquery in
        // HAVING, in the projection, or in WHERE stays legal.
        _ = Seeded().ExecuteScalar(sql);
}
