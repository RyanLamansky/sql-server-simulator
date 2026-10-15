using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SqlClient's parameterized command reaches the plan cache as real records
/// it: an <c>sp_executesql</c> call, or after <c>Prepare</c> an
/// <c>sp_prepexec</c> and <c>sp_execute</c>, each a prepared plan whose text
/// is the declaration SqlClient sent in parentheses followed by the statement
/// (probed 2026-10-15 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class PlanCacheDmvWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ParameterizedCommand_OverWire_IsOnePreparedPlan(bool prepared)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (a int primary key, b varchar(10)); insert t values (1, 'x'), (2, 'y')");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using (var command = new SqlCommand("select b from t where a = @a", connection))
        {
            _ = command.Parameters.Add("@a", System.Data.SqlDbType.Int);
            if (prepared)
                command.Prepare();
            for (var value = 1; value <= 3; value++)
            {
                command.Parameters[0].Value = value;
                _ = await command.ExecuteScalarAsync(TestContext.CancellationToken);
            }
        }
        await using var check = new SqlCommand("""
            select cp.objtype, cp.usecounts, qs.execution_count, qs.statement_start_offset
            from sys.dm_exec_cached_plans cp
            cross apply sys.dm_exec_sql_text(cp.plan_handle) st
            join sys.dm_exec_query_stats qs on qs.plan_handle = cp.plan_handle
            where st.text = '(@a int)select b from t where a = @a'
            """, connection);
        await using var reader = await check.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("Prepared", reader.GetString(0));
        AreEqual(3, reader.GetInt32(1));
        AreEqual(3L, reader.GetInt64(2));
        AreEqual(16, reader.GetInt32(3));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }
}
