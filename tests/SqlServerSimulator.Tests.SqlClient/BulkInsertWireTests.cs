using System.Text;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>BULK INSERT</c> over the wire: each full batch closes with a DONE of its
/// own, and a row error reaches the client as an error token inside the
/// statement while the load and the batch carry on.
/// </summary>
[TestClass]
public sealed class BulkInsertWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation WithFile(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new Simulation { OpenBulkFile = path => path == "f.txt" ? new MemoryStream(bytes) : null };
    }

    [TestMethod]
    public async Task BatchSize_EachFullBatchCompletesAStatement()
    {
        var simulation = WithFile("1|a\n2|b\n3|c\n4|d\n5|e\n");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("create table t (id int, name varchar(9)); bulk insert t from 'f.txt' with (fieldterminator = '|', batchsize = 2)", connection);
        var counts = new List<int>();
        command.StatementCompleted += (_, e) => counts.Add(e.RecordCount);
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { 2, 2, 5 }, counts);
    }

    [TestMethod]
    public async Task RowError_InAParameterizedBatch_LoadAndBatchCarryOn()
    {
        var simulation = WithFile("x,a\n2,b\n");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var create = new SqlCommand("create table t (id int, name varchar(9)); create table log (n int)", connection))
            _ = await create.ExecuteNonQueryAsync(TestContext.CancellationToken);
        var ex = await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await using var command = new SqlCommand("bulk insert t from 'f.txt' with (fieldterminator = ','); insert log values (@n)", connection);
            _ = command.Parameters.AddWithValue("@n", 7);
            _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        });
        AreEqual(4864, ex.Number);
        await using var check = new SqlCommand("select concat((select count(*) from t), ':', (select n from log))", connection);
        AreEqual("1:7", await check.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
