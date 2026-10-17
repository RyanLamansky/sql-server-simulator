using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Over the wire a DML statement writes each row as its <c>OUTPUT</c> row goes
/// out, and a nested or another database's trigger's rows go out as the
/// client reads them, as real sends them (probed 2026-10-09 against SQL
/// Server 2025 over MARS SqlClient connections).
/// </summary>
[TestClass]
public sealed class WriteAsSentWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private const int Rows = 2000;

    private static Simulation Big()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, $"""
            create table t (id int primary key, v int not null, pad char(2000) not null default 'x');
            insert t (id, v) select value, 0 from generate_series(1, {Rows})
            """);
        return simulation;
    }

    private async Task<short> SpidAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("select @@spid", connection);
        return (short)await command.ExecuteScalarAsync(TestContext.CancellationToken);
    }

    private async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// A reader partway into an <c>UPDATE</c>'s rows leaves just the rows it
    /// was sent locked and changed; a cancel rolls them back.
    /// </summary>
    [TestMethod]
    public async Task Update_WritesTheRowsItSends()
    {
        var simulation = Big();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";MultipleActiveResultSets=True");
        await using var observer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await SpidAsync(connection);
        await using var command = new SqlCommand("update t set v = v + 1 output inserted.id, inserted.v, inserted.pad", connection);
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            await Wire.AwaitSuspendedAsync(observer, spid, TestContext.CancellationToken);
            var locked = (int)(await ScalarAsync(observer, $"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and resource_type = 'KEY' and request_mode = 'X'"))!;
            IsLessThan(100, locked);
            AreEqual(locked, await ScalarAsync(observer, "select count(*) from t with (nolock) where v = 1"));
            command.Cancel();
        }
        AreEqual(0, await ScalarAsync(observer, "select count(*) from t where v = 1"));
    }

    /// <summary>
    /// A <c>MERGE</c> upsert from a keyed source judges each source row's
    /// action as it goes, so a reader partway in leaves no matched row ahead
    /// held in U, only the source's position in S.
    /// </summary>
    [TestMethod]
    public async Task MergeUpsert_HoldsNothingAhead()
    {
        var simulation = Big();
        Wire.ExecInProc(simulation, "create table t2 (id int primary key, v int not null, pad char(2000) not null); insert t2 select id, v, pad from t where id % 2 = 0");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";MultipleActiveResultSets=True");
        await using var observer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await SpidAsync(connection);
        await using var command = new SqlCommand("merge t2 using t on t2.id = t.id when matched then update set v = t2.v + 1 when not matched then insert (id, v, pad) values (t.id, 5, t.pad) output inserted.id, $action, inserted.pad;", connection);
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            await Wire.AwaitSuspendedAsync(observer, spid, TestContext.CancellationToken);
            AreEqual(0, await ScalarAsync(observer, $"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and request_mode = 'U'"));
            AreEqual(1, await ScalarAsync(observer, $"select count(*) from sys.dm_tran_locks where request_session_id = {spid} and resource_type = 'KEY' and request_mode = 'S'"));
            var read = 2;
            while (await reader.ReadAsync(TestContext.CancellationToken))
                read++;
            AreEqual(Rows, read);
        }
        AreEqual(Rows, await ScalarAsync(observer, "select count(*) from t2"));
    }

    /// <summary>The rows before a failing row go out ahead of its error.</summary>
    [TestMethod]
    public async Task RowsBeforeAnError_GoOutAheadOfIt()
    {
        var simulation = Big();
        Wire.ExecInProc(simulation, "create table s (id int primary key); insert s values (1)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("insert s output inserted.id values (2), (1), (3)", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(2, reader.GetInt32(0));
        var error = await ThrowsAsync<SqlException>(() => reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(2627, error.Number);
    }

    /// <summary>
    /// A trigger fired by another trigger's statement, or attached to another
    /// database's table, sends its rows as the client reads them, the
    /// statements after its <c>SELECT</c> and its firing body's after the
    /// write not yet run.
    /// </summary>
    [TestMethod]
    [DataRow("insert a values (1)", 3)]
    [DataRow("insert other.dbo.x values (1)", 2)]
    public async Task NestedOrOtherDatabasesTriggerRows_GoOutAsRead(string sql, int logged)
    {
        var simulation = Big();
        Wire.ExecInProc(simulation, "create table a (id int); create table b (id int); create table l (s varchar(20)); create database other");
        Wire.ExecInProc(simulation, "create trigger ta on a after insert as begin insert b values (1); insert l values ('a after'); end");
        Wire.ExecInProc(simulation, "create trigger tb on b after insert as begin insert l values ('b before'); select id, v, pad from t; insert l values ('b after'); end");
        Wire.ExecInProc(simulation, "use other; create table x (id int); exec ('create trigger tx on x after insert as begin insert simulated.dbo.l values (''x before''); select id, v, pad from simulated.dbo.t; insert simulated.dbo.l values (''x after''); end')");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken, ";MultipleActiveResultSets=True");
        await using var observer = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var spid = await SpidAsync(connection);
        await using var command = new SqlCommand(sql, connection);
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            await Wire.AwaitSuspendedAsync(observer, spid, TestContext.CancellationToken);
            AreEqual(1, await ScalarAsync(observer, "select count(*) from l with (nolock)"));
            var read = 2;
            while (await reader.ReadAsync(TestContext.CancellationToken))
                read++;
            AreEqual(Rows, read);
        }
        AreEqual(logged, await ScalarAsync(observer, "select count(*) from l"));
    }
}
