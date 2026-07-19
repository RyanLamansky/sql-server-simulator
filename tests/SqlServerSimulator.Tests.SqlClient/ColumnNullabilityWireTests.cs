using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Result-set nullability metadata (the COLMETADATA fNullable flag) as a real
/// SqlClient reader observes it via <c>GetColumnSchema()</c>. Single-source
/// SELECT projections carry per-column nullability inferred at parse — direct
/// refs preserve the base column's declaration, expressions claim nullable —
/// while joined shapes fall back to claiming every column nullable.
/// Load-bearing for DacFx bacpac export: its BCP data files drop the
/// per-value length prefix on fixed-width columns whose wire metadata says
/// NOT NULL, and the bacpac loader decodes per the model.xml declaration, so
/// the two must agree for an exported bacpac to re-import.
/// </summary>
[TestClass]
public sealed class ColumnNullabilityWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task SingleTableSelect_DirectRefsCarryDeclaredNullability()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (id int not null primary key, v nvarchar(40) null, d date not null);
            insert t values (1, N'a', '2026-01-01')
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select id, v, d, id + 1, isnull(v, N'x') from t", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var columns = reader.GetColumnSchema();
        IsFalse(columns[0].AllowDBNull);
        IsTrue(columns[1].AllowDBNull);
        IsFalse(columns[2].AllowDBNull);
        // Integer arithmetic claims nullable (overflow), ISNULL with a
        // non-null fallback claims NOT NULL — SQL Server's documented
        // inference, shared with SELECT INTO's destination schema.
        IsTrue(columns[3].AllowDBNull);
        IsFalse(columns[4].AllowDBNull);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public async Task JoinedSelect_FallsBackToAllNullable()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table a (id int not null primary key);
            create table b (id int not null primary key, aid int not null);
            insert a values (1); insert b values (10, 1)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            "select a.id, b.id from a join b on b.aid = a.id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        // Joined shapes don't model outer-join NULL-filling in the
        // inference, so every column claims nullable — the safe over-claim.
        var columns = reader.GetColumnSchema();
        IsTrue(columns[0].AllowDBNull);
        IsTrue(columns[1].AllowDBNull);
    }
}
