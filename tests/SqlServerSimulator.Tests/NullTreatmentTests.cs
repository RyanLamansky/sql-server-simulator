using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>IGNORE NULLS</c> / <c>RESPECT NULLS</c> on the offset window functions,
/// probed 2026-09-24 against SQL Server 2025. <c>FIRST_VALUE</c> /
/// <c>LAST_VALUE</c> take the first / last non-null value in the frame;
/// <c>LAG</c> / <c>LEAD</c> step the offset as usual, then past a null target
/// in the same direction, and the default applies only when that first step
/// leaves the partition.
/// </summary>
[TestClass]
public sealed class NullTreatmentTests
{
    private const string Values = "(values (1, null), (2, 10), (3, null), (4, 20), (5, null)) t(i, v)";

    private static string Column(string window, string values = Values) =>
        (string)new Simulation().ExecuteScalar(
            $"select string_agg(isnull(cast(w as varchar), '-'), ',') within group (order by i) from (select i, {window} as w from {values}) x")!;

    [TestMethod]
    [DataRow("first_value(v) ignore nulls over (order by i)", "-,10,10,10,10")]
    [DataRow("last_value(v) ignore nulls over (order by i)", "-,10,10,20,20")]
    [DataRow("last_value(v) respect nulls over (order by i)", "-,10,-,20,-")]
    [DataRow("first_value(v) ignore nulls over (order by i rows between 1 following and unbounded following)", "10,20,20,-,-")]
    [DataRow("lag(v) ignore nulls over (order by i)", "-,-,10,10,20")]
    [DataRow("lead(v) ignore nulls over (order by i)", "10,20,20,-,-")]
    [DataRow("lag(v, 1, -1) ignore nulls over (order by i)", "-1,-,10,10,20")]
    [DataRow("lead(v, 1, -1) ignore nulls over (order by i)", "10,20,20,-,-1")]
    public void IgnoreNulls(string window, string expected)
        => AreEqual(expected, Column(window));

    [TestMethod]
    public void LagIgnoreNulls_StepsTheOffsetThenPastANull()
        => AreEqual("-1,-1,5,5,7", Column(
            "lag(v, 2, -1) ignore nulls over (order by i)",
            "(values (1, 5), (2, null), (3, 7), (4, null), (5, 9)) t(i, v)"));

    [TestMethod]
    public void LastValueIgnoreNulls_StaysInsideThePartition()
        => AreEqual("5,5,-", Column(
            "last_value(v) ignore nulls over (partition by g order by i rows between unbounded preceding and unbounded following)",
            "(values (1, 1, 5), (2, 1, null), (3, 2, null)) t(i, g, v)"));

    [TestMethod]
    [DataRow("row_number() ignore nulls over (order by i)", "The function 'row_number' does not support IGNORE NULLS.")]
    [DataRow("sum(i) ignore nulls over (order by i)", "The function 'sum' does not support IGNORE NULLS.")]
    [DataRow("sum(i) respect nulls over (order by i)", "The function 'sum' does not support RESPECT NULLS.")]
    public void OtherFunctions_RaiseMsg16208(string window, string message)
        => new Simulation().AssertSqlError($"select {window} from (values (1)) t(i)", 16208, message);

    [TestMethod]
    public void WithoutOver_RaisesMsg156()
        => new Simulation().AssertSqlError("select first_value(v) ignore nulls from (values (1)) t(v)", 156);
}
