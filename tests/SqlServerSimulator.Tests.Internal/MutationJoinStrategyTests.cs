using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the row-source passes a joined UPDATE / DELETE
/// takes before it enumerates (<c>Selection.PrepareMutationJoinSources</c>): a
/// deferred source materializes once and keys into the O(L + R) hash path
/// instead of re-executing per target row, a WHERE equality narrows the joined
/// source, and the mutation <b>target</b> is never narrowed. Every one of those
/// is result-transparent — <c>Tests</c>' <c>MutationJoinSourceTests</c> pins the
/// rows — so a silent revert to the per-target-row re-execution would otherwise
/// go unnoticed until a real workload crawled. Reads the opt-in
/// <see cref="JoinDiagnostics"/> / <see cref="IndexSeekDiagnostics"/> traces
/// rather than timing, so the guard is exact and non-flaky.
/// </summary>
[TestClass]
public sealed class MutationJoinStrategyTests
{
    private const string Setup = """
        create table t (id int not null primary key, v int not null);
        create table s (sid int not null primary key, id int not null, k int not null, w int not null);
        create index ix_s_k on s (k);
        insert t values (1, 10), (2, 20), (3, 30);
        insert s values (1, 1, 7, 100), (2, 1, 7, 5), (3, 2, 8, 200), (4, 3, 7, 300), (5, 3, 8, 1)
        """;

    /// <summary>The join strategies a mutation resolved to, over <see cref="Setup"/>.</summary>
    private static List<string> JoinTrace(string mutation)
    {
        var connection = Prepared();
        JoinDiagnostics.Sink = [];
        try
        {
            _ = Run(connection, mutation);
            return JoinDiagnostics.Sink;
        }
        finally
        {
            JoinDiagnostics.Sink = null;
        }
    }

