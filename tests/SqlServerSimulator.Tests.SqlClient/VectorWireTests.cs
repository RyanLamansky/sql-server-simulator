using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>vector</c> columns over the wire. The endpoint acknowledges no vector
/// feature extension, so it sends a vector the way SQL Server 2025 sends one
/// to a client without vector support — <c>varchar(max)</c> holding the text
/// form (probed 2026-09-26 through SqlClient 5.1) — and SqlClient reads a
/// string whatever its own version.
/// </summary>
[TestClass]
public sealed class VectorWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task VectorColumn_ReadsAsVarcharMaxText()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (id int, v vector(3));
            insert t values (1, '[1, 2, 3]'), (2, null)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from t order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("varchar", reader.GetDataTypeName(0));
        AreEqual(typeof(string), reader.GetFieldType(0));
        AreEqual(int.MaxValue, reader.GetColumnSchema()[0].ColumnSize);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("[1.0000000e+000,2.0000000e+000,3.0000000e+000]", reader.GetString(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TextParameter_WritesAVector()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (v vector(2))");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var insert = new SqlCommand("insert t values (@p)", connection))
        {
            _ = insert.Parameters.AddWithValue("@p", "[5, 6]");
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }
        await using var select = new SqlCommand("select vector_norm(v, 'norm1') from t", connection);
        AreEqual(11.0, await select.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// A <c>SqlVector&lt;float&gt;</c> parameter — type token <c>0xF5</c>, a
    /// 2-byte maximum length and the element-type byte, then a 2-byte length
    /// and the vector's binary form — binds as <c>vector(n)</c>.
    /// </summary>
    [TestMethod]
    public async Task VectorParameter_BindsAsVector()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select cast(@v as nvarchar(max)), vector_norm(@v, 'norm2'), iif(@n is null, 'null', 'value')", connection);
        _ = command.Parameters.Add(new SqlParameter("@v", Microsoft.Data.SqlDbTypeExtensions.Vector) { Value = new Microsoft.Data.SqlTypes.SqlVector<float>(new float[] { 1f, 2f, 3f }) });
        _ = command.Parameters.Add(new SqlParameter("@n", Microsoft.Data.SqlDbTypeExtensions.Vector) { Value = Microsoft.Data.SqlTypes.SqlVector<float>.CreateNull(3) });
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("[1.0000000e+000,2.0000000e+000,3.0000000e+000]", reader.GetString(0));
        AreEqual(Math.Sqrt(14), reader.GetDouble(1), 1e-12);
        AreEqual("null", reader.GetString(2));
    }
}
