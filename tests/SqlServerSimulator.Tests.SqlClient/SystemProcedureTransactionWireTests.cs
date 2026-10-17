using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Real SqlClient over the transactions system procedures open themselves: a
/// legacy security procedure announces the one it wraps its work in, and one
/// a <c>TRY</c> caught its error in leaves it open — the client must carry
/// its descriptor on the requests after, as it does against SQL Server 2025
/// (probed 2026-10-17, where a client sending none is refused with Msg 3989).
/// </summary>
[TestClass]
public sealed class SystemProcedureTransactionWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TransactionALegacyProcedureLeftOpen_CarriesOnUntilRolledBack()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var caught = new SqlCommand("begin try exec sp_droprole 'nosuch' end try begin catch select @@trancount end catch", connection))
            AreEqual(1, await caught.ExecuteScalarAsync(TestContext.CancellationToken));
        await using (var create = new SqlCommand("create table t (a int); insert t values (1); select @@trancount", connection))
            AreEqual(1, await create.ExecuteScalarAsync(TestContext.CancellationToken));
        await using var rollback = new SqlCommand("rollback; select object_id('t')", connection);
        AreEqual(DBNull.Value, await rollback.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task LegacyProcedure_CommitsItsOwnTransaction()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var add = new SqlCommand("exec sp_addrole 'r'; select @@trancount", connection))
            AreEqual(0, await add.ExecuteScalarAsync(TestContext.CancellationToken));
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(TestContext.CancellationToken);
        await using var refused = new SqlCommand("exec sp_droprole 'r'", connection, transaction);
        AreEqual(15002, (await ThrowsAsync<SqlException>(() => refused.ExecuteNonQueryAsync(TestContext.CancellationToken))).Number);
        await transaction.RollbackAsync(TestContext.CancellationToken);
        await using var present = new SqlCommand("select count(*) from sys.database_principals where name = 'r'", connection);
        AreEqual(1, await present.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GetAppLock_EfMigrationsShape_AnswersZero()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("DECLARE @result int; EXEC @result = sp_getapplock @Resource = '__EFMigrationsLock', @LockOwner = 'Session', @LockMode = 'Exclusive'; SELECT @result", connection);
        var completed = new List<int>();
        command.StatementCompleted += (_, e) => completed.Add(e.RecordCount);
        AreEqual(0, await command.ExecuteScalarAsync(TestContext.CancellationToken));
        // The procedure's own statements count no rows a client sees.
        CollectionAssert.AreEqual(new[] { 1 }, completed);
    }
}
