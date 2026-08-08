using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Over the wire, a statement-terminating error (severity 11-16) ends its
/// statement but the batch continues to the next one — real SQL Server's
/// default (non-XACT_ABORT) behavior, and the fix that lets SMO's all-DROP
/// temp-table cleanup batch run so Object Explorer can enumerate. Contrast
/// with the in-process ADO surface, which stays fail-fast (first error throws)
/// — see <c>BatchErrorContinuationInProcTests</c> in <c>SqlServerSimulator.Tests</c>.
/// </summary>
[TestClass]
public sealed class BatchErrorContinuationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task StatementError_MidBatch_LaterStatementStillRuns()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // drop of a nonexistent temp table raises Msg 3701 (severity 11);
        // the insert after it must still execute because the batch continues.
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand(
                "create table #t (a int); drop table #nope; insert #t values (1)",
                connection);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        });
        AreEqual(3701, ex.Number);
        AreEqual(1, ex.Errors.Count);

        // Proof the batch continued past the 3701: the insert landed a row.
        await using var count = new SqlCommand("select count(*) from #t", connection);
        AreEqual(1, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SmoStyleTempDropCleanup_AllRaise3701_SessionStaysUsable()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // The exact shape of SMO's Object-Explorer "Databases" refresh cleanup:
        // every drop is Msg 3701 on a fresh session. Real SQL Server runs them
        // all and surfaces one SqlException carrying every error token.
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand(
                "drop table #tmp_db_ars; drop table #tmp_db_ags; drop table #tmp_db_hadr_dbrs; drop table #tmp_sync_states",
                connection);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        });
        AreEqual(4, ex.Errors.Count);
        foreach (SqlError error in ex.Errors)
            AreEqual(3701, error.Number);

        // Session survived the cleanup batch, so SMO proceeds to enumerate.
        await using var ok = new SqlCommand("select 1", connection);
        AreEqual(1, await ok.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ResultSetBeforeError_FirstResultReadable_NextResultThrows()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select 1 as a; drop table #nope", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1, reader.GetInt32(0));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));

        // The error follows the first result set's DONE, so it surfaces when
        // the reader advances past that result set.
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
            await reader.NextResultAsync(TestContext.CancellationToken));
        AreEqual(3701, ex.Number);
    }

    [TestMethod]
    public async Task ExecuteScalar_TrailingError_DrainsBatch_Throws()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // ExecuteScalar drains the whole batch: the value of the first result
        // set is produced, but a trailing statement-terminating error surfaces
        // as a throw rather than the value being returned (oracle probe 5).
        await using var command = new SqlCommand("select 42; select 1/0", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(8134, ex.Number);
    }

    [TestMethod]
    public async Task BatchAbortingError_AbortsBatch_LaterStatementDoesNotRun()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        Wire.ExecInProc(simulation, "create procedure dbo.p as select 1");

        // No SimulatedSqlException factory produces a class >= 17 (batch/
        // connection-terminating) severity, and deadlock (class 13) needs
        // concurrent sessions to provoke; NotSupportedException is the
        // reachable batch-aborting case here (WITH RESULT SETS' AS OBJECT
        // definition shorthand), surfacing as a Msg 50000 error token that
        // ends the batch. The insert after it must NOT run — the contrast with
        // continued errors above.
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand(
                "create table #t (a int); exec dbo.p with result sets (as object dbo.nothing); insert #t values (1)",
                connection);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        });
        AreEqual(50000, ex.Number);

        await using var count = new SqlCommand("select count(*) from #t", connection);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
