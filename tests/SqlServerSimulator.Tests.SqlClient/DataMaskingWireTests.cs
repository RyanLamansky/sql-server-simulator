using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What SqlClient reads of a masked value no client can decode: the single
/// byte real sends for a <c>geography</c>, <c>geometry</c> or
/// <c>hierarchyid</c> (<c>0x00</c>) and a <c>vector</c> (<c>0x30</c>), and the
/// client's own failure reading it back (probed 2026-10-16 against SQL Server
/// 2025 with SqlClient 7.0).
/// </summary>
[TestClass]
public sealed class DataMaskingWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table m (
                g geography masked with (function = 'default()'),
                gm geometry masked with (function = 'default()'),
                h hierarchyid masked with (function = 'default()'),
                v vector(3) masked with (function = 'default()'));
            insert m values (geography::Point(1, 2, 4326), geometry::Point(1, 2, 0), '/1/', '[1, 2, 3]');
            create user u without login;
            grant select on m to u
            """);
        return simulation;
    }

    [TestMethod]
    public async Task MaskedUdtValues_ReadAsOneUnreadableByte()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("execute as user = 'u'; select g, gm, h from m", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        _ = Throws<FormatException>(() => reader.GetValue(0));
        CollectionAssert.AreEqual(new byte[] { 0x00 }, reader.GetSqlBytes(0).Value);
        _ = Throws<FormatException>(() => reader.GetValue(1));
        CollectionAssert.AreEqual(new byte[] { 0x00 }, reader.GetSqlBytes(1).Value);
        _ = Throws<Microsoft.SqlServer.Types.HierarchyIdException>(() => reader.GetValue(2));
        CollectionAssert.AreEqual(new byte[] { 0x00 }, reader.GetSqlBytes(2).Value);
    }

    [TestMethod]
    public async Task MaskedVector_FailsSqlClientsVectorReader()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("execute as user = 'u'; select v from m", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        // Real's own bytes (one byte, 0x30) fail the client's vector reader as
        // it buffers the row.
        _ = await ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(TestContext.CancellationToken));
    }
}
