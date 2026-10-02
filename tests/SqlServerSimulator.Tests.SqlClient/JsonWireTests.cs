using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>json</c> columns over the wire. SqlClient 7 asks for the json feature
/// extension at login and the endpoint acknowledges it, as SQL Server 2025
/// does, so a json value arrives as the native type (<c>0xF4</c>, UTF-8 PLP)
/// and reads as a string typed <c>json</c> (captured 2026-10-02 through
/// SqlClient 7.0.2).
/// </summary>
[TestClass]
public sealed class JsonWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task JsonColumn_ReadsAsNativeJson()
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

        AreEqual("json", reader.GetDataTypeName(0));
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

    /// <summary>
    /// A <c>SqlDbType.Json</c> parameter — type token <c>0xF4</c>, which a
    /// json-aware SqlClient sends whether or not the server acknowledged the
    /// json feature — binds as <c>json</c>: a <c>.modify()</c> fed it inserts
    /// JSON, not a string, as EF Core's bulk update of a JSON column needs.
    /// </summary>
    [TestMethod]
    public async Task JsonParameter_BindsAsJson()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (j json); insert t values ('{\"a\":1}')");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var update = new SqlCommand("update t set j.modify('$.b', @p)", connection))
        {
            _ = update.Parameters.Add(new SqlParameter("@p", System.Data.SqlDbType.Json) { Value = "[1, 2]" });
            _ = await update.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }
        await using (var nulls = new SqlCommand("select iif(@p is null, 'null', 'value')", connection))
        {
            _ = nulls.Parameters.Add(new SqlParameter("@p", System.Data.SqlDbType.Json) { Value = DBNull.Value });
            AreEqual("null", await nulls.ExecuteScalarAsync(TestContext.CancellationToken));
        }
        await using var select = new SqlCommand("select cast(j as nvarchar(max)) from t", connection);
        AreEqual("{\"a\":1,\"b\":[1,2]}", await select.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
