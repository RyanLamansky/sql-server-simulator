using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Most system procedures run under <c>SET NOCOUNT ON</c>, so their result
/// sets close without a count and SqlClient raises no
/// <c>StatementCompleted</c>; the catalog-procedure family keeps its counts.
/// Probed procedure by procedure 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class SystemProcedureCountWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("exec sp_help 't'", "")]
    [DataRow("exec sp_helptext 'p'", "")]
    [DataRow("exec sp_helpindex 't'", "")]
    [DataRow("exec sp_spaceused 't'", "")]
    [DataRow("exec sp_configure 'show advanced options'", "")]
    [DataRow("exec sp_databases", "")]
    [DataRow("exec sp_tables 't'", "1")]
    [DataRow("exec sp_columns 't'", "2")]
    [DataRow("exec sp_pkeys 't'", "1")]
    public async Task ResultSets_CountAsRealsDo(string sql, string completedCounts)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int primary key, a int)");
        Wire.ExecInProc(simulation, "create procedure p as select 1");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var completed = new List<int>();
        command.StatementCompleted += (_, e) => completed.Add(e.RecordCount);
        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                {
                }
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
        }
        AreEqual(completedCounts, string.Join(",", completed));
    }
}
