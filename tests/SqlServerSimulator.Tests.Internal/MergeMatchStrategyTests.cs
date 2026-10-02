using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for MERGE's match phase (<c>Simulation.Merge.cs</c>):
/// an indexed target seeks per source row, an unindexed one hashes the source by
/// the ON's equality keys, and only an ON with no <c>target = source</c> conjunct
/// falls back to the O(target × source) scan. The correctness suite passes under
/// any of the three, so a silent fall-back to the quadratic walk would otherwise
/// go unnoticed until a real workload hung. Reads the opt-in
/// <see cref="JoinDiagnostics"/> trace (recorded where the strategy is chosen)
/// rather than timing, so the guard is exact and non-flaky.
/// </summary>
[TestClass]
public sealed class MergeMatchStrategyTests
{
    // Heap target — no PRIMARY KEY and no index, so nothing on it can be seeked
    // and the match phase has only the scan or the source hash to choose from.
    private const string HeapSetup = """
        create table t (id int, k int, v int);
        insert t values (1, 10, 100), (2, 20, 200)
        """;

    private static List<string> CaptureStrategies(string merge) => CaptureStrategies([HeapSetup], merge);

    private static List<string> CaptureStrategies(string[] setupBatches, string merge)
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
            command.CommandText = merge;
            _ = command.ExecuteNonQuery();
            return JoinDiagnostics.Sink;
        }
        finally
        {
            JoinDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void UnindexedTarget_EquiOn_HashesTheSource()
        => Contains("Merge:HashMatch(keys=1,residual=0)", CaptureStrategies("""
            merge t using (values (1, 11)) as s (id, v) on t.id = s.id
            when matched then update set v = s.v;
            """));

    /// <summary>
    /// The ON's non-equality conjunct isn't a hash key — it stays a residual
    /// re-checked per probed pair.
    /// </summary>
    [TestMethod]
    public void UnindexedTarget_ExtraOnCondition_KeepsItAsResidual()
        => Contains("Merge:HashMatch(keys=1,residual=1)", CaptureStrategies("""
            merge t using (values (1, 11)) as s (id, v) on t.id = s.id and t.v > s.v
            when matched then update set v = s.v;
            """));

    [TestMethod]
    public void UnindexedTarget_CompositeEquiOn_HashesBothColumns()
        => Contains("Merge:HashMatch(keys=2,residual=0)", CaptureStrategies("""
            merge t using (values (1, 10, 11)) as s (id, k, v) on t.id = s.id and t.k = s.k
            when matched then update set v = s.v;
            """));

    /// <summary>
    /// An unqualified operand naming only a source column reads the source, so
    /// the pair still splits across the two sides and hashes.
    /// </summary>
    [TestMethod]
    public void UnqualifiedSourceOperand_StillHashes()
        => Contains("Merge:HashMatch(keys=1,residual=0)", CaptureStrategies("""
            merge t using (values (1, 11)) as s (sid, sv) on t.id = sid
            when matched then update set v = sv;
            """));

    /// <summary>
    /// An unqualified operand only the target answers to splits the pair across
    /// the two sides as a qualified one does. (One both sides answer to is
    /// real's Msg 209.)
    /// </summary>
    [TestMethod]
    public void UnqualifiedTargetOperand_StillHashes()
        => Contains("Merge:HashMatch(keys=1,residual=0)", CaptureStrategies("""
            merge t using (values (1, 11)) as s (sid, sv) on id = s.sid
            when matched then update set v = s.sv;
            """));

    /// <summary>
    /// Key values coerce to the <c>=</c> operator's own promotion target before
    /// hashing, so a pair of different-but-promotable types is still a key.
    /// </summary>
    [TestMethod]
    public void MixedNumericKeyTypes_StillHash()
        => Contains("Merge:HashMatch(keys=1,residual=0)", CaptureStrategies(
            ["create table big_t (id bigint, v int); insert big_t values (1, 100)"],
            """
            merge big_t using (values (1, 11)) as s (id, v) on big_t.id = s.id
            when matched then update set v = s.v;
            """));

    /// <summary>
    /// An ON settled false while compiling (EF Core's multi-row insert writes
    /// ON 1=0) reads no target row at all, so no strategy runs; a NOT MATCHED
    /// BY SOURCE clause still needs the scan to find the unmatched targets.
    /// </summary>
    [TestMethod]
    public void ConstantFalseOn_ReadsNoTarget()
        => Contains("Merge:NoTargetRead", CaptureStrategies("""
            merge t using (values (3, 30)) as s (id, v) on 1 = 0
            when not matched then insert (id, k, v) values (s.id, 0, s.v);
            """));

    [TestMethod]
    public void ConstantFalseOn_WithNotMatchedBySource_KeepsTheScan()
        => Contains("Merge:Scan", CaptureStrategies("""
            merge t using (values (3, 30)) as s (id, v) on 1 = 0
            when not matched by source then delete;
            """));

    [TestMethod]
    public void NonEquiOn_KeepsTheScan()
        => Contains("Merge:Scan", CaptureStrategies("""
            merge t using (values (1, 11)) as s (id, v) on t.id < s.id
            when matched then update set v = s.v;
            """));

    /// <summary>
    /// Both operands on the same side is an ordinary filter, not a join key —
    /// there's nothing to hash the source by.
    /// </summary>
    [TestMethod]
    public void SameSideEquality_IsNotAKey()
        => Contains("Merge:Scan", CaptureStrategies("""
            merge t using (values (1, 11)) as s (id, v) on t.id = t.k
            when matched then update set v = s.v;
            """));

    /// <summary>
    /// Only a bare column reference is a key operand — that keeps side
    /// classification one exact name lookup, so a computed operand can never be
    /// misattributed to a side. It stays in the predicate instead.
    /// </summary>
    [TestMethod]
    public void ComputedOnOperand_DeclinesToTheScan()
        => Contains("Merge:Scan", CaptureStrategies("""
            merge t using (values (1, 11)) as s (id, v) on t.id = s.id + 0
            when matched then update set v = s.v;
            """));

    /// <summary>
    /// An indexed target still inverts the loop — the source hash is the
    /// unindexed target's answer, not a replacement for the seek.
    /// </summary>
    [TestMethod]
    public void IndexedTarget_SeeksPerSourceRow()
        => Contains("Merge:TargetSeek", CaptureStrategies(
            ["create table pk_t (id int primary key, v int); insert pk_t values (1, 100)"],
            """
            merge pk_t using (values (1, 11)) as s (id, v) on pk_t.id = s.id
            when matched then update set v = s.v;
            """));

    /// <summary>
    /// A view target can't be seeked (its column names don't map to the base
    /// heap), so it takes the same match phase an unindexed table does — hashing
    /// the source even though the base carries a primary key.
    /// </summary>
    [TestMethod]
    public void ViewTarget_HashesTheSource()
        => Contains("Merge:HashMatch(keys=1,residual=0)", CaptureStrategies(
            [
                "create table base_t (id int primary key, v int); insert base_t values (1, 100)",
                "create view v_base as select id, v from base_t",
            ],
            """
            merge v_base using (values (1, 11)) as s (id, v) on v_base.id = s.id
            when matched then update set v = s.v;
            """));
}
