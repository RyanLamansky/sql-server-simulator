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
            var execution = b.ExecuteNonQueryAsync(TestContext.CancellationToken);
            await CancelUntilComplete(b, execution, TestContext.CancellationToken);
            _ = await ThrowsExactlyAsync<SqlException>(async () => await execution);
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
            // The request carrying the reset keeps the settings it leaves.
            await using (var first = new SqlCommand("select isdate('13/01/2020'); set dateformat dmy", connection))
                AreEqual(0, await first.ExecuteScalarAsync(TestContext.CancellationToken));
            await using (var second = new SqlCommand("select isdate('13/01/2020')", connection))
                AreEqual(1, await second.ExecuteScalarAsync(TestContext.CancellationToken));
            await using var outer = new SqlCommand("select id from t order by id", connection);
            await using var reader = await outer.ExecuteReaderAsync(TestContext.CancellationToken);
            _ = await reader.ReadAsync(TestContext.CancellationToken);
            await using var inner = new SqlCommand("select count(*) from t", connection);
            AreEqual(5, await inner.ExecuteScalarAsync(TestContext.CancellationToken));
        }
    }

    /// <summary>
    /// A MARS connection scopes a transaction a batch begins by SQL text to
    /// that batch: one left open is rolled back at the batch's end with
    /// Msg 3997, no other request needing to be active (probed 2026-09-30
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task BeginTran_LeftOpenByABatch_RollsBackWithMsg3997()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using (var begin = new SqlCommand("begin tran; insert t values (6, 'f')", connection))
        {
            var error = await ThrowsExactlyAsync<SqlException>(async () => await begin.ExecuteNonQueryAsync(TestContext.CancellationToken));
            AreEqual(3997, error.Number);
            AreEqual(1, error.LineNumber);
        }

        await using var state = new SqlCommand("select concat(@@trancount, ',', (select count(*) from t))", connection);
        AreEqual("0,5", await state.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// While a reader is still receiving a large result, a transaction-manager
    /// begin is Msg 3988, and a commit is Msg 3981 that rolls the transaction
    /// back, after which requests are Msg 3989 until the reader finishes
    /// (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task TransactionRequests_WhileAReaderIsPending_AreRefused()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table big (id int primary key); insert big select value from generate_series(1, 20000); create table logt (v int)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using (var pending = new SqlCommand("select id from big order by id", connection))
        await using (var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(3988, Throws<SqlException>(connection.BeginTransaction).Number);
        }

        var transaction = connection.BeginTransaction();
        await using (var insert = new SqlCommand("insert logt values (1)", connection, transaction))
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        await using (var pending = new SqlCommand("select id from big order by id", connection, transaction))
        await using (var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(3981, Throws<SqlException>(transaction.Commit).Number);
            await using var refused = new SqlCommand("select 1", connection);
            AreEqual(3989, (await ThrowsExactlyAsync<SqlException>(async () => await refused.ExecuteScalarAsync(TestContext.CancellationToken))).Number);
        }

        await using var state = new SqlCommand("select concat(@@trancount, ',', (select count(*) from logt))", connection);
        AreEqual("0,0", await state.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// The same refusal by SQL text: a <c>COMMIT</c> that would end the
    /// transaction (state 1) or a <c>SAVE TRANSACTION</c> (state 2) while a
    /// reader is pending rolls the transaction back, where a nested one only
    /// counts down (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("commit", 1)]
    [DataRow("save tran s", 2)]
    public async Task TransactionStatements_WhileAReaderIsPending_AreMsg3981(string statement, int state)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table big (id int primary key); insert big select value from generate_series(1, 20000)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        var transaction = connection.BeginTransaction();
        await using (var pending = new SqlCommand("select id from big order by id", connection, transaction))
        await using (var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            await using var nested = new SqlCommand("begin tran; commit; select @@trancount", connection, transaction);
            AreEqual(1, await nested.ExecuteScalarAsync(TestContext.CancellationToken));
            await using var refused = new SqlCommand(statement, connection, transaction);
            var error = await ThrowsExactlyAsync<SqlException>(async () => await refused.ExecuteNonQueryAsync(TestContext.CancellationToken));
            AreEqual(3981, error.Number);
            AreEqual(state, error.State);
        }

        await using var trancount = new SqlCommand("select @@trancount", connection);
        AreEqual(0, await trancount.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// A table whose full scan is far more than a MARS response gets ahead of
    /// its client, so a reader over it stays pending while it reads.
    /// </summary>
    private static Simulation WideRows()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table big (id int primary key, v int not null, pad char(2000) not null default 'x'); insert big (id, v) select value, 0 from generate_series(1, 200)");
        return simulation;
    }

    /// <summary>Reads the rest of a reader's batch, returning the error its end raised, if any.</summary>
    private async Task<SqlException?> DrainAsync(SqlDataReader reader)
    {
        try
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                {
                }
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
            return null;
        }
        catch (SqlException error)
        {
            return error;
        }
    }

    /// <summary>
    /// A reader running outside any transaction doesn't stop another request
    /// beginning, saving and committing its own (probed 2026-10-06 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task AutocommitReader_LeavesAnotherRequestFreeToCommit()
    {
        var simulation = WideRows();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using var pending = new SqlCommand("select id, pad from big order by id", connection);
        await using var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));

        await using var own = new SqlCommand("begin tran; save tran s; update big set v = 5 where id = 1; commit; select @@trancount", connection);
        AreEqual(0, await own.ExecuteScalarAsync(TestContext.CancellationToken));
        await using var state = new SqlCommand("select concat(@@trancount, ',', (select v from big where id = 1))", connection);
        AreEqual("0,5", await state.ExecuteScalarAsync(TestContext.CancellationToken));
        IsNull(await this.DrainAsync(reader));
    }

    /// <summary>
    /// A rollback of the transaction a pending reader works in goes ahead, but
    /// every request meanwhile is Msg 3989 and the reader's batch carries on
    /// doomed, ending with Msg 3998 at line 1 (probed 2026-10-06 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public async Task RollbackUnderAPendingReader_EndsItWithMsg3998()
    {
        var simulation = WideRows();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        var transaction = connection.BeginTransaction();
        await using (var pending = new SqlCommand("select id, pad from big order by id", connection, transaction))
        await using (var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            await using (var write = new SqlCommand("update big set v = 5 where id = 1", connection, transaction))
                AreEqual(1, await write.ExecuteNonQueryAsync(TestContext.CancellationToken));
            transaction.Rollback();
            await using var refused = new SqlCommand("select 1", connection);
            AreEqual(3989, (await ThrowsExactlyAsync<SqlException>(async () => await refused.ExecuteScalarAsync(TestContext.CancellationToken))).Number);
            var end = await this.DrainAsync(reader);
            IsNotNull(end);
            AreEqual(3998, end.Number);
            AreEqual(1, end.LineNumber);
        }

        await using var state = new SqlCommand("select concat(@@trancount, ',', (select v from big where id = 1))", connection);
        AreEqual("0,0", await state.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// A commit refused under a pending reader working in the transaction —
    /// a parameterized one, sent as an RPC, included — ends that reader with
    /// Msg 3998 too (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task CommitRefusedUnderAPendingReader_EndsItWithMsg3998()
    {
        var simulation = WideRows();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        var transaction = connection.BeginTransaction();
        await using var pending = new SqlCommand("select id, pad from big where id > @from order by id", connection, transaction);
        _ = pending.Parameters.AddWithValue("@from", 0);
        await using var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(3981, Throws<SqlException>(transaction.Commit).Number);
        var end = await this.DrainAsync(reader);
        IsNotNull(end);
        AreEqual(3998, end.Number);
    }

    /// <summary>
    /// SqlClient counts a reader outstanding until it has read past the end,
    /// and says so in each request's transaction-descriptor header, which is
    /// what real's Msg 3988 and Msg 3981 read: a one-row result the server
    /// sent whole still refuses both. The server's own view decides the rest
    /// — that reader's batch is done, so the refused commit's rollback leaves
    /// it alone (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task UnreadSmallReader_IsOutstanding_ForTheTransactionRefusals()
    {
        var simulation = WideRows();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using (var pending = new SqlCommand("select 1", connection))
        await using (var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(3988, Throws<SqlException>(connection.BeginTransaction).Number);
            IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
            connection.BeginTransaction().Rollback();
        }

        var transaction = connection.BeginTransaction();
        await using (var pending = new SqlCommand("select 1; select concat(@@trancount, ',', xact_state())", connection, transaction))
        await using (var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(3981, Throws<SqlException>(transaction.Commit).Number);
            await using var next = new SqlCommand("select 2", connection);
            AreEqual(2, await next.ExecuteScalarAsync(TestContext.CancellationToken));
            IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
            IsTrue(await reader.NextResultAsync(TestContext.CancellationToken));
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual("1,1", reader.GetString(0));
            IsNull(await this.DrainAsync(reader));
        }
    }

    /// <summary>
    /// Each request works on its own copy of the session's settings, taken
    /// as it starts and published as its response ends, so the last request
    /// to finish wins: a reader finishing after another request's
    /// <c>SET DATEFORMAT</c> and <c>USE</c> puts both back (probed 2026-10-06
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task SessionSettings_AreEachRequestsOwn_AndTheLastToFinishWins()
    {
        var simulation = WideRows();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);
        const string Read = "select concat(isdate('13/01/2020'), ',', db_name())";

        await using (var pending = new SqlCommand("select id, pad from big order by id; " + Read, connection))
        await using (var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            await using var change = new SqlCommand("set dateformat dmy; use master; " + Read, connection);
            AreEqual("1,master", await change.ExecuteScalarAsync(TestContext.CancellationToken));
            await using var after = new SqlCommand(Read, connection);
            AreEqual("1,master", await after.ExecuteScalarAsync(TestContext.CancellationToken));
            while (await reader.ReadAsync(TestContext.CancellationToken))
            {
            }
            IsTrue(await reader.NextResultAsync(TestContext.CancellationToken));
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual("0,simulated", reader.GetString(0));
        }

        await using var last = new SqlCommand(Read, connection);
        AreEqual("0,simulated", await last.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// A request's settings reach no other request before it finishes, and a
    /// MARS session's <c>sys.dm_exec_sessions</c> row reports the settings
    /// its last finished request left, even to the request changing them
    /// (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task SessionSettings_PublishOnlyWhenTheRequestFinishes()
    {
        var simulation = WideRows();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using (var own = new SqlCommand("set dateformat dmy; select date_format from sys.dm_exec_sessions where session_id = @@spid", connection))
            AreEqual("mdy", await own.ExecuteScalarAsync(TestContext.CancellationToken));
        await using (var next = new SqlCommand("select date_format from sys.dm_exec_sessions where session_id = @@spid", connection))
            AreEqual("dmy", await next.ExecuteScalarAsync(TestContext.CancellationToken));

        await using (var reset = new SqlCommand("set dateformat mdy", connection))
            _ = await reset.ExecuteNonQueryAsync(TestContext.CancellationToken);
        const string Read = "select concat(isdate('13/01/2020'), ',', cast(session_context(N'a') as int), ',', cast(session_context(N'b') as int))";
        await using var pending = new SqlCommand("set dateformat dmy; exec sp_set_session_context N'a', 1; select id, pad from big order by id", connection);
        await using var reader = await pending.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        await using (var meanwhile = new SqlCommand("exec sp_set_session_context N'b', 2; " + Read, connection))
            AreEqual("0,,2", await meanwhile.ExecuteScalarAsync(TestContext.CancellationToken));
        IsNull(await this.DrainAsync(reader));

        // The session context merges: each request publishes the keys it set.
        await using var merged = new SqlCommand(Read, connection);
        AreEqual("1,1,2", await merged.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// While a DML statement's <c>OUTPUT</c> rows are still going out, every
    /// other request — a transaction-manager request and a procedure call
    /// included — is Msg 3980, until they have gone; rows that fit what the
    /// response gets ahead of the client hold nothing, and a reader still in
    /// an earlier <c>SELECT</c> of the same batch holds nothing yet (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task DmlOutputRowsStillGoingOut_RefuseOtherRequestsWithMsg3980()
    {
        var simulation = WideRows();
        Wire.ExecInProc(simulation, "create procedure p as select 1");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);
        await using var probe = new SqlCommand("select 1", connection);
        await using var call = new SqlCommand("p", connection) { CommandType = System.Data.CommandType.StoredProcedure };

        await using (var output = new SqlCommand("update big set v = v + 1 output inserted.id, inserted.pad", connection))
        await using (var reader = await output.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            var busy = await ThrowsExactlyAsync<SqlException>(async () => await probe.ExecuteScalarAsync(TestContext.CancellationToken));
            AreEqual(3980, busy.Number);
            AreEqual(1, busy.LineNumber);
            AreEqual(3980, (await ThrowsExactlyAsync<SqlException>(async () => await call.ExecuteScalarAsync(TestContext.CancellationToken))).Number);
            AreEqual(3980, Throws<SqlException>(connection.BeginTransaction).Number);
            IsNull(await this.DrainAsync(reader));
        }
        AreEqual(1, await probe.ExecuteScalarAsync(TestContext.CancellationToken));

        await using (var small = new SqlCommand("update big set v = v + 1 output inserted.id where id <= 10", connection))
        await using (var reader = await small.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(1, await probe.ExecuteScalarAsync(TestContext.CancellationToken));
        }

        await using (var selectFirst = new SqlCommand("select id, pad from big order by id; update big set v = 0 output inserted.id where id <= 5", connection))
        await using (var reader = await selectFirst.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(1, await probe.ExecuteScalarAsync(TestContext.CancellationToken));
            IsNull(await this.DrainAsync(reader));
        }
    }
}
