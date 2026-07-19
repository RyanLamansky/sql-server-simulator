using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET TEXTSIZE</c> over the TDS endpoint with real SqlClient: the session
/// byte cap clips MAX-typed result columns and output parameters at wire
/// egress (probe-confirmed against SQL Server 2025, 2026-07-19), while xml
/// and server-side state stay untouched.
/// </summary>
[TestClass]
public sealed class TextSizeWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Default_ReadsMinusOne()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        AreEqual(-1, await new SqlCommand("select @@TEXTSIZE", connection).ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MaxColumns_TruncateAtByteCap()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        _ = await new SqlCommand("set textsize 10", connection).ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var reader = await new SqlCommand("""
            select replicate(cast('x' as varchar(max)), 100),
                   replicate(cast(N'x' as nvarchar(max)), 100),
                   cast('<r><a>hello</a><b>world</b></r>' as xml)
            """, connection).ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(new string('x', 10), reader.GetString(0));
        AreEqual(new string('x', 5), reader.GetString(1));
        AreEqual("<r><a>hello</a><b>world</b></r>", reader.GetString(2));
    }

    [TestMethod]
    public async Task OutputParameter_TruncatesAtByteCap()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create proc dbo.get_doc @o varchar(max) output as set @o = replicate(cast('x' as varchar(max)), 100)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        _ = await new SqlCommand("set textsize 10", connection).ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.get_doc", connection) { CommandType = CommandType.StoredProcedure };
        var output = command.Parameters.Add(new SqlParameter("@o", SqlDbType.VarChar, -1) { Direction = ParameterDirection.Output });
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual(new string('x', 10), output.Value);
    }
}
