using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Pins the outcome-stream markers that drive the TDS endpoint's DONEINPROC /
/// RETURNSTATUS / DONEPROC discipline for an <c>EXEC('…')</c> / sp_executesql
/// dynamic-SQL scope. Real SQL Server runs such a body as a nested procedure
/// scope — its statements report DONEINPROC (0xFF) and the scope closes with
/// RETURNSTATUS + DONEPROC — where the simulator previously emitted a plain
/// batch DONE (0xFD). The engine now brackets the dynamic body's outcomes with
/// <see cref="SimulatedProcScopeBoundary"/> markers (Enter / Exit) that
/// <c>StreamOutcomesAsync</c> consumes; every in-process consumer ignores them.
/// Token-level shape captured cleartext against SQL Server 2025 (2026-07-19).
/// </summary>
[TestClass]
public sealed class ProcScopeBoundaryTests
{
    private static List<SimulatedStatementOutcome> Outcomes(string sql)
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return [.. simulation.CreateResultSetsForCommand(command)];
    }

    private static string Shape(SimulatedStatementOutcome outcome) => outcome switch
    {
        SimulatedProcScopeBoundary { IsEnter: true } => "ENTER",
        SimulatedProcScopeBoundary => "EXIT",
        SimulatedQueryResult => "QUERY",
        _ => "OTHER",
    };

    [TestMethod]
    public void ExecString_BracketsInnerOutcomesWithEnterAndExitMarkers()
    {
        var shapes = Outcomes("select 1 as a; exec('select 2 as b'); select 3 as c").Select(Shape).ToList();

        CollectionAssert.AreEqual(new[] { "QUERY", "ENTER", "QUERY", "EXIT", "QUERY" }, shapes);
    }

    [TestMethod]
    public void ExecString_WithNoResultSet_StillBracketsScope()
    {
        // A body that produces no result set must still open and close the scope
        // (real emits RETURNSTATUS + DONEPROC even with no DONEINPROC in between).
        var boundaries = Outcomes("exec('declare @x int; set @x = 1')")
            .OfType<SimulatedProcScopeBoundary>()
            .Select(b => b.IsEnter)
            .ToList();

        CollectionAssert.AreEqual(new[] { true, false }, boundaries);
    }

    [TestMethod]
    public void SpExecuteSql_BracketsScope()
    {
        var shapes = Outcomes("exec sp_executesql N'select 42 as answer'").Select(Shape).ToList();

        AreEqual("ENTER", shapes[0]);
        AreEqual("QUERY", shapes[1]);
        AreEqual("EXIT", shapes[^1]);
    }

    [TestMethod]
    public void BatchWithoutExec_HasNoBoundaryMarkers()
    {
        var boundaries = Outcomes("select 1; select 2").OfType<SimulatedProcScopeBoundary>().Count();

        AreEqual(0, boundaries);
    }
}
