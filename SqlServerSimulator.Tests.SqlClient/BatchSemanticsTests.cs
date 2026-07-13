using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Multi-result batches, summed rows-affected, empty result sets, and the
/// session id — all as the real client observes them over the wire.
/// </summary>
[TestClass]
public sealed class BatchSemanticsTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MultiStatementBatch_NextResult_WalksResultSets()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select 1; select 2, 3", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual(1, reader.FieldCount);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1, reader.GetInt32(0));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));

        IsTrue(await reader.NextResultAsync(TestContext.CancellationToken));
        AreEqual(2, reader.FieldCount);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(2, reader.GetInt32(0));
        AreEqual(3, reader.GetInt32(1));

        IsFalse(await reader.NextResultAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteNonQuery_MultiDml_ReturnsSummedRowsAffected()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("insert t values (1), (2); insert t values (3)", connection);
        AreEqual(3, await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EmptyResultSet_HasRowsFalse_FieldCountCorrect()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (a int, b int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select a, b from t where 1 = 0", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual(2, reader.FieldCount);
        IsFalse(reader.HasRows);
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Spid_IsAtLeast51_AndMatchesInProcessType()
    {
        var simulation = new Simulation();
        var oracle = Wire.ReadAllInProc(simulation, "select @@spid");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @@spid", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        var wireSpid = reader.GetValue(0);
        // @@SPID is a distinct session per surface, so the numbers differ; the
        // CLR shape (smallint -> short) and the >= 51 floor must still match.
        AreEqual(oracle[0][0]!.GetType(), wireSpid.GetType());
        IsGreaterThanOrEqualTo(51, Convert.ToInt32(wireSpid));
    }
}
