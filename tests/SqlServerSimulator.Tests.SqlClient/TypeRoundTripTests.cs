using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Every storage type round-trips over the wire: a representative value plus a
/// NULL row, read back through the real SqlClient reader. Values whose exact
/// wire bytes are nontrivial (datetime 1/300 rounding, money scale,
/// collation-dependent varchar) are checked against the same simulation's
/// in-process ADO surface (the dual-read oracle), never hardcoded.
/// </summary>
[TestClass]
public sealed class TypeRoundTripTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("tinyint", "255", "tinyint")]
    [DataRow("smallint", "-32768", "smallint")]
    [DataRow("int", "2147483647", "int")]
    [DataRow("bigint", "9223372036854775807", "bigint")]
    [DataRow("bit", "1", "bit")]
    [DataRow("real", "cast(1.5 as real)", "real")]
    [DataRow("float", "3.141592653589793", "float")]
    [DataRow("smallmoney", "214748.3647", "smallmoney")]
    [DataRow("money", "922337203685477.5807", "money")]
    [DataRow("decimal(9,2)", "1234567.89", "decimal")]
    [DataRow("decimal(19,4)", "123456789012345.6789", "decimal")]
    [DataRow("decimal(38,10)", "123456789012345678.0123456789", "decimal")]
    [DataRow("uniqueidentifier", "'6F9619FF-8B86-D011-B42D-00C04FC964FF'", "uniqueidentifier")]
    [DataRow("date", "'2024-02-29'", "date")]
    [DataRow("time(0)", "'13:45:30'", "time")]
    [DataRow("time(3)", "'13:45:30.123'", "time")]
    [DataRow("time(7)", "'13:45:30.1234567'", "time")]
    [DataRow("datetime2(0)", "'2024-02-29T13:45:30'", "datetime2")]
    [DataRow("datetime2(7)", "'2024-02-29T13:45:30.1234567'", "datetime2")]
    [DataRow("datetimeoffset(7)", "'2024-02-29T13:45:30.1234567+05:30'", "datetimeoffset")]
    [DataRow("smalldatetime", "'2024-02-29T13:45:00'", "smalldatetime")]
    [DataRow("datetime", "'2024-02-29T13:45:30.123'", "datetime")]
    [DataRow("char(10)", "'hello'", "char")]
    [DataRow("varchar(50)", "'café'", "varchar")]
    [DataRow("varchar(max)", "'abc'", "varchar")]
    [DataRow("nchar(10)", "N'アイウ'", "nchar")]
    [DataRow("nvarchar(50)", "N'アイウé€'", "nvarchar")]
    [DataRow("nvarchar(max)", "N'unicode'", "nvarchar")]
    [DataRow("binary(8)", "0x0102030405060708", "binary")]
    [DataRow("varbinary(50)", "0x0A0B0C", "varbinary")]
    [DataRow("varbinary(max)", "0xDEADBEEF", "varbinary")]
    [DataRow("xml", "'<root><a>1</a></root>'", "xml")]
    public async Task Type_ValueAndNull_RoundTrip(string columnType, string valueLiteral, string wireTypeName)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, $"""
            create table t (id int not null, v {columnType} null);
            insert t (id, v) values (1, {valueLiteral}), (2, null)
            """);
        var oracle = Wire.ReadAllInProc(simulation, "select v from t order by id");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from t order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(wireTypeName, reader.GetDataTypeName(0));
        IsFalse(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
        Wire.AssertValueEqual(oracle[0][0]!, reader.GetValue(0));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));

        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Rowversion_RoundTrips_EightNonNullBytes()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (id int not null, v rowversion);
            insert t (id) values (1)
            """);
        var oracle = Wire.ReadAllInProc(simulation, "select v from t");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsFalse(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
        var bytes = (byte[])reader.GetValue(0);
        HasCount(8, bytes);
        Wire.AssertValueEqual(oracle[0][0]!, bytes);
    }

    [TestMethod]
    public async Task Integers_TypedGetters()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (a tinyint, b smallint, c int, d bigint, e bit);
            insert t values (200, -12345, 1000000, 9000000000, 1)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select a, b, c, d, e from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual((byte)200, reader.GetByte(0));
        AreEqual((short)-12345, reader.GetInt16(1));
        AreEqual(1000000, reader.GetInt32(2));
        AreEqual(9000000000L, reader.GetInt64(3));
        IsTrue(reader.GetBoolean(4));
    }

    [TestMethod]
    public async Task FloatsAndMoney_TypedGetters()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (r real, f float, sm smallmoney, m money, d decimal(19,4));
            insert t values (cast(1.5 as real), 2.5, 12.3456, 922337203685477.5807, 12345.6789)
            """);
        var oracle = Wire.ReadAllInProc(simulation, "select r, f, sm, m, d from t");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select r, f, sm, m, d from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1.5f, reader.GetFloat(0));
        AreEqual(2.5d, reader.GetDouble(1));
        AreEqual(oracle[0][2], reader.GetDecimal(2));
        AreEqual(oracle[0][3], reader.GetDecimal(3));
        AreEqual(12345.6789m, reader.GetDecimal(4));
    }

    [TestMethod]
    public async Task DateTimes_TypedGetters()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (dt date, tm time(3), d2 datetime2(7), dto datetimeoffset(7), sdt smalldatetime, legacy datetime);
            insert t values ('2024-02-29', '13:45:30.123', '2024-02-29T13:45:30.1234567', '2024-02-29T13:45:30.1234567+05:30', '2024-02-29T13:45:00', '2024-02-29T13:45:30.123')
            """);
        var oracle = Wire.ReadAllInProc(simulation, "select dt, tm, d2, dto, sdt, legacy from t");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select dt, tm, d2, dto, sdt, legacy from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(new DateTime(2024, 2, 29), reader.GetDateTime(0));
        AreEqual(new TimeSpan(0, 13, 45, 30, 123), reader.GetTimeSpan(1));
        AreEqual(oracle[0][2], reader.GetDateTime(2));
        AreEqual(oracle[0][3], reader.GetDateTimeOffset(3));
        AreEqual(new DateTime(2024, 2, 29, 13, 45, 0), reader.GetDateTime(4));
        // datetime rounds to 1/300-second ticks at the ADO.NET boundary; trust the in-process oracle for the exact value.
        AreEqual(oracle[0][5], reader.GetDateTime(5));
    }

    [TestMethod]
    public async Task Guid_TypedGetter()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (g uniqueidentifier);
            insert t values ('6F9619FF-8B86-D011-B42D-00C04FC964FF')
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select g from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(new Guid("6F9619FF-8B86-D011-B42D-00C04FC964FF"), reader.GetGuid(0));
    }

    [TestMethod]
    public async Task Strings_TypedGetters_IncludingUnicodeAndCp1252AndEmpty()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (fixed char(10), ansi varchar(50), unicode nvarchar(50), empty varchar(50));
            insert t values ('hello', 'café', N'アイウé€', '')
            """);
        var oracle = Wire.ReadAllInProc(simulation, "select fixed, ansi, unicode, empty from t");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select fixed, ansi, unicode, empty from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("hello     ", reader.GetString(0));
        AreEqual("café", reader.GetString(1));
        AreEqual("アイウé€", reader.GetString(2));
        AreEqual("", reader.GetString(3));
        AreEqual(oracle[0][1], reader.GetString(1));
    }

    /// <summary>
    /// SqlClient's <c>GetDecimal</c> hands back the declared scale, which the
    /// value-equality assertions above can't see (scale is invisible to
    /// <see cref="decimal"/> equality) and which the in-proc oracle can't
    /// witness on its own. Expectations are what SqlClient reads from a real
    /// server for the same columns.
    /// </summary>
    [TestMethod]
    public async Task DecimalAndMoney_TypedGetters_CarryTheDeclaredScale()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (n numeric(10, 2), z numeric(10, 0), m money, s smallmoney);
            insert t values (1, 1, 1, 1)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select n, z, m, s from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("1.00", reader.GetDecimal(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AreEqual("1", reader.GetDecimal(1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AreEqual("1.0000", reader.GetDecimal(2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AreEqual("1.0000", reader.GetDecimal(3).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task Binary_TypedGetters()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (fixed binary(8), var varbinary(50));
            insert t values (0x0102030405060708, 0xDEADBEEF)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select fixed, var from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, (byte[])reader.GetValue(0));
        CollectionAssert.AreEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, reader.GetSqlBinary(1).Value);
    }
}
