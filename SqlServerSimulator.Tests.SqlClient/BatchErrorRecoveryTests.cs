using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Over the wire, a batch-aborting name-resolution error (Msg 208 and kin)
/// streams the results of the statements before it, surfaces exactly one error
/// token, and stops — the statements after it never run. This mirrors the
/// DacFx reverse-engineering shape (every query ends with
/// <c>OPTION (USE HINT('FORCE_LEGACY_CARDINALITY_ESTIMATION'))</c>): before the
/// fix, a mid-batch reference to an unmodeled object left the parser abandoned
/// mid-statement and the dispatch loop re-entered on the OPTION clause's
/// leading <c>USE</c> token, spewing a Msg 911 / 319 / 102 cascade. The engine
/// now produces one clean per-statement error and the wire writes one error
/// token. Probe-confirmed against SQL Server 2025 (2026-07-16): a missing
/// object aborts the remaining batch (contrast the continued-error path in
/// <see cref="BatchErrorContinuationTests"/>).
/// </summary>
[TestClass]
public sealed class BatchErrorRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string DacFxTail = " option (use hint('FORCE_LEGACY_CARDINALITY_ESTIMATION'))";

    [TestMethod]
    public async Task DacFxShape_MissingObjectMidBatch_FirstResultReadable_NextResultThrowsSingleMsg208()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var batch =
            $"select * from (select 1 as x) as [_results]{DacFxTail};" +
            $"select * from (select * from sys.not_a_view) as [_results]{DacFxTail};" +
            $"select * from (select 3 as z) as [_results]{DacFxTail}";
        await using var command = new SqlCommand(batch, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        // The statement before the missing object streamed its result set.
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1, reader.GetInt32(0));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));

        // Advancing past it surfaces exactly one Msg 208 — no Msg 911 / 319 / 102
        // cascade from the abandoned OPTION (USE HINT(...)) tail.
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
            await reader.NextResultAsync(TestContext.CancellationToken));
        AreEqual(208, ex.Number);
        AreEqual(1, ex.Errors.Count);
    }

    [TestMethod]
    public async Task DacFxShape_MissingObjectMidBatch_ExactlyOneErrorToken_LaterStatementDoesNotRun()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);

        var errors = new List<int>();
        await using var connection = new SqlConnection(Wire.ConnectionString(listener));
        connection.FireInfoMessageEventOnUserErrors = true;
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                errors.Add(error.Number);
        };
        await connection.OpenAsync(TestContext.CancellationToken);

        var batch =
            $"select * from (select 1 as x) as [_results]{DacFxTail};" +
            $"select * from (select * from sys.not_a_view) as [_results]{DacFxTail};" +
            $"select * from (select 3 as z) as [_results]{DacFxTail}";
        await using var command = new SqlCommand(batch, connection);

        var seen = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                    seen.Add(reader.GetInt32(0));
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        }

        // Only the first statement's result arrived (batch aborted at the error);
        // the third statement's `z` never streamed.
        HasCount(1, seen);
        AreEqual(1, seen[0]);
        // Exactly one error, and it is the object miss — not a mangled 911 / 319.
        HasCount(1, errors);
        AreEqual(208, errors[0]);
    }

    [TestMethod]
    public async Task UseHintUnknownName_SurfacesMsg10715()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand(
            "select 1 option (use hint('BANANA_NOT_A_HINT'))", connection);
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
            _ = await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(10715, ex.Number);
    }
}
