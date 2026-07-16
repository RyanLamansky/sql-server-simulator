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
/// way. The in-process ADO surface reaches the same end state through its
/// fail-fast contract (first error throws, later statements never run — see
/// <see cref="BatchErrorContinuationInProcTests"/>); the wire-path contrast
/// (results before the error still arrive, exactly one error token, no
/// abandoned-mid-parse Msg 319 / 102 cascade) lives in
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
    public void SyntaxErrorMidBatch_AbortsBatch_FollowingDoesNotRun()
    {
        // A true syntax error aborts the batch on real SQL Server (probe:
        // `SELECT 1; SELECT FROM; SELECT 2` returns only Msg 156, no SELECT 2).
        // The in-process path reaches the same end state fail-fast.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table marker (n int)").ExecuteNonQuery();

        using var failing = connection.CreateCommand(
            "insert marker values (1); select from; insert marker values (2)");
        _ = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(1, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
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

    [TestMethod]
    public void SkipModeBranch_ToleratesMissingColumn_Unchanged()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            create table t (id int);
            if 1 = 0 select no_such_col from t;
            select 7
            """));

    [TestMethod]
    public void ContinuableError_DoesNotAbort_InProcStillFailsFast()
    {
        // Msg 3701 (drop missing) is statement-terminating, not batch-aborting:
        // the wire continues past it. The in-process path still fails fast — this
        // pins that the batch-abort carve-out didn't change the in-process
        // first-error-throws contract for a continuable error.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table marker (n int)").ExecuteNonQuery();
        using var failing = connection.CreateCommand(
            "insert marker values (1); drop table #nope; insert marker values (2)");
        var ex = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(3701, ex.Number);
        AreEqual(1, connection.CreateCommand("select count(*) from marker").ExecuteScalar());
    }
}
