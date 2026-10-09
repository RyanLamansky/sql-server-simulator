using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A MARS request's <c>SELECT</c> over the wire suspends mid-result once the
/// client's window is shut, and the session's other requests run beside it:
/// the suspended statement holds what its position holds against them as
/// against another session, and reads on as the table stands after their
/// writes (probed 2026-10-05 and 2026-10-08 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class MarsInterleavingWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string MarsExtra = ";MultipleActiveResultSets=True";

    private const int Rows = 2000;

    private static Simulation Big(params string[] batches)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, $"create table big (k int primary key, v char(2000) not null); insert big select value, 'x' from generate_series(1, {Rows})");
        foreach (var batch in batches)
            Wire.ExecInProc(simulation, batch);
        return simulation;
    }

    private async Task<SqlDataReader> ReadAsync(SqlConnection connection, string sql, int rows, SqlTransaction? transaction = null)
    {
        var command = new SqlCommand(sql, connection, transaction);
        var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        for (var read = 0; read < rows; read++)
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        return reader;
    }

    /// <summary>Runs <paramref name="sql"/> under <c>LOCK_TIMEOUT 0</c>: the error number it raised, or 0.</summary>
    private async Task<int> AttemptAsync(SqlConnection connection, string sql, SqlTransaction? transaction = null)
    {
        try
        {
            await using var command = new SqlCommand($"set lock_timeout 0; {sql}", connection, transaction);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            return 0;
        }
        catch (SqlException error)
        {
            return error.Number;
        }
    }

    private async Task<Dictionary<int, string>> RestAsync(SqlDataReader reader)
    {
        Dictionary<int, string> rows = [];
        while (await reader.ReadAsync(TestContext.CancellationToken))
            rows[reader.GetInt32(0)] = reader.GetString(1).TrimEnd();
        return rows;
    }

    private async Task<string> LocksAsync(SqlConnection observer, short spid)
    {
        await using var command = new SqlCommand($"""
            select string_agg(concat(resource_type, ' ', request_mode, iif(n > 1, concat('x', n), '')), ', ') within group (order by resource_type, request_mode)
            from (select resource_type, request_mode, count(*) n from sys.dm_tran_locks where request_session_id = {spid} and resource_type <> 'DATABASE' group by resource_type, request_mode) l
            """, observer);
        return await command.ExecuteScalarAsync(TestContext.CancellationToken) as string ?? "";
    }

    private async Task<short> SpidAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("select @@spid", connection);
        return (short)await command.ExecuteScalarAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task SuspendedReader_AnotherRequestRunsBesideIt()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);
        await using var observer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await this.SpidAsync(connection);

        await using var rows = await this.ReadAsync(connection, "select k, v from big", 2);
        // Another request of the session runs once the reader has suspended.
        AreEqual(1222, await this.AttemptAsync(connection, "alter table big add c int"));
        AreEqual("KEY S, OBJECT IS", await this.LocksAsync(observer, spid));
        AreEqual(0, await this.AttemptAsync(connection, "update big set v = 'b' where k = 1"));
        AreEqual(0, await this.AttemptAsync(connection, "update big set v = 'u' where k = 1500"));
        AreEqual(0, await this.AttemptAsync(connection, "delete big where k = 1700"));
        AreEqual(0, await this.AttemptAsync(connection, $"insert big values ({Rows + 1}, 'i')"));
        var read = await this.RestAsync(rows);
        HasCount(Rows - 2, read);
        AreEqual("u", read[1500]);
        IsFalse(read.ContainsKey(1700));
        IsTrue(read.ContainsKey(Rows + 1));
        AreEqual("", await this.LocksAsync(observer, spid));
    }

    /// <summary>
    /// A <c>REPEATABLE READ</c> reader's key locks keep the session's own other
    /// request out of the rows it has produced, as another session's would be.
    /// </summary>
    [TestMethod]
    public async Task RepeatableReadReader_BlocksTheSessionsOwnWrite()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using var rows = await this.ReadAsync(connection, "set transaction isolation level repeatable read; select k, v from big", 2);
        AreEqual(1222, await this.AttemptAsync(connection, "update big set v = 'b' where k = 1"));
        AreEqual(0, await this.AttemptAsync(connection, "update big set v = 'u' where k = 1500"));
        HasCount(Rows - 2, await this.RestAsync(rows));
        AreEqual(0, await this.AttemptAsync(connection, "update big set v = 'b' where k = 1"));
    }

    /// <summary>A request in the reader's own transaction leaves the reader's statement reading as it began.</summary>
    [TestMethod]
    public async Task RequestInTheReadersTransaction_LeavesItsReadAsItBegan()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestContext.CancellationToken);

        await using var rows = await this.ReadAsync(connection, "select k, v from big", 2, transaction);
        AreEqual(0, await this.AttemptAsync(connection, "update big set v = 'u' where k = 1500", transaction));
        AreEqual(0, await this.AttemptAsync(connection, "delete big where k = 1700", transaction));
        var read = await this.RestAsync(rows);
        HasCount(Rows - 2, read);
        AreEqual("x", read[1500]);
        IsTrue(read.ContainsKey(1700));
        await rows.DisposeAsync();
        await transaction.RollbackAsync(TestContext.CancellationToken);
    }

    /// <summary>A procedure's <c>SELECT</c> suspends with its position held, as a batch's does.</summary>
    [TestMethod]
    public async Task ProcedureSelect_SuspendsHoldingItsPosition()
    {
        var simulation = Big("create procedure p as select k, v from big");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using var rows = await this.ReadAsync(connection, "exec p", 2);
        AreEqual(1222, await this.AttemptAsync(connection, "alter table big add c int"));
        AreEqual(0, await this.AttemptAsync(connection, "update big set v = 'u' where k = 1500"));
        AreEqual("u", (await this.RestAsync(rows))[1500]);
    }

    /// <summary>
    /// A cancel while a DML statement's <c>OUTPUT</c> rows go out rolls the
    /// statement back, as real's does (probed 2026-10-05 against SQL Server
    /// 2025); drained, the rows commit.
    /// </summary>
    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, Rows)]
    public async Task OutputRowsGoingOut_HoldTheStatementOpen(bool cancel, int committed)
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, MarsExtra);

        await using (var command = new SqlCommand("update big set v = 'y' output inserted.k, inserted.v", connection))
        {
            var rows = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            for (var read = 0; read < 10; read++)
                IsTrue(await rows.ReadAsync(TestContext.CancellationToken));
            AreEqual(3980, await this.AttemptAsync(connection, "select 1"));
            if (cancel)
                command.Cancel();
            await rows.DisposeAsync();
        }
        await using var count = new SqlCommand("select count(*) from big where v = 'y'", connection);
        AreEqual(committed, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
