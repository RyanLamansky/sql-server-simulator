using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Select-list subqueries that reference the enclosing query's columns. These
/// need the FROM sources bound <em>before</em> the select list parses, because
/// a projection's type is resolved statically by <c>GetSqlType</c> rather than
/// deferred to <c>Run</c> the way a WHERE reference is. Expected values are
/// what SQL Server 2025 returned for the same statement.
/// </summary>
[TestClass]
public sealed class OuterScopeCorrelationTests
{
    private static Simulation Seeded()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int identity, col int)",
            "insert t (col) values (3), (7)",
            "create table u (a int)",
            "insert u values (3)");
        return sim;
    }

    private static List<string> Column(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? "NULL" : $"{reader.GetValue(0)}");
        return values;
    }

    /// <summary>
    /// The simplest form: a FROM-less subquery projecting an outer column,
    /// evaluated once per outer row.
    /// </summary>
    [TestMethod]
    [DataRow("select (select t.col) from t", "3,7")]
    [DataRow("select (select t.col + 1) from t", "4,8")]
    [DataRow("select (select t.col) + 100 from t", "103,107")]
    public void FromLessSubquery_ProjectingOuterColumn_EvaluatesPerRow(string sql, string expected) =>
        AreEqual(expected, string.Join(",", Column(Seeded(), sql)));

    /// <summary>
    /// A FROM-less projection with no outer reference still folds at parse
    /// time — the deferral is opt-in, so constant projections are unaffected.
    /// </summary>
    [TestMethod]
    public void FromLessSelect_WithoutOuterReference_StillEvaluates()
    {
        var sim = Seeded();
        AreEqual(1, sim.ExecuteScalar<int>("select 1"));
        AreEqual(2, sim.ExecuteScalar<int>("select 2 as x order by x"));
        AreEqual("5", string.Join(",", Column(sim, "select (select 5) from t where col = 3")));
    }

    /// <summary>
    /// A derived table in a subquery's FROM reaches the outermost query, for
    /// the VALUES constructor and the SELECT form alike — including when the
    /// derived body is itself FROM-less, and through a set operation.
    /// </summary>
    [TestMethod]
    [DataRow("select (select min(v) from (values (t.col), (5)) x(v)) from t", "3,5")]
    [DataRow("select (select min(v) from (select t.col as v) x) from t", "3,7")]
    [DataRow("select (select min(v) from (select t.col as v union all select 5) x) from t", "3,5")]
    [DataRow("select (select min(v) from (select 5 as v union all select t.col) x) from t", "3,5")]
    public void DerivedTableInSubqueryFrom_SeesOutermostQuery(string sql, string expected) =>
        AreEqual(expected, string.Join(",", Column(Seeded(), sql)));

    /// <summary>
    /// APPLY correlates the same way, both at the top level and nested inside
    /// a subquery, and an alias on the projected expression doesn't hide the
    /// reference.
    /// </summary>
    [TestMethod]
    [DataRow("select x.col from t cross apply (select t.col) x", "3,7")]
    [DataRow("select x.v from t cross apply (select t.col as v) x", "3,7")]
    [DataRow("select (select min(x.v) from u cross apply (values (t.col)) x(v)) from t", "3,7")]
    public void Apply_CorrelatesToOuterQuery(string sql, string expected) =>
        AreEqual(expected, string.Join(",", Column(Seeded(), sql)));

    /// <summary>
    /// The pre-pass must not mistake the <c>FROM</c> of <c>IS DISTINCT FROM</c>
    /// for a FROM clause — it is part of an expression, and sits at the same
    /// paren depth as a real one.
    /// </summary>
    [TestMethod]
    public void IsDistinctFrom_IsNotMistakenForAFromClause()
    {
        var sim = Seeded();
        IsTrue(sim.ExecuteScalar<bool>("select cast(1 as bit) & cast(case when 5 is distinct from 6 then 1 else 0 end as bit)"));
        AreEqual("3,7", string.Join(",", Column(sim, "select col from t where col is distinct from null")));
    }

    /// <summary>
    /// Correlation through WHERE keeps working — it resolves at runtime rather
    /// than at parse time, and is the path that already worked.
    /// </summary>
    [TestMethod]
    public void CorrelationThroughWhere_Unchanged()
    {
        var sim = Seeded();
        AreEqual("1,0", string.Join(",", Column(sim, "select (select count(*) from u where u.a = t.col) from t")));
        AreEqual("3", string.Join(",", Column(sim, "select col from t where exists (select 1 from u where u.a = t.col)")));
    }

    /// <summary>
    /// An aggregate reading only the enclosing query's columns binds to the
    /// <em>outer</em> query on real, which then collapses to one row. The
    /// simulator would bind it to the query it is written in and silently
    /// return one row per outer row, so it refuses instead of guessing.
    /// </summary>
    [TestMethod]
    [DataRow("select (select max(t.col) from u) from t")]
    [DataRow("select (select min(value) from (values (min(t.col)), (5)) as _l(value)) from t")]
    public void AggregateOverOuterScope_IsRefusedRatherThanAnswered(string sql)
    {
        var error = Throws<NotSupportedException>(() => Column(Seeded(), sql));
        Contains("enclosing query", error.Message);
    }

    /// <summary>
    /// Aggregates that read this query's own columns, or no column at all, are
    /// untouched by that gate.
    /// </summary>
    [TestMethod]
    public void AggregateOverOwnOrNoColumns_StillWorks()
    {
        var sim = Seeded();
        AreEqual(7, sim.ExecuteScalar<int>("select max(col) from t"));
        AreEqual(2, sim.ExecuteScalar<int>("select count(*) from t"));
        AreEqual("1,1", string.Join(",", Column(sim, "select (select count(*) from u) from t")));
        // Mixed inner + outer references are left alone rather than refused.
        AreEqual("1", string.Join(",", Column(sim, "select (select count(*) from u where u.a = t.col) from t where col = 3")));
    }
}
