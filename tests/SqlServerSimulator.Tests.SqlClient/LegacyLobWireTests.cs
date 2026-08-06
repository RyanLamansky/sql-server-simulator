using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The deprecated <c>text</c> / <c>ntext</c> / <c>image</c> types over the wire.
/// The codec advertises them with their legacy TYPE_INFO — LONGLEN max size
/// (0x7FFFFFFF text/image, 0x7FFFFFFE ntext), the 5-byte collation for the
/// string pair, and the TableName field only these types carry — and streams
/// each ROW value in the in-band textptr form (a 16-byte text pointer + 8-byte
/// timestamp placeholder, a 4-byte data length, then the raw bytes; a single
/// zero byte for NULL). Wire shapes probe-captured against SQL Server 2025
/// through a cleartext tee proxy (2026-07-19). Values are dual-read against the
/// in-process ADO oracle over the same simulation.
/// </summary>
[TestClass]
public sealed class LegacyLobWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly string LargeText = new('Z', 100_000);
    private static readonly string LargeNText = new('ç', 60_000);

    [TestMethod]
    public async Task LegacyLobColumns_SelectAllShapes_MatchInProcOracle()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table L (id int not null, t text null, n ntext null, i image null);");
        Wire.ExecInProcParam(simulation, "insert L (id, t, n, i) values (1, null, null, null)", "@x", 0);
        Wire.ExecInProc(simulation, "insert L (id, t, n, i) values (2, '', N'', 0x);");
        Wire.ExecInProc(simulation, "insert L (id, t, n, i) values (3, 'hello', N'wörld', 0x0102030405);");
        Wire.ExecInProcParam(simulation, "insert L (id, t, n, i) values (4, @x, null, null)", "@x", LargeText);
        Wire.ExecInProcParam(simulation, "insert L (id, t, n, i) values (5, null, @x, null)", "@x", LargeNText);
        Wire.ExecInProcParam(simulation, "insert L (id, t, n, i) values (6, null, null, @x)", "@x", MakeBytes(100_000));

        var oracle = Wire.ReadAllInProc(simulation, "select t, n, i from L order by id");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select t, n, i from L order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("text", reader.GetDataTypeName(0));
        AreEqual("ntext", reader.GetDataTypeName(1));
        AreEqual("image", reader.GetDataTypeName(2));

        var wireRows = Wire.Drain(reader);
        HasCount(oracle.Count, wireRows);
        for (var row = 0; row < oracle.Count; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                if (oracle[row][col] is null)
                    IsNull(wireRows[row][col]);
                else
                    Wire.AssertValueEqual(oracle[row][col]!, wireRows[row][col]!);
            }
        }
    }

    [TestMethod]
    public async Task LegacyLobColumns_AccessorMatrix()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table L (id int not null, t text null, n ntext null, i image null);");
        Wire.ExecInProc(simulation, "insert L values (1, 'hello world', N'grüße', 0x00ff10203040);");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select t, n, i from L", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));

        // GetString / GetValue on the string pair.
        AreEqual("hello world", reader.GetString(0));
        AreEqual("grüße", reader.GetString(1));
        AreEqual("hello world", (string)reader.GetValue(0));

        // GetChars materializes a windowed slice of the text column.
        var window = new char[5];
        var copied = reader.GetChars(0, 6, window, 0, 5);
        AreEqual(5L, copied);
        AreEqual("world", new string(window));

        // GetTextReader streams the ntext column.
        using (var textReader = reader.GetTextReader(1))
            AreEqual("grüße", await textReader.ReadToEndAsync(TestContext.CancellationToken));

        // GetSqlBytes / GetBytes on the image column.
        var expected = new byte[] { 0x00, 0xFF, 0x10, 0x20, 0x30, 0x40 };
        CollectionAssert.AreEqual(expected, reader.GetSqlBytes(2).Value);
        AreEqual(expected.Length, (int)reader.GetBytes(2, 0, null, 0, 0));
        CollectionAssert.AreEqual(expected, (byte[])reader.GetValue(2));
    }

    /// <summary>
    /// <c>text</c> streams CP1252 bytes per the column collation, so a Windows-1252
    /// character round-trips to the same string the in-process path returns and
    /// <c>DATALENGTH</c> counts single bytes; <c>ntext</c> streams UTF-16LE.
    /// </summary>
    [TestMethod]
    public async Task LegacyLobColumns_StringCollationBytes()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table L (t text null, n ntext null);");
        // Euro sign: one byte 0x80 in CP1252, two bytes in UTF-16LE.
        Wire.ExecInProc(simulation, "insert L values ('a€b', N'a€b');");

        var oracle = Wire.ReadAllInProc(simulation, "select t, n, datalength(t), datalength(n) from L");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select t, n, datalength(t), datalength(n) from L", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));

        AreEqual((string)oracle[0][0]!, reader.GetString(0));
        AreEqual("a€b", reader.GetString(0));
        AreEqual("a€b", reader.GetString(1));
        AreEqual(3, reader.GetInt32(2)); // CP1252: 3 bytes
        AreEqual(6, reader.GetInt32(3)); // UTF-16LE: 6 bytes
    }

    [TestMethod]
    public async Task LegacyLobColumns_LargeMultiPacket_RoundTrips()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table L (t text null, n ntext null, i image null);");
        Wire.ExecInProcParam(simulation, "insert L (t) values (@x)", "@x", LargeText);
        Wire.ExecInProcParam(simulation, "insert L (n) values (@x)", "@x", LargeNText);
        Wire.ExecInProcParam(simulation, "insert L (i) values (@x)", "@x", MakeBytes(100_000));

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select t, n, i from L", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(LargeText, reader.GetString(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(LargeNText, reader.GetString(1));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(MakeBytes(100_000), reader.GetSqlBytes(2).Value);
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task LegacyLobInputParameters_ThroughStoredProc()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table L (id int identity primary key, t text null, n ntext null, i image null);
            """);
        Wire.ExecInProc(simulation, """
            create procedure Ins @a text, @b ntext, @c image as
                insert L (t, n, i) values (@a, @b, @c);
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await ExecProc(connection, "hi", "né", [9, 8, 7]);
        await ExecProc(connection, null, null, null);
        await ExecProc(connection, LargeText, LargeNText, MakeBytes(20_000));

        await AssertBackFillOverWire(listener);
    }

    [TestMethod]
    public async Task LegacyLobInputParameters_ThroughSpExecuteSql()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table L (id int identity primary key, t text null, n ntext null, i image null);");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await ExecInsert(connection, "hi", "né", [9, 8, 7]);
        await ExecInsert(connection, null, null, null);
        await ExecInsert(connection, LargeText, LargeNText, MakeBytes(20_000));

        await AssertBackFillOverWire(listener);
    }

    private static async Task ExecProc(SqlConnection connection, string? text, string? ntext, byte[]? image)
    {
        await using var command = new SqlCommand("Ins", connection) { CommandType = CommandType.StoredProcedure };
        _ = command.Parameters.Add(new SqlParameter("@a", SqlDbType.Text) { Value = (object?)text ?? DBNull.Value });
        _ = command.Parameters.Add(new SqlParameter("@b", SqlDbType.NText) { Value = (object?)ntext ?? DBNull.Value });
        _ = command.Parameters.Add(new SqlParameter("@c", SqlDbType.Image) { Value = (object?)image ?? DBNull.Value });
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecInsert(SqlConnection connection, string? text, string? ntext, byte[]? image)
    {
        await using var command = new SqlCommand("insert L (t, n, i) values (@a, @b, @c)", connection);
        _ = command.Parameters.Add(new SqlParameter("@a", SqlDbType.Text) { Value = (object?)text ?? DBNull.Value });
        _ = command.Parameters.Add(new SqlParameter("@b", SqlDbType.NText) { Value = (object?)ntext ?? DBNull.Value });
        _ = command.Parameters.Add(new SqlParameter("@c", SqlDbType.Image) { Value = (object?)image ?? DBNull.Value });
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task AssertBackFillOverWire(SimulatedNetworkListener listener)
    {
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select t, n, i from L order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        AssertBackFillRows(Wire.Drain(reader));
    }

    private static void AssertBackFillRows(List<object?[]> rows)
    {
        HasCount(3, rows);

        AreEqual("hi", rows[0][0]);
        AreEqual("né", rows[0][1]);
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, (byte[])rows[0][2]!);

        IsNull(rows[1][0]);
        IsNull(rows[1][1]);
        IsNull(rows[1][2]);

        AreEqual(LargeText, rows[2][0]);
        AreEqual(LargeNText, rows[2][1]);
        CollectionAssert.AreEqual(MakeBytes(20_000), (byte[])rows[2][2]!);
    }

    /// <summary>
    /// A binary slice advertises the <c>varbinary</c> family at the width the
    /// constant length names, <c>image</c> included; a MAX source stays MAX
    /// (SqlClient reports <c>int.MaxValue</c>) and a non-constant length leaves
    /// the source's own width, which for <c>image</c> is the 8000-byte
    /// container. Probe-confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("select substring(vb, 2, 3) as x from B", 3)]
    [DataRow("select substring(im, 2, 3) as x from B", 3)]
    [DataRow("select substring(vbm, 2, 3) as x from B", int.MaxValue)]
    [DataRow("declare @n int = 3; select substring(vb, 2, @n) as x from B", 30)]
    [DataRow("declare @n int = 3; select substring(im, 2, @n) as x from B", 8000)]
    public async Task BinarySubstring_ColumnSize(string sql, int expected)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table B (id int not null, vb varbinary(30), im image, vbm varbinary(max), t text);
            insert B values (1, 0x0102030405060708090A, 0x0102030405060708, 0x01020304050607080910, 'Hello world');
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        AreEqual(expected, reader.GetColumnSchema()[0].ColumnSize);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(new byte[] { 0x02, 0x03, 0x04 }, (byte[])reader.GetValue(0));
    }

    /// <summary>
    /// <c>READTEXT</c> hands the client one row of one column carrying the read
    /// column's own name and legacy type.
    /// </summary>
    [TestMethod]
    public async Task ReadText_ResultSetOverTheWire()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table L (id int not null, t text);
            insert L values (1, 'Hello world');
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            """
            declare @p varbinary(16);
            select @p = textptr(t) from L where id = 1;
            readtext L.t @p 6 5;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        AreEqual("t", reader.GetName(0));
        AreEqual("text", reader.GetDataTypeName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("world", reader.GetString(0));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    private static byte[] MakeBytes(int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
            bytes[i] = (byte)(i & 0xFF);

        return bytes;
    }
}
