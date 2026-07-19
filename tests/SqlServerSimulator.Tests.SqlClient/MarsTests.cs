using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Multiple Active Result Sets (MARS) over the wire. With
/// <c>MultipleActiveResultSets=True</c> the server acks MARS in prelogin and
/// wraps every post-login TDS message in SMP (Session Multiplex Protocol,
/// [MC-SMP]) frames; SqlClient opens a new SMP session per concurrent command
/// so a second command runs while a reader is still open. All logical sessions
/// share one backing <see cref="SimulatedDbConnection"/> (one @@SPID, shared
/// temp tables and transaction), and engine execution is serialized —
/// cooperative multiplexing, never parallel execution. Semantics
/// probe-confirmed against SQL Server 2025 (2026-07-18).
/// </summary>
[TestClass]
public sealed class MarsTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string MarsExtra = ";MultipleActiveResultSets=True";

    private static void CancelAfter(SqlCommand command, int millis) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(millis);
            command.Cancel();
        });

    private static Simulation Seeded(int rows = 5)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int primary key, name nvarchar(50))");
        var values = string.Join(", ", Enumerable.Range(1, rows).Select(i => $"({i}, '{(char)('a' + i - 1)}')"));
        Wire.ExecInProc(simulation, $"insert into t values {values}");
        return simulation;
    }

    [TestMethod]
    public async Task OverlappingReaders_TwoDeep_NestedQueryPerRow()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        var seen = new List<string>();
        await using var outer = new SqlCommand("select id from t order by id", connection);
        await using (var reader = await outer.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.CancellationToken))
            {
                var id = reader.GetInt32(0);
                await using var inner = new SqlCommand("select name from t where id = @id", connection);
                _ = inner.Parameters.AddWithValue("@id", id);
                seen.Add((string)(await inner.ExecuteScalarAsync(TestContext.CancellationToken))!);
            }
        }

        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d", "e" }, seen);
    }

    [TestMethod]
    public async Task OverlappingReaders_ThreeDeep_NestedReadersInterleave()
    {
        var simulation = Seeded(3);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        var triples = new List<string>();
        await using var a = new SqlCommand("select id from t order by id", connection);
        await using var ra = await a.ExecuteReaderAsync(TestContext.CancellationToken);
        while (await ra.ReadAsync(TestContext.CancellationToken))
        {
            var i = ra.GetInt32(0);
            await using var b = new SqlCommand("select id from t where id >= @i order by id", connection);
            _ = b.Parameters.AddWithValue("@i", i);
            await using var rb = await b.ExecuteReaderAsync(TestContext.CancellationToken);
            while (await rb.ReadAsync(TestContext.CancellationToken))
            {
                var j = rb.GetInt32(0);
                await using var cmd = new SqlCommand("select name from t where id = @j", connection);
                _ = cmd.Parameters.AddWithValue("@j", j);
                triples.Add($"{i}:{j}:{await cmd.ExecuteScalarAsync(TestContext.CancellationToken)}");
            }
        }

        CollectionAssert.AreEqual(
            new[] { "1:1:a", "1:2:b", "1:3:c", "2:2:b", "2:3:c", "3:3:c" },
            triples);
    }

    [TestMethod]
    public async Task SessionReuse_CommandAfterReaderClose_Succeeds()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        // SqlClient reuses an SMP session id once its reader closes; run several
        // commands in sequence, each opening and closing a reader, to exercise it.
        for (var i = 1; i <= 5; i++)
        {
            await using var reader = new SqlCommand("select id from t order by id", connection);
            await using var r = await reader.ExecuteReaderAsync(TestContext.CancellationToken);
            _ = await r.ReadAsync(TestContext.CancellationToken);
            await using var scalar = new SqlCommand($"select {i}", connection);
            AreEqual(i, await scalar.ExecuteScalarAsync(TestContext.CancellationToken));
        }
    }

    [TestMethod]
    public async Task NonMars_SecondCommandWhileReaderOpen_RejectedClientSide()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);

        // No MultipleActiveResultSets: SqlClient rejects the overlap itself,
        // before anything reaches the server — the regression guard proving the
        // prelogin ack is strictly opt-in.
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var outer = new SqlCommand("select id from t order by id", connection);
        await using var reader = await outer.ExecuteReaderAsync(TestContext.CancellationToken);
        _ = await reader.ReadAsync(TestContext.CancellationToken);

        await using var second = new SqlCommand("select 1", connection);
        _ = await ThrowsExactlyAsync<InvalidOperationException>(
            async () => await second.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SharedState_SpidAndTempTable_AcrossSessions()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using (var mk = new SqlCommand("create table #s (x int); insert into #s values (42)", connection))
            _ = await mk.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var spid = new SqlCommand("select @@spid", connection);
        await using var reader = await spid.ExecuteReaderAsync(TestContext.CancellationToken);
        _ = await reader.ReadAsync(TestContext.CancellationToken);
        var spidOnFirst = reader.GetInt16(0);

        // A second session on the same connection sees the temp table (shared
        // connection state) and reports the same @@spid.
        await using var second = new SqlCommand("select x, @@spid from #s", connection);
        await using var r2 = await second.ExecuteReaderAsync(TestContext.CancellationToken);
        _ = await r2.ReadAsync(TestContext.CancellationToken);
        AreEqual(42, r2.GetInt32(0));
        AreEqual(spidOnFirst, r2.GetInt16(1));
    }

    [TestMethod]
    public async Task Transaction_SharedAcrossOverlappingCommands_RollbackUndoesAll()
    {
        var simulation = Seeded(3);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(TestContext.CancellationToken);
        await using (var insert = new SqlCommand("insert into t values (10, 'x')", connection, transaction))
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using (var outer = new SqlCommand("select id from t order by id", connection, transaction))
        await using (var reader = await outer.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            _ = await reader.ReadAsync(TestContext.CancellationToken);
            // A second command shares the one transaction while the reader is open.
            await using var insert2 = new SqlCommand("insert into t values (11, 'y')", connection, transaction);
            _ = await insert2.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await transaction.RollbackAsync(TestContext.CancellationToken);

        await using var count = new SqlCommand("select count(*) from t where id in (10, 11)", connection);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Transaction_CommandWithoutTransaction_RejectedClientSide()
    {
        // Probe-confirmed against SQL Server 2025: with a pending local
        // transaction, a command that omits its Transaction property is rejected
        // by SqlClient itself (InvalidOperationException) before any bytes hit
        // the wire — there is no server-side Msg 3997 to mirror.
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(TestContext.CancellationToken);
        await using (var noTransaction = new SqlCommand("select 1", connection))
        {
            _ = await ThrowsExactlyAsync<InvalidOperationException>(
                async () => await noTransaction.ExecuteScalarAsync(TestContext.CancellationToken));
        }

        await transaction.RollbackAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task LargeDrain_OnOneSession_WhileAnotherExecutes()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        // A large result streams on one session while short commands run on
        // another mid-drain — the send window must not stall the second session.
        await using var big = new SqlCommand("select value from generate_series(1, 20000)", connection);
        await using var reader = await big.ExecuteReaderAsync(TestContext.CancellationToken);
        long sum = 0;
        var interleaved = 0;
        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            sum += reader.GetInt32(0);
            if (sum % 1000 == 0 && interleaved < 3)
            {
                interleaved++;
                await using var probe = new SqlCommand("select count(*) from t", connection);
                AreEqual(5, await probe.ExecuteScalarAsync(TestContext.CancellationToken));
            }
        }

        AreEqual(20000L * 20001 / 2, sum);
        AreEqual(3, interleaved);
    }

    [TestMethod]
    public async Task Cancel_OneOfTwoActiveCommands_LeavesOtherReaderUsable()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        // Reader A stays open (its rows already buffered) while command B waits
        // and is cancelled. B's attention targets only B's session; A remains
        // fully readable afterward.
        await using var a = new SqlCommand("select id from t order by id", connection);
        await using var readerA = await a.ExecuteReaderAsync(TestContext.CancellationToken);
        _ = await readerA.ReadAsync(TestContext.CancellationToken);

        await using (var b = new SqlCommand("waitfor delay '00:00:30'", connection) { CommandTimeout = 0 })
        {
            CancelAfter(b, 200);
            _ = await ThrowsExactlyAsync<SqlException>(
                async () => await b.ExecuteNonQueryAsync(TestContext.CancellationToken));
        }

        var remaining = new List<int> { readerA.GetInt32(0) };
        while (await readerA.ReadAsync(TestContext.CancellationToken))
            remaining.Add(readerA.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, remaining);
    }

    [TestMethod]
    public async Task PooledConnectionReset_WithMars_ReusesAndWorks()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = Wire.ConnectionString(listener, ";Max Pool Size=1" + MarsExtra)
            .Replace("Pooling=False", "Pooling=True", StringComparison.Ordinal);

        for (var pass = 0; pass < 3; pass++)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(TestContext.CancellationToken);
            await using var outer = new SqlCommand("select id from t order by id", connection);
            await using var reader = await outer.ExecuteReaderAsync(TestContext.CancellationToken);
            _ = await reader.ReadAsync(TestContext.CancellationToken);
            await using var inner = new SqlCommand("select count(*) from t", connection);
            AreEqual(5, await inner.ExecuteScalarAsync(TestContext.CancellationToken));
        }
    }
}
