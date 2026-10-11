using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The wire shapes SQL Server's OLE DB provider relies on when a real
/// server's linked server points at the simulator: <c>sp_reset_connection</c>
/// called by name, a system procedure called by a database-qualified name with
/// output parameters, a prepared statement with no parameter declaration, the
/// prepared statement's metadata, and an API cursor's base-table metadata and
/// row counts (probed 2026-10-11 against SQL Server 2025, through its MSOLEDBSQL
/// linked server and SqlClient).
/// </summary>
[TestClass]
public sealed class LinkedServerProviderWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Token => TestContext.CancellationToken;

    private static async Task<SqlConnection> OpenWithTableAsync(SimulatedNetworkListener listener, CancellationToken token)
    {
        var connection = await Wire.OpenAsync(listener, token);
        await using var setup = new SqlCommand("create table dbo.lk (id int primary key, a nvarchar(10)); insert dbo.lk values (1, N'x'), (2, N'y');", connection);
        _ = await setup.ExecuteNonQueryAsync(token);
        return connection;
    }

    private static SqlCommand Proc(string name, SqlConnection connection, SqlTransaction? transaction = null) =>
        new(name, connection, transaction) { CommandType = CommandType.StoredProcedure };

    [TestMethod]
    public async Task ResetConnection_CalledByName_StartsTheSessionOver()
    {
        await using var listener = await new Simulation().ListenLocalAsync(0, Token);
        await using var connection = await Wire.OpenAsync(listener, Token);
        await using (var setup = new SqlCommand("create table #gone (a int)", connection))
            _ = await setup.ExecuteNonQueryAsync(Token);

        await using var reset = Proc("sp_reset_connection", connection);
        var status = reset.Parameters.Add(new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue });
        _ = await reset.ExecuteNonQueryAsync(Token);
        AreEqual(0, status.Value);

        await using var probe = new SqlCommand("select object_id('tempdb..#gone')", connection);
        AreEqual(DBNull.Value, await probe.ExecuteScalarAsync(Token));
    }

    [TestMethod]
    public async Task SystemProcedure_CalledByQualifiedName_RunsInThatDatabaseAndWritesItsOutputs()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);
        await using (var use = new SqlCommand("use master", connection))
            _ = await use.ExecuteNonQueryAsync(Token);

        await using (var columns = Proc("[simulated].[sys].sp_columns_100_rowset", connection))
        {
            _ = columns.Parameters.AddWithValue("@table_name", "lk");
            await using var reader = await columns.ExecuteReaderAsync(Token);
            var names = new List<string>();
            while (await reader.ReadAsync(Token))
                names.Add($"{reader["TABLE_CATALOG"]}.{reader["COLUMN_NAME"]}");
            AreEqual("simulated.id,simulated.a", string.Join(",", names));
        }

        await using var transaction = connection.BeginTransaction();
        await using var schemaLock = Proc("[sys].sp_getschemalock", connection, transaction);
        var handle = schemaLock.Parameters.Add(new SqlParameter("@p1", SqlDbType.Int) { Direction = ParameterDirection.InputOutput, Value = 1 });
        var version = schemaLock.Parameters.Add(new SqlParameter("@p2", SqlDbType.BigInt) { Direction = ParameterDirection.InputOutput, Value = 0L });
        _ = schemaLock.Parameters.AddWithValue("@p3", "\"simulated\".\"dbo\".\"lk\"");
        _ = await schemaLock.ExecuteNonQueryAsync(Token);
        AreEqual(1, handle.Value);
        await using var expected = new SqlCommand("select cast(cast(modify_date as binary(8)) as bigint) from simulated.sys.objects where name = 'lk'", connection, transaction);
        AreEqual(await expected.ExecuteScalarAsync(Token), version.Value);
    }

    [TestMethod]
    public async Task Prepare_WithoutDeclaration_AndWithItsMetadata()
    {
        await using var listener = await new Simulation().ListenLocalAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        await using (var prepExec = Proc("sp_prepexec", connection))
        {
            _ = prepExec.Parameters.Add(new SqlParameter("@handle", SqlDbType.Int) { Direction = ParameterDirection.Output });
            _ = prepExec.Parameters.Add(new SqlParameter("@params", SqlDbType.NVarChar, 4000) { Value = DBNull.Value });
            _ = prepExec.Parameters.AddWithValue("@stmt", "SELECT \"T\".\"a\" FROM \"dbo\".\"lk\" \"T\" WHERE \"T\".\"id\" = 2");
            AreEqual("y", await prepExec.ExecuteScalarAsync(Token));
        }

        // RETURN_METADATA describes the result as an empty result set ahead of the handle.
        await using var prepare = Proc("sp_prepare", connection);
        var handle = prepare.Parameters.Add(new SqlParameter("@handle", SqlDbType.Int) { Direction = ParameterDirection.Output });
        _ = prepare.Parameters.Add(new SqlParameter("@params", SqlDbType.NVarChar, 4000) { Value = DBNull.Value });
        _ = prepare.Parameters.AddWithValue("@stmt", "select id, a from dbo.lk");
        _ = prepare.Parameters.AddWithValue("@options", 1);
        await using (var reader = await prepare.ExecuteReaderAsync(Token))
        {
            AreEqual("id,a", string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
            IsFalse(await reader.ReadAsync(Token));
        }
        _ = IsInstanceOfType<int>(handle.Value);
    }

    /// <summary>
    /// The ODBC driver prepares a statement taking a long-text parameter with
    /// RETURN_METADATA before executing it: the description binds the declared
    /// parameters without their values, so it must not refuse them as unsupplied.
    /// </summary>
    [TestMethod]
    public async Task Prepare_WithItsMetadata_DescribesADeclaredParameterWithoutItsValue()
    {
        await using var listener = await new Simulation().ListenLocalAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        await using var prepare = Proc("sp_prepare", connection);
        var handle = prepare.Parameters.Add(new SqlParameter("@handle", SqlDbType.Int) { Direction = ParameterDirection.Output });
        _ = prepare.Parameters.AddWithValue("@params", "@P1 int,@P2 ntext");
        _ = prepare.Parameters.AddWithValue("@stmt", "SET NOCOUNT ON INSERT INTO dbo.lk (id, a) VALUES (@P1, @P2)");
        _ = prepare.Parameters.AddWithValue("@options", 1);
        _ = await prepare.ExecuteNonQueryAsync(Token);

        await using var execute = Proc("sp_execute", connection);
        _ = execute.Parameters.AddWithValue("@handle", handle.Value);
        _ = execute.Parameters.AddWithValue("@P1", 3);
        _ = execute.Parameters.Add(new SqlParameter("@P2", SqlDbType.NText) { Value = "z" });
        _ = await execute.ExecuteNonQueryAsync(Token);

        await using var check = new SqlCommand("select count(*) from dbo.lk", connection);
        AreEqual(3, await check.ExecuteScalarAsync(Token));
    }

    private static async Task<int> OpenCursorAsync(SqlConnection connection, string statement, int scrollopt, int ccopt, CancellationToken token)
    {
        await using var open = Proc("sp_cursoropen", connection);
        var cursor = open.Parameters.Add(new SqlParameter("@cursor", SqlDbType.Int) { Direction = ParameterDirection.Output });
        _ = open.Parameters.AddWithValue("@stmt", statement);
        _ = open.Parameters.Add(new SqlParameter("@scrollopt", SqlDbType.Int) { Direction = ParameterDirection.InputOutput, Value = scrollopt });
        _ = open.Parameters.Add(new SqlParameter("@ccopt", SqlDbType.Int) { Direction = ParameterDirection.InputOutput, Value = ccopt });
        _ = open.Parameters.Add(new SqlParameter("@rowcount", SqlDbType.Int) { Direction = ParameterDirection.InputOutput, Value = -1 });
        _ = await open.ExecuteNonQueryAsync(token);
        return (int)cursor.Value;
    }

    [TestMethod]
    public async Task CursorMetadata_NamesTheBaseTableAndAKeysetCursorsKey()
    {
        await using var listener = await new Simulation().ListenLocalAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);

        async Task<string> DescribeAsync(int scrollopt, int ccopt)
        {
            var cursor = await OpenCursorAsync(connection, "select * from \"dbo\".\"lk\"", scrollopt, ccopt, Token);
            await using var fetch = Proc("sp_cursorfetch", connection);
            _ = fetch.Parameters.AddWithValue("@cursor", cursor);
            _ = fetch.Parameters.AddWithValue("@fetchtype", 16);
            _ = fetch.Parameters.AddWithValue("@rownum", 1);
            _ = fetch.Parameters.AddWithValue("@nrows", 1);
            await using var reader = await fetch.ExecuteReaderAsync(Token);
            return string.Join(",", (await reader.GetColumnSchemaAsync(Token)).Select(column => $"{column.ColumnName}:{column.BaseTableName}:{column.IsKey}:{column.IsHidden}"));
        }

        AreEqual("id:lk:True:False,a:lk:False:False,ROWSTAT::False:True", await DescribeAsync(1, 4));
        // A static cursor names the table but marks no key.
        AreEqual("id:lk:False:False,a:lk:False:False,ROWSTAT::False:True", await DescribeAsync(8, 1));
    }

    [TestMethod]
    public async Task CursorWrites_CountTheirRows()
    {
        await using var listener = await new Simulation().ListenLocalAsync(0, Token);
        await using var connection = await OpenWithTableAsync(listener, Token);
        var cursor = await OpenCursorAsync(connection, "select * from \"dbo\".\"lk\"", 0x18001, 0x4C004, Token);

        async Task<string> CountsAsync(SqlCommand command)
        {
            var counts = new List<int>();
            command.StatementCompleted += (_, e) => counts.Add(e.RecordCount);
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.NextResultAsync(Token))
            {
            }
            return string.Join(",", counts);
        }

        await using (var fetch = Proc("sp_cursorfetch", connection))
        {
            _ = fetch.Parameters.AddWithValue("@cursor", cursor);
            _ = fetch.Parameters.AddWithValue("@fetchtype", 16);
            _ = fetch.Parameters.AddWithValue("@rownum", 1);
            _ = fetch.Parameters.AddWithValue("@nrows", 100);
            AreEqual("2", await CountsAsync(fetch));
        }

        // A positioned update reports its row twice; an insert, which needs
        // no fetched row, once.
        await using (var update = Proc("sp_cursor", connection))
        {
            _ = update.Parameters.AddWithValue("@cursor", cursor);
            _ = update.Parameters.AddWithValue("@optype", 33);
            _ = update.Parameters.AddWithValue("@rownum", 1);
            _ = update.Parameters.AddWithValue("@table", "[dbo].[lk]");
            _ = update.Parameters.AddWithValue("@a", "x!");
            AreEqual("1,1", await CountsAsync(update));
        }
        await using (var insert = Proc("sp_cursor", connection))
        {
            _ = insert.Parameters.AddWithValue("@cursor", cursor);
            _ = insert.Parameters.AddWithValue("@optype", 4);
            _ = insert.Parameters.AddWithValue("@rownum", 0);
            _ = insert.Parameters.AddWithValue("@table", "[dbo].[lk]");
            _ = insert.Parameters.AddWithValue("@id", 3);
            _ = insert.Parameters.AddWithValue("@a", "z");
            AreEqual("1", await CountsAsync(insert));
        }

        await using var check = new SqlCommand("select string_agg(concat(id, a), ',') within group (order by id) from dbo.lk", connection);
        AreEqual("1x!,2y,3z", await check.ExecuteScalarAsync(Token));
    }
}
