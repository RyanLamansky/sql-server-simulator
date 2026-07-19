using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// First-light checks for the RPC path: parameterized commands arrive as
/// sp_executesql RPC requests, not batches.
/// </summary>
[TestClass]
public sealed class RpcSmokeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ParameterizedSelect_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select @a + @b", connection);
        _ = command.Parameters.AddWithValue("@a", 40);
        _ = command.Parameters.AddWithValue("@b", 2);
        AreEqual(42, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ParameterizedInsertAndReadBack_StringParameter()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int, name nvarchar(50))");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var insert = new SqlCommand("insert t values (@id, @name)", connection))
        {
            _ = insert.Parameters.AddWithValue("@id", 7);
            _ = insert.Parameters.AddWithValue("@name", "café アイウ");
            AreEqual(1, await insert.ExecuteNonQueryAsync(TestContext.CancellationToken));
        }

        await using var select = new SqlCommand("select name from t where id = @id", connection);
        _ = select.Parameters.AddWithValue("@id", 7);
        AreEqual("café アイウ", await select.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task OutputParameter_ThroughSpExecuteSql()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("set @result = @input * 3", connection);
        _ = command.Parameters.AddWithValue("@input", 14);
        var result = command.Parameters.Add("@result", System.Data.SqlDbType.Int);
        result.Direction = System.Data.ParameterDirection.Output;
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        AreEqual(42, result.Value);
    }

    [TestMethod]
    public async Task PreparedCommand_ExecutesRepeatedly()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("insert t values (@v)", connection);
        var parameter = command.Parameters.Add("@v", System.Data.SqlDbType.Int);
        await command.PrepareAsync(TestContext.CancellationToken);
        for (var i = 1; i <= 3; i++)
        {
            parameter.Value = i;
            AreEqual(1, await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        }

        await using var sum = new SqlCommand("select sum(id) from t", connection);
        AreEqual(6, await sum.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // The SSMS "Edit Top 200 Rows" commit shape: UPDATE TOP (200) with a
    // parameterized SET and a multi-predicate concurrency WHERE, arriving as
    // an sp_executesql RPC. Must update exactly the one matching row.
    [TestMethod]
    public async Task UpdateTop200_EditRowsShape_UpdatesOneRow()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table Invoices (
                InvoiceID int identity primary key,
                CustomerID int,
                InvoiceDate datetime,
                Total money);
            insert Invoices (CustomerID, InvoiceDate, Total)
            values (1, '2020-01-01', 100), (2, '2020-02-01', 200), (3, '2020-03-01', 300)
            """);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var update = new SqlCommand(
            "update top (200) Invoices set InvoiceDate = @p " +
            "where InvoiceID = @k1 and CustomerID = @k2 and Total = @k3", connection);
        _ = update.Parameters.AddWithValue("@p", new DateTime(2021, 6, 15));
        _ = update.Parameters.AddWithValue("@k1", 2);
        _ = update.Parameters.AddWithValue("@k2", 2);
        _ = update.Parameters.AddWithValue("@k3", 200m);
        AreEqual(1, await update.ExecuteNonQueryAsync(TestContext.CancellationToken));

        await using var check = new SqlCommand("select InvoiceDate from Invoices where InvoiceID = 2", connection);
        AreEqual(new DateTime(2021, 6, 15), await check.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
