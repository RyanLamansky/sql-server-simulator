using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Transaction control expressed in the batch text works over the wire; the
/// SqlClient <c>BeginTransaction</c> API (a TDS transaction-manager request)
/// is not yet handled and surfaces as an error — documented here as the
/// current contract.
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
    public async Task BeginTransactionApi_Throws50000_TransactionManagerUnsupported()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var ex = Assert.Throws<SqlException>(() => _ = connection.BeginTransaction());
        AreEqual(50000, ex.Number);
        Contains("transaction-manager", ex.Message);
    }
}
