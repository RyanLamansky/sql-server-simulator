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

    /// <summary>Cancels the command shortly after execution starts, off the calling thread.</summary>
    private static void CancelAfter(SqlCommand command, int millis) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(millis);
            command.Cancel();
        });

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
            CancelAfter(command, 200);
            var error = await ThrowsExactlyAsync<SqlException>(
                async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
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
            CancelAfter(command, 50);
            _ = await ThrowsExactlyAsync<SqlException>(async () =>
            {
                await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
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
            CancelAfter(command, 200);
            _ = await ThrowsExactlyAsync<SqlException>(
                async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
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
            CancelAfter(command, 200);
            _ = await ThrowsExactlyAsync<SqlException>(
                async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
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
            CancelAfter(batch, 200);
            _ = await ThrowsExactlyAsync<SqlException>(
                async () => await batch.ExecuteNonQueryAsync(TestContext.CancellationToken));
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
            CancelAfter(command, 200);
            _ = await ThrowsExactlyAsync<SqlException>(async () =>
            {
                await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
                do
                {
                    while (await reader.ReadAsync(TestContext.CancellationToken))
                    {
                    }
                }
                while (await reader.NextResultAsync(TestContext.CancellationToken));
            });
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
            CancelAfter(command, 0);
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
            CancelAfter(command, 200);
            _ = await ThrowsExactlyAsync<SqlException>(
                async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        }

        for (var i = 1; i <= 5; i++)
        {
            await using var command = new SqlCommand($"select {i}", connection);
            AreEqual(i, await command.ExecuteScalarAsync(TestContext.CancellationToken));
        }
    }
}
