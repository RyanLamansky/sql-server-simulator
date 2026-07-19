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
    /// ANSI-89 comma join with an equi-predicate in WHERE: the parser rewrites
    /// `FROM a, b WHERE a.id = b.a_id` into `a INNER JOIN b ON a.id = b.a_id`,
    /// so it hashes instead of nested-looping the cross product.
    /// </summary>
    [TestMethod]
    public void CommaJoin_WithEquiPredicate_TakesHashPath()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a, b where a.id = b.a_id"));

    /// <summary>
    /// Explicit CROSS JOIN carrying an equi-predicate in WHERE is the same shape
    /// post-parse (JoinKind.Cross, null ON) and rewrites identically.
    /// </summary>
    [TestMethod]
    public void ExplicitCrossJoin_WithEquiPredicate_TakesHashPath()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a cross join b where a.id = b.a_id"));

    /// <summary>
    /// A non-equi WHERE term alongside the equi-key isn't pulled into the
    /// synthesized ON (only equi-keys are) — it stays a post-join WHERE filter,
    /// so the join's residual count is 0, not 1.
    /// </summary>
    [TestMethod]
    public void CommaJoin_NonEquiTermStaysInWhere_NotPulledToOn()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a, b where a.id = b.a_id and b.id > 10"));

    /// <summary>
    /// No equi-key connects the two comma sources, so there's nothing to pull
    /// into an ON — the join stays a Cross nested loop (the cross product is
    /// genuinely required).
    /// </summary>
    [TestMethod]
    public void CommaJoin_NoEquiPredicate_StaysNestedLoops()
        => Contains("Cross:NestedLoops",
            CaptureStrategies("select a.id from a, b where a.id <> b.a_id"));

    /// <summary>
    /// Comma join + WHERE filter on the small outer: after the Cross→Inner
    /// rewrite the leftmost is seeked to one row and the indexed inner is seeked
    /// per outer row — same acceleration the explicit-JOIN form already got.
    /// </summary>
    [TestMethod]
    public void CommaJoin_SmallOuter_IndexedInner_SeeksPerOuter()
        => Contains("Inner:NestedLoopIndexSeek(keys=1)",
            CaptureStrategies(IndexedSetup, "select p.label, c.amt from p, c where c.pid = p.id and p.id = 5"));

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

    /// <summary>
    /// A catalog view on the right of an equi-join must take the hash path, not
    /// a nested loop. <c>sys.*</c> sources are deferred lateral plans, but the
    /// uncorrelated ones are materialized once per query
    /// (<c>MaterializeUncorrelatedDeferredSources</c>) into a re-enumerable row
    /// list, which makes them eligible for <c>TryPlanEquiJoin</c>'s O(L+R) hash
    /// build. Without materialization the lateral plan blocks the equi-plan and
    /// the join re-generates the whole view per outer row — the SMO per-column
    /// property-bag query's O(outer × Σ view-sizes) blowup this guard pins down.
    /// </summary>
    [TestMethod]
    public void CatalogView_EquiJoin_TakesHashPath()
        => Contains("Left:HashMatch(keys=1,residual=0)",
            CaptureStrategies("create table t (id int)",
                "select col.name from sys.all_columns col left join sys.types st on st.user_type_id = col.user_type_id"));
}
