using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SqlClient reads past the ORDER token an ordered result carries after its
/// COLMETADATA — and after the browse tokens under <c>KeyInfo</c> — as it
/// does against SQL Server 2025, which sends one for every ORDER BY.
/// </summary>
[TestClass]
public sealed class OrderTokenWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int primary key, name varchar(10)); insert t values (1, 'b'), (2, 'a')");
        Wire.ExecInProc(simulation, "create procedure p as select name, id from t order by name");
        return simulation;
    }

    [TestMethod]
    public async Task OrderedResults_ReadWhole()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select id, name from t order by name, 3 - id; exec p; exec sp_columns t; select 1", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        var counts = new List<int>();
        do
        {
            var rows = 0;
            while (await reader.ReadAsync(TestContext.CancellationToken))
                rows++;
            counts.Add(rows);
        }
        while (await reader.NextResultAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(new[] { 2, 2, 2, 1 }, counts);
    }

    [TestMethod]
    public async Task OrderedBrowseResult_DescribesItsColumns()
    {
        var simulation = Seeded();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select name from t order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, TestContext.CancellationToken);
        var schema = (await reader.GetSchemaTableAsync(TestContext.CancellationToken))!;
        AreEqual("t", schema.Rows[0]["BaseTableName"]);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("b", reader.GetString(0));
    }
}
