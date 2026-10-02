using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A row-writing statement whose error a TRY frame catches closes with a DONE
/// counting 0 ahead of the CATCH block's output — one per failing write on the
/// way out — while <c>NOCOUNT</c> suppresses it and an uncaught error leaves
/// none; a client-bound <c>OUTPUT</c> clause reports it as an empty result set
/// (probed 2026-09-28 against SQL Server 2025). A write failing inside a
/// procedure's block, IF or WHILE reports its count once, the enclosing
/// statements adding none (probed 2026-10-02).
/// </summary>
[TestClass]
public sealed class CaughtWriteCountWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private async Task<(string Completed, string Shape)> RunAsync(string setup, string sql)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, setup);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        connection.FireInfoMessageEventOnUserErrors = true;
        await using var command = new SqlCommand(sql, connection);
        var completed = new List<int>();
        command.StatementCompleted += (_, e) => completed.Add(e.RecordCount);
        var shape = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                if (reader.FieldCount > 0)
                    shape.Add(reader.GetName(0));
                while (await reader.ReadAsync(TestContext.CancellationToken))
                {
                }
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        }
        return (string.Join(",", completed), string.Join(",", shape));
    }

    [TestMethod]
    [DataRow("begin try insert t values (1); end try begin catch select 'c' c; end catch", "0,1")]
    [DataRow("begin try insert t values (2); insert t values (1); end try begin catch select 'c' c; end catch", "1,0,1")]
    [DataRow("begin try insert t values ('x'); end try begin catch select 'c' c; end catch", "0,1")]
    [DataRow("begin try exec p; end try begin catch select 'c' c; end catch", "0,1")]
    [DataRow("begin try exec pb; end try begin catch select 'c' c; end catch", "0,1")]
    [DataRow("begin try exec pi; end try begin catch select 'c' c; end catch", "0,1")]
    [DataRow("begin try exec pw; end try begin catch select 'c' c; end catch", "0,1")]
    [DataRow("begin try insert u values (1); end try begin catch select 'c' c; end catch", "0,0,1")]
    [DataRow("set nocount on; begin try insert t values (1); end try begin catch select 'c' c; end catch", "")]
    [DataRow("insert t values (1); select 'c' c", "1")]
    public async Task CaughtWrite_ReportsAZeroCount(string sql, string completed)
    {
        var (counts, _) = await RunAsync(
            """
            create table t (id int primary key); insert t values (1);
            create table n (id int not null); insert n values (1);
            create table u (id int);
            exec('create trigger tr on u after insert as update n set id = null');
            exec('create proc p as insert t values (1)');
            exec('create proc pb as begin insert t values (1); end');
            exec('create proc pi as if 1 = 1 insert t values (1)');
            exec('create proc pw as while 1 = 1 begin insert t values (1); end');
            """,
            sql);
        AreEqual(completed, counts);
    }

    [TestMethod]
    public async Task CaughtWriteWithOutput_ReportsAnEmptyResultSet()
    {
        var (counts, shape) = await RunAsync(
            "create table t (id int primary key); insert t values (1)",
            "begin try insert t output inserted.id values (1); end try begin catch select 'c' c; end catch");
        AreEqual("0,1", counts);
        AreEqual("id,c", shape);
    }
}
