using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>SELECT</c> over the wire sends its rows as the client reads them,
/// running ahead of a client that stops reading by what the connection's
/// buffers take and then waiting on <c>ASYNC_NETWORK_IO</c> with the locks
/// of its position, as real does (probed 2026-10-08 against SQL Server 2025
/// with 20,000 rows of <c>char(2000)</c> and a client two rows in: real's
/// <c>REPEATABLE READ</c> reader held 2,119 keys there).
/// </summary>
[TestClass]
public sealed class StreamedResultWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private const int Rows = 20000;

    private static Simulation Big()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, $"""
            create table big (k int primary key, v char(2000) not null);
            alter table big set (lock_escalation = disable);
            insert big select value, 'x' from generate_series(1, {Rows})
            """);
        return simulation;
    }

    private async Task<short> SpidAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("select @@spid", connection);
        return (short)await command.ExecuteScalarAsync(TestContext.CancellationToken);
    }

    /// <summary>Waits for <paramref name="spid"/>'s request to be waiting on its client, read off <c>sys.dm_exec_requests</c>.</summary>
    private async Task AwaitSuspendedAsync(SqlConnection observer, short spid)
    {
        await using var probe = new SqlCommand($"select count(*) from sys.dm_exec_requests where session_id = {spid} and status = 'suspended' and wait_type = 'ASYNC_NETWORK_IO'", observer);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while ((int)await probe.ExecuteScalarAsync(TestContext.CancellationToken) == 0)
        {
            IsLessThan(10_000, waited.ElapsedMilliseconds, "the reader's request never waited on its client");
            await Task.Delay(10, TestContext.CancellationToken);
        }
    }

    private async Task<int> AttemptAsync(SqlConnection connection, string sql)
    {
        try
        {
            await using var command = new SqlCommand($"set lock_timeout 0; begin tran; {sql}; if @@trancount > 0 rollback", connection);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            return 0;
        }
        catch (SqlException error)
        {
            await using var rollback = new SqlCommand("if @@trancount > 0 rollback", connection);
            _ = await rollback.ExecuteNonQueryAsync(TestContext.CancellationToken);
            return error.Number;
        }
    }

    private async Task<int> HeldKeysAsync(SqlConnection observer, short spid, string mode)
    {
        await using var command = new SqlCommand($"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and resource_type = 'KEY' and request_mode = '{mode}'", observer);
        return (int)await command.ExecuteScalarAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// A <c>READ COMMITTED</c> reader holds the table's <c>IS</c>, which keeps a
    /// redefinition out, and S on where its scan stands — real's the current
    /// page's over a table this size, its scan locking pages, here the row's.
    /// </summary>
    [TestMethod]
    public async Task ReadCommitted_SuspendedReader_HoldsIntentSharedAndKeepsDdlOut()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var other = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await this.SpidAsync(reader);

        await using var command = new SqlCommand("select * from big", reader);
        await using var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
        IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
        await this.AwaitSuspendedAsync(other, spid);

        // The statement runs on while the socket takes its rows, letting go of
        // where it stood each time; it settles once the socket is full.
        await using (var locks = new SqlCommand($"select string_agg(concat(resource_type, ' ', request_mode), ', ') from sys.dm_tran_locks where request_session_id = {spid} and resource_type <> 'DATABASE'", other))
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            object? held;
            while (!Equals(held = await locks.ExecuteScalarAsync(TestContext.CancellationToken), "OBJECT IS, KEY S") && waited.ElapsedMilliseconds < 10_000)
                await Task.Delay(10, TestContext.CancellationToken);
            AreEqual("OBJECT IS, KEY S", held);
        }
        AreEqual(1222, await this.AttemptAsync(other, "alter table big add c int"));
        AreEqual(0, await this.AttemptAsync(other, "update big set v = v where k = 1"));
        AreEqual(0, await this.AttemptAsync(other, $"update big set v = v where k = {Rows - 10}"));

        var read = 2;
        while (await rows.ReadAsync(TestContext.CancellationToken))
            read++;
        AreEqual(Rows, read);
    }

    [TestMethod]
    public async Task RepeatableRead_SuspendedReader_HoldsTheKeysProducedSoFar()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var other = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await this.SpidAsync(reader);
        await using var transaction = (SqlTransaction)await reader.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.CancellationToken);

        await using var command = new SqlCommand("select * from big", reader, transaction);
        await using (var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
            IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
            await this.AwaitSuspendedAsync(other, spid);

            var held = await this.HeldKeysAsync(other, spid, "S");
            IsGreaterThan(2, held);
            IsLessThan(Rows, held);
            AreEqual(1222, await this.AttemptAsync(other, "update big set v = v where k = 1"));
            AreEqual(0, await this.AttemptAsync(other, $"update big set v = v where k = {Rows - 10}"));
            var read = 2;
            while (await rows.ReadAsync(TestContext.CancellationToken))
                read++;
            AreEqual(Rows, read);
        }
        AreEqual(Rows, await this.HeldKeysAsync(other, spid, "S"));
        await transaction.RollbackAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// However the read comes to lock its rows to the transaction's end — the
    /// level by <c>SET</c> and a transaction begun in SQL, a transaction
    /// SqlClient began at that level, or a table hint — with or without
    /// <c>ORDER BY</c> the clustered key, a suspended reader holds the keys of
    /// the rows produced so far and none ahead.
    /// </summary>
    [TestMethod]
    [DataRow("set", "repeatable read", "", "S")]
    [DataRow("api", "repeatable read", "order by k", "S")]
    [DataRow("hint", "repeatableread", "order by k", "S")]
    [DataRow("set", "serializable", "order by k", "RangeS-S")]
    [DataRow("api", "serializable", "", "RangeS-S")]
    [DataRow("hint", "holdlock", "", "RangeS-S")]
    public async Task RowLockingRead_HoldsTheKeysProducedHoweverItsLevelCame(string entry, string level, string order, string keyMode)
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var other = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await this.SpidAsync(reader);
        SqlTransaction? transaction = null;
        switch (entry)
        {
            case "set":
                await using (var begin = new SqlCommand($"set transaction isolation level {level}; begin tran", reader))
                    _ = await begin.ExecuteNonQueryAsync(TestContext.CancellationToken);
                break;
            case "api":
                transaction = (SqlTransaction)await reader.BeginTransactionAsync(level == "serializable" ? IsolationLevel.Serializable : IsolationLevel.RepeatableRead, TestContext.CancellationToken);
                break;
            default:
                transaction = (SqlTransaction)await reader.BeginTransactionAsync(TestContext.CancellationToken);
                break;
        }
        await using (var command = new SqlCommand($"select * from big{(entry == "hint" ? $" with ({level})" : "")} {order}", reader, transaction))
        await using (var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
            IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
            await this.AwaitSuspendedAsync(other, spid);
            var held = await this.HeldKeysAsync(other, spid, keyMode);
            IsGreaterThan(2, held);
            IsLessThan(Rows / 2, held);
            AreEqual(1222, await this.AttemptAsync(other, "update big set v = v where k = 1"));
            AreEqual(0, await this.AttemptAsync(other, $"update big set v = v where k = {Rows - 10}"));
            while (await rows.ReadAsync(TestContext.CancellationToken))
            {
            }
        }
        if (transaction is null)
        {
            await using var rollback = new SqlCommand("rollback", reader);
            _ = await rollback.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(TestContext.CancellationToken);
            await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The probe that found the gap, as written: a transaction SqlClient began
    /// at <c>REPEATABLE READ</c>, a read ordered by the clustered key, no
    /// escalation setting — two rows in, the reader holds the keys sent so far
    /// rather than the table.
    /// </summary>
    [TestMethod]
    public async Task ApiRepeatableRead_OrderedByTheKey_HoldsTheKeysSentNotTheTable()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, $"create table big (id int primary key, pad char(2000) not null); insert big select value, 'x' from generate_series(1, {Rows})");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var other = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await this.SpidAsync(reader);
        await using var transaction = (SqlTransaction)await reader.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.CancellationToken);
        await using (var command = new SqlCommand("select id, pad from big order by id", reader, transaction))
        await using (var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
            IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
            await this.AwaitSuspendedAsync(other, spid);
            IsLessThan(Rows / 2, await this.HeldKeysAsync(other, spid, "S"));
            AreEqual(1222, await this.AttemptAsync(other, "update big set pad = pad where id = 1"));
            AreEqual(0, await this.AttemptAsync(other, "update big set pad = pad where id = 19000"));
            while (await rows.ReadAsync(TestContext.CancellationToken))
            {
            }
        }
        await transaction.RollbackAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// The reader meets a row another transaction writes when it gets there:
    /// the rows before it arrive, and its <c>LOCK_TIMEOUT</c> ends the result
    /// set there.
    /// </summary>
    [TestMethod]
    public async Task UncommittedWriter_StopsTheReaderWhereItsRowIs()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var writer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var write = new SqlCommand($"begin tran; update big set v = 'z' where k = {Rows - 1000}", writer))
            _ = await write.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var command = new SqlCommand("set lock_timeout 0; select k from big", reader);
        await using var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var read = 0;
        var error = await ThrowsExactlyAsync<SqlException>(async () =>
        {
            while (await rows.ReadAsync(TestContext.CancellationToken))
                read++;
        });
        AreEqual(1222, error.Number);
        AreEqual(Rows - 1001, read);
    }

    /// <summary>
    /// While the statement waits on a row an uncommitted writer holds, the
    /// client reads the rows the packets sent before the wait carry, MARS or
    /// not, until its <c>CommandTimeout</c> ends the wait (probed 2026-10-09
    /// against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow(2000, 30, 28, true)]
    [DataRow(100, 100, 75, false)]
    [DataRow(100, 301, 299, true)]
    public async Task UncommittedWriter_RowsBeforeItGoOutWhileTheStatementWaits(int width, int held, int readable, bool mars)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, $"create table b (k int primary key, v char({width}) not null); insert b select value, 'x' from generate_series(1, 3000)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken, mars ? ";MultipleActiveResultSets=True" : "");
        await using var writer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var write = new SqlCommand($"begin tran; update b set v = 'z' where k = {held}", writer))
            _ = await write.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var command = new SqlCommand("select k, v from b", reader) { CommandTimeout = 1 };
        var read = 0;
        var error = await ThrowsExactlyAsync<SqlException>(async () =>
        {
            await using var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            while (await rows.ReadAsync(TestContext.CancellationToken))
                read++;
        });
        AreEqual(-2, error.Number);
        AreEqual(readable, read);
    }

    /// <summary>
    /// A streamed result's DONE counts the rows it sent, and one a row's error
    /// cut short reports no count, as real's does.
    /// </summary>
    [TestMethod]
    public async Task StreamedResult_DoneCountsItsRows()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var counts = new List<int>();
        await using var command = new SqlCommand($"select v from big; select v, 1 / (k - {Rows - 100}) from big", connection);
        command.StatementCompleted += (_, e) => counts.Add(e.RecordCount);
        await using var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var read = 0;
        while (await rows.ReadAsync(TestContext.CancellationToken))
            read++;
        AreEqual(Rows, read);
        IsTrue(await rows.NextResultAsync(TestContext.CancellationToken));
        read = 0;
        var error = await ThrowsExactlyAsync<SqlException>(async () =>
        {
            while (await rows.ReadAsync(TestContext.CancellationToken))
                read++;
        });
        AreEqual(8134, error.Number);
        AreEqual(Rows - 101, read);
        CollectionAssert.AreEqual(new[] { Rows }, counts);
    }

    /// <summary>
    /// A streamed <c>FOR JSON</c> document's DONE counts the rows it
    /// serialized, not the chunks it sent.
    /// </summary>
    [TestMethod]
    public async Task StreamedForJson_DoneCountsTheRowsSerialized()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var counts = new List<int>();
        await using var command = new SqlCommand("select top (1000) k, v from big order by k for json path", connection);
        command.StatementCompleted += (_, e) => counts.Add(e.RecordCount);
        await using var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var chunks = 0;
        while (await rows.ReadAsync(TestContext.CancellationToken))
            chunks++;
        IsGreaterThan(100, chunks);
        _ = await rows.NextResultAsync(TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { 1000 }, counts);
    }

    /// <summary>
    /// <c>KILL</c> ends a request waiting on its client by dropping its
    /// connection, its locks going at once, and the client's next read fails
    /// on the transport (probed 2026-10-08 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public async Task Kill_DropsTheSuspendedReadersConnection()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var reader = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var other = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await this.SpidAsync(reader);
        // Not disposed: the kill ends the transaction with the connection, and
        // SqlClient's dispose would try to roll it back.
        var transaction = (SqlTransaction)await reader.BeginTransactionAsync(IsolationLevel.RepeatableRead, TestContext.CancellationToken);
        await using var command = new SqlCommand("select * from big", reader, transaction);
        await using var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
        await this.AwaitSuspendedAsync(other, spid);

        await using (var kill = new SqlCommand(string.Create(CultureInfo.InvariantCulture, $"kill {spid}"), other))
            _ = await kill.ExecuteNonQueryAsync(TestContext.CancellationToken);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await this.HeldKeysAsync(other, spid, "S") != 0)
        {
            IsLessThan(10_000, waited.ElapsedMilliseconds, "the killed reader's locks stayed");
            await Task.Delay(10, TestContext.CancellationToken);
        }
        _ = await ThrowsAsync<SqlException>(async () =>
        {
            while (await rows.ReadAsync(TestContext.CancellationToken))
            {
            }
        });
    }
}
