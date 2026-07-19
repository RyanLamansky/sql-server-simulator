using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Loopback-wire oracle for the SSMS server-level Policy Health connect path:
/// real SqlClient reads <c>has_dbaccess('msdb')</c> = 1 and then the empty
/// <c>msdb.dbo.syspolicy_system_health_state</c> view. This is the exact
/// sequence SSMS issues at connect; before the msdb seed it popped a
/// permission error. The wire oracle catches TDS-level regressions the
/// in-process tests can't.
/// </summary>
[TestClass]
public sealed class SystemDatabaseTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task HasDbAccessMsdb_OverWire_ReturnsOne()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("select has_dbaccess('msdb')", connection);
        AreEqual(1, await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task SyspolicyHealthState_OverWire_ReturnsSixColumnsNoRows()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand(
            "select * from msdb.dbo.syspolicy_system_health_state", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual(6, reader.FieldCount);
        AreEqual("health_state_id", reader.GetName(0));
        AreEqual("policy_id", reader.GetName(1));
        AreEqual("last_run_date", reader.GetName(2));
        AreEqual("target_query_expression_with_id", reader.GetName(3));
        AreEqual("target_query_expression", reader.GetName(4));
        AreEqual("result", reader.GetName(5));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }
}
