using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlServerSimulator;

/// <summary>
/// Wire-level test for <c>xp_msver</c> invoked as a name-form TDS RPC
/// (<see cref="CommandType.StoredProcedure"/>) — DacFx's bacpac-export path.
/// The engine's <c>CommandType.StoredProcedure</c> entrypoint routes a modeled
/// system procedure through a synthesized top-level EXEC; each <c>@optname</c>
/// parameter selects one row, and the result carries only the requested rows in
/// <c>Index</c> order. DacFx sends five parameters that all share the name
/// <c>@optname</c>, so the synthesis must be positional (named-argument
/// synthesis would collide on the repeated name).
/// </summary>
[TestClass]
public sealed class XpMsverRpcTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task XpMsver_FiveRepeatedOptnames_ReturnIndexOrderedRows()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("xp_msver", connection) { CommandType = CommandType.StoredProcedure };
        foreach (var optname in new[] { "Platform", "WindowsVersion", "ProcessorCount", "PhysicalMemory", "FileDescription" })
            _ = command.Parameters.Add(new SqlParameter("@optname", optname));

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        var names = new List<string>();
        var indexes = new List<short>();
        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            indexes.Add(reader.GetInt16(0));
            names.Add(reader.GetString(1));
        }

        // Result ordered by Index (4, 7, 15, 16, 19), not by argument order.
        CollectionAssert.AreEqual(new[] { (short)4, (short)7, (short)15, (short)16, (short)19 }, indexes);
        CollectionAssert.AreEqual(
            new[] { "Platform", "FileDescription", "WindowsVersion", "ProcessorCount", "PhysicalMemory" },
            names);
    }
}
