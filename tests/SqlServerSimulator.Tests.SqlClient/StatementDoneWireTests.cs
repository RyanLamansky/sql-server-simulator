using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Real SqlClient over the per-statement DONE stream: every statement closing
/// with a DONE of its own, a SELECT-kind count that no client folds into
/// <c>RecordsAffected</c>, procedure scopes closing with RETURNSTATUS +
/// DONEPROC, and an RPC whose statements an error ended (captured 2026-09-28
/// against SQL Server 2025 with a raw TDS client).
/// </summary>
[TestClass]
public sealed class StatementDoneWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MixedBatch_ReadsEveryResult_CountingOnlyWrites()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (a int)");
        Wire.ExecInProc(simulation, "create proc p @o int output as begin set @o = 7; select @o; return 3 end");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("""
            set nocount off; declare @x int = 1, @o int; begin tran; save tran s;
            insert t values (1), (2); if @x = 1 print 'x';
            exec p @o output; select @o; commit; waitfor delay '00:00:00'
            """, connection);
        var values = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                    values.Add(reader.GetInt32(0));
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
            AreEqual(2, reader.RecordsAffected);
        }
        CollectionAssert.AreEqual(new[] { 7, 7 }, values);
        AreEqual(2, await new SqlCommand("select count(*) from t", connection).ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task StoredProcedureRpc_RunsOnPastAStatementError_ReturningItsStatus()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table log (a int)");
        Wire.ExecInProc(simulation, "create proc p as begin insert log values (1); select 1/0; insert log values (2) end");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("p", connection) { CommandType = CommandType.StoredProcedure };
        var returned = command.Parameters.Add(new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue });
        AreEqual(8134, (await ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(TestContext.CancellationToken))).Number);
        AreEqual(-6, returned.Value);
        AreEqual(2, await new SqlCommand("select count(*) from log", connection).ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ParameterizedCommand_EndedByAnError_LeavesTheConnectionUsable()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        foreach (var (text, number) in new[] { ("select @p; select * from nosuch; select 2", 208), ("select @p; throw 50001, 'x', 1", 50001) })
        {
            await using var command = new SqlCommand(text, connection);
            _ = command.Parameters.AddWithValue("@p", 1);
            AreEqual(number, (await ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(TestContext.CancellationToken))).Number);
        }
        AreEqual(5, await new SqlCommand("select 5", connection).ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
