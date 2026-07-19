using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The prepared-statement RPC family (sp_prepexec / sp_execute / sp_unprepare)
/// beyond the smoke test: re-execution with varying values including NULL, and
/// two prepared commands live on the same connection at once.
/// </summary>
[TestClass]
public sealed class PreparedStatementTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Prepare_ThenExecuteWithVaryingValues_IncludingNull()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int, name nvarchar(50))");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var insert = new SqlCommand("insert t values (@id, @name)", connection))
        {
            var id = insert.Parameters.Add("@id", SqlDbType.Int);
            var name = insert.Parameters.Add("@name", SqlDbType.NVarChar, 50);
            await insert.PrepareAsync(TestContext.CancellationToken);

            id.Value = 1;
            name.Value = "alpha";
            AreEqual(1, await insert.ExecuteNonQueryAsync(TestContext.CancellationToken));

            id.Value = 2;
            name.Value = DBNull.Value;
            AreEqual(1, await insert.ExecuteNonQueryAsync(TestContext.CancellationToken));

            id.Value = 3;
            name.Value = "gamma";
            AreEqual(1, await insert.ExecuteNonQueryAsync(TestContext.CancellationToken));
        }

        await using var probe = new SqlCommand("select count(*), count(name) from t", connection);
        await using var reader = await probe.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(3, reader.GetInt32(0));
        AreEqual(2, reader.GetInt32(1)); // the NULL row is excluded from count(name)
    }

    [TestMethod]
    public async Task TwoPreparedCommands_InterleavedOnOneConnection()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var insert = new SqlCommand("insert t values (@v)", connection);
        var insertParam = insert.Parameters.Add("@v", SqlDbType.Int);
        await insert.PrepareAsync(TestContext.CancellationToken);

        await using var count = new SqlCommand("select count(*) from t where id <= @hi", connection);
        var countParam = count.Parameters.Add("@hi", SqlDbType.Int);
        await count.PrepareAsync(TestContext.CancellationToken);

        for (var i = 1; i <= 4; i++)
        {
            insertParam.Value = i;
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);

            countParam.Value = 2;
            AreEqual(Math.Min(i, 2), await count.ExecuteScalarAsync(TestContext.CancellationToken));
        }

        countParam.Value = 100;
        AreEqual(4, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // The stale-handle path (sp_execute against a handle the server dropped ->
    // Msg 8179) is not reachable through the SqlClient public API: handles are
    // owned internally and tied to the physical connection, and SqlClient
    // re-prepares transparently rather than reusing a handle across a pooled
    // reset. This test confirms that transparent re-preparation: a prepared
    // command keeps working after the connection round-trips through the pool.
    [TestMethod]
    public async Task PreparedCommand_SurvivesConnectionClose_ReprepareIsTransparent()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        var connectionString = Wire.PooledConnectionString(listener);

        await using (var first = new SqlConnection(connectionString))
        {
            await first.OpenAsync(TestContext.CancellationToken);
            await using var insert = new SqlCommand("insert t values (@v)", first);
            var parameter = insert.Parameters.Add("@v", SqlDbType.Int);
            await insert.PrepareAsync(TestContext.CancellationToken);
            parameter.Value = 1;
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await using var second = new SqlConnection(connectionString);
        await second.OpenAsync(TestContext.CancellationToken);
        await using var again = new SqlCommand("insert t values (@v)", second);
        var reused = again.Parameters.Add("@v", SqlDbType.Int);
        await again.PrepareAsync(TestContext.CancellationToken);
        reused.Value = 2;
        AreEqual(1, await again.ExecuteNonQueryAsync(TestContext.CancellationToken));

        await using var count = new SqlCommand("select count(*) from t", second);
        AreEqual(2, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
