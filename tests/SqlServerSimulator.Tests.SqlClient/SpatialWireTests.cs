using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>geography</c> / <c>geometry</c> result columns over the wire: the codec
/// advertises them as the UDT type (0xF0) and streams the MS spatial binary
/// (CLR-UDT) serialization as PLP, so real SqlClient surfaces them as
/// <c>SqlDbType.Udt</c> and — with <c>Microsoft.SqlServer.Types</c> absent, the
/// DacFx-export case — hands the raw bytes back through
/// <c>GetSqlBytes</c> / <c>GetBytes</c>. Expected bytes are hard-coded from a
/// live SQL Server 2025 reference probe (2026-07-16); the shapes mirror
/// WWI-Standard's <c>Application.Cities.Location</c> points and
/// <c>StateProvinces.Border</c> polygons.
/// </summary>
[TestClass]
public sealed class SpatialWireTests
{
    public TestContext TestContext { get; set; } = null!;

    // geography::STGeomFromText(..., 4326) CAST-to-varbinary bytes.
    private const string CityPointHex = "E6100000010C3333333333D347406666666666965EC0";
    private const string GeographyPolygonHex = "E61000000104040000008716D9CEF7D34740D7A3703D0A975EC08716D9CEF7D34740CBA145B6F3955EC0F853E3A59BD44740CBA145B6F3955EC08716D9CEF7D34740D7A3703D0A975EC001000000020000000001000000FFFFFFFF0000000003";

    // geometry::STGeomFromText(..., 0) CAST-to-varbinary bytes.
    private const string GeometryPointHex = "00000000010C000000000000F03F0000000000000040";
    private const string GeometryPolygonHex = "00000000010405000000000000000000000000000000000000000000000000001040000000000000000000000000000010400000000000001040000000000000000000000000000010400000000000000000000000000000000001000000020000000001000000FFFFFFFF0000000003";

    [TestMethod]
    public async Task GeographyColumn_ReadsAsUdtBytes()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table Cities (CityID int not null, Location geography null);
            insert Cities (CityID, Location) values
                (1, geography::STGeomFromText(N'POINT (-122.35 47.65)', 4326)),
                (2, geography::STGeomFromText(N'POLYGON ((-122.36 47.656, -122.343 47.656, -122.343 47.661, -122.36 47.656))', 4326)),
                (3, null)
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select CityID, Location from Cities order by CityID", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("Location", reader.GetName(1));
        IsTrue(reader.GetDataTypeName(1).EndsWith("sys.geography", StringComparison.Ordinal));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(CityPointHex, ReadUdtHex(reader, 1));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(GeographyPolygonHex, ReadUdtHex(reader, 1));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(1, TestContext.CancellationToken));

        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GeometryColumn_ReadsAsUdtBytes()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table Shapes (id int not null, g geometry null);
            insert Shapes (id, g) values
                (1, geometry::STGeomFromText(N'POINT (1 2)', 0)),
                (2, geometry::STGeomFromText(N'POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))', 0))
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select g from Shapes order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(reader.GetDataTypeName(0).EndsWith("sys.geometry", StringComparison.Ordinal));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(GeometryPointHex, ReadUdtHex(reader, 0));

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(GeometryPolygonHex, ReadUdtHex(reader, 0));

        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// Curved instances and <c>FULLGLOBE</c> travel as version 2 of the
    /// serialization — figure types, the segment table, the
    /// larger-than-a-hemisphere bit — byte for byte as SQL Server 2025 wrote
    /// them (probed 2026-09-29).
    /// </summary>
    [TestMethod]
    public async Task CurvedAndWholeGlobeColumns_ReadAsVersionTwoBytes()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table Curves (id int not null, g geometry null, h geography null);
            insert Curves (id, g, h) values
                (1, geometry::Parse('COMPOUNDCURVE((0 0, 1 1), CIRCULARSTRING(1 1, 2 2, 3 1), (3 1, 4 0))'), geography::Parse('FULLGLOBE'))
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select g, h from Curves", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(
            "0000000002040500000000000000000000000000000000000000000000000000F03F000000000000F03F000000000000004000000000000000400000000000000840000000000000F03F0000000000001040000000000000000001000000030000000001000000FFFFFFFF000000000903000000020302",
            ReadUdtHex(reader, 0));
        AreEqual("E61000000224000000000000000001000000FFFFFFFFFFFFFFFF0B", ReadUdtHex(reader, 1));
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