    /// <summary>The seek / scan decisions a mutation reached, over <see cref="Setup"/>.</summary>
    private static List<string> SeekTrace(string mutation)
    {
        var connection = Prepared();
        IndexSeekDiagnostics.Sink = [];
        try
        {
            _ = Run(connection, mutation);
            return IndexSeekDiagnostics.Sink;
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    private static System.Data.Common.DbConnection Prepared()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        _ = Run(connection, Setup);
        return connection;
    }

    private static int Run(System.Data.Common.DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command.ExecuteNonQuery();
    }

    // ---- the deferred source materializes -----------------------------------

    /// <summary>
    /// The motivating shape: the grouped derived table becomes a plain row list,
    /// so the join level that used to re-execute it per target row hashes it.
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedTable_HashesTheMaterializedSource()
        => Contains("Inner:HashMatch(keys=1,residual=0)", JoinTrace(
            "update t set v = d.total from t join (select id, sum(w) as total from s group by id) d on d.id = t.id"));

    /// <summary>The CTE spelling materializes the same way.</summary>
    [TestMethod]
    public void UpdateFromCteReference_HashesTheMaterializedSource()
        => Contains("Inner:HashMatch(keys=1,residual=0)", JoinTrace("""
            with d as (select id, sum(w) as total from s group by id)
            update t set v = d.total from t join d on d.id = t.id
            """));

    /// <summary>A view source materializes like a derived table.</summary>
    [TestMethod]
    public void UpdateFromViewSource_HashesTheMaterializedSource()
    {
        var connection = Prepared();
        _ = Run(connection, "create view totals as select id, sum(w) as total from s group by id");
        JoinDiagnostics.Sink = [];
        try
        {
            _ = Run(connection, "update t set v = d.total from t join totals d on d.id = t.id");
            Contains("Inner:HashMatch(keys=1,residual=0)", JoinDiagnostics.Sink);
        }
        finally
        {
            JoinDiagnostics.Sink = null;
        }
    }

    /// <summary>The DELETE form takes the same pass.</summary>
    [TestMethod]
    public void DeleteFromDerivedTable_HashesTheMaterializedSource()
        => Contains("Inner:HashMatch(keys=1,residual=0)", JoinTrace(
            "delete t from t join (select id, sum(w) as total from s group by id) d on d.id = t.id"));

    /// <summary>A LEFT-joined derived source materializes and hashes too.</summary>
    [TestMethod]
    public void UpdateLeftJoinDerivedTable_HashesTheMaterializedSource()
        => Contains("Left:HashMatch(keys=1,residual=0)", JoinTrace(
            "update t set v = isnull(d.total, -1) from t left join (select id, sum(w) as total from s group by id) d on d.id = t.id"));

    /// <summary>
    /// A per-call-varying built-in inside the body declines the reuse — real
    /// re-draws <c>NEWID()</c> per target row — so that source keeps its
    /// per-outer-row execution and the level stays a nested loop.
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedTableDrawingNewid_KeepsTheNestedLoop()
        => Contains("Inner:NestedLoops", JoinTrace(
            "update t set v = d.w from t join (select id, w, newid() as g from s) d on d.id = t.id"));

    /// <summary>
    /// <c>CROSS APPLY</c>'s right side is lateral by construction, so the pass
    /// leaves it re-executing per outer row whatever its body reads.
    /// </summary>
    [TestMethod]
    public void UpdateFromCrossApply_KeepsTheNestedLoop()
        => Contains("CrossApply:NestedLoops", JoinTrace(
            "update t set v = d.w from t cross apply (select max(w) as w from s where s.id = t.id) d"));

    // ---- the WHERE narrowing, and the target it leaves alone -----------------

    /// <summary>
    /// An equality on the joined source's indexed column seeks that source
    /// before the join runs, exactly as the read path narrows a SELECT's.
    /// </summary>
    [TestMethod]
    public void UpdateWithEqualityOnTheJoinedSource_SeeksThatSource()
        => Contains("Seek(s)", SeekTrace("update t set v = s.w from t join s on s.id = t.id where s.k = 7"));

    /// <summary>The DELETE form narrows the same way.</summary>
    [TestMethod]
    public void DeleteWithEqualityOnTheJoinedSource_SeeksThatSource()
        => Contains("Seek(s)", SeekTrace("delete t from t join s on s.id = t.id where s.k = 8"));

    /// <summary>
    /// The target's own primary-key equality is left as a residual filter: the
    /// write pipeline reads the target through an address side-channel keyed by
    /// the instances its enumerator yields, so that source stays as it was. The
    /// joined source in the same statement still seeks, which is what shows the
    /// pass ran at all rather than declining wholesale.
    /// </summary>
    [TestMethod]
    public void UpdateWithEqualityOnBothSides_SeeksTheJoinedSourceAndNotTheTarget()
    {
        var trace = SeekTrace("update t set v = s.w from t join s on s.id = t.id where t.id = 2 and s.k = 8");
        Contains("Seek(s)", trace);
        DoesNotContain("Seek(t)", trace);
    }

    /// <summary>The DELETE form leaves its target alone the same way.</summary>
    [TestMethod]
    public void DeleteWithEqualityOnBothSides_SeeksTheJoinedSourceAndNotTheTarget()
    {
        var trace = SeekTrace("delete t from t join s on s.id = t.id where t.id = 3 and s.k = 7");
        Contains("Seek(s)", trace);
        DoesNotContain("Seek(t)", trace);
    }

    /// <summary>
    /// A joined source owing a SERIALIZABLE phantom fence keeps its whole-table
    /// scan: the fence is settled inside the seek attempt, so probing it would
    /// change which key ranges the statement locks and when.
    /// </summary>
    [TestMethod]
    public void UpdateWithHoldlockOnTheJoinedSource_DeclinesTheNarrowing()
        => DoesNotContain("Seek(s)",
            SeekTrace("update t set v = s.w from t join s with (holdlock) on s.id = t.id where s.k = 7"));
}
