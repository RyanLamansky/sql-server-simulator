using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Transactions over the wire, both ways SqlClient expresses them: control
/// statements in the batch text, and the <c>SqlTransaction</c> object model
/// (TDS transaction-manager requests).
/// </summary>
[TestClass]
public sealed class TransactionsViaSqlTextTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task BeginInsertRollback_InBatchText_LeavesNoRows()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var batch = new SqlCommand("begin tran; insert t values (1); insert t values (2); rollback", connection))
            _ = await batch.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var count = new SqlCommand("select count(*) from t", connection);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task BeginTransactionApi_CommitPersistsRows()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var transaction = connection.BeginTransaction();
        await using (var insert = new SqlCommand("insert t values (1), (2)", connection, transaction))
            AreEqual(2, await insert.ExecuteNonQueryAsync(TestContext.CancellationToken));

        transaction.Commit();

        await using var count = new SqlCommand("select count(*) from t", connection);
        AreEqual(2, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task BeginTransactionApi_RollbackDiscardsRows()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var transaction = connection.BeginTransaction();
        await using (var insert = new SqlCommand("insert t values (1)", connection, transaction))
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);

        transaction.Rollback();

        await using var count = new SqlCommand("select count(*) from t", connection);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task BeginTransactionApi_SaveAndRollbackToSavepoint_KeepsEarlierWork()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var transaction = connection.BeginTransaction();
        await using (var first = new SqlCommand("insert t values (1)", connection, transaction))
            _ = await first.ExecuteNonQueryAsync(TestContext.CancellationToken);

        transaction.Save("mark");
        await using (var second = new SqlCommand("insert t values (2)", connection, transaction))
            _ = await second.ExecuteNonQueryAsync(TestContext.CancellationToken);

        transaction.Rollback("mark");
        transaction.Commit();

        await using var values = new SqlCommand("select count(*), min(id) from t", connection);
        await using var reader = await values.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task BeginTransactionApi_SerializableIsolation_RoundTrips()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int)");

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        await using (var insert = new SqlCommand("insert t values (1)", connection, transaction))
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);

        transaction.Commit();

        await using var count = new SqlCommand("select count(*) from t", connection);
        AreEqual(1, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
