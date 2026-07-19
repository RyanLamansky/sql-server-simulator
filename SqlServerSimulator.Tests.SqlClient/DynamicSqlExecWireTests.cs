using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end wire coverage for <c>EXEC('…')</c> dynamic SQL inside a SQLBatch.
/// Real SQL Server runs the dynamic body as a nested procedure scope: its
/// statements report DONEINPROC (0xFF) and the scope closes with RETURNSTATUS +
/// DONEPROC, where the simulator previously emitted a plain batch DONE (0xFD).
/// The SSMS report viewer's environment-probe batch (whose final result set
/// comes from an <c>EXEC('…')</c>) froze .NET Framework's stricter native TDS
/// parser on the old shape; these tests drive the same shape over real SqlClient
/// so the corrected token stream stays well-formed. Real-server token shape
/// captured cleartext 2026-07-19.
/// </summary>
[TestClass]
public sealed class DynamicSqlExecWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ExecInBatch_AfterBatchLevelSelects_AllResultSetsRead()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        // Batch-level SELECTs (plain DONE) followed by an EXEC('…') result set
        // (DONEINPROC + RETURNSTATUS + DONEPROC) — the report-viewer shape.
        const string batch = "select 1 as a; select 2 as b; exec('select 3 as c')";
        await using var command = new SqlCommand(batch, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var seen = new List<(string Column, int Value)>();
        do
        {
            while (await reader.ReadAsync(TestContext.CancellationToken))
                seen.Add((reader.GetName(0), reader.GetInt32(0)));
        }
        while (await reader.NextResultAsync(TestContext.CancellationToken));

        CollectionAssert.AreEqual(
            new[] { ("a", 1), ("b", 2), ("c", 3) },
            seen);
    }

    [TestMethod]
    public async Task ReportViewerEnvironmentProbe_ThreeResultSets()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var lockTimeout = new SqlCommand("SET LOCK_TIMEOUT 10000", connection))
            _ = await lockTimeout.ExecuteNonQueryAsync(TestContext.CancellationToken);

        const string probe = """
            DECLARE @edition sysname;
            SET @edition = cast(SERVERPROPERTY(N'EDITION') as sysname);
            SELECT case when @edition = N'SQL Azure' then 2 else 1 end as 'DatabaseEngineType', SERVERPROPERTY('EngineEdition') AS DatabaseEngineEdition, SERVERPROPERTY('ProductVersion') AS ProductVersion, @@MICROSOFTVERSION AS MicrosoftVersion, 0 as IsFabricServer, convert(sysname, SERVERPROPERTY(N'Collation')) AS Collation;
            select host_platform from sys.dm_os_host_info
            if @edition = N'SQL Azure'
              select 'TCP' as ConnectionProtocol
            else
              exec ('select CONVERT(nvarchar(40),CONNECTIONPROPERTY(''net_transport'')) as ConnectionProtocol')
            """;

        await using var command = new SqlCommand(probe, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var resultSets = 0;
        var lastColumn = "";
        do
        {
            var rows = 0;
            while (await reader.ReadAsync(TestContext.CancellationToken))
                rows++;
            AreEqual(1, rows);
            lastColumn = reader.GetName(reader.FieldCount - 1);
            resultSets++;
        }
        while (await reader.NextResultAsync(TestContext.CancellationToken));

        AreEqual(3, resultSets);
        AreEqual("ConnectionProtocol", lastColumn);
    }
}
