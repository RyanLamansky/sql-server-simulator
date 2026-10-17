using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An <c>INSERT … EXEC</c> whose body's <c>SELECT</c> couldn't convert a row to
/// the target's types reaches SqlClient as real's does (probed 2026-10-17
/// against SQL Server 2025): the INSERT's DONE carries the error bit with no
/// error token, which SqlClient reports as its own class-11 Msg 0 when the
/// body's error was caught, and not at all behind the body's own error.
/// </summary>
[TestClass]
public sealed class InsertExecWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ConversionCaughtInTheBody_IsTheClientsSevereError()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table dbo.t (a int)");
        Wire.ExecInProc(simulation, "create procedure dbo.p as begin select 1; begin try select 'x' end try begin catch end catch end");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("insert dbo.t exec dbo.p; print concat('after ', @@error)", connection);
        var ex = await ThrowsAsync<SqlException>(async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        AreEqual((0, (byte)11, 0), (ex.Errors[0].Number, ex.Errors[0].Class, ex.Errors[0].LineNumber));
        StartsWith("A severe error occurred on the current command.", ex.Errors[0].Message);
        AreEqual("after 0", ex.Errors[^1].Message);
        await using var count = new SqlCommand("select count(*) from dbo.t", connection);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UncaughtTruncation_IsTheBodysErrorAlone()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table dbo.t (a int, b varchar(3))");
        Wire.ExecInProc(simulation, "create procedure dbo.p as\nbegin\n  select 1, 'abcd';\n  select 2, 'y'\nend");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("insert dbo.t exec dbo.p", connection);
        var ex = await ThrowsAsync<SqlException>(async () => await command.ExecuteNonQueryAsync(TestContext.CancellationToken));
        HasCount(1, ex.Errors);
        AreEqual((8152, (byte)14, 3, "dbo.p"), (ex.Number, ex.State, ex.LineNumber, ex.Procedure));
        await using var count = new SqlCommand("select count(*) from dbo.t", connection);
        AreEqual(0, await count.ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
