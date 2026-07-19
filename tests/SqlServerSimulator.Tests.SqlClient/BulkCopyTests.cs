using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="SqlBulkCopy"/> over the TDS endpoint: the metadata pre-batch
/// (FMTONLY + <c>sp_tablecollations_100</c>), the <c>INSERT BULK</c> statement,
/// and the <c>BulkLoadBCP</c> data stream. Semantics (constraint trust, trigger
/// firing, KeepIdentity / KeepNulls, transaction scope) were probed against
/// SQL Server 2025 (2026-07-18) and are asserted here to those probed facts.
/// </summary>
[TestClass]
public sealed class BulkCopyTests
{
    public TestContext TestContext { get; set; } = null!;

    private static async Task<(Simulation Simulation, SimulatedNetworkListener Listener, SqlConnection Connection)> SetUpAsync(string ddl, CancellationToken token)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, ddl);
        var listener = await simulation.ListenAsync(0, token);
        var connection = await Wire.OpenAsync(listener, token);
        return (simulation, listener, connection);
    }

    private static object? Scalar(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        return command.ExecuteScalar();
    }

    private static void Exec(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        _ = command.ExecuteNonQuery();
    }

    private static void Map(SqlBulkCopy bulk, string source, string destination) =>
        _ = bulk.ColumnMappings.Add(source, destination);

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
            _ = table.Columns.Add(name, type);
        return table;
    }

    [TestMethod]
    public async Task DataTableSource_Async_InsertsRowsAndGeneratesServerSideColumns()
    {
        var (_, listener, connection) = await SetUpAsync(
            "create table t (id int identity(1,1) primary key, name nvarchar(50) null, qty int not null default(7), payload varbinary(max) null, amount money null, computed_col as (qty*2), rv rowversion)",
            TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("name", typeof(string)), ("qty", typeof(int)), ("payload", typeof(byte[])), ("amount", typeof(decimal)));
        _ = data.Rows.Add("alpha", 10, new byte[] { 1, 2, 3 }, 12.34m);
        _ = data.Rows.Add("beta", 20, DBNull.Value, DBNull.Value);

        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "dbo.t" })
        {
            Map(bulk, "name", "name");
            Map(bulk, "qty", "qty");
            Map(bulk, "payload", "payload");
            Map(bulk, "amount", "amount");
            await bulk.WriteToServerAsync(data, TestContext.CancellationToken);
        }

        await using var read = new SqlCommand("select id, name, qty, computed_col, datalength(payload), amount, convert(bigint, rv) from dbo.t order by id", connection);
        await using var reader = await read.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1, reader.GetInt32(0));
        AreEqual("alpha", reader.GetString(1));
        AreEqual(10, reader.GetInt32(2));
        AreEqual(20, reader.GetInt32(3)); // computed server-side
        AreEqual(3L, reader.GetInt64(4));
        AreEqual(12.34m, reader.GetDecimal(5));
        AreEqual(1L, reader.GetInt64(6)); // rowversion stamped server-side

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(2, reader.GetInt32(0));
        AreEqual(40, reader.GetInt32(3)); // qty 20 * 2
        IsTrue(reader.IsDBNull(4));
        IsTrue(reader.IsDBNull(5));
        AreEqual(2L, reader.GetInt64(6));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task DataTableSource_Sync_InsertsRows()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int, name nvarchar(20))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("id", typeof(int)), ("name", typeof(string)));
        _ = data.Rows.Add(1, "one");
        _ = data.Rows.Add(2, "two");
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
            bulk.WriteToServer(data);

        AreEqual(2, Scalar(connection, "select count(*) from t"));
        AreEqual("one", Scalar(connection, "select name from t where id = 1"));
    }

    [TestMethod]
    public async Task DataReaderSource_InsertsRows()
    {
        var (simulation, listener, connection) = await SetUpAsync("create table dest (id int, name nvarchar(20))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;
        Wire.ExecInProc(simulation, "create table src (id int, name nvarchar(20)); insert src values (1,'a'),(2,'b'),(3,'c')");

        await using var sourceConnection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var sourceCommand = new SqlCommand("select id, name from src order by id", sourceConnection);
        await using var sourceReader = await sourceCommand.ExecuteReaderAsync(TestContext.CancellationToken);

        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "dest" })
            await bulk.WriteToServerAsync(sourceReader, TestContext.CancellationToken);

        AreEqual(3, Scalar(connection, "select count(*) from dest"));
        AreEqual("b", Scalar(connection, "select name from dest where id = 2"));
    }

    [TestMethod]
    public async Task DataRowArraySource_InsertsRows()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int, name nvarchar(20))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("id", typeof(int)), ("name", typeof(string)));
        _ = data.Rows.Add(1, "x");
        _ = data.Rows.Add(2, "y");
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
            bulk.WriteToServer([data.Rows[0], data.Rows[1]]);

        AreEqual(2, Scalar(connection, "select count(*) from t"));
    }

    [TestMethod]
    public async Task ColumnMappings_ByOrdinal_InsertsRows()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (a int, b nvarchar(10))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("first", typeof(int)), ("second", typeof(string)));
        _ = data.Rows.Add(7, "hi");
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
        {
            _ = bulk.ColumnMappings.Add(0, 0);
            _ = bulk.ColumnMappings.Add(1, 1);
            bulk.WriteToServer(data);
        }

        AreEqual("7/hi", Scalar(connection, "select concat(a, '/', b) from t"));
    }

    [TestMethod]
    public async Task ColumnMappings_Reordered_LandInNamedDestinationColumns()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (a int, b nvarchar(10))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("bcol", typeof(string)), ("acol", typeof(int)));
        _ = data.Rows.Add("hi", 42);
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
        {
            Map(bulk, "acol", "a");
            Map(bulk, "bcol", "b");
            bulk.WriteToServer(data);
        }

        AreEqual("42/hi", Scalar(connection, "select concat(a, '/', b) from t"));
    }

    [TestMethod]
    public async Task KeepIdentity_On_KeepsSuppliedValuesAndAdvancesSeed()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int identity(1,1), v int)", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("id", typeof(int)), ("v", typeof(int)));
        _ = data.Rows.Add(50, 500);
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepIdentity, null) { DestinationTableName = "t" })
        {
            Map(bulk, "id", "id");
            Map(bulk, "v", "v");
            bulk.WriteToServer(data);
        }

        AreEqual(50, Scalar(connection, "select id from t"));
        AreEqual(50m, Scalar(connection, "select ident_current('t')"));

        Exec(connection, "insert t(v) values (999)");
        AreEqual(51, Scalar(connection, "select id from t where v = 999"));
    }

    [TestMethod]
    public async Task KeepIdentity_Off_GeneratesServerSideValues()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int identity(10,5), v int)", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("v", typeof(int)));
        _ = data.Rows.Add(7);
        _ = data.Rows.Add(8);
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
        {
            Map(bulk, "v", "v");
            bulk.WriteToServer(data);
        }

        AreEqual("10:7,15:8", Scalar(connection, "select string_agg(concat(id, ':', v), ',') within group (order by id) from t"));
    }

    [TestMethod]
    public async Task CheckConstraints_Off_SkipsAndUntrusts()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int primary key, v int check (v > 0))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("id", typeof(int)), ("v", typeof(int)));
        _ = data.Rows.Add(1, -5); // violates the CHECK, but default bulk does not enforce it
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
            bulk.WriteToServer(data);

        AreEqual(1, Scalar(connection, "select count(*) from t where v = -5"));
        IsTrue((bool)Scalar(connection, "select is_not_trusted from sys.check_constraints where parent_object_id = object_id('t')")!);
    }

    [TestMethod]
    public async Task CheckConstraints_On_EnforcesAndStaysTrusted()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int primary key, v int check (v > 0))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var good = Table(("id", typeof(int)), ("v", typeof(int)));
        _ = good.Rows.Add(1, 5);
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null) { DestinationTableName = "t" })
            bulk.WriteToServer(good);

        IsFalse((bool)Scalar(connection, "select is_not_trusted from sys.check_constraints where parent_object_id = object_id('t')")!);

        var bad = Table(("id", typeof(int)), ("v", typeof(int)));
        _ = bad.Rows.Add(2, -1);
        using var violating = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, null) { DestinationTableName = "t" };
        var error = Throws<SqlException>(() => violating.WriteToServer(bad));
        AreEqual(547, error.Number);
    }

    [TestMethod]
    public async Task FireTriggers_Off_DoesNotFire_On_Fires()
    {
        var (simulation, listener, connection) = await SetUpAsync("create table t (id int)", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;
        Wire.ExecInProc(simulation, "create table trg_log (n int)");
        Wire.ExecInProc(simulation, "create trigger t_ai on t after insert as insert trg_log select count(*) from inserted");

        static DataTable Two()
        {
            var d = Table(("id", typeof(int)));
            _ = d.Rows.Add(1);
            _ = d.Rows.Add(2);
            return d;
        }

        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
            bulk.WriteToServer(Two());
        AreEqual(0, Scalar(connection, "select count(*) from trg_log"));

        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.FireTriggers, null) { DestinationTableName = "t" })
            bulk.WriteToServer(Two());
        AreEqual(1, Scalar(connection, "select count(*) from trg_log"));
        AreEqual(2, Scalar(connection, "select max(n) from trg_log"));
    }

    [TestMethod]
    public async Task KeepNulls_Off_AppliesDefault_On_KeepsNull()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int, v int not null default(77), w int null default(5))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        static DataTable Row(int id)
        {
            var d = Table(("id", typeof(int)), ("w", typeof(int)));
            _ = d.Rows.Add(id, DBNull.Value);
            return d;
        }

        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
        {
            Map(bulk, "id", "id");
            Map(bulk, "w", "w");
            bulk.WriteToServer(Row(1));
        }

        // Default off: the omitted v and the NULL-sent w both take their DEFAULT.
        AreEqual(77, Scalar(connection, "select v from t where id = 1"));
        AreEqual(5, Scalar(connection, "select w from t where id = 1"));

        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.KeepNulls, null) { DestinationTableName = "t" })
        {
            Map(bulk, "id", "id");
            Map(bulk, "w", "w");
            bulk.WriteToServer(Row(2));
        }

        // KeepNulls: the omitted v still takes its DEFAULT; the NULL-sent w stays NULL.
        AreEqual(77, Scalar(connection, "select v from t where id = 2"));
        AreEqual(DBNull.Value, Scalar(connection, "select w from t where id = 2"));
    }

    [TestMethod]
    public async Task ExternalTransaction_RollbackUndoesRows_CommitPersists()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int)", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        static DataTable Two()
        {
            var d = Table(("id", typeof(int)));
            _ = d.Rows.Add(1);
            _ = d.Rows.Add(2);
            return d;
        }

        var rollbackTx = connection.BeginTransaction();
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, rollbackTx) { DestinationTableName = "t" })
            bulk.WriteToServer(Two());
        rollbackTx.Rollback();
        AreEqual(0, Scalar(connection, "select count(*) from t"));

        var commitTx = connection.BeginTransaction();
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, commitTx) { DestinationTableName = "t" })
            bulk.WriteToServer(Two());
        commitTx.Commit();
        AreEqual(2, Scalar(connection, "select count(*) from t"));
    }

    [TestMethod]
    public async Task BatchSize_MultipleRounds_InsertsAllRows()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int)", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("id", typeof(int)));
        for (var i = 1; i <= 5; i++)
            _ = data.Rows.Add(i);
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t", BatchSize = 2 })
            await bulk.WriteToServerAsync(data, TestContext.CancellationToken);

        AreEqual(5, Scalar(connection, "select count(*) from t"));
        AreEqual(15, Scalar(connection, "select sum(id) from t"));
    }

    [TestMethod]
    public async Task PrimaryKeyViolation_DuringBulk_RaisesMsg2627()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int primary key)", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;
        Exec(connection, "insert t values (1)");

        var data = Table(("id", typeof(int)));
        _ = data.Rows.Add(1);
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" };
        var error = Throws<SqlException>(() => bulk.WriteToServer(data));
        AreEqual(2627, error.Number);
        AreEqual(1, Scalar(connection, "select count(*) from t"));
    }

    [TestMethod]
    public async Task LobPayload_LargerThan8000_RoundTrips()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int, big nvarchar(max))", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var big = new string('x', 20000);
        var data = Table(("id", typeof(int)), ("big", typeof(string)));
        _ = data.Rows.Add(1, big);
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
            await bulk.WriteToServerAsync(data, TestContext.CancellationToken);

        AreEqual(20000, Scalar(connection, "select len(big) from t"));
    }

    [TestMethod]
    public async Task EmptySource_WritesNoRows()
    {
        var (_, listener, connection) = await SetUpAsync("create table t (id int)", TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var data = Table(("id", typeof(int)));
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
            await bulk.WriteToServerAsync(data, TestContext.CancellationToken);

        AreEqual(0, Scalar(connection, "select count(*) from t"));
    }

    [TestMethod]
    public async Task LegacyLobColumns_TextNtextImage_InsertAndRoundTrip()
    {
        // SqlClient sends legacy text / ntext / image destination columns in the
        // BCP stream with their LONGLEN TYPE_INFO (a 4-byte max size, the 5-byte
        // collation for the string pair, and a zero-part TableName field) and the
        // in-band text-pointer ROW value form — the same value form results carry,
        // now decoded by the shared column decoder. Probe-captured against
        // SqlClient 7.0.2 (2026-07-19).
        var (_, listener, connection) = await SetUpAsync(
            "create table t (id int, t_col text null, n_col ntext null, i_col image null)",
            TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var big = new string('Z', 20000);
        var image = new byte[300];
        for (var i = 0; i < image.Length; i++)
            image[i] = (byte)(i & 0xFF);

        var data = Table(("id", typeof(int)), ("t_col", typeof(string)), ("n_col", typeof(string)), ("i_col", typeof(byte[])));
        _ = data.Rows.Add(1, "hello text", "wörld ntext", new byte[] { 1, 2, 3, 254 });
        _ = data.Rows.Add(2, DBNull.Value, DBNull.Value, DBNull.Value);
        _ = data.Rows.Add(3, big, big, image);
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
        {
            Map(bulk, "id", "id");
            Map(bulk, "t_col", "t_col");
            Map(bulk, "n_col", "n_col");
            Map(bulk, "i_col", "i_col");
            await bulk.WriteToServerAsync(data, TestContext.CancellationToken);
        }

        await using var read = new SqlCommand("select t_col, n_col, i_col from t order by id", connection);
        await using var reader = await read.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("hello text", reader.GetString(0));
        AreEqual("wörld ntext", reader.GetString(1));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 254 }, (byte[])reader.GetValue(2));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(reader.IsDBNull(0));
        IsTrue(reader.IsDBNull(1));
        IsTrue(reader.IsDBNull(2));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(big, reader.GetString(0));
        AreEqual(big, reader.GetString(1));
        CollectionAssert.AreEqual(image, (byte[])reader.GetValue(2));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MixedScalarTypes_RoundTripOverTheWire()
    {
        var (_, listener, connection) = await SetUpAsync(
            "create table t (i bigint, s smallint, ti tinyint, b bit, f float, r real, m money, d decimal(12,3), g uniqueidentifier, dt datetime, d2 datetime2(3), da date)",
            TestContext.CancellationToken);
        await using var listenerScope = listener;
        await using var connectionScope = connection;

        var guid = Guid.NewGuid();
        var data = Table(
            ("i", typeof(long)), ("s", typeof(short)), ("ti", typeof(byte)), ("b", typeof(bool)),
            ("f", typeof(double)), ("r", typeof(float)), ("m", typeof(decimal)), ("d", typeof(decimal)),
            ("g", typeof(Guid)), ("dt", typeof(DateTime)), ("d2", typeof(DateTime)), ("da", typeof(DateTime)));
        _ = data.Rows.Add(9_000_000_000L, (short)-42, (byte)255, true, 3.5, 1.25f, 19.99m, 123.456m,
            guid, new DateTime(2021, 6, 15, 13, 30, 0), new DateTime(2022, 1, 2, 3, 4, 5, 678), new DateTime(2020, 12, 31));
        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "t" })
            await bulk.WriteToServerAsync(data, TestContext.CancellationToken);

        await using var read = new SqlCommand("select i, s, ti, b, f, r, m, d, g, da from t", connection);
        await using var reader = await read.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(9_000_000_000L, reader.GetInt64(0));
        AreEqual((short)-42, reader.GetInt16(1));
        AreEqual((byte)255, reader.GetByte(2));
        IsTrue(reader.GetBoolean(3));
        AreEqual(3.5, reader.GetDouble(4));
        AreEqual(1.25f, reader.GetFloat(5));
        AreEqual(19.99m, reader.GetDecimal(6));
        AreEqual(123.456m, reader.GetDecimal(7));
        AreEqual(guid, reader.GetGuid(8));
        AreEqual(new DateTime(2020, 12, 31), reader.GetDateTime(9));
    }
}
