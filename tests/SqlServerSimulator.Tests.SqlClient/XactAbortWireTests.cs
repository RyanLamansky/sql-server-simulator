using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET XACT_ABORT ON</c> over the wire: real SqlClient sees the same
/// promotion the in-process front door does, since it rides the existing error
/// plumbing rather than any new TDS token. Also pins the fresh-session
/// <c>@@OPTIONS</c> a SqlClient login reports — 5432, with the XACT_ABORT bit
/// clear (probe-confirmed against SQL Server 2025 through both SqlClient and
/// sqlcmd).
/// </summary>
[TestClass]
public sealed class XactAbortWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PromotedErrorRollsBackAndEndsTheBatch()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int primary key, v int not null)");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var failing = new SqlCommand(
            """
            set xact_abort on;
            begin tran;
            insert into t (id, v) values (1, 1);
            insert into t (id, v) values (1, 2);
            insert into t (id, v) values (2, 2);
            """,
            connection))
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => failing.ExecuteNonQueryAsync(TestContext.CancellationToken));
            AreEqual(2627, ex.Number);
        }

        await using var check = new SqlCommand("select @@trancount, (select count(*) from t)", connection);
        await using var reader = await check.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(0, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task FreshSessionOptionsMatchSqlClientLogin()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select @@options, @@options & 16384, @@datefirst", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(5432, reader.GetInt32(0));
        AreEqual(0, reader.GetInt32(1));
        AreEqual((byte)7, reader.GetByte(2));
    }
}
