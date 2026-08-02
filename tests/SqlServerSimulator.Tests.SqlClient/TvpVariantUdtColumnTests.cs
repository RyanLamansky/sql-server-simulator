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
/// Most of these drive the TVP through the <c>sp_executesql</c> text path (the
/// parameter binds as its own <c>@rows</c> table variable); the LOB-backed
/// proc test covers the stored-procedure READONLY route, whose parameter copy
/// re-homes off-row values into the parameter's own heap.
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
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
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
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
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
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[] { new SqlMetaData("id", SqlDbType.Int), new SqlMetaData("g", SqlDbType.Udt, typeof(SqlGeography), "geography") };
        var record = new SqlDataRecord(metadata);
        record.SetInt32(0, 1);
        record.SetValue(1, SqlGeography.STGeomFromText(new SqlChars("POINT(-122.3 47.6)"), 4326));

        await InsertViaTvp(connection, "dbo.GeoRows", [record], TestContext.CancellationToken);

        AreEqual("POINT (-122.3 47.6)", await new SqlCommand("select g.ToString() from dbo.sink", connection)
            .ExecuteScalarAsync(TestContext.CancellationToken));
    }

    // LOB-backed columns (nvarchar(max), geography) bound to a stored-proc
    // READONLY parameter: the proc-parameter copy must carry the off-row
    // values into the parameter's table variable, not just the row bytes.
    [TestMethod]
    public async Task LobBackedColumns_ThroughProcReadonlyParameter_RoundTrip()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.LobRows as table (id int, doc nvarchar(max), g geography)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, doc nvarchar(max), g geography)");
        Wire.ExecInProc(simulation, "create proc dbo.ins_lob @rows dbo.LobRows readonly as insert into dbo.sink select id, doc, g from @rows");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[]
        {
            new SqlMetaData("id", SqlDbType.Int),
            new SqlMetaData("doc", SqlDbType.NVarChar, SqlMetaData.Max),
            new SqlMetaData("g", SqlDbType.Udt, typeof(SqlGeography), "geography"),
        };
        var record = new SqlDataRecord(metadata);
        record.SetInt32(0, 1);
        record.SetString(1, new string('x', 100000));
        record.SetValue(2, SqlGeography.STGeomFromText(new SqlChars("POINT(-122.3 47.6)"), 4326));

        await using var command = new SqlCommand("dbo.ins_lob", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@rows", new[] { record });
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.LobRows";
        _ = await command.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using var reader = await new SqlCommand("select cast(len(doc) as int), g.ToString() from dbo.sink", connection)
            .ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(100000, reader.GetInt32(0));
        AreEqual("POINT (-122.3 47.6)", reader.GetString(1));
    }

    [TestMethod]
    public async Task GeometryColumn_RoundTripsToWkt()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.GeomRows as table (id int, g geometry)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, g geometry)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[] { new SqlMetaData("id", SqlDbType.Int), new SqlMetaData("g", SqlDbType.Udt, typeof(SqlGeometry), "geometry") };
        var record = new SqlDataRecord(metadata);
        record.SetInt32(0, 1);
        record.SetSqlBytes(1, new SqlBytes(Convert.FromHexString(GeometryPointWkbHex)));

        await InsertViaTvp(connection, "dbo.GeomRows", [record], TestContext.CancellationToken);

        AreEqual("POINT (3 4)", await new SqlCommand("select g.ToString() from dbo.sink", connection)
            .ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// The base types a <c>sql_variant</c> TVP column reaches that an RPC
    /// <c>sql_variant</c> parameter cannot: SqlClient picks a parameter's base
    /// type from the CLR value alone (a <see cref="DateTime"/> is always
    /// <c>datetime</c>, a string always <c>nvarchar</c>), while a row's
    /// <see cref="SqlMetaData"/> names the base type outright.
    /// </summary>
    [TestMethod]
    [DataRow(SqlDbType.SmallDateTime, "smalldatetime")]
    [DataRow(SqlDbType.DateTime2, "datetime2")]
    [DataRow(SqlDbType.DateTimeOffset, "datetimeoffset")]
    [DataRow(SqlDbType.VarChar, "varchar")]
    public async Task SqlVariantColumn_MetadataDeclaredBaseType_SurvivesTheRoundTrip(SqlDbType declared, string baseType)
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.VarRows as table (v sql_variant)");
        Wire.ExecInProc(simulation, "create table dbo.sink (v sql_variant)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        object value = declared switch
        {
            SqlDbType.SmallDateTime => new DateTime(2024, 3, 15, 13, 45, 0),
            SqlDbType.DateTime2 => new DateTime(2024, 3, 15, 13, 45, 12).AddTicks(1234567),
            SqlDbType.DateTimeOffset => new DateTimeOffset(new DateTime(2024, 3, 15, 13, 45, 12), TimeSpan.FromMinutes(330)),
            _ => "ansi text",
        };
        var record = new SqlDataRecord(declared == SqlDbType.VarChar
            ? new SqlMetaData("v", declared, 40)
            : new SqlMetaData("v", declared));
        record.SetValue(0, value);

        await InsertViaTvp(connection, "dbo.VarRows", [record], TestContext.CancellationToken);

        AreEqual(baseType, await new SqlCommand("select SQL_VARIANT_PROPERTY(v, 'BaseType') from dbo.sink", connection)
            .ExecuteScalarAsync(TestContext.CancellationToken));
        AreEqual(value, await new SqlCommand("select v from dbo.sink", connection)
            .ExecuteScalarAsync(TestContext.CancellationToken));
    }

    /// <summary>
    /// An <c>xml</c> TVP column travels as its own XMLTYPE (<c>0xF1</c>) with a
    /// PLP value — the one column type that reaches the decoder's XML arm, since
    /// a bulk-copy <c>xml</c> destination goes as the MAX-string form instead.
    /// The leading byte-order mark a PLP <c>xml</c> value carries is dropped on
    /// the way in, as on every other <c>xml</c> entry path.
    /// </summary>
    [TestMethod]
    public async Task XmlColumn_RoundTripsAndStripsBom()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create type dbo.XmlRows as table (id int, x xml)");
        Wire.ExecInProc(simulation, "create table dbo.sink (id int, x xml)");
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var metadata = new[] { new SqlMetaData("id", SqlDbType.Int), new SqlMetaData("x", SqlDbType.Xml) };
        var rows = new List<SqlDataRecord>();
        foreach (var (id, value) in new (int, string?)[] { (1, "<r a=\"1\"><c>x</c></r>"), (2, null) })
        {
            var record = new SqlDataRecord(metadata);
            record.SetInt32(0, id);
            if (value is null)
                record.SetDBNull(1);
            else
                record.SetString(1, value);
            rows.Add(record);
        }

        await InsertViaTvp(connection, "dbo.XmlRows", rows, TestContext.CancellationToken);

        await using var read = new SqlCommand("select cast(x as nvarchar(max)) from dbo.sink order by id", connection);
        await using var reader = await read.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("<r a=\"1\"><c>x</c></r>", reader.GetString(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
    }
}
