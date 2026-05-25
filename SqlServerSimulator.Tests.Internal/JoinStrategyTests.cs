using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the join executor's strategy choice
/// (<c>Selection.Execution.Joins.cs</c>): an equi-join <c>a.col = b.col</c> must
/// take the O(L+R) hash path, while a non-equi / CROSS predicate must fall back
/// to the nested loop. The correctness suite passes under either strategy, so a
/// silent revert to the O(L×R) loop would otherwise go unnoticed until a real
/// workload hung. Reads the opt-in <see cref="JoinDiagnostics"/> trace (recorded
/// at the single dispatch point) rather than timing, so the guard is exact and
/// non-flaky.
/// </summary>
[TestClass]
public sealed class JoinStrategyTests
{
    private static List<string> CaptureStrategies(string joinQuery)
    {
        var sim = new Simulation();
        var connection = sim.CreateDbConnection();
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                create table a (id int, name varchar(20));
                create table b (id int, a_id int);
                insert a values (1, 'one'), (2, 'two');
                insert b values (10, 1), (11, 2)
                """;
            _ = setup.ExecuteNonQuery();
        }

        JoinDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = joinQuery;
            using var reader = command.ExecuteReader();
            while (reader.Read()) { }
            return JoinDiagnostics.Sink;
        }
        finally
        {
            JoinDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void EquiJoin_TakesHashPath()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a join b on a.id = b.a_id"));

    [TestMethod]
    public void EquiJoinWithExtraPredicate_HashesKeyKeepsRestAsResidual()
        => Contains("Inner:HashMatch(keys=1,residual=1)",
            CaptureStrategies("select a.id from a join b on a.id = b.a_id and b.id > 10"));

    [TestMethod]
    public void LeftJoin_TakesHashPath()
        => Contains("Left:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a left join b on a.id = b.a_id"));

    [TestMethod]
    public void NonEquiJoin_FallsBackToNestedLoops()
        => Contains("Inner:NestedLoops",
            CaptureStrategies("select a.id from a join b on a.id <> b.a_id"));

    [TestMethod]
    public void CrossJoin_UsesNestedLoops()
        => Contains("Cross:NestedLoops",
            CaptureStrategies("select a.id from a cross join b"));
}
