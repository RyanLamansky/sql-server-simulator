using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A SELECT its own run-time error cuts short closes with DONE_ERROR and no
/// count — even for the rows it sent first — so SqlClient raises no
/// <c>StatementCompleted</c> for it, as against SQL Server 2025 (captured
/// 2026-09-26); a statement that completed keeps its count.
/// </summary>
[TestClass]
public sealed class CutShortStatementDoneTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("select 10 / x from (values (1), (0)) v(x)", "")]
    [DataRow("select 2147483647 + 1", "")]
    [DataRow("select 1; select 1 / 0", "1")]
    public async Task CutShortSelect_RaisesNoStatementCompleted(string sql, string completedCounts)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var completed = new List<int>();
        command.StatementCompleted += (_, e) => completed.Add(e.RecordCount);

        _ = await ThrowsAsync<SqlException>(async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                {
                }
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        });

        AreEqual(completedCounts, string.Join(",", completed));
    }

    /// <summary>
    /// A cut-short SELECT whose error a TRY frame caught reports a count of 0
    /// and no error of its own; a THROW in the CATCH then raises the one error
    /// the client sees (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("begin try select 10 / x from (values (1), (0), (2)) v(x); end try begin catch throw; end catch;", "0", 1)]
    [DataRow("begin try select 10 / x from (values (1), (0), (2)) v(x); end try begin catch throw; end catch", "0", 1)]
    [DataRow("begin try select 1 / 0; end try begin catch throw 50001, 'm', 1; end catch;", "0", 0)]
    public async Task CaughtCutShortSelect_CountsZero_AndTheThrowIsTheOneError(string sql, string completedCounts, int rowsRead)
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var completed = new List<int>();
        command.StatementCompleted += (_, e) => completed.Add(e.RecordCount);

        var read = 0;
        var error = await ThrowsAsync<SqlException>(async () =>
        {
            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                    read++;
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        });

        HasCount(1, error.Errors);
        AreEqual(completedCounts, string.Join(",", completed));
        AreEqual(rowsRead, read);
    }
}
