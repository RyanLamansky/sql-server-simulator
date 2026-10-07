using System.Buffers.Binary;
using System.Text;
using SqlServerSimulator.Network;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>WRITETEXT BULK</c> and <c>UPDATETEXT BULK</c> over the wire: the batch's
/// response ends where the statement waits, and the session's next message
/// is its data or isn't. Every token sequence is the one SQL Server 2025 sent
/// a raw TDS client (captured 2026-10-07); no driver sends the bulk-load
/// packet these take on its own.
/// </summary>
[TestClass]
public sealed class BulkTextWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Pointer = "declare @p binary(16) = (select textptr(tx) from t where id = 1);";

    private static async Task<(Simulation Simulation, SimulatedNetworkListener Listener, RawTdsClient Client)> OpenAsync(CancellationToken cancellationToken)
    {
        var simulation = new Simulation();
        _ = Scalar(simulation, "create table t (id int primary key, tx text, nx ntext, im image); insert t values (1, 'abc', N'xyz', 0x0102);");
        var listener = await simulation.ListenLocalAsync(0, cancellationToken);
        var client = await RawTdsClient.ConnectAsync(listener, "simulated", cancellationToken);
        return (simulation, listener, client);
    }

    private static object? Scalar(Simulation simulation, string sql)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static byte[] Data(byte[] bytes, int? announced = null)
    {
        var payload = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, announced ?? bytes.Length);
        bytes.CopyTo(payload, 4);
        return payload;
    }

    private static void AssertTokens(byte[] response, params string[] expected)
    {
        var actual = TdsSessionFixture.Describe(response);
        CollectionAssert.AreEqual(expected, actual, string.Join(" / ", actual));
    }

    [TestMethod]
    public async Task WriteText_TheBatchEndsWhereItWaits_TheBulkPacketWritesAndTheRestRuns()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(await client.BatchAsync($"{Pointer} writetext bulk t.tx @p timestamp = 0x0000000000000000 with log; select @@rowcount", TestContext.CancellationToken), "DONE C1 COUNT 1");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray()), TestContext.CancellationToken), "DONE FC MORE 0", "ROWS", "DONE C1 COUNT 1");
        AreEqual("hello", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }

    [TestMethod]
    public async Task WriteText_ANonBulkRequest_IsMsg4022_ItsOwnTextUnread()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(await client.BatchAsync($"select 1; {Pointer} writetext bulk t.tx @p; select 2", TestContext.CancellationToken), "ROWS", "DONE C1 MORE|COUNT 1", "DONE C1 COUNT 1");
        AssertTokens(await client.BatchAsync("insert t values (2, 'x', null, null)", TestContext.CancellationToken), "ERR 4022", "INFO 3621", "DONE FD ERROR 0");
        AssertTokens(await client.BatchAsync("select count(*) from t", TestContext.CancellationToken), "ROWS", "DONE C1 COUNT 1");
        AreEqual(1, Scalar(simulation, "select count(*) from t"));
        AreEqual("abc", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }

    [TestMethod]
    public async Task WriteText_AShortAnnouncedLengthTakesItsPrefix_ALongOneIsMsg4002()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        _ = await client.BatchAsync($"{Pointer} writetext bulk t.tx @p", TestContext.CancellationToken);
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("XYZ"u8.ToArray(), announced: 2), TestContext.CancellationToken), "DONE FC FINAL 0");
        AreEqual("XY", Scalar(simulation, "select cast(tx as varchar(10)) from t"));

        _ = await client.BatchAsync($"{Pointer} writetext bulk t.tx @p; select 2", TestContext.CancellationToken);
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("XYZ"u8.ToArray(), announced: 5), TestContext.CancellationToken), "ERR 4002", "INFO 3621", "DONE FD ERROR 0");
        AreEqual("XY", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }

    /// <summary>
    /// The data are the column's own encoding, an odd byte for <c>ntext</c>
    /// reading as a last character's low byte.
    /// </summary>
    [TestMethod]
    public async Task WriteText_DataAreTheColumnsOwnEncoding()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        _ = await client.BatchAsync("declare @p binary(16) = (select textptr(nx) from t where id = 1); writetext bulk t.nx @p", TestContext.CancellationToken);
        _ = await client.SendAsync(Tds.PacketBulkLoad, Data([0x61, 0x00, 0x62]), TestContext.CancellationToken);
        _ = await client.BatchAsync("declare @p binary(16) = (select textptr(im) from t where id = 1); writetext bulk t.im @p", TestContext.CancellationToken);
        var image = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        _ = await client.SendAsync(Tds.PacketBulkLoad, Data(image), TestContext.CancellationToken);
        AreEqual("ab", Scalar(simulation, "select cast(nx as nvarchar(10)) from t"));
        CollectionAssert.AreEqual(image, (byte[])Scalar(simulation, "select cast(im as varbinary(max)) from t")!);
    }

    [TestMethod]
    public async Task UpdateText_SplicesTheData_ItsOffsetJudgedOnceTheyArrive()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(await client.BatchAsync($"{Pointer} updatetext bulk t.tx @p 1 1", TestContext.CancellationToken), "DONE C1 COUNT 1");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("XYZ"u8.ToArray()), TestContext.CancellationToken), "DONE F8 FINAL 0");
        AreEqual("aXYZc", Scalar(simulation, "select cast(tx as varchar(10)) from t"));

        AssertTokens(await client.BatchAsync($"{Pointer} updatetext bulk t.tx @p 99 0; select 2", TestContext.CancellationToken), "DONE C1 COUNT 1");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("Q"u8.ToArray()), TestContext.CancellationToken), "ERR 7116", "INFO 3621", "DONE FD ERROR 0");
    }

    [TestMethod]
    public async Task AnAttention_EndsTheWaitingBatch()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        _ = await client.BatchAsync($"{Pointer} writetext bulk t.tx @p; select 2", TestContext.CancellationToken);
        AssertTokens(await client.SendAsync(Tds.PacketAttention, [], TestContext.CancellationToken), "INFO 3621", "DONE FD ERROR|ATTN 0");
        AssertTokens(await client.BatchAsync("select @@error", TestContext.CancellationToken), "ROWS", "DONE C1 COUNT 1");
        AreEqual("abc", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }

    [TestMethod]
    public async Task ATransactionManagerRequest_EndsTheSession()
    {
        var (_, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        _ = await client.BatchAsync($"begin tran; {Pointer} writetext bulk t.tx @p", TestContext.CancellationToken);
        AssertTokens(await client.BeginTransactionAsync(TestContext.CancellationToken), "ERR 4014", "ENV10", "INFO 3621", "ERR 596", "DONE FD ERROR|SRVERROR 0");
        IsEmpty(await client.BatchAsync("select 1", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UnderXactAbort_Msg4022RollsBackAheadOfMsg3621()
    {
        var (_, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        _ = await client.BatchAsync($"set xact_abort on; begin tran; {Pointer} writetext bulk t.tx @p", TestContext.CancellationToken);
        AssertTokens(await client.BatchAsync("select 1", TestContext.CancellationToken), "ERR 4022", "ENV10", "INFO 3621", "DONE FD ERROR 0");
    }

    [TestMethod]
    public async Task ABulkPacketNothingAwaits_EndsTheSession()
    {
        var (_, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data(Encoding.ASCII.GetBytes("MM")), TestContext.CancellationToken), "DONE 00 ERROR 0");
        IsEmpty(await client.BatchAsync("select 1", TestContext.CancellationToken));
    }
}
