using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A SELECT statement's own FOR JSON / FOR XML document over the wire: the
/// rows SqlClient reads are 2033-unit chunks, the untyped FOR XML column is
/// <c>ntext</c>, and the DONE count <c>StatementCompleted</c> reports is the
/// rows the clause serialized, as against SQL Server 2025 (probed 2026-09-26).
/// </summary>
[TestClass]
public sealed class ForClauseStreamWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("select a from t for json path", "nvarchar", 4, 500)]
    [DataRow("select a from t for xml auto", "ntext", 4, 500)]
    [DataRow("select a from t for xml raw, type", "xml", 1, 1)]
    [DataRow("select (select a from t for json path) j", "nvarchar", 1, 1)]
    public async Task StreamsTheDocumentAndCountsTheSerializedRows(string sql, string typeName, int rows, int completedCount)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (a varchar(10));
            insert t select 'xxxxxxx' from (values (0), (1), (2), (3), (4)) f (n)
                cross join (values (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) g (n) cross join (values (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) h (n)
            """);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var completed = new List<int>();
        command.StatementCompleted += (_, e) => completed.Add(e.RecordCount);

        await using (var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            AreEqual(typeName, reader.GetDataTypeName(0));
            var read = 0;
            while (await reader.ReadAsync(TestContext.CancellationToken))
                read++;
            AreEqual(rows, read);
        }
        AreEqual(completedCount, completed.Single());
    }
}
