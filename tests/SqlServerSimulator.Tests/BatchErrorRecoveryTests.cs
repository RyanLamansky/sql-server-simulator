using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Statement-scoped error handling for a name-resolution failure mid-batch.
/// Probe-confirmed against SQL Server 2025 (2026-07-16): a batch
/// <c>&lt;stmt A&gt;; &lt;missing-object stmt&gt;; &lt;stmt B&gt;</c> runs
/// statement A, surfaces exactly one Msg 208, and does NOT run statement B —
/// the missing object is a batch-aborting bind error, not a merely
/// statement-terminating one (unlike Msg 3701 / 8134 / a severity-16 RAISERROR,
/// which let the batch continue). Missing column (Msg 207), ambiguous column
/// (Msg 209), and unbindable multi-part identifiers (Msg 4104) abort the same
/// way. The in-process ADO surface now shares the wire's continue-on-error
/// engine: it drains the batch and surfaces the aggregated error(s) at
/// completion, so a batch-aborting miss still stops the following statements
/// (the dispatch loop breaks) while a statement-terminating error lets them
/// run — see <see cref="BatchErrorContinuationInProcTests"/>. The wire-path
/// contrast (results before the error still arrive, exactly one error token,
/// no abandoned-mid-parse Msg 319 / 102 cascade) lives in
/// <c>BatchErrorRecoveryTests</c> in <c>SqlServerSimulator.Tests.SqlClient</c>.
/// </summary>
[TestClass]
public sealed class BatchErrorRecoveryTests
{
    [TestMethod]
    public void MissingObjectMidBatch_PriorStatementRan_FollowingDidNot()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table marker (n int)").ExecuteNonQuery();

        using var failing = connection.CreateCommand(
            "insert marker values (1); select * from dbo.does_not_exist_xyz; insert marker values (2)");
        var ex = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(208, ex.Number);

        // The insert before the missing object committed; the one after never ran.
        AreEqual(1, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
        AreEqual(1, connection.CreateCommand("select max(n) from marker").ExecuteScalar());
    }

    [TestMethod]
    public void MissingCatalogViewMidBatch_AbortsBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table marker (n int)").ExecuteNonQuery();

        using var failing = connection.CreateCommand(
            "insert marker values (1); select * from sys.not_a_view; insert marker values (2)");
        var ex = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(208, ex.Number);
        AreEqual(1, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
    }

    [TestMethod]
    public void MissingColumnMidBatch_AbortsBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table marker (n int)").ExecuteNonQuery();

        using var failing = connection.CreateCommand(
            "insert marker values (1); select no_such_col from marker; insert marker values (2)");
        var ex = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(207, ex.Number);
        AreEqual(1, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
    }

    [TestMethod]
    public void SyntaxErrorMidBatch_Continues_FollowingRuns()
    {
        // Accepted divergence: a true syntax error aborts the batch on real SQL
        // Server (it fails at compile), but the simulator interleaves parse and
        // execution and can't tell a parse-origin error (Msg 156, class 15)
        // from a runtime one — so a mid-batch syntax error is statement-
        // terminating and the batch continues. This is the same parse-vs-runtime
        // divergence documented for the wire path; unifying the engine extends
        // it to the in-process surface. The insert after the syntax error runs.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table marker (n int)").ExecuteNonQuery();

        using var failing = connection.CreateCommand(
            "insert marker values (1); select from; insert marker values (2)");
        _ = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(2, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
    }

    /// <summary>
    /// An un-taken IF branch that names a missing object still compiles-and-
    /// discards via deferred name resolution — the batch-abort carve-out is
    /// scoped to top-level statements, not skip-mode, so this stays intact.
    /// </summary>
    [TestMethod]
    public void SkipModeBranch_ToleratesMissingObject_Unchanged()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            if 1 = 0 select * from dbo.does_not_exist_xyz;
            select 7
            """));

    /// <summary>
    /// A missing column on a <em>resolvable</em> table aborts the batch even
    /// from an un-taken IF branch. Probe-confirmed (SQL Server 2025,
    /// 2026-07-17): real SQL Server binds the columns of an existing table at
    /// compile time and raises Msg 207 regardless of the branch being dead, so
    /// the statement after the IF never runs. Deferred name resolution applies
    /// only when the base object is itself missing (see
    /// <see cref="SkipModeBranch_ToleratesMissingObject_Unchanged"/>) — a
    /// resolvable table's columns bind eagerly.
    /// </summary>
    [TestMethod]
    public void SkipModeBranch_MissingColumnOnResolvableTable_AbortsBatch()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int); create table marker (n int)").ExecuteNonQuery();
        using var failing = connection.CreateCommand(
            "insert marker values (1); if 1 = 0 select no_such_col from t; insert marker values (2)");
        var ex = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(207, ex.Number);
        AreEqual(1, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
    }

    [TestMethod]
    public void ContinuableError_DoesNotAbort_FollowingRuns()
    {
        // Msg 3701 (drop missing) is statement-terminating, not batch-aborting:
        // the batch continues past it on both front doors. In-process,
        // ExecuteNonQuery drains the whole batch — both inserts land — and
        // surfaces the 3701 at completion. Contrast the batch-aborting bind
        // errors above, where the following insert never runs.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table marker (n int)").ExecuteNonQuery();
        using var failing = connection.CreateCommand(
            "insert marker values (1); drop table #nope; insert marker values (2)");
        var ex = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(3701, ex.Number);
        AreEqual(2, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
    }
}
