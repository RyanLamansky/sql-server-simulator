using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>DBCC SHOW_STATISTICS … WITH HISTOGRAM</c> result set reaches a real
/// SqlClient reader over loopback TCP+TLS — the shape DacFx's bacpac export
/// reads. The load-bearing case is the dynamically-typed <c>RANGE_HI_KEY</c>
/// column (the statistic's leading key column type), which must flow through the
/// standard TDS codecs; here an <c>int</c> PK and an <c>nvarchar</c> PK are read
/// back so both a fixed-length and a variable-length dynamic key are exercised.
/// </summary>
[TestClass]
public sealed class DbccShowStatisticsWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task IntLeadingKey_HistogramRoundTripsOverWire()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (id int not null, constraint pk_t primary key (id));
            insert t values (1), (2), (3), (4), (5)
            """);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbcc show_statistics(N't', N'pk_t') with histogram", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual(5, reader.FieldCount);
        AreEqual("RANGE_HI_KEY", reader.GetName(0));
        AreEqual("int", reader.GetDataTypeName(0));
        AreEqual("real", reader.GetDataTypeName(1));
        AreEqual("bigint", reader.GetDataTypeName(3));

        // One step per distinct value: MIN (1) first through MAX (5) last.
        for (var expected = 1; expected <= 5; expected++)
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            AreEqual(expected, reader.GetInt32(0));
            AreEqual(0f, reader.GetFloat(1));   // RANGE_ROWS
            AreEqual(1f, reader.GetFloat(2));   // EQ_ROWS
            AreEqual(0L, reader.GetInt64(3));   // DISTINCT_RANGE_ROWS
            AreEqual(1f, reader.GetFloat(4));   // AVG_RANGE_ROWS
        }
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task StringLeadingKey_RangeHiKeyReachesClientAsNVarchar()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table s (code nvarchar(10) not null, constraint pk_s primary key (code));
            insert s values (N'alpha'), (N'bravo'), (N'charlie')
            """);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbcc show_statistics(N's', N'pk_s') with histogram", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("nvarchar", reader.GetDataTypeName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("alpha", reader.GetString(0));     // MIN step first, collation order
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("bravo", reader.GetString(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("charlie", reader.GetString(0));   // MAX step last
        AreEqual(0f, reader.GetFloat(1));
        AreEqual(1f, reader.GetFloat(2));
        AreEqual(0L, reader.GetInt64(3));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }
}
