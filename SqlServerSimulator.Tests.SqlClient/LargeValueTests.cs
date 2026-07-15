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
