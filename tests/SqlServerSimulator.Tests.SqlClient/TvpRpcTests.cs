using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Table-valued parameters (<see cref="SqlDbType.Structured"/>) over the TDS
/// wire: a <c>SqlParameter</c> whose value is a <see cref="DataTable"/>,
/// <see cref="IEnumerable{SqlDataRecord}"/>, or <see cref="SqlDataReader"/>
/// arrives as RPC parameter TYPE_INFO <c>0xF3</c> (TVP_TYPE_INFO) and binds the
/// decoded rows into the named table type — the wire path feeds the same
/// engine Structured-parameter binding the in-process ADO.NET path uses. Both
/// the direct proc-RPC (<see cref="CommandType.StoredProcedure"/>) and the
/// sp_executesql text-command paths are exercised, plus the probed error
/// parity (Msg 500 / 515 / 547 / 2627 / 2715 / 245). Probed against SQL Server
/// 2025 (2026-07-18).
/// </summary>
[TestClass]
public sealed class TvpRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    private static void Seed(Simulation simulation)
    {
        Wire.ExecInProc(simulation, "create type dbo.IdName as table (id int not null, name nvarchar(50) null)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, name nvarchar(50))");
        Wire.ExecInProc(simulation, "create proc dbo.ins_idname @rows dbo.IdName readonly as insert into dbo.sink select id, name from @rows order by id");
    }

    private static DataTable IdNameTable(params (int? Id, string? Name)[] rows)
    {
        var table = new DataTable();
        _ = table.Columns.Add("id", typeof(int));
        _ = table.Columns.Add("name", typeof(string));
        foreach (var (id, name) in rows)
            _ = table.Rows.Add((object?)id ?? DBNull.Value, (object?)name ?? DBNull.Value);
        return table;
    }

    private async Task<List<object?[]>> SinkOverWire(SqlConnection connection) =>
        Wire.Drain(await new SqlCommand("select id, name from dbo.sink order by id", connection).ExecuteReaderAsync(TestContext.CancellationToken));

    // ---- Sources ----

    [TestMethod]
    public async Task DataTable_ProcRpc_PassesRows()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", IdNameTable((1, "alpha"), (2, "beta")));
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        var rows = await this.SinkOverWire(connection);
        HasCount(2, rows);
        AreEqual(1, rows[0][0]);
        AreEqual("alpha", rows[0][1]);
        AreEqual(2, rows[1][0]);
        AreEqual("beta", rows[1][1]);
    }

    [TestMethod]
    public async Task DataTable_ExecuteSqlText_PassesRows()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // CommandType.Text with a Structured parameter arrives as sp_executesql
        // (ProcID 10) with the TVP as its own 0xF3 parameter.
        await using var command = new SqlCommand("insert into dbo.sink select id, name from @rows", connection);
        var parameter = command.Parameters.AddWithValue("@rows", IdNameTable((10, "x"), (20, "y")));
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";
        AreEqual(2, await command.ExecuteNonQueryAsync(TestContext.CancellationToken));

        var rows = await this.SinkOverWire(connection);
        HasCount(2, rows);
        AreEqual(10, rows[0][0]);
        AreEqual(20, rows[1][0]);
    }

    [TestMethod]
    public async Task SqlDataRecord_ProcRpc_PassesRows()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // IEnumerable<SqlDataRecord> is a documented TVP source the in-process
        // path can't take (no SqlClient dependency), but SqlClient serializes it
        // to the same TVP wire form, so it binds over the wire.
        var metadata = new[]
        {
            new SqlMetaData("id", SqlDbType.Int),
            new SqlMetaData("name", SqlDbType.NVarChar, 50),
        };
        var records = new List<SqlDataRecord>();
        foreach (var (id, name) in new[] { (5, "e"), (6, "f") })
        {
            var record = new SqlDataRecord(metadata);
            record.SetInt32(0, id);
            record.SetString(1, name);
            records.Add(record);
        }

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", records);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        var rows = await this.SinkOverWire(connection);
        HasCount(2, rows);
        AreEqual(5, rows[0][0]);
        AreEqual("e", rows[0][1]);
        AreEqual(6, rows[1][0]);
    }

    [TestMethod]
    public async Task SqlDataReader_ProcRpc_PassesRows()
    {
        var simulation = new Simulation();
        Seed(simulation);
        Wire.ExecInProc(simulation, "create table dbo.src (id int, name nvarchar(50))");
        Wire.ExecInProc(simulation, "insert dbo.src values (30, 'p'), (31, 'q')");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);

        // A separate connection drives the source reader (the simulator has no
        // MARS, so the source can't share the consuming command's connection).
        await using var sourceConnection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var sourceReader = await new SqlCommand("select id, name from dbo.src order by id", sourceConnection).ExecuteReaderAsync(TestContext.CancellationToken);

        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", sourceReader);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        var rows = await this.SinkOverWire(connection);
        HasCount(2, rows);
        AreEqual(30, rows[0][0]);
        AreEqual(31, rows[1][0]);
    }

    [TestMethod]
    public async Task EmptyTvp_PassesZeroRows()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", IdNameTable());
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        IsEmpty(await this.SinkOverWire(connection));
    }

    [TestMethod]
    public async Task NullStructuredParameter_RejectedClientSide()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", DBNull.Value);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";

        // SqlClient rejects a DBNull-valued TVP before any bytes hit the wire —
        // the server never sees it (probe-confirmed against SQL Server 2025).
        _ = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
    }

    // ---- Error parity ----

    [TestMethod]
    public async Task SubsetColumns_RaisesMsg500()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var table = new DataTable();
        _ = table.Columns.Add("id", typeof(int));
        _ = table.Rows.Add(1);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", table);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(500, exception.Number);
    }

    [TestMethod]
    public async Task ExtraColumns_RaisesMsg500()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var table = new DataTable();
        _ = table.Columns.Add("id", typeof(int));
        _ = table.Columns.Add("name", typeof(string));
        _ = table.Columns.Add("extra", typeof(int));
        _ = table.Rows.Add(1, "a", 9);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", table);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(500, exception.Number);
    }

    [TestMethod]
    public async Task ReorderedIncompatibleColumns_RaisesMsg245()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // Binding is positional (column names ignored): a (name, id) source
        // drives the nvarchar value under the int id column, so real SQL Server
        // fails the conversion with Msg 245.
        var table = new DataTable();
        _ = table.Columns.Add("name", typeof(string));
        _ = table.Columns.Add("id", typeof(int));
        _ = table.Rows.Add("zed", 9);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", table);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(245, exception.Number);
    }

    [TestMethod]
    public async Task UnknownTypeName_RaisesMsg2715()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", IdNameTable((1, "a")));
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.NoSuchType";

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(2715, exception.Number);
    }

    [TestMethod]
    public async Task NullIntoNotNullColumn_RaisesMsg515()
    {
        var simulation = new Simulation();
        Seed(simulation);
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.ins_idname", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", IdNameTable((null, "x")));
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdName";

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(515, exception.Number);
    }

    [TestMethod]
    public async Task DuplicatePrimaryKey_RaisesMsg2627()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.PkType as table (id int not null primary key, name nvarchar(50) null)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, name nvarchar(50))");
        Wire.ExecInProc(simulation, "create proc dbo.ins_pk @rows dbo.PkType readonly as insert into dbo.sink select id, name from @rows");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var table = new DataTable();
        _ = table.Columns.Add("id", typeof(int));
        _ = table.Columns.Add("name", typeof(string));
        _ = table.Rows.Add(1, "a");
        _ = table.Rows.Add(1, "b");

        await using var command = new SqlCommand("dbo.ins_pk", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", table);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.PkType";

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(2627, exception.Number);
    }

    [TestMethod]
    public async Task CheckConstraintViolation_RaisesMsg547()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.CkType as table (id int not null check (id > 0))");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int)");
        Wire.ExecInProc(simulation, "create proc dbo.ins_ck @rows dbo.CkType readonly as insert into dbo.sink select id from @rows");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var table = new DataTable();
        _ = table.Columns.Add("id", typeof(int));
        _ = table.Rows.Add(-5);

        await using var command = new SqlCommand("dbo.ins_ck", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", table);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.CkType";

        var exception = await Assert.ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual(547, exception.Number);
    }

    // ---- Scalar-type round-trip matrix (dual-oracle) ----

    [TestMethod]
    public async Task ScalarTypeMatrix_RoundTripsAgainstInProcess()
    {
        const string createType = """
            create type dbo.Scalars as table (
                c_int int, c_bigint bigint, c_smallint smallint, c_tinyint tinyint,
                c_bit bit, c_decimal decimal(12, 3), c_money money, c_float float,
                c_real real, c_date date, c_datetime2 datetime2(3), c_dto datetimeoffset(3),
                c_guid uniqueidentifier, c_nvarchar nvarchar(50), c_varchar varchar(50),
                c_nvarcharmax nvarchar(max), c_varbinary varbinary(50))
            """;
        const string createSink = """
            create table dbo.sink (
                c_int int, c_bigint bigint, c_smallint smallint, c_tinyint tinyint,
                c_bit bit, c_decimal decimal(12, 3), c_money money, c_float float,
                c_real real, c_date date, c_datetime2 datetime2(3), c_dto datetimeoffset(3),
                c_guid uniqueidentifier, c_nvarchar nvarchar(50), c_varchar varchar(50),
                c_nvarcharmax nvarchar(max), c_varbinary varbinary(50))
            """;

        static DataTable BuildRow()
        {
            var table = new DataTable();
            _ = table.Columns.Add("c_int", typeof(int));
            _ = table.Columns.Add("c_bigint", typeof(long));
            _ = table.Columns.Add("c_smallint", typeof(short));
            _ = table.Columns.Add("c_tinyint", typeof(byte));
            _ = table.Columns.Add("c_bit", typeof(bool));
            _ = table.Columns.Add("c_decimal", typeof(decimal));
            _ = table.Columns.Add("c_money", typeof(decimal));
            _ = table.Columns.Add("c_float", typeof(double));
            _ = table.Columns.Add("c_real", typeof(float));
            _ = table.Columns.Add("c_date", typeof(DateTime));
            _ = table.Columns.Add("c_datetime2", typeof(DateTime));
            _ = table.Columns.Add("c_dto", typeof(DateTimeOffset));
            _ = table.Columns.Add("c_guid", typeof(Guid));
            _ = table.Columns.Add("c_nvarchar", typeof(string));
            _ = table.Columns.Add("c_varchar", typeof(string));
            _ = table.Columns.Add("c_nvarcharmax", typeof(string));
            _ = table.Columns.Add("c_varbinary", typeof(byte[]));
            _ = table.Rows.Add(
                42, 9_000_000_000L, (short)-7, (byte)255, true, 123.456m, 78.90m, 3.14159d, 2.5f,
                new DateTime(2024, 6, 15), new DateTime(2024, 6, 15, 12, 34, 56, 789),
                new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.FromHours(-5)),
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                "unicode ﬁ", "ansi text", new string('z', 5000), new byte[] { 1, 2, 3, 254 });
            return table;
        }

        // Oracle: the same DataTable bound in-process.
        List<object?[]> oracle;
        var oracleSimulation = new Simulation();
        Wire.ExecInProc(oracleSimulation, createType);
        Wire.ExecInProc(oracleSimulation, createSink);
        using (var connection = oracleSimulation.CreateDbConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "insert into dbo.sink select * from @rows";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@rows";
            parameter.Value = BuildRow();
            parameter.TypeName = "dbo.Scalars";
            _ = command.Parameters.Add(parameter);
            _ = command.ExecuteNonQuery();
            using var read = connection.CreateCommand();
            read.CommandText = "select * from dbo.sink";
            oracle = Wire.Drain(read.ExecuteReader());
        }

        // Wire: the same DataTable over the TVP RPC path.
        var wireSimulation = new Simulation();
        Wire.ExecInProc(wireSimulation, createType);
        Wire.ExecInProc(wireSimulation, createSink);
        await using var listener = await wireSimulation.ListenAsync(0, TestContext.CancellationToken);
        await using var wireConnection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var command = new SqlCommand("insert into dbo.sink select * from @rows", wireConnection))
        {
            var parameter = command.Parameters.AddWithValue("@rows", BuildRow());
            parameter.SqlDbType = SqlDbType.Structured;
            parameter.TypeName = "dbo.Scalars";
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        var wire = Wire.Drain(await new SqlCommand("select * from dbo.sink", wireConnection).ExecuteReaderAsync(TestContext.CancellationToken));

        HasCount(1, oracle);
        HasCount(1, wire);
        HasCount(oracle[0].Length, wire[0]);
        for (var i = 0; i < oracle[0].Length; i++)
            Wire.AssertValueEqual(oracle[0][i] ?? DBNull.Value, wire[0][i] ?? DBNull.Value);
    }
}
