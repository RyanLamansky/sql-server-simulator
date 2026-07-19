using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Payloads large enough to force PLP chunking and multi-packet TDS responses,
/// plus a wide row count, all over the real wire.
/// </summary>
[TestClass]
public sealed class LargeValueTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NVarcharMax_200k_RoundTripsExactly()
    {
        var value = string.Concat(Enumerable.Range(0, 20000).Select(i => $"アイウ{i:D6}x")); // 200,000 chars
        AreEqual(200000, value.Length);

        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (v nvarchar(max))");
        Wire.ExecInProcParam(simulation, "insert t (v) values (@v)", "@v", value);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select v from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        var read = reader.GetString(0);
        AreEqual(200000, read.Length);
        AreEqual(value, read);
    }

    /// <summary>
    /// The concatenation of two CAST(… AS nvarchar(max)) operands selected
    /// through a variable — DacFx's data-phase row-size sampler builds its
    /// dynamic TABLESAMPLE SQL exactly this way, and the malformed wire value
    /// killed the session as a transport-level error.
    /// </summary>
    [TestMethod]
    public async Task NVarcharMax_CastConcat_SelectsOverWire()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand(
            "declare @sql nvarchar(max); select @sql = cast(N'select 1' as nvarchar(max)) + cast(N'+1' as nvarchar(max)); select @sql",
            connection);
        AreEqual("select 1+1", (string?)await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task VarbinaryMax_300k_RoundTripsExactly()
    {
        var value = new byte[300000];
        for (var i = 0; i < value.Length; i++)
            value[i] = (byte)((i * 31) + 7);

        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (v varbinary(max))");
        Wire.ExecInProcParam(simulation, "insert t (v) values (@v)", "@v", value);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select v from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        var read = (byte[])reader.GetValue(0);
        HasCount(300000, read);
        CollectionAssert.AreEqual(value, read);
    }

    [TestMethod]
    public async Task ObjectDefinition_LargeModule_RoundTripsOverWire()
    {
        // OBJECT_DEFINITION types as nvarchar(max) — a module definition can
        // exceed the bounded 2-byte wire length prefix (WWI's DataLoadSimulation
        // procs are ~250 KB). Before it was retyped, its length-0 nvarchar hit
        // the bounded value path, overflowed the ushort length, and the
        // OverflowException escaped the session's crash boundary as a silent
        // transport-level error — the SMO API sweep's most severe find. A body
        // over 32,767 chars is the reproduction threshold.
        var simulation = new Simulation();
        var padding = new string('x', 40000);
        Wire.ExecInProc(simulation, $"create procedure dbo.big_proc as /* {padding} */ select 1");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select object_definition(object_id('dbo.big_proc'))", connection);
        var definition = (string?)await command.ExecuteScalarAsync(TestContext.CancellationToken);

        IsNotNull(definition);
        IsGreaterThan(40000, definition!.Length);
        Contains(padding, definition);
    }

    // Each scalar below is nvarchar(max) on real SQL Server (probe-confirmed
    // against SQL Server 2025). Before being retyped from the length-0
    // "value-width" nvarchar, a result over 32,767 chars hit the codec's
    // bounded 2-byte length prefix and overflowed the ushort — the same latent
    // crash class as OBJECT_DEFINITION. Every expression here produces >40,000
    // chars, so a bounded typing would kill the session; a max typing streams
    // it as PLP. The inputs use CAST(... AS nvarchar(max)) so REPLICATE carries
    // unbounded length through.
    [DataRow("JSON_QUERY", "json_query(N'[0' + replicate(cast(N',0' as nvarchar(max)), 20000) + N']', '$')")]
    [DataRow("JSON_MODIFY", "json_modify(N'{}', '$.a', replicate(cast(N'a' as nvarchar(max)), 40000))")]
    [DataRow("JSON_OBJECT", "json_object('k': replicate(cast(N'a' as nvarchar(max)), 40000))")]
    [DataRow("JSON_ARRAY", "json_array(replicate(cast(N'a' as nvarchar(max)), 40000))")]
    [DataRow("STRING_ESCAPE", "string_escape(replicate(cast(N'a' as nvarchar(max)), 40000), 'json')")]
    [DataRow("CONCAT", "concat(replicate(cast(N'a' as nvarchar(max)), 40000), N'b')")]
    [DataRow("CONCAT_WS", "concat_ws(N',', replicate(cast(N'a' as nvarchar(max)), 40000), N'b')")]
    [DataRow("TRANSLATE", "translate(replicate(cast(N'a' as nvarchar(max)), 40000), N'a', N'b')")]
    [TestMethod]
    public async Task NVarcharMaxScalars_LargeResult_RoundTripOverWire(string name, string expression)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand($"select {expression}", connection);
        var result = (string?)await command.ExecuteScalarAsync(TestContext.CancellationToken);

        // 32,767 chars (65,535 bytes) is the bounded-nvarchar ushort-overflow
        // threshold; each expression clears it (≥40,000 chars) and round-trips.
        IsNotNull(result, name);
        IsGreaterThan(32767, result!.Length, name);
    }

    [TestMethod]
    public async Task Decompress_LargeResult_RoundTripsOverWire()
    {
        // COMPRESS / DECOMPRESS are varbinary(max) on real SQL Server; a large
        // inflated payload exceeds the bounded wire prefix and must stream as
        // PLP. 40,000 nvarchar(max) chars → 80,000 UTF-16 bytes inflated.
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select decompress(compress(replicate(cast(N'a' as nvarchar(max)), 40000)))", connection);
        var result = (byte[]?)await command.ExecuteScalarAsync(TestContext.CancellationToken);

        IsNotNull(result);
        HasCount(80000, result!);
    }

    [TestMethod]
    public async Task StringAgg_LargeMaxOperand_RoundTripsOverWire()
    {
        // STRING_AGG over an nvarchar(max) operand streams unbounded, so a
        // multi-row concatenation past the bounded wire prefix rides PLP.
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (v nvarchar(max))");
        Wire.ExecInProc(simulation,
            "insert t (v) select replicate(cast(N'a' as nvarchar(max)), 40000) from generate_series(1, 3)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select string_agg(v, N',') from t", connection);
        var result = (string?)await command.ExecuteScalarAsync(TestContext.CancellationToken);

        IsNotNull(result);
        IsGreaterThan(120000, result!.Length);
    }

    [TestMethod]
    public async Task StringAgg_BoundedOperandOverflow_Raises9829OverWire()
    {
        // A bounded (non-MAX) STRING_AGG operand whose concatenation exceeds
        // 8000 bytes raises Msg 9829 on real SQL Server (rather than truncating
        // or, on the simulator's wire, overflowing the bounded length prefix).
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (v nvarchar(100))");
        Wire.ExecInProc(simulation,
            "insert t (v) select replicate(N'a', 100) from generate_series(1, 200)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select string_agg(v, N',') from t", connection);
        var exception = await ThrowsExactlyAsync<SqlException>(
            async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));

        AreEqual(9829, exception.Number);
    }

    [DataRow("isnull-user", "select isnull(big, N'') from t")]
    [DataRow("coalesce-user", "select coalesce(big, N'') from t")]
    [DataRow("case-user", "select case when id = 1 then big else N'' end from t")]
    [TestMethod]
    public async Task MaxColumn_WrappedInExpression_RoundTripsOverWire(string label, string query)
    {
        // A HeapColumn can carry its MAX-ness in MaxLength while its .Type is a
        // length-0 "value-width" variant (catalog columns like
        // sys.sql_modules.definition are declared this way, but the projection
        // resolver applies to every source). Wrapping such a column in an
        // expression must still type nvarchar(max) — before the resolver folded
        // MaxLength back in, ISNULL/COALESCE/CASE over a large value lost MAX
        // and overflowed the bounded wire prefix, silently killing the session
        // (SMO reads proc bodies as ISNULL(sql_modules.definition, …) — the SMO
        // API sweep's residual transport crash).
        var value = new string('x', 40000);
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int, big nvarchar(max))");
        Wire.ExecInProcParam(simulation, "insert t values (1, @v)", "@v", value);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(query, connection);
        var result = (string?)await command.ExecuteScalarAsync(TestContext.CancellationToken);

        IsNotNull(result, label);
        IsGreaterThan(32767, result!.Length, label);
    }

    [TestMethod]
    public async Task CatalogModuleDefinition_WrappedInIsNull_RoundTripsOverWire()
    {
        // SMO's exact proc-body shape: ISNULL(sql_modules.definition, …) over a
        // >32,767-char module definition, which must stream as PLP.
        var simulation = new Simulation();
        var padding = new string('x', 40000);
        Wire.ExecInProc(simulation, $"create procedure dbo.big_proc as /* {padding} */ select 1");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select isnull(m.definition, N'') from sys.sql_modules m where m.object_id = object_id('dbo.big_proc')",
            connection);
        var definition = (string?)await command.ExecuteScalarAsync(TestContext.CancellationToken);

        IsNotNull(definition);
        IsGreaterThan(40000, definition!.Length);
    }

    [TestMethod]
    public async Task TenThousandRows_StreamOverWire()
    {
        var simulation = new Simulation();

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select value from generate_series(1, 10000) order by value", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var count = 0;
        var first = 0L;
        var last = 0L;
        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            var current = Convert.ToInt64(reader.GetValue(0));
            if (count == 0)
                first = current;
            last = current;
            count++;
        }

        AreEqual(10000, count);
        AreEqual(1L, first);
        AreEqual(10000L, last);
    }
}
