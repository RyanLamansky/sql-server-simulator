using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Mid-stream attention (TDS cancel) handling: a client <c>SqlCommand.Cancel()</c>
/// or an expiring <c>CommandTimeout</c> sends a TDS attention (packet type 6)
/// while a batch is executing or streaming. The listener notices it at a safe
/// point, aborts the batch, and replies with a DONE token carrying the
/// DONE_ATTN flag; the session stays alive and reusable. SqlClient synthesizes
/// the surfaced exception client-side (Msg -2 "Execution Timeout Expired" for a
/// timeout, Msg 0 "Operation cancelled by user" for an explicit cancel) — the
/// server emits no error token, only the acknowledgment. Semantics
/// probe-confirmed against SQL Server 2025 (2026-07-18).
/// </summary>
[TestClass]
public sealed class AttentionTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string LongWait = "waitfor delay '00:00:30'";

    /// <summary>
    /// Cancels the command repeatedly until the in-flight execution completes.
    /// A cancel landing before the batch starts executing is a documented
    /// <see cref="SqlCommand.Cancel"/> no-op, so a single timer-fired cancel
    /// can miss on a stalled runner and let the 30-second WAITFOR run to
    /// natural completion; retrying until the task transitions guarantees an
    /// attention lands mid-execution.
    /// </summary>
    private static async Task CancelUntilComplete(SqlCommand command, Task execution, CancellationToken cancellationToken)
    {
        while (!execution.IsCompleted)
        {
            await Task.Delay(100, cancellationToken);
            command.Cancel();
        }
    }

    private static async Task AssertSessionReusableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var probe = new SqlCommand("select 42", connection);
        AreEqual(42, await probe.ExecuteScalarAsync(cancellationToken));
    }

    [TestMethod]
    public async Task CommandTimeout_OnWaitfor_RaisesTimeoutAndSessionStaysReusable()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var command = new SqlCommand(LongWait, connection) { CommandTimeout = 1 })
        {
            var error = await ThrowsExactlyAsync<SqlException>(
                async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
            AreEqual(-2, error.Number);
        }

        await AssertSessionReusableAsync(connection, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Cancel_DuringWaitfor_RaisesCancelAndSessionStaysReusable()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var command = new SqlCommand(LongWait, connection) { CommandTimeout = 0 })
        {
            var execution = command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            await CancelUntilComplete(command, execution, TestContext.CancellationToken);
            var error = await ThrowsExactlyAsync<SqlException>(async () => await execution);
            AreEqual(0, error.Number);
        }

        await AssertSessionReusableAsync(connection, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Cancel_MidLargeResult_AbortsAndSessionStaysReusable()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        const string bigResult = "select value, replicate('x', 200) from generate_series(1, 500000)";
        await using (var command = new SqlCommand(bigResult, connection) { CommandTimeout = 0 })
        {
            // Cancel only after the first row arrives, so the command is
            // provably mid-execution. A timer-based cancel races two ways on a
            // slow runner: landing before execution starts is a documented
            // Cancel() no-op (the drain then completes with no exception), and
            // landing during ExecuteReaderAsync's setup trips a client-side
            // SqlClient race that surfaces InvalidOperationException instead
            // of the cancel SqlException.
            _ = await ThrowsExactlyAsync<SqlException>(async () =>
            {
                await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
                IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
                command.Cancel();
                while (await reader.ReadAsync(TestContext.CancellationToken))
                {
                }
            });
        }

        await AssertSessionReusableAsync(connection, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Cancel_WithOpenTransaction_DefaultXactAbort_LeavesTransactionOpen()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var begin = new SqlCommand("begin tran; insert t values (1)", connection))
            _ = await begin.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using (var command = new SqlCommand(LongWait, connection) { CommandTimeout = 0 })
        {
            var execution = command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            await CancelUntilComplete(command, execution, TestContext.CancellationToken);
            _ = await ThrowsExactlyAsync<SqlException>(async () => await execution);
        }

        // XACT_ABORT OFF (the default): the transaction survives the cancel.
        await using (var trancount = new SqlCommand("select @@trancount", connection))
            AreEqual(1, await trancount.ExecuteScalarAsync(TestContext.CancellationToken));
        await using (var rows = new SqlCommand("select count(*) from t", connection))
            AreEqual(1, await rows.ExecuteScalarAsync(TestContext.CancellationToken));

        await using (var rollback = new SqlCommand("rollback", connection))
            _ = await rollback.ExecuteNonQueryAsync(TestContext.CancellationToken);
        await using var afterRollback = new SqlCommand("select count(*) from t", connection);
        AreEqual(0, await afterRollback.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Cancel_WithOpenTransaction_XactAbortOn_RollsTransactionBack()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var begin = new SqlCommand("set xact_abort on; begin tran; insert t values (1)", connection))
            _ = await begin.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using (var command = new SqlCommand(LongWait, connection) { CommandTimeout = 0 })
        {
            var execution = command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            await CancelUntilComplete(command, execution, TestContext.CancellationToken);
            _ = await ThrowsExactlyAsync<SqlException>(async () => await execution);
        }

        // XACT_ABORT ON: the cancel rolls the transaction back.
        await using (var trancount = new SqlCommand("select @@trancount", connection))
            AreEqual(0, await trancount.ExecuteScalarAsync(TestContext.CancellationToken));
        await using var rows = new SqlCommand("select count(*) from t", connection);
        AreEqual(0, await rows.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Cancel_MidBatch_DiscardsRemainingStatements()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // The first statement commits (autocommit); the cancel fires during the
        // WAITFOR, so the batch aborts at the statement boundary and the third
        // statement never runs — statement-boundary atomicity.
        var batchText = $"insert t values (1); {LongWait}; insert t values (2)";
        await using (var batch = new SqlCommand(batchText, connection) { CommandTimeout = 0 })
        {
            var execution = batch.ExecuteNonQueryAsync(TestContext.CancellationToken);
            await CancelUntilComplete(batch, execution, TestContext.CancellationToken);
            _ = await ThrowsExactlyAsync<SqlException>(async () => await execution);
        }

        await using var rows = new SqlCommand("select count(*) from t", connection);
        AreEqual(1, await rows.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Cancel_DuringParameterizedRpc_AbortsAndSessionStaysReusable()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var command = new SqlCommand($"select @p; {LongWait}", connection) { CommandTimeout = 0 })
        {
            _ = command.Parameters.AddWithValue("@p", 7);
            // The drain blocks inside the first ReadAsync: the server holds
            // the tiny first result set in its TDS send buffer until the
            // batch ends (probe-confirmed against real SQL Server), so a
            // cancel can't be sequenced after an observed row — it has to
            // land while the read is parked, retried until it takes.
            var drain = DrainAsync(command, TestContext.CancellationToken);
            await CancelUntilComplete(command, drain, TestContext.CancellationToken);
            _ = await ThrowsExactlyAsync<SqlException>(async () => await drain);

            static async Task DrainAsync(SqlCommand command, CancellationToken cancellationToken)
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                    }
                }
                while (await reader.NextResultAsync(cancellationToken));
            }
        }

        await AssertSessionReusableAsync(connection, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task Cancel_RacingNaturalCompletion_DoesNotHang()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // Fire a cancel around a query that completes almost immediately: the
        // attention may land before, during, or after natural completion. Every
        // outcome must acknowledge without hanging, and the session must survive.
        for (var i = 0; i < 30; i++)
        {
            await using var command = new SqlCommand("select 1", connection) { CommandTimeout = 5 };
            _ = Task.Run(command.Cancel, TestContext.CancellationToken);
            try
            {
                await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
                while (await reader.ReadAsync(TestContext.CancellationToken))
                {
                }
            }
            catch (SqlException)
            {
                // A cancel that landed mid-flight surfaces here; tolerated.
            }
            catch (InvalidOperationException)
            {
                // A cancel that beat ExecuteReaderAsync to the starting line is
                // rejected client-side by SqlClient before anything reaches the
                // server; also a valid race outcome.
            }
        }

        await AssertSessionReusableAsync(connection, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task SequentialCommands_AfterCancel_AllSucceed()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var command = new SqlCommand(LongWait, connection) { CommandTimeout = 0 })
        {
            var execution = command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            await CancelUntilComplete(command, execution, TestContext.CancellationToken);
            _ = await ThrowsExactlyAsync<SqlException>(async () => await execution);
        }

        for (var i = 1; i <= 5; i++)
        {
            await using var command = new SqlCommand($"select {i}", connection);
            AreEqual(i, await command.ExecuteScalarAsync(TestContext.CancellationToken));
        }
    }
}
