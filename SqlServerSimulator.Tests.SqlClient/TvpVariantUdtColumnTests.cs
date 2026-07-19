using System.Data;
using System.Data.SqlTypes;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Microsoft.SqlServer.Types;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sql_variant</c> and CLR-UDT (<c>hierarchyid</c> / <c>geography</c> /
/// <c>geometry</c>) columns inside a table-valued parameter. Once the RPC value
/// decoders and the COLMETADATA-shaped column decoder share one implementation
/// (<c>TdsWireValue</c>), a TVP column carrying a self-describing
/// <c>sql_variant</c> body or a PLP-carried UDT value decodes through the same
/// path an RPC <c>sql_variant</c> / UDT parameter uses. Wire shapes probe-captured
/// against SQL Server 2025 + SqlClient 7.0.2 through a cleartext tee proxy
/// (2026-07-19): the <c>sql_variant</c> column TYPE_INFO is <c>0x62</c> + a 4-byte
/// max length with a 4-byte-total-length (0 = NULL) body value; the UDT column
/// TYPE_INFO is <c>0xF0</c> + three B_VARCHARs (db / schema / type) with a PLP
/// value — both the same forms as the matching RPC parameters.
/// </summary>
/// <remarks>
/// These drive the TVP through the <c>sp_executesql</c> text path (the parameter
/// binds as its own <c>@rows</c> table variable). Routing a LOB-backed TVP column
/// (<c>geography</c> / <c>geometry</c>, like <c>nvarchar(max)</c>) through a
/// stored-procedure READONLY parameter hits a pre-existing table-variable LOB-copy
/// gap in proc-parameter binding, unrelated to the wire decode; see
/// docs/claude/tds-endpoint.md.
/// </remarks>
[TestClass]
public sealed class TvpVariantUdtColumnTests
{
    public TestContext TestContext { get; set; } = null!;

    // geometry POINT(3 4): the 22-byte single-point MS spatial binary
    // (SqlGeometry.STGeomFromText is unavailable on managed Types under Linux, so
    // geometry rides raw WKB bytes — the shape DacFx / bulk-import consumers send).
    private const string GeometryPointWkbHex = "00000000010C00000000000008400000000000001040";

    private static async Task InsertViaTvp(SqlConnection connection, string typeName, IEnumerable<SqlDataRecord> rows, CancellationToken token)
    {
        await using var command = new SqlCommand("insert into dbo.sink select * from @rows", connection);
        var parameter = command.Parameters.AddWithValue("@rows", rows);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = typeName;
        _ = await command.ExecuteNonQueryAsync(token);
    }

    [TestMethod]
    public async Task SqlVariantColumn_PerBaseType_RoundTripsWithBaseType()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.VarRows as table (id int, v sql_variant)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, v sql_variant)");
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[] { new SqlMetaData("id", SqlDbType.Int), new SqlMetaData("v", SqlDbType.Variant) };
        var rows = new List<SqlDataRecord>();
        foreach (var (id, value) in new (int, object?)[] { (1, 4242), (2, "hello"), (3, 3.5d), (4, null) })
        {
            var record = new SqlDataRecord(metadata);
            record.SetInt32(0, id);
            if (value is null)
                record.SetDBNull(1);
            else
                record.SetValue(1, value);
            rows.Add(record);
        }

        await InsertViaTvp(connection, "dbo.VarRows", rows, TestContext.CancellationToken);

        var read = Wire.Drain(await new SqlCommand(
            "select id, v, sql_variant_property(v, 'BaseType') from dbo.sink order by id",
            connection).ExecuteReaderAsync(TestContext.CancellationToken));
        HasCount(4, read);
        AreEqual(4242, read[0][1]);
        AreEqual("int", read[0][2]);
        AreEqual("hello", read[1][1]);
        AreEqual("nvarchar", read[1][2]);
        AreEqual(3.5d, read[2][1]);
        AreEqual("float", read[2][2]);
        IsNull(read[3][1]);
    }

    [TestMethod]
    public async Task HierarchyIdColumn_RoundTripsToPathAndDataLength()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.HierRows as table (id int, h hierarchyid)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, h hierarchyid)");
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[] { new SqlMetaData("id", SqlDbType.Int), new SqlMetaData("h", SqlDbType.Udt, typeof(SqlHierarchyId), "hierarchyid") };
        var record = new SqlDataRecord(metadata);
        record.SetInt32(0, 1);
        record.SetValue(1, SqlHierarchyId.Parse(new SqlString("/1/2/")));

        await InsertViaTvp(connection, "dbo.HierRows", [record], TestContext.CancellationToken);

        await using var reader = await new SqlCommand("select h.ToString(), datalength(h) from dbo.sink", connection)
            .ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("/1/2/", reader.GetString(0));
        AreEqual(2, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task GeographyColumn_RoundTripsToWkt()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.GeoRows as table (id int, g geography)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, g geography)");
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[] { new SqlMetaData("id", SqlDbType.Int), new SqlMetaData("g", SqlDbType.Udt, typeof(SqlGeography), "geography") };
        var record = new SqlDataRecord(metadata);
        record.SetInt32(0, 1);
        record.SetValue(1, SqlGeography.STGeomFromText(new SqlChars("POINT(-122.3 47.6)"), 4326));

        await InsertViaTvp(connection, "dbo.GeoRows", [record], TestContext.CancellationToken);

        AreEqual("POINT (-122.3 47.6)", await new SqlCommand("select g.ToString() from dbo.sink", connection)
            .ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GeometryColumn_RoundTripsToWkt()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.GeomRows as table (id int, g geometry)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, g geometry)");
        await using var listener = await simulation.ListenAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[] { new SqlMetaData("id", SqlDbType.Int), new SqlMetaData("g", SqlDbType.Udt, typeof(SqlGeometry), "geometry") };
        var record = new SqlDataRecord(metadata);
        record.SetInt32(0, 1);
        record.SetSqlBytes(1, new SqlBytes(Convert.FromHexString(GeometryPointWkbHex)));

        await InsertViaTvp(connection, "dbo.GeomRows", [record], TestContext.CancellationToken);

        AreEqual("POINT (3 4)", await new SqlCommand("select g.ToString() from dbo.sink", connection)
            .ExecuteScalarAsync(TestContext.CancellationToken));
    }
}
