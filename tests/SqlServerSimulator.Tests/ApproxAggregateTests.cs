using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The clauses real accepts and refuses around <c>APPROX_COUNT_DISTINCT</c>
/// and the <c>APPROX_PERCENTILE</c> pair (probed 2026-09-29 against SQL Server
/// 2025); the values both answer are covered in <see cref="AggregateTests"/>.
/// </summary>
[TestClass]
public sealed class ApproxAggregateTests
{
    [TestMethod]
    [DataRow("approx_count_distinct")]
    [DataRow("APPROX_COUNT_DISTINCT")]
    public void ApproxCountDistinctOver_IsMsg4113NamedAsWritten(string name)
        => new Simulation().AssertSqlError(
            $"select {name}(value) over (partition by value % 2) from generate_series(1, 3)",
            4113,
            $"The function '{name}' is not a valid windowing function, and cannot be used with the OVER clause.");

    [TestMethod]
    public void ApproxCountDistinctOver_IsRefusedAfterTheWindowClauseAndBeforeBinding()
    {
        var simulation = new Simulation();
        simulation.AssertSqlError("select approx_count_distinct(value) over (order by x x) from generate_series(1, 3)", 102, "Incorrect syntax near 'x'.");
        _ = simulation.AssertSqlError("select approx_count_distinct(a) over () from nosuch", 4113);
        _ = simulation.AssertSqlError("if 1 = 0 select approx_count_distinct(value) over () from generate_series(1, 3)", 4113);
    }

    [TestMethod]
    public void ApproxCountDistinctAllOver_IsAWindowedDistinctCount()
        => AreEqual("1:3,1,3,1;2:3,2,3,2;3:3,3,3,2;4:3,3,3,2;5:3,3,3,2;6:3,3,3,2", new Simulation().ExecuteScalar("""
            select string_agg(concat(value, ':', whole, ',', running, ',', parity, ',', sliding), ';') within group (order by value)
            from (
                select value,
                    approx_count_distinct(all value % 3) over () whole,
                    approx_count_distinct(all value % 3) over (order by value) running,
                    approx_count_distinct(all value % 3) over (partition by value % 2) parity,
                    approx_count_distinct(all value % 3) over (order by value rows between 1 preceding and current row) sliding
                from generate_series(1, 6)) w
            """));

    [TestMethod]
    [DataRow("select approx_percentile_cont(value / 10.0) within group (order by value) from generate_series(1, 10)", 8726)]
    [DataRow("select approx_percentile_cont(case when value > 0 then 0.5 end) within group (order by value) from generate_series(1, 10)", 8726)]
    [DataRow("if 1 = 0 select approx_percentile_disc(value / 10.0) within group (order by value) from generate_series(1, 10)", 8726)]
    [DataRow("select approx_percentile_cont(v) within group (order by v) from (values (0.5), (0.7)) t(v)", 8726)]
    [DataRow("select approx_percentile_cont(p) within group (order by value) from (select 0.5 p union all select 0.5) x cross join generate_series(1, 10)", 8726)]
    [DataRow("select approx_percentile_cont(value / 10.0) within group (order by value), nosuchcol from generate_series(1, 10)", 207)]
    [DataRow("select approx_percentile_cont((select 0.5)) within group (order by value) from generate_series(1, 10)", 130)]
    [DataRow("select approx_percentile_cont(min(0.5)) within group (order by value) from generate_series(1, 10)", 130)]
    [DataRow("select (select approx_percentile_cont(p.p) within group (order by g.value) from generate_series(1, 10) g) from (values (0.5)) p(p)", 8124)]
    [DataRow("select (select approx_percentile_cont(g.value / 10.0) within group (order by p.p) from generate_series(1, 10) g) from (values (0.5)) p(p)", 8124)]
    [DataRow("select (select approx_percentile_cont(p.p) within group (order by p.v) from generate_series(1, 1) g) from (values (0.5, 1), (0.5, 3)) p(p, v)", 8726)]
    public void ApproxPercentileFraction_ReadingAColumn_IsRefusedAsRealDoes(string sql, int number)
        => new Simulation().AssertSqlError(sql, number);

    /// <summary>
    /// A column of a one-row constant source folds to a constant before real
    /// asks, so it is a fraction like a literal.
    /// </summary>
    [TestMethod]
    [DataRow("select approx_percentile_cont(p) within group (order by value) from (select 0.5 p) x cross join generate_series(1, 10)", 5.5)]
    [DataRow("select approx_percentile_cont(p) within group (order by value) from (values (0.5)) x(p) cross join generate_series(1, 10)", 5.5)]
    [DataRow("select approx_percentile_cont(v) within group (order by v) from (values (0.5)) t(v)", 0.5)]
    [DataRow("select (select approx_percentile_cont(p.p) within group (order by p.p) from generate_series(1, 1) g) from (values (0.5)) p(p)", 0.5)]
    [DataRow("declare @p float = 0.25; select approx_percentile_cont(@p + @p) within group (order by value) from generate_series(1, 10)", 5.5)]
    public void ApproxPercentileFraction_FoldingToAConstant_IsAccepted(string sql, double expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));
}
