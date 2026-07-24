using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET NOCOUNT ON</c> wire behavior: a statement's rows-affected count is
/// suppressed, so the TDS DONE token omits DONE_COUNT. Observable two ways over
/// real SqlClient — <c>ExecuteNonQuery()</c> returns -1 (no count to sum), and
/// the ubiquitous <c>SET NOCOUNT ON; INSERT …; SELECT SCOPE_IDENTITY()</c>
/// identity-retrieval batch (mssql-django and every ODBC/pyodbc data layer)
/// lands its trailing SELECT as the first result instead of stalling on the
/// INSERT's rowcount. Probe-confirmed against SQL Server 2025; surfaced by the
/// Django test suite over pyodbc, where the missing suppression made ODBC read
/// the INSERT's rowcount and never reach the SELECT.
/// </summary>
[TestClass]
public sealed class NoCountWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NoCountOn_ExecuteNonQuery_ReturnsMinusOne()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int identity primary key, v int)");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // Baseline: without NOCOUNT the INSERT reports its one-row count.
        await using var counted = new SqlCommand("insert into t (v) values (10)", connection);
        AreEqual(1, await counted.ExecuteNonQueryAsync(TestContext.CancellationToken));

        // NOCOUNT ON suppresses DONE_COUNT, so SqlClient has no count to sum
        // and ExecuteNonQuery returns -1 (matching real SQL Server).
        await using var silent = new SqlCommand("set nocount on insert into t (v) values (20)", connection);
        AreEqual(-1, await silent.ExecuteNonQueryAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task NoCountOn_InsertThenScopeIdentity_ScalarReadsIdentity()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int identity primary key, v int)");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // The mssql-django single-AutoField insert shape: without the count
        // suppressed, a driver stalls on the INSERT's rowcount result; with it,
        // the SELECT SCOPE_IDENTITY() is the first (and only) result set.
        await using var command = new SqlCommand(
            "set nocount on insert into t (v) values (99); select cast(scope_identity() as bigint)",
            connection);
        AreEqual(1L, await command.ExecuteScalarAsync(TestContext.CancellationToken));

        await using var second = new SqlCommand(
            "set nocount on insert into t (v) values (98); select cast(scope_identity() as bigint)",
            connection);
        AreEqual(2L, await second.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
