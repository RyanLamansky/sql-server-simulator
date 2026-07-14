using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The in-process ADO surface keeps its long-standing fail-fast contract: the
/// first error in a batch throws immediately and later statements never run.
/// This is deliberately the opposite of the TDS wire path, which continues a
/// batch past a statement-terminating error (see
/// <c>BatchErrorContinuationTests</c> in <c>SqlServerSimulator.Tests.SqlClient</c>).
/// The wire opts in via <c>CreateResultSetsForCommand(command, continueOnError: true)</c>;
/// the in-process path leaves the default <see langword="false"/>, and its
/// reader filters outcomes on result sets so it never observes the wire-only
/// error outcome anyway.
/// </summary>
[TestClass]
public sealed class BatchErrorContinuationInProcTests
{
    [TestMethod]
    public void DropNonexistentTemp_ThenSelect_FailsFast()
        => _ = new Simulation().AssertSqlError("drop table #nope\nselect 1", 3701);

    [TestMethod]
    public void StatementErrorMidBatch_LaterInsertNeverRuns()
    {
        // One shared connection so the session #t survives the failed batch for
        // the follow-up count (a fresh-connection-per-call helper would drop it).
        using var connection = new Simulation().CreateOpenConnection();

        using var failing = connection.CreateCommand();
        failing.CommandText = "create table #t (a int); drop table #nope; insert #t values (1)";
        var ex = Throws<SimulatedSqlException>(() => failing.ExecuteNonQuery());
        AreEqual(3701, ex.Number);

        // The insert after the failed drop never executed — fail-fast aborts
        // the whole batch in-process (the wire would have continued and run it).
        using var count = connection.CreateCommand();
        count.CommandText = "select count(*) from #t";
        AreEqual(0, count.ExecuteScalar());
    }
}
