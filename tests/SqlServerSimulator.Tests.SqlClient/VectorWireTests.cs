using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>vector</c> columns over the wire. SqlClient 7 asks for the vector
/// feature extension at login and the endpoint acknowledges it, as SQL Server
/// 2025 does, so a float32 vector arrives as the native type (<c>0xF5</c>) and
/// reads as <c>SqlVector&lt;float&gt;</c>; a float16 one still travels as its
/// <c>varchar(max)</c> text (captured 2026-10-02 through SqlClient 7.0.2).
/// </summary>
[TestClass]
public sealed class VectorWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task VectorColumn_ReadsAsNativeVector()
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

        AreEqual("vector", reader.GetDataTypeName(0));
        AreEqual(typeof(Microsoft.Data.SqlTypes.SqlVector<float>), reader.GetFieldType(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        CollectionAssert.AreEqual(new float[] { 1f, 2f, 3f }, reader.GetFieldValue<Microsoft.Data.SqlTypes.SqlVector<float>>(0).Memory.ToArray());
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
    }

    /// <summary>A vector output parameter comes back as the native type too.</summary>
    [TestMethod]
    public async Task VectorOutputParameter_ReadsAsNativeVector()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("set @v = cast('[4, 5]' as vector(2))", connection);
        var output = new SqlParameter("@v", Microsoft.Data.SqlDbTypeExtensions.Vector) { Direction = System.Data.ParameterDirection.Output, Value = Microsoft.Data.SqlTypes.SqlVector<float>.CreateNull(2) };
        _ = command.Parameters.Add(output);
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        CollectionAssert.AreEqual(new float[] { 4f, 5f }, ((Microsoft.Data.SqlTypes.SqlVector<float>)output.Value).Memory.ToArray());
    }

    /// <summary>A float16 vector travels as its text even to a vector-aware client.</summary>
    [TestMethod]
    public async Task Float16VectorColumn_ReadsAsVarcharMaxText()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "alter database scoped configuration set preview_features = on");
        Wire.ExecInProc(simulation, "create table t (v vector(2, float16)); insert t values ('[1, 2]')");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        AreEqual("varchar", reader.GetDataTypeName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(typeof(string), reader.GetValue(0).GetType());
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
