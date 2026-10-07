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

    /// <summary>
    /// Inside a <c>TRY</c>, a transaction-manager request's end of the session
    /// sends Msg 3621 ahead of the rollback, and the waiting statement closes
    /// with its own DONE.
    /// </summary>
    [TestMethod]
    [DataRow("begin tran; ", "writetext", new[] { "ERR 4014", "INFO 3621", "DONE FC MORE|ERROR 0", "ENV10", "ERR 596", "DONE FD ERROR|SRVERROR 0" })]
    [DataRow("", "writetext", new[] { "ERR 4014", "INFO 3621", "ERR 596", "DONE FC ERROR|SRVERROR 0" })]
    [DataRow("", "updatetext", new[] { "ERR 4014", "INFO 3621", "ERR 596", "DONE F8 ERROR|SRVERROR 0" })]
    public async Task ATransactionManagerRequest_EndsTheSession_FromInsideATry(string prefix, string statement, string[] expected)
    {
        var (_, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        var waiting = statement == "writetext" ? "writetext bulk t.tx @p" : "updatetext bulk t.tx @p 0 0";
        _ = await client.BatchAsync($"{prefix}{Pointer} begin try {waiting} end try begin catch select 1 end catch", TestContext.CancellationToken);
        AssertTokens(await client.BeginTransactionAsync(TestContext.CancellationToken), expected);
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
    /// <summary>
    /// A bulk form in a block, an <c>IF</c>, a <c>WHILE</c> or a <c>TRY</c>
    /// suspends the batch there too: the response ends with the last DONE
    /// sent ahead of it, and the data's packet resumes the batch inside the
    /// statement enclosing it.
    /// </summary>
    [TestMethod]
    [DataRow("begin writetext bulk t.tx @p; select 2 end; select 3",
        new[] { "DONE C1 COUNT 1" },
        new[] { "DONE FC MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1" })]
    [DataRow("if 1 = 1 writetext bulk t.tx @p; select 2",
        new[] { "DONE C1 MORE|COUNT 1", "DONE C0 FINAL 0" },
        new[] { "DONE FC MORE 0", "ROWS", "DONE C1 COUNT 1" })]
    [DataRow("if 1 = 1 begin select 1; writetext bulk t.tx @p; select 2 end else select 9; select 3",
        new[] { "DONE C1 MORE|COUNT 1", "DONE C0 MORE 0", "ROWS", "DONE C1 COUNT 1" },
        new[] { "DONE FC MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1" })]
    [DataRow("begin try select 1; writetext bulk t.tx @p; select 2 end try begin catch select error_number() end catch; select 3",
        new[] { "DONE C1 MORE|COUNT 1", "DONE 15D MORE 0", "ROWS", "DONE C1 COUNT 1" },
        new[] { "DONE FC MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15F MORE 0", "ROWS", "DONE C1 COUNT 1" })]
    public async Task WriteText_NestedInACompoundStatement_SuspendsTheBatchThere(string statement, string[] suspended, string[] resumed)
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(await client.BatchAsync($"{Pointer} {statement}", TestContext.CancellationToken), suspended);
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray()), TestContext.CancellationToken), resumed);
        AreEqual("hello", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }

    /// <summary>A loop's bulk form suspends the batch at every pass.</summary>
    [TestMethod]
    public async Task WriteText_InAWhileLoop_SuspendsTheBatchAtEveryPass()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(
            await client.BatchAsync($"{Pointer} declare @i int = 0; while @i < 2 begin writetext bulk t.tx @p; set @i += 1; select @i end; select 3", TestContext.CancellationToken),
            "DONE C1 MORE|COUNT 1", "DONE C1 MORE|COUNT 1", "DONE C0 FINAL 0");
        AssertTokens(
            await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray()), TestContext.CancellationToken),
            "DONE FC MORE 0", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 MORE|COUNT 1", "DONE C0 FINAL 0");
        AssertTokens(
            await client.SendAsync(Tds.PacketBulkLoad, Data("again"u8.ToArray()), TestContext.CancellationToken),
            "DONE FC MORE 0", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 MORE|COUNT 1", "DONE C0 MORE 0", "ROWS", "DONE C1 COUNT 1");
        AreEqual("again", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }

    /// <summary>
    /// A <c>TRY</c> around a waiting bulk form catches its Msg 4022 or
    /// Msg 4002, the error's line the statement's, and the batch's remainder
    /// answers the request that brought no data; the caught error dooms a
    /// transaction, which the batch's end rolls back with Msg 3998.
    /// </summary>
    [TestMethod]
    public async Task WriteText_InATry_ItsMsg4022OrMsg4002IsCaught()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;
        const string Try = "\nbegin try\n select 1;\n\n writetext bulk t.tx @p;\n select 2\nend try\nbegin catch\n select error_number(), error_line(), xact_state()\nend catch;\nselect 3";

        AssertTokens(await client.BatchAsync(Pointer + Try, TestContext.CancellationToken), "DONE C1 MORE|COUNT 1", "DONE 15D MORE 0", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.BatchAsync("select 42", TestContext.CancellationToken), "DONE FC MORE 0", "DONE 15E MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15F MORE 0", "ROWS", "DONE C1 COUNT 1");

        AssertTokens(await client.BatchAsync(Pointer + Try, TestContext.CancellationToken), "DONE C1 MORE|COUNT 1", "DONE 15D MORE 0", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray(), announced: 10), TestContext.CancellationToken), "DONE FC MORE 0", "DONE 15E MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15F MORE 0", "ROWS", "DONE C1 COUNT 1");

        AssertTokens(await client.BatchAsync("begin tran; " + Pointer + Try + "; select @@trancount", TestContext.CancellationToken), "ENV8", "DONE D4 MORE 0", "DONE C1 MORE|COUNT 1", "DONE 15D MORE 0", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.BatchAsync("select 42", TestContext.CancellationToken), "DONE FC MORE 0", "DONE 15E MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15F MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 MORE|COUNT 1", "ERR 3998", "ENV10", "DONE FD ERROR 0");
        AssertTokens(await client.BatchAsync("select @@trancount", TestContext.CancellationToken), "ROWS", "DONE C1 COUNT 1");
        AreEqual("abc", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }

    /// <summary>
    /// Uncaught, a nested bulk form's Msg 4022 ends the batch as one at the
    /// batch's level does, and an attention while one waits ends the batch
    /// with Msg 3621.
    /// </summary>
    [TestMethod]
    public async Task WriteText_InABlock_AnotherRequestOrAnAttentionEndsTheBatch()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(await client.BatchAsync($"begin tran; {Pointer} begin select 1; writetext bulk t.tx @p; select 2 end; select 3", TestContext.CancellationToken), "ENV8", "DONE D4 MORE 0", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.BatchAsync("select 42", TestContext.CancellationToken), "ERR 4022", "ENV10", "INFO 3621", "DONE FD ERROR 0");
        AssertTokens(await client.BatchAsync("select @@trancount", TestContext.CancellationToken), "ROWS", "DONE C1 COUNT 1");

        AssertTokens(await client.BatchAsync($"{Pointer} declare @i int = 0; while @i < 2 begin writetext bulk t.tx @p; set @i += 1; select @i end; select 3", TestContext.CancellationToken), "DONE C1 MORE|COUNT 1", "DONE C1 MORE|COUNT 1", "DONE C0 FINAL 0");
        AssertTokens(await client.SendAsync(Tds.PacketAttention, [], TestContext.CancellationToken), "INFO 3621", "DONE FD ERROR|ATTN 0");
        AssertTokens(await client.BatchAsync("select 5", TestContext.CancellationToken), "ROWS", "DONE C1 COUNT 1");
        AreEqual("abc", Scalar(simulation, "select cast(tx as varchar(10)) from t"));
    }
    /// <summary>
    /// A bulk form in a procedure suspends the batch there. The response
    /// ending ahead of it closes with a DONE where a DONEINPROC was last, and
    /// the response resuming it renders at batch level until the outermost
    /// call returns: DONE for DONEINPROC, a nested call's exit a DONEPROC with
    /// no RETURNSTATUS.
    /// </summary>
    [TestMethod]
    public async Task WriteText_InAProcedure_SuspendsTheBatchInsideTheCall()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;
        _ = Scalar(simulation, $"create proc pw as\nset nocount off;\n{Pointer}\nselect 1;\nwritetext bulk t.tx @p;\nselect 2;\nreturn 5");

        AssertTokens(await client.BatchAsync("declare @r int; exec @r = pw; select @r, 3", TestContext.CancellationToken),
            "DONEINPROC BA MORE 0", "DONEINPROC C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray()), TestContext.CancellationToken),
            "DONE FC MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE C1 MORE|COUNT 1", "RET 5", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1");
        AreEqual("hello", Scalar(simulation, "select cast(tx as varchar(10)) from t"));

        _ = await client.BatchAsync("declare @r int; exec @r = pw; select @r, 3", TestContext.CancellationToken);
        AssertTokens(await client.BatchAsync("select 42", TestContext.CancellationToken), "ERR 4022", "INFO 3621", "DONE FD ERROR 0");

        AssertTokens(await client.BatchAsync("declare @r int;\nbegin try\n exec @r = pw;\n select @r, 3\nend try\nbegin catch\n select error_number(), error_line()\nend catch", TestContext.CancellationToken),
            "DONE 15D MORE 0", "DONEINPROC BA MORE 0", "DONEINPROC C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.BatchAsync("select 42", TestContext.CancellationToken),
            "DONE FC MORE 0", "DONEPROC E0 MORE 0", "DONE 15E MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15F FINAL 0");

        // An attention ends the batch where it waits, the call's state unwound.
        _ = await client.BatchAsync("exec pw", TestContext.CancellationToken);
        AssertTokens(await client.SendAsync(Tds.PacketAttention, [], TestContext.CancellationToken), "INFO 3621", "DONE FD ERROR|ATTN 0");
        AssertTokens(await client.BatchAsync("select 1 / (1 - @@nestlevel)", TestContext.CancellationToken), "ROWS", "DONE C1 COUNT 1");
    }

    /// <summary>
    /// Calls nested around and after the waiting statement: what follows it
    /// renders at batch level until the call the batch made returns, a
    /// response ending at a nested call's exit closes with its DONEPROC, and
    /// <c>NOCOUNT</c> drops nothing there.
    /// </summary>
    [TestMethod]
    public async Task WriteText_InNestedCalls_RendersAtBatchLevelUntilTheOutermostReturns()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;
        _ = Scalar(simulation, "create table u2 (id int)");
        _ = Scalar(simulation, "create proc pin as select 10; if 1 = 1 select 11");
        _ = Scalar(simulation, $"create proc pw2 as\n{Pointer}\nselect 1;\nwritetext bulk t.tx @p;\nselect 2;\nexec pin;\nif 1 = 1 select 3;\nbegin try select 4 end try begin catch end catch");
        _ = Scalar(simulation, $"create proc pw3 as\n{Pointer}\nexec pin; writetext bulk t.tx @p; exec pin; select 2");
        _ = Scalar(simulation, "create proc pout as exec pw3; select 6; exec pin");
        _ = Scalar(simulation, "create proc pnin as set nocount on; insert u2 values (5); select 1");
        _ = Scalar(simulation, $"create proc pn as set nocount on; {Pointer} insert u2 values (1); writetext bulk t.tx @p; insert u2 values (2); exec pnin; set nocount off; insert u2 values (3); select 2");

        AssertTokens(await client.BatchAsync("exec pw2; select 5; exec pin", TestContext.CancellationToken),
            "DONEINPROC C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray()), TestContext.CancellationToken),
            "DONE FC MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 MORE|COUNT 1", "DONE C0 MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONEPROC E0 MORE 0",
            "DONE C0 MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15D MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15F MORE 0", "RET 0", "DONEPROC E0 MORE 0",
            "ROWS", "DONE C1 MORE|COUNT 1", "ROWS", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0", "ROWS", "DONEINPROC C1 MORE|COUNT 1", "RET 0", "DONEPROC E0 FINAL 0");

        AssertTokens(await client.BatchAsync("exec pout; select 7", TestContext.CancellationToken),
            "DONEINPROC C1 MORE|COUNT 1", "ROWS", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0", "ROWS", "DONEINPROC C1 MORE|COUNT 1", "DONEPROC E0 FINAL 0");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray()), TestContext.CancellationToken),
            "DONE FC MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE C0 MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONEPROC E0 MORE 0",
            "ROWS", "DONE C1 MORE|COUNT 1", "ROWS", "DONE C1 MORE|COUNT 1", "DONE C0 MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONEPROC E0 MORE 0", "RET 0", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1");

        AssertTokens(await client.BatchAsync("exec pn; select 9", TestContext.CancellationToken), "DONE FD FINAL 0");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hi"u8.ToArray()), TestContext.CancellationToken),
            "DONE FC MORE 0", "DONE C3 MORE 1", "DONE B9 MORE 0", "DONE C3 MORE 1", "ROWS", "DONE C1 MORE 1", "DONEPROC E0 MORE 0", "DONE BA MORE 0", "DONE C3 MORE|COUNT 1",
            "ROWS", "DONE C1 MORE|COUNT 1", "RET 0", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1");
        AreEqual(4, Scalar(simulation, "select count(*) from u2"));
    }

    /// <summary>A text write's error ending a procedure is followed by Msg 3621, as one at the batch's level is.</summary>
    [TestMethod]
    public async Task WriteText_AnErrorInAProcedure_IsFollowedByMsg3621()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;
        _ = Scalar(simulation, "create proc pbad as select 1; writetext t.tx 0x00000000000000000000000000000000 'x'; select 2");

        AssertTokens(await client.BatchAsync("exec pbad; select 3", TestContext.CancellationToken), "ROWS", "DONEINPROC C1 MORE|COUNT 1", "ERR 7123", "INFO 3621", "DONE FD ERROR 0");
    }

    /// <summary>A bulk form in dynamic SQL suspends the batch inside it, as one in a procedure does.</summary>
    [TestMethod]
    public async Task WriteText_InDynamicSql_SuspendsTheBatchInsideIt()
    {
        var (simulation, listener, client) = await OpenAsync(TestContext.CancellationToken);
        await using var listening = listener;
        await using var connected = client;

        AssertTokens(await client.BatchAsync($"exec ('{Pointer} select 1; writetext bulk t.tx @p; select 2'); select 3", TestContext.CancellationToken),
            "DONEINPROC C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.SendAsync(Tds.PacketBulkLoad, Data("hello"u8.ToArray()), TestContext.CancellationToken),
            "DONE FC MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "RET 0", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1");
        AreEqual("hello", Scalar(simulation, "select cast(tx as varchar(10)) from t"));

        AssertTokens(await client.BatchAsync($"exec sp_executesql N'{Pointer} select 1; writetext bulk t.tx @p; select 2'; select 3", TestContext.CancellationToken),
            "DONEINPROC C1 MORE|COUNT 1", "ROWS", "DONE C1 COUNT 1");
        AssertTokens(await client.BatchAsync("select 42", TestContext.CancellationToken), "ERR 4022", "INFO 3621", "DONE FD ERROR 0");
    }
}
