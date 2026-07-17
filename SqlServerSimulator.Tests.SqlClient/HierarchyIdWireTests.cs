using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>hierarchyid</c> result columns over the wire: the codec advertises them
/// as the UDT type (0xF0, max byte size 892) and streams the OrdPath binary
/// serialization as PLP, so real SqlClient surfaces them as
/// <c>SqlDbType.Udt</c> and — with <c>Microsoft.SqlServer.Types</c> absent, the
/// DacFx-export case — hands the raw bytes back through
/// <c>GetSqlBytes</c> / <c>GetBytes</c>. Expected bytes are hard-coded from a
/// live SQL Server 2025 reference probe (2026-07-16), matching the shapes DacFx
/// reads from AdventureWorks' <c>HumanResources.Employee.OrganizationNode</c>.
/// </summary>
[TestClass]
public sealed class HierarchyIdWireTests
{
    public TestContext TestContext { get; set; } = null!;

    // CAST(CAST('/N/' AS hierarchyid) AS varbinary(892)) bytes.
    private const string RootHex = "";
    private const string Node1Hex = "58";
    private const string Node6_1_10Hex = "957540";
    private const string DeepHex = "7C33D1BF823B7E";

    [TestMethod]
    public async Task HierarchyIdColumn_ReadsAsUdtBytes()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table Org (id int not null, node hierarchyid null);
            insert Org (id, node) values
                (1, hierarchyid::Parse('/')),
                (2, hierarchyid::Parse('/1/')),
                (3, hierarchyid::Parse('/6/1/10/')),
                (4, hierarchyid::Parse('/3/4/7/8/15/16/79/')),
                (5, null)
            """);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select node from Org order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(reader.GetDataTypeName(0).EndsWith("sys.hierarchyid", StringComparison.Ordinal));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(RootHex, ReadUdtHex(reader, 0));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(Node1Hex, ReadUdtHex(reader, 0));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(Node6_1_10Hex, ReadUdtHex(reader, 0));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(DeepHex, ReadUdtHex(reader, 0));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));

        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// The DacFx export shape: a hierarchyid column beside its
    /// <c>DATALENGTH([col])</c> companion, both read over the wire — the length
    /// must equal the UDT value's byte count so it becomes a correct BCP length
    /// prefix.
    /// </summary>
    [TestMethod]
    public async Task HierarchyIdColumn_DataLengthMatchesUdtByteCount()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table Org (id int not null, node hierarchyid null);
            insert Org (id, node) values
                (1, hierarchyid::Parse('/6/1/10/')),
                (2, hierarchyid::Parse('/3/4/7/8/15/16/79/'))
            """);

        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select node, datalength(node) from Org order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
            var bytes = reader.GetSqlBytes(0).Value;
            AreEqual(bytes.Length, reader.GetInt32(1));
        }
    }

    /// <summary>
    /// Reads a UDT cell the DacFx way — <c>GetSqlBytes</c> (assembly-independent,
    /// unlike <c>GetValue</c> which needs the CLR type) — and returns it as hex.
    /// Cross-checks the length-only <c>GetBytes</c> probe used by bulk readers.
    /// </summary>
    private static string ReadUdtHex(SqlDataReader reader, int ordinal)
    {
        var sqlBytes = reader.GetSqlBytes(ordinal);
        var bytes = sqlBytes.Value;
        var length = reader.GetBytes(ordinal, 0, null, 0, 0);
        AreEqual(bytes.Length, (int)length);
        return Convert.ToHexString(bytes);
    }
}
