using System.Data;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Types;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// CLR-UDT RPC parameters (TDS type token 0xF0): <c>geography</c> /
/// <c>geometry</c> / <c>hierarchyid</c> arrive as a three-B_VARCHAR UDT_INFO
/// (db / schema / type, only the type filled) plus a PLP value — OrdPath bytes
/// for hierarchyid (stored verbatim) or the MS spatial binary for the spatial
/// types (decoded back to WKT via <c>SpatialWkbDecoder</c>). SqlClient serializes
/// a typed <c>SqlGeography</c> / <c>SqlHierarchyId</c> value or a raw
/// <c>byte[]</c> to the same wire form; both are exercised. Semantics and error
/// numbers probe-confirmed against SQL Server 2025 + SqlClient 7.0.2
/// (2026-07-19). <c>SqlGeometry.STGeomFromText</c> is not implemented on Linux
/// (managed <c>Microsoft.SqlServer.Types</c>), so geometry is constructed from
/// raw WKB bytes — the shape DacFx / bulk-import consumers send.
/// </summary>
[TestClass]
public sealed class UdtRpcParameterTests
{
    public TestContext TestContext { get; set; } = null!;

    // geometry POINT(3 4): SRID 0 + version 1 + props 0x0C (isValid|isSinglePoint)
    // + x=3.0 + y=4.0, the 22-byte single-point MS spatial binary.
    private const string GeometryPointWkbHex = "00000000010C00000000000008400000000000001040";

    // hierarchyid '/1/2/' canonical OrdPath bytes.
    private static readonly byte[] Node1_2 = [0x5B, 0x40];

    [TestMethod]
    public async Task GeographyParameter_TypedValue_RoundTripsToWkt()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p.ToString()", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt)
        {
            UdtTypeName = "geography",
            Value = SqlGeography.STGeomFromText(new SqlChars("POINT(-122.3 47.6)"), 4326),
        });

        AreEqual("POINT (-122.3 47.6)", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GeographyParameter_Null_ReadsAsNull()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p.ToString()", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt) { UdtTypeName = "geography", Value = DBNull.Value });

        _ = IsInstanceOfType<DBNull>(await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GeometryParameter_RawWkbBytes_RoundTripsToWkt()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p.ToString()", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt)
        {
            UdtTypeName = "geometry",
            Value = Convert.FromHexString(GeometryPointWkbHex),
        });

        AreEqual("POINT (3 4)", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task HierarchyIdParameter_TypedValue_RoundTripsToPathAndDataLength()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p.ToString(), datalength(@p)", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt)
        {
            UdtTypeName = "hierarchyid",
            Value = SqlHierarchyId.Parse(new SqlString("/1/2/")),
        });

        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("/1/2/", reader.GetString(0));
        AreEqual(2, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task HierarchyIdParameter_RawOrdPathBytes_RoundTripsToPath()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p.ToString()", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt) { UdtTypeName = "hierarchyid", Value = Node1_2 });

        AreEqual("/1/2/", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task HierarchyIdParameter_Null_ReadsAsNull()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p.ToString()", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt) { UdtTypeName = "hierarchyid", Value = DBNull.Value });

        _ = IsInstanceOfType<DBNull>(await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task UnknownUdtTypeName_RaisesMsg8064()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt) { UdtTypeName = "nosuchtype", Value = new byte[] { 1, 2 } });

        var ex = await ThrowsExactlyAsync<SqlException>(async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(8064, ex.Number);
        // db segment resolves to the current database; schema stays empty.
        Contains("[simulated].[].[nosuchtype]", ex.Message);
        Contains("The CLR type does not exist", ex.Message);
    }

    [TestMethod]
    public async Task InvalidGeographyBytes_RaisesMsg8023()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select @p.ToString()", connection);
        _ = command.Parameters.Add(new SqlParameter("@p", SqlDbType.Udt) { UdtTypeName = "geography", Value = new byte[] { 0, 1, 2, 3 } });

        var ex = await ThrowsExactlyAsync<SqlException>(async () => await command.ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(8023, ex.Number);
        Contains("not a valid instance of data type geography", ex.Message);
    }

    [TestMethod]
    public async Task HierarchyIdParameter_DirectProcedureCall_Binds()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create procedure dbo.EchoNode @h hierarchyid as select @h.ToString()");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.EchoNode", connection) { CommandType = CommandType.StoredProcedure };
        _ = command.Parameters.Add(new SqlParameter("@h", SqlDbType.Udt)
        {
            UdtTypeName = "hierarchyid",
            Value = SqlHierarchyId.Parse(new SqlString("/3/4/")),
        });

        AreEqual("/3/4/", await command.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // ---- Output direction ----

    // RETURNVALUE for an output CLR-UDT parameter carries the COLMETADATA-shaped
    // UDT_INFO (USHORT max byte size — 892 for hierarchyid, 0xFFFF for the
    // spatial types — then db / schema / type B_VARCHARs and the US_VARCHAR
    // assembly-qualified name) with a PLP value, PLP NULL for a NULL output —
    // probe-captured against SQL Server 2025 + SqlClient 7.0.2 (2026-07-19).

    [TestMethod]
    public async Task HierarchyIdOutput_ThroughProc_RoundTrips()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create proc dbo.get_h @h hierarchyid output as set @h = hierarchyid::Parse('/1/2/')");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.get_h", connection) { CommandType = CommandType.StoredProcedure };
        var output = command.Parameters.Add(new SqlParameter("@h", SqlDbType.Udt) { UdtTypeName = "hierarchyid", Direction = ParameterDirection.Output });
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual("/1/2/", ((SqlHierarchyId)output.Value).ToString());
    }

    [TestMethod]
    public async Task GeographyOutput_ThroughProc_RoundTrips()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create proc dbo.get_g @g geography output as set @g = geography::STGeomFromText('POINT(-122.3 47.6)', 4326)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.get_g", connection) { CommandType = CommandType.StoredProcedure };
        var output = command.Parameters.Add(new SqlParameter("@g", SqlDbType.Udt) { UdtTypeName = "geography", Direction = ParameterDirection.Output });
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual("POINT (-122.3 47.6)", ((SqlGeography)output.Value).ToString());
    }

    [TestMethod]
    public async Task GeometryOutput_ThroughTextCommand_RoundTrips()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("set @g = geometry::STGeomFromText('POINT(3 4)', 0)", connection);
        var output = command.Parameters.Add(new SqlParameter("@g", SqlDbType.Udt) { UdtTypeName = "geometry", Direction = ParameterDirection.Output });
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        AreEqual("POINT (3 4)", ((SqlGeometry)output.Value).ToString());
    }

    // A NULL UDT output arrives as PLP NULL; SqlClient surfaces the typed
    // Null instance (SqlHierarchyId.Null), not DBNull — probed.
    [TestMethod]
    public async Task HierarchyIdOutput_NullValue_ReadsAsTypedNull()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create proc dbo.get_h @h hierarchyid output as set @h = null");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("dbo.get_h", connection) { CommandType = CommandType.StoredProcedure };
        var output = command.Parameters.Add(new SqlParameter("@h", SqlDbType.Udt) { UdtTypeName = "hierarchyid", Direction = ParameterDirection.Output });
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        IsTrue(((SqlHierarchyId)output.Value).IsNull);
    }
}
