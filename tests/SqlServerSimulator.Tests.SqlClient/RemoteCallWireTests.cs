using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Real SqlClient over a loopback linked server's remote calls, which the
/// provider relays as a procedure scope (captured 2026-10-17 against SQL
/// Server 2025): every rowset read in order, the server's write counts
/// folded into <c>RecordsAffected</c> as real's DONEINPROCs carry them, and
/// one pooled provider session answering every caller.
/// </summary>
[TestClass]
public sealed class RemoteCallWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Loopback()
    {
        var simulation = new Simulation();
        simulation.AddRemoteSimulation("SELF", simulation);
        Wire.ExecInProc(simulation, "exec sp_addlinkedserver 'SELF', 'SQL Server'");
        Wire.ExecInProc(simulation, "create table t (a int)");
        return simulation;
    }

    [TestMethod]
    public async Task ExecuteAt_ReadsEveryRowset_AndCountsTheServersWrites()
    {
        var simulation = Loopback();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var writes = new SqlCommand("exec ('insert simulated..t values (1); insert simulated..t values (2), (3)') at SELF", connection))
            AreEqual(3, await writes.ExecuteNonQueryAsync(TestContext.CancellationToken));
        await using var reads = new SqlCommand("exec ('select 1 as a; select count(*) as b from simulated..t') at SELF; select 7 as c", connection);
        var values = new List<int>();
        await using (var reader = await reads.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                    values.Add(reader.GetInt32(0));
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        }
        CollectionAssert.AreEqual(new[] { 1, 3, 7 }, values);
    }

    [TestMethod]
    public async Task RemoteCalls_FromTwoClients_MeetThePooledSession()
    {
        var simulation = Loopback();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var first = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var second = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var fromFirst = new SqlCommand("exec ('select @@spid') at SELF", first);
        await using var fromSecond = new SqlCommand("exec ('select @@spid') at SELF", second);
        AreEqual(await fromFirst.ExecuteScalarAsync(TestContext.CancellationToken), await fromSecond.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
