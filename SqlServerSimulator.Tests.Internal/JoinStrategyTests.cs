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
    private const string DefaultSetup = """
        create table a (id int, name varchar(20));
        create table b (id int, a_id int);
        insert a values (1, 'one'), (2, 'two');
        insert b values (10, 1), (11, 2)
        """;

    private static List<string> CaptureStrategies(string joinQuery) => CaptureStrategies(DefaultSetup, joinQuery);

    private static List<string> CaptureStrategies(string setup, string joinQuery)
    {
        var sim = new Simulation();
        var connection = sim.CreateDbConnection();
        connection.Open();
        using (var setupCmd = connection.CreateCommand())
        {
            setupCmd.CommandText = setup;
            _ = setupCmd.ExecuteNonQuery();
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

    // Parent p (PK id) + child c with a nonclustered index on the join key (pid);
    // 200 parents so an unfiltered outer exceeds the per-outer-seek row cap.
    private const string IndexedSetup = """
        create table p (id int not null primary key, label varchar(20));
        create table c (cid int not null primary key, pid int, amt int);
        create index ix_c_pid on c (pid);
        declare @i int = 1;
        while @i <= 200 begin insert p values (@i, 'p'); insert c values (@i, @i, @i * 10); set @i += 1; end
        """;

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

    /// <summary>
    /// WHERE p.id = 5 pushes down to seek the leftmost to one row, then the
    /// inner (indexed on pid) is seeked per outer row instead of hash-built.
    /// </summary>
    [TestMethod]
    public void FilterThenJoin_SmallOuter_IndexedInner_SeeksPerOuter()
        => Contains("Inner:NestedLoopIndexSeek(keys=1)",
            CaptureStrategies(IndexedSetup, "select p.label, c.amt from p join c on c.pid = p.id where p.id = 5"));

    [TestMethod]
    public void LeftFilterThenJoin_SmallOuter_IndexedInner_SeeksPerOuter()
        => Contains("Left:NestedLoopIndexSeek(keys=1)",
            CaptureStrategies(IndexedSetup, "select p.label, c.amt from p left join c on c.pid = p.id where p.id = 5"));

    /// <summary>
    /// No WHERE filter: 200 outer rows exceed the seek cap, so the join keeps
    /// the O(L+R) hash build rather than 200 per-outer seeks.
    /// </summary>
    [TestMethod]
    public void LargeOuter_IndexedInner_FallsBackToHash()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies(IndexedSetup, "select p.label, c.amt from p join c on c.pid = p.id"));

    /// <summary>
    /// a/b are unindexed, so the inner can't seek on the join key — hash.
    /// </summary>
    [TestMethod]
    public void SmallOuter_UnindexedInner_FallsBackToHash()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a join b on a.id = b.a_id where a.id = 1"));
}
