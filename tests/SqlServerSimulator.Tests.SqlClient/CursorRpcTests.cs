using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The API-server-cursor RPC family (<c>sp_cursoropen</c> / <c>sp_cursorfetch</c>
/// / <c>sp_cursor</c> / <c>sp_cursorclose</c> / the prepexec family), invoked as
/// named RPCs over the loopback endpoint the way legacy ODBC/OLE DB server-cursor
/// apps and SSMS's grid editor drive them. Each test mirrors a probe run against
/// SQL Server 2025: same parameters, asserting the same return statuses, output
/// parameters, and result-set shapes (a trailing ROWSTAT column).
/// </summary>
[TestClass]
public sealed class CursorRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Token => TestContext.CancellationToken;

    private static async Task<SqlConnection> OpenWithTableAsync(SimulatedNetworkListener listener, CancellationToken token)
    {
        var connection = await Wire.OpenAsync(listener, token);
        await using var setup = new SqlCommand(
            "CREATE TABLE dbo.curp (id int PRIMARY KEY, name nvarchar(50), qty int);" +
            "INSERT INTO dbo.curp VALUES (1,N'alpha',10),(2,N'beta',20),(3,N'gamma',30),(4,N'delta',40),(5,N'eps',50);",
            connection);
        _ = await setup.ExecuteNonQueryAsync(token);
        return connection;
    }

    private static SqlCommand Proc(string name, SqlConnection connection) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private static SqlParameter Out(string name) =>
        new(name, SqlDbType.Int) { Direction = ParameterDirection.Output };

    private static SqlParameter InOut(string name, int value) =>
        new(name, SqlDbType.Int) { Direction = ParameterDirection.InputOutput, Value = value };

    private static async Task<(int Handle, int Scroll, int Cc, int RowCount)> OpenAsync(
        SqlConnection connection, string stmt, int scrollopt, int ccopt, CancellationToken token)
    {
        await using var cmd = Proc("sp_cursoropen", connection);
        var handle = Out("@cursor");
        var scroll = InOut("@scrollopt", scrollopt);
        var cc = InOut("@ccopt", ccopt);
        var rowcount = Out("@rowcount");
        _ = cmd.Parameters.Add(handle);
        _ = cmd.Parameters.Add(new SqlParameter("@stmt", SqlDbType.NVarChar, 4000) { Value = stmt });
        _ = cmd.Parameters.Add(scroll);
        _ = cmd.Parameters.Add(cc);
        _ = cmd.Parameters.Add(rowcount);
        _ = await cmd.ExecuteNonQueryAsync(token);
        return ((int)handle.Value, (int)scroll.Value, (int)cc.Value, (int)rowcount.Value);
    }

    private static async Task<List<object?[]>> FetchAsync(
        SqlConnection connection, int handle, int fetchType, int rownum, int nrows, CancellationToken token)
    {
        await using var cmd = Proc("sp_cursorfetch", connection);
        _ = cmd.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Value = handle });
        _ = cmd.Parameters.Add(new SqlParameter("@fetchtype", SqlDbType.Int) { Value = fetchType });
        _ = cmd.Parameters.Add(new SqlParameter("@rownum", SqlDbType.Int) { Value = rownum });
        _ = cmd.Parameters.Add(new SqlParameter("@nrows", SqlDbType.Int) { Value = nrows });
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(token))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<int> CloseAsync(SqlConnection connection, int handle, CancellationToken token)
    {
        await using var cmd = Proc("sp_cursorclose", connection);
        var ret = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        _ = cmd.Parameters.Add(ret);
        _ = cmd.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Value = handle });
        _ = await cmd.ExecuteNonQueryAsync(token);
        return (int)ret.Value;
    }

    [TestMethod]
    public async Task OpenFetchClose_KeysetLifecycle()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        var (handle, scroll, cc, rowcount) = await OpenAsync(connection, "SELECT id, name, qty FROM dbo.curp ORDER BY id", 0x1, 0x1, Token);
        AreNotEqual(0, handle);
        AreEqual(0x1, scroll);
        AreEqual(0x1, cc);
        AreEqual(5, rowcount);

        var first = await FetchAsync(connection, handle, 0x1, 1, 2, Token);
        HasCount(2, first);
        AreEqual(1, first[0][0]);
        AreEqual("alpha", first[0][1]);
        AreEqual(1, first[0][3]); // ROWSTAT

        var next = await FetchAsync(connection, handle, 0x2, 0, 2, Token);
        HasCount(2, next);
        AreEqual(3, next[0][0]);

        var tail = await FetchAsync(connection, handle, 0x2, 0, 2, Token);
        HasCount(1, tail);
        AreEqual(5, tail[0][0]);

        var pastEnd = await FetchAsync(connection, handle, 0x2, 0, 2, Token);
        IsEmpty(pastEnd);

        AreEqual(0, await CloseAsync(connection, handle, Token));
    }

    [TestMethod]
    public async Task Open_ColumnMetadata_HasRowStatAndZeroRows()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        await using var cmd = Proc("sp_cursoropen", connection);
        _ = cmd.Parameters.Add(Out("@cursor"));
        _ = cmd.Parameters.Add(new SqlParameter("@stmt", SqlDbType.NVarChar, 4000) { Value = "SELECT id, name, qty FROM dbo.curp ORDER BY id" });
        _ = cmd.Parameters.Add(InOut("@scrollopt", 0x1));
        _ = cmd.Parameters.Add(InOut("@ccopt", 0x1));
        _ = cmd.Parameters.Add(Out("@rowcount"));
        await using var reader = await cmd.ExecuteReaderAsync(Token);

        AreEqual(4, reader.FieldCount);
        AreEqual("id", reader.GetName(0));
        AreEqual("ROWSTAT", reader.GetName(3));
        IsFalse(await reader.ReadAsync(Token));
    }

    [TestMethod]
    public async Task Open_ScrollOptDowngrade_NonUpdatableForcedStatic()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        // Keyset over a GROUP BY is forced STATIC (0x8) / READ_ONLY (0x1); rowcount is the count.
        var (_, scroll, cc, rowcount) = await OpenAsync(connection, "SELECT qty, COUNT(*) c FROM dbo.curp GROUP BY qty", 0x1, 0x1, Token);
        AreEqual(0x8, scroll);
        AreEqual(0x1, cc);
        AreEqual(5, rowcount);

        // Optimistic over a non-updatable shape downgrades ccopt to READ_ONLY too.
        var (_, scroll2, cc2, _) = await OpenAsync(connection, "SELECT DISTINCT qty FROM dbo.curp", 0x1, 0x4, Token);
        AreEqual(0x8, scroll2);
        AreEqual(0x1, cc2);
    }

    [TestMethod]
    public async Task Open_DynamicAndForwardOnly_ReportNegativeRowCount()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        var (_, dScroll, _, dRows) = await OpenAsync(connection, "SELECT id, name, qty FROM dbo.curp ORDER BY id", 0x2, 0x1, Token);
        AreEqual(0x2, dScroll);
        AreEqual(-1, dRows);

        var (_, fScroll, _, fRows) = await OpenAsync(connection, "SELECT id, name, qty FROM dbo.curp ORDER BY id", 0x4, 0x1, Token);
        AreEqual(0x4, fScroll);
        AreEqual(-1, fRows);
    }

    [TestMethod]
    public async Task PositionedUpdateAndDelete_ViaSpCursor()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        var (handle, _, _, _) = await OpenAsync(connection, "SELECT id, name, qty FROM dbo.curp ORDER BY id", 0x1, 0x2, Token);
        _ = await FetchAsync(connection, handle, 0x1, 1, 3, Token); // buffer rows 1,2,3

        // UPDATE buffer row 1 (id 1): set qty.
        await using (var upd = Proc("sp_cursor", connection))
        {
            var ret = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            _ = upd.Parameters.Add(ret);
            _ = upd.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Value = handle });
            _ = upd.Parameters.Add(new SqlParameter("@optype", SqlDbType.Int) { Value = 0x1 });
            _ = upd.Parameters.Add(new SqlParameter("@rownum", SqlDbType.Int) { Value = 1 });
            _ = upd.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = "dbo.curp" });
            _ = upd.Parameters.Add(new SqlParameter("@qty", SqlDbType.Int) { Value = 999 });
            _ = await upd.ExecuteNonQueryAsync(Token);
            AreEqual(0, ret.Value);
        }

        // DELETE buffer row 3 (id 3).
        await using (var del = Proc("sp_cursor", connection))
        {
            var ret = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
            _ = del.Parameters.Add(ret);
            _ = del.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Value = handle });
            _ = del.Parameters.Add(new SqlParameter("@optype", SqlDbType.Int) { Value = 0x2 });
            _ = del.Parameters.Add(new SqlParameter("@rownum", SqlDbType.Int) { Value = 3 });
            _ = del.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = "dbo.curp" });
            _ = await del.ExecuteNonQueryAsync(Token);
            AreEqual(0, ret.Value);
        }

        _ = await CloseAsync(connection, handle, Token);

        await using var verify = new SqlCommand("SELECT id, qty FROM dbo.curp ORDER BY id", connection);
        await using var reader = await verify.ExecuteReaderAsync(Token);
        var rows = Wire.Drain(reader);
        HasCount(4, rows);
        AreEqual(1, rows[0][0]);
        AreEqual(999, rows[0][1]);
        CollectionAssert.AreEqual(new object?[] { 2, 4, 5 }, rows.Skip(1).Select(r => r[0]).ToArray());
    }

    [TestMethod]
    public async Task PositionedUpdate_PastFetchBuffer_Msg16930()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        var (handle, _, _, _) = await OpenAsync(connection, "SELECT id, name, qty FROM dbo.curp ORDER BY id", 0x1, 0x2, Token);
        _ = await FetchAsync(connection, handle, 0x1, 1, 2, Token); // buffer of 2

        await using var upd = Proc("sp_cursor", connection);
        var ret = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        _ = upd.Parameters.Add(ret);
        _ = upd.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Value = handle });
        _ = upd.Parameters.Add(new SqlParameter("@optype", SqlDbType.Int) { Value = 0x1 });
        _ = upd.Parameters.Add(new SqlParameter("@rownum", SqlDbType.Int) { Value = 5 });
        _ = upd.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = "dbo.curp" });
        _ = upd.Parameters.Add(new SqlParameter("@qty", SqlDbType.Int) { Value = 1 });

        var ex = await ThrowsExactlyAsync<SqlException>(async () => _ = await upd.ExecuteNonQueryAsync(Token));
        AreEqual(16930, ex.Number);
        AreEqual(16930, ret.Value);
    }

    [TestMethod]
    public async Task PrepExecAndExecute_ReuseHandle()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        int prepHandle, cursorHandle;
        await using (var cmd = Proc("sp_cursorprepexec", connection))
        {
            var prep = Out("@prep");
            var cursor = Out("@cursor");
            var rowcount = Out("@rowcount");
            _ = cmd.Parameters.Add(prep);
            _ = cmd.Parameters.Add(cursor);
            _ = cmd.Parameters.Add(new SqlParameter("@paramdef", SqlDbType.NVarChar, 4000) { Value = "@minqty int" });
            _ = cmd.Parameters.Add(new SqlParameter("@stmt", SqlDbType.NVarChar, 4000) { Value = "SELECT id, name, qty FROM dbo.curp WHERE qty >= @minqty ORDER BY id" });
            _ = cmd.Parameters.Add(InOut("@scrollopt", 0x1001));
            _ = cmd.Parameters.Add(InOut("@ccopt", 0x1));
            _ = cmd.Parameters.Add(rowcount);
            _ = cmd.Parameters.Add(new SqlParameter("@minqty", SqlDbType.Int) { Value = 30 });
            _ = await cmd.ExecuteNonQueryAsync(Token);
            prepHandle = (int)prep.Value;
            cursorHandle = (int)cursor.Value;
            AreEqual(3, (int)rowcount.Value);
        }

        var prepRows = await FetchAsync(connection, cursorHandle, 0x1, 1, 5, Token);
        HasCount(3, prepRows);
        AreEqual(3, prepRows[0][0]);
        _ = await CloseAsync(connection, cursorHandle, Token);

        await using (var exec = Proc("sp_cursorexecute", connection))
        {
            var cursor = Out("@cursor");
            var rowcount = Out("@rowcount");
            _ = exec.Parameters.Add(new SqlParameter("@prep", SqlDbType.Int) { Value = prepHandle });
            _ = exec.Parameters.Add(cursor);
            _ = exec.Parameters.Add(InOut("@scrollopt", 0x1));
            _ = exec.Parameters.Add(InOut("@ccopt", 0x1));
            _ = exec.Parameters.Add(rowcount);
            _ = exec.Parameters.Add(new SqlParameter("@minqty", SqlDbType.Int) { Value = 45 });
            _ = await exec.ExecuteNonQueryAsync(Token);
            cursorHandle = (int)cursor.Value;
            AreEqual(1, (int)rowcount.Value);
        }

        var execRows = await FetchAsync(connection, cursorHandle, 0x1, 1, 5, Token);
        HasCount(1, execRows);
        AreEqual(5, execRows[0][0]);
        _ = await CloseAsync(connection, cursorHandle, Token);

        await using var unprepare = Proc("sp_cursorunprepare", connection);
        var unprepRet = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        _ = unprepare.Parameters.Add(unprepRet);
        _ = unprepare.Parameters.Add(new SqlParameter("@prep", SqlDbType.Int) { Value = prepHandle });
        _ = await unprepare.ExecuteNonQueryAsync(Token);
        AreEqual(0, unprepRet.Value);
    }

    [TestMethod]
    public async Task Close_DoubleClose_And_InvalidHandle_Msg16909()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        var (handle, _, _, _) = await OpenAsync(connection, "SELECT id FROM dbo.curp", 0x1, 0x1, Token);
        AreEqual(0, await CloseAsync(connection, handle, Token));

        await using var doubleClose = Proc("sp_cursorclose", connection);
        var ret = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        _ = doubleClose.Parameters.Add(ret);
        _ = doubleClose.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Value = handle });
        var ex = await ThrowsExactlyAsync<SqlException>(async () => _ = await doubleClose.ExecuteNonQueryAsync(Token));
        AreEqual(16909, ex.Number);
        AreEqual(1, ret.Value);
    }

    [TestMethod]
    public async Task Open_InvalidStatement_Msg16945_ReturnsErrorNumber()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        await using var cmd = Proc("sp_cursoropen", connection);
        var ret = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        var handle = Out("@cursor");
        _ = cmd.Parameters.Add(ret);
        _ = cmd.Parameters.Add(handle);
        _ = cmd.Parameters.Add(new SqlParameter("@stmt", SqlDbType.NVarChar, 4000) { Value = "SELECT * FROM dbo.nonexistent_zzz" });
        _ = cmd.Parameters.Add(InOut("@scrollopt", 0x1));
        _ = cmd.Parameters.Add(InOut("@ccopt", 0x1));
        _ = cmd.Parameters.Add(Out("@rowcount"));

        var ex = await ThrowsExactlyAsync<SqlException>(async () => _ = await cmd.ExecuteNonQueryAsync(Token));
        // The engine's object-name error (208) plus the "cursor was not declared" 16945.
        var numbers = ex.Errors.Cast<SqlError>().Select(e => e.Number).ToList();
        Contains(208, numbers);
        Contains(16945, numbers);
        AreEqual(208, ret.Value);
        AreEqual(0, handle.Value);
    }

    [TestMethod]
    public async Task CursorOption_AcceptedAndIgnored()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        var (handle, _, _, _) = await OpenAsync(connection, "SELECT id FROM dbo.curp", 0x1, 0x1, Token);

        await using var option = Proc("sp_cursoroption", connection);
        var ret = new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        _ = option.Parameters.Add(ret);
        _ = option.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Value = handle });
        _ = option.Parameters.Add(new SqlParameter("@code", SqlDbType.Int) { Value = 2 });
        _ = option.Parameters.Add(new SqlParameter("@value", SqlDbType.Int) { Value = 0 });
        _ = await option.ExecuteNonQueryAsync(Token);
        AreEqual(0, ret.Value);

        // The cursor still works after the ignored option call.
        var rows = await FetchAsync(connection, handle, 0x1, 1, 1, Token);
        HasCount(1, rows);
        _ = await CloseAsync(connection, handle, Token);
    }
}
