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

    private static List<string> CaptureStrategies(string setup, string joinQuery) =>
        CaptureStrategies([setup], joinQuery);

    // Batch-list form: CREATE VIEW has to be the first statement of its own
    // batch (Msg 111), so a setup that ends in one can't be a single command.
    private static List<string> CaptureStrategies(string[] setupBatches, string joinQuery)
    {
        var sim = new Simulation();
        var connection = sim.CreateDbConnection();
        connection.Open();
        foreach (var setup in setupBatches)
        {
            using var setupCmd = connection.CreateCommand();
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

    /// <summary>
    /// A derived table on the right of an equi-join must take the hash path.
    /// Its plan is deferred, which blocks <c>TryPlanEquiJoin</c> outright, so
    /// without <c>MaterializeUncorrelatedDeferredSources</c> hoisting it out of
    /// the per-left-row loop the join re-executes the whole body per outer row —
    /// the O(outer × body) blowup this guard pins down.
    /// </summary>
    [TestMethod]
    public void DerivedTable_EquiJoin_TakesHashPath()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a join (select id, a_id from b) d on d.a_id = a.id"));

    /// <summary>A grouped body is the same shape post-materialization.</summary>
    [TestMethod]
    public void GroupedDerivedTable_LeftEquiJoin_TakesHashPath()
        => Contains("Left:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a left join (select a_id, count(*) as n from b group by a_id) d on d.a_id = a.id"));

    /// <summary>A CTE reference carries the same deferred plan a derived table does.</summary>
    [TestMethod]
    public void CteReference_EquiJoin_TakesHashPath()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("with d as (select id, a_id from b) select a.id from a join d on d.a_id = a.id"));

    /// <summary>A view's body is isolated from caller scope, so it materializes too.</summary>
    [TestMethod]
    public void View_EquiJoin_TakesHashPath()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies([DefaultSetup, "create view bv as select id, a_id from b"],
                "select a.id from a join bv on bv.a_id = a.id"));

    /// <summary>
    /// CROSS APPLY's body reads the left row, so it is never materialized and
    /// never hashed — the laterality gate, asserted at the dispatch point.
    /// </summary>
    [TestMethod]
    public void CrossApplyDerivedTable_StaysNestedLoops()
        => Contains("CrossApply:NestedLoops",
            CaptureStrategies("select a.id from a cross apply (select id from b where b.a_id = a.id) d"));

    /// <summary>
    /// A <c>NEWID()</c> draw declines the reuse (real re-draws per row), so the
    /// source stays deferred and the join stays on the nested loop rather than
    /// silently freezing one draw into a hash build.
    /// </summary>
    [TestMethod]
    public void NewIdInDerivedTable_StaysNestedLoops()
        => Contains("Inner:NestedLoops",
            CaptureStrategies("select a.id from a join (select top 1 a_id, newid() as g from b) d on d.a_id = a.id"));

    /// <summary>
    /// A rowset function under a plain JOIN can no longer read a sibling (that
    /// is Msg 4104, probed), so its arguments are fixed for the enumeration and
    /// the source materializes once — which is what promotes the level from the
    /// O(L × R) nested loop to the equi-join hash.
    /// </summary>
    [TestMethod]
    public void UncorrelatedRowsetFunction_MaterializesAndHashes()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies("select a.id from a join string_split('x,y', ',') s on s.value = a.name"));

    // ---- narrowed-source-first reorder ---------------------------------------

    /// <summary>
    /// A three-table INNER chain: <c>r</c> (200 leaf rows) → <c>q</c> → <c>p</c>,
    /// every join column a primary key on one side and indexed on the other, so
    /// a filter anywhere in the chain can seek.
    /// </summary>
    private const string ChainSetup = """
        create table p (id int not null primary key, label varchar(20));
        create table q (id int not null primary key, p_id int not null);
        create table r (id int not null primary key, q_id int not null);
        create index ix_q_p on q (p_id);
        create index ix_r_q on r (q_id);
        declare @i int = 1;
        while @i <= 200 begin
          insert p values (@i, 'p'); insert q values (@i, @i); insert r values (@i, @i);
          set @i += 1;
        end
        """;

    /// <summary>
    /// The reorder entry, or null when the chain kept its written order. The
    /// payload is the placement order in <em>written</em> source indices, so
    /// <c>Reorder(2,1,0)</c> reads "drive from the third-written source".
    /// </summary>
    private static string? ReorderOf(List<string> trace)
        => trace.Find(entry => entry.StartsWith("Reorder(", StringComparison.Ordinal));

    /// <summary>
    /// The filter sits on the last-written source, so the chain reverses to drive
    /// from it — and every link then seeks per outer row instead of hash-building
    /// the tables ahead of the filter.
    /// </summary>
    [TestMethod]
    public void MidChainFilter_ReordersToDriveFromTheNarrowedSource()
    {
        var trace = CaptureStrategies(ChainSetup,
            "select r.id from r join q on q.id = r.q_id join p on p.id = q.p_id where p.id = 5");
        AreEqual("Reorder(2,1,0)", ReorderOf(trace));
        Contains("Inner:NestedLoopIndexSeek(keys=1)", trace);
    }

    /// <summary>
    /// A filter on the leftmost source is already driving, so the chain keeps its
    /// written order — the long-standing leftmost pushdown, unchanged.
    /// </summary>
    [TestMethod]
    public void LeftmostFilter_KeepsTheWrittenOrder()
        => IsNull(ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from p join q on q.p_id = p.id join r on r.q_id = q.id where p.id = 5")));

    /// <summary>
    /// Two narrowed sources: the reorder drives from the one the seek narrowed
    /// hardest (one <c>p</c> row against forty <c>q</c> rows), not the one
    /// written first.
    /// </summary>
    [TestMethod]
    public void TwoNarrowedSources_DrivesFromTheSmallerSeek()
        => AreEqual("Reorder(2,1,0)", ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from r join q on q.id = r.q_id join p on p.id = q.p_id where p.id = 5 and q.p_id < 40")));

    /// <summary>
    /// An outer join anywhere in the chain declines the reorder — its rows depend
    /// on which side is preserved, which commuting would change.
    /// </summary>
    [TestMethod]
    public void OuterJoinInTheChain_DeclinesTheReorder()
        => IsNull(ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from r left join q on q.id = r.q_id join p on p.id = q.p_id where p.id = 5")));

    /// <summary>
    /// A non-equi ON conjunct isn't a join-graph edge, so the reorder has nothing
    /// to rebuild the chain from and declines.
    /// </summary>
    [TestMethod]
    public void NonEquiOnPredicate_DeclinesTheReorder()
        => IsNull(ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from r join q on q.id > r.q_id join p on p.id = q.p_id where p.id = 5")));

    /// <summary>
    /// A single-source conjunct in an ON clause isn't an edge either — the
    /// reorder declines rather than re-homing it.
    /// </summary>
    [TestMethod]
    public void SingleSourceOnConjunct_DeclinesTheReorder()
        => IsNull(ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from r join q on q.id = r.q_id join p on p.id = q.p_id and p.id > 0 where p.id = 5")));

    /// <summary>
    /// An ON clause naming none of its own level's columns leaves that source
    /// unreachable in the join graph; the reorder declines whole rather than
    /// placing it arbitrarily.
    /// </summary>
    [TestMethod]
    public void DisconnectedJoinGraph_DeclinesTheReorder()
        => IsNull(ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from r join q on q.id = r.q_id join p on q.id = r.q_id where q.id = 5")));

    /// <summary>
    /// A CROSS level with no ON carries no edge at all, so a chain mixing one in
    /// declines.
    /// </summary>
    [TestMethod]
    public void CrossJoinInTheChain_DeclinesTheReorder()
        => IsNull(ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from r cross join q join p on p.id = q.p_id where p.id = 5")));

    /// <summary>
    /// A wide narrowing declines: driving from 150 seeked rows would trade the
    /// per-outer seek chain for hash probes, so the written order stands.
    /// </summary>
    [TestMethod]
    public void WideNarrowing_DeclinesTheReorder()
        => IsNull(ReorderOf(CaptureStrategies(ChainSetup,
            "select r.id from r join q on q.id = r.q_id join p on p.id = q.p_id where p.id <= 150")));

    /// <summary>
    /// The seek-vs-hash choice is a ratio, not an absolute outer size: 200 outer
    /// rows against a 200-row inner hashes, since a build row costs less than a
    /// seek call.
    /// </summary>
    [TestMethod]
    public void MidSizedOuter_AgainstAnEquallySizedInner_Hashes()
        => Contains("Inner:HashMatch(keys=1,residual=0)",
            CaptureStrategies(IndexedSetup, "select p.label, c.amt from p join c on c.pid = p.id"));

    /// <summary>
    /// …and the same 200-row outer against an inner twenty times larger seeks per
    /// outer row instead, which is what lets a reordered chain keep seeking past
    /// the first link.
    /// </summary>
    [TestMethod]
    public void MidSizedOuter_AgainstAMuchLargerInner_SeeksPerOuter()
        => Contains("Inner:NestedLoopIndexSeek(keys=1)",
            CaptureStrategies("""
                create table p (id int not null primary key, label varchar(20));
                create table c (cid int not null primary key, pid int, amt int);
                create index ix_c_pid on c (pid);
                declare @i int = 1;
                while @i <= 200 begin insert p values (@i, 'p'); set @i += 1; end
                set @i = 1;
                while @i <= 4000 begin insert c values (@i, @i % 200 + 1, @i); set @i += 1; end
                """,
                "select p.label, c.amt from p join c on c.pid = p.id"));
}
