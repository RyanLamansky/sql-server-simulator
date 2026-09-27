using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>json</c> columns over the wire. The endpoint acknowledges no json
/// feature extension, so it sends a json value the way SQL Server 2025's
/// documentation says it goes to a TDS 7.4 client without json support —
/// <c>varchar(max)</c> collated <c>Latin1_General_100_BIN2_UTF8</c> holding
/// the canonical text — and SqlClient reads a string.
/// </summary>
[TestClass]
public sealed class JsonWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task JsonColumn_ReadsAsVarcharMaxText()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (id int, j json);
            insert t values (1, '{ "a" : [1, 2], "b" : "é" }'), (2, null)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select j from t order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("varchar", reader.GetDataTypeName(0));
        AreEqual(typeof(string), reader.GetFieldType(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("{\"a\":[1,2],\"b\":\"é\"}", reader.GetString(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TextParameter_WritesJson()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (j json)");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var insert = new SqlCommand("insert t values (@p)", connection))
        {
            _ = insert.Parameters.AddWithValue("@p", "[ 5 , 6 ]");
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }
        await using var select = new SqlCommand("select datalength(j) from t", connection);
        AreEqual(30, await select.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
