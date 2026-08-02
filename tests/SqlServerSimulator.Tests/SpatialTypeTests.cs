using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>geography</c> / <c>geometry</c> surface —
/// column round-trip, sys.types / sys.columns identity, static-call
/// construction (<c>geography::Parse</c> / <c>geometry::Point</c>), the
/// measures and predicates that still raise at execute, CREATE SPATIAL INDEX
/// parse-and-discard, and the three catalog views
/// (<c>sys.spatial_indexes</c> / <c>sys.spatial_index_tessellations</c> /
/// <c>sys.spatial_reference_systems</c>). The value model itself — WKT
/// canonicalization, SRID, Z/M, EMPTY, the binary form and the accessor
/// members — lives in <see cref="SpatialValueTests"/>.
/// </summary>
[TestClass]
public sealed class SpatialTypeTests
{
    [TestMethod]
    public void GeographyColumn_AcceptsAndRoundTrips()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, geography::Parse('POINT(-122.34 47.65)'))");
        AreEqual("POINT (-122.34 47.65)", sim.ExecuteScalar("select g from dbo.loc where id = 1"));
    }

    [TestMethod]
    public void GeometryColumn_AcceptsAndRoundTrips()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.shape (id int, g geometry)");
        _ = sim.ExecuteNonQuery("insert dbo.shape values (1, geometry::STGeomFromText('POINT(0 0)', 0))");
        AreEqual("POINT (0 0)", sim.ExecuteScalar("select g from dbo.shape where id = 1"));
    }

    [TestMethod]
    public void GeographyColumn_NullStoresAsNull()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography null)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, null)");
        AreEqual(DBNull.Value, sim.ExecuteScalar("select g from dbo.loc"));
    }

    [TestMethod]
    public void SysTypes_ReportsGeographyIdentity()
        => AreEqual(130, new Simulation().ExecuteScalar("select user_type_id from sys.types where name = 'geography'"));

    [TestMethod]
    public void SysTypes_ReportsGeometryIdentity()
        => AreEqual(129, new Simulation().ExecuteScalar("select user_type_id from sys.types where name = 'geometry'"));

    [TestMethod]
    public void SysTypes_BothShareSystemTypeId240()
        => AreEqual(2, new Simulation().ExecuteScalar("select count(*) from sys.types where system_type_id = 240 and name in ('geography','geometry')"));

    [TestMethod]
    public void SysColumns_ReportsGeographyTypeIdentity()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        AreEqual("geography", sim.ExecuteScalar(@"
            select t.name from sys.columns c
            join sys.types t on t.user_type_id = c.user_type_id
            where c.object_id = object_id('dbo.loc') and c.name = 'g'"));
    }

    [TestMethod]
    public void SysColumns_ReportsMaxLengthMinusOne_ForSpatial()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography, m geometry)");
        AreEqual((short)-1, sim.ExecuteScalar("select max_length from sys.columns where object_id = object_id('dbo.loc') and name = 'g'"));
        AreEqual((short)-1, sim.ExecuteScalar("select max_length from sys.columns where object_id = object_id('dbo.loc') and name = 'm'"));
    }

    [TestMethod]
    public void GeographyParse_FromNVarcharLiteral_Roundtrips()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, geography::Parse(N'LINESTRING(0 0, 1 1)'))");
        AreEqual("LINESTRING (0 0, 1 1)", sim.ExecuteScalar("select g from dbo.loc where id = 1"));
    }

    [TestMethod]
    public void GeometryPoint_ConstructsFromCoordinates()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.shape (id int, g geometry)");
        _ = sim.ExecuteNonQuery("insert dbo.shape values (1, geometry::Point(3.5, 7.25, 0))");
        Assert.Contains("POINT", sim.ExecuteScalar("select g from dbo.shape where id = 1") as string ?? "");
        Assert.Contains("3.5", sim.ExecuteScalar("select g from dbo.shape where id = 1") as string ?? "");
        Assert.Contains("7.25", sim.ExecuteScalar("select g from dbo.shape where id = 1") as string ?? "");
    }

    [TestMethod]
    public void GeographyToString_ReturnsStoredWkt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, geography::Parse('POINT(-122.34 47.65)'))");
        AreEqual("POINT (-122.34 47.65)", sim.ExecuteScalar("select g.ToString() from dbo.loc"));
    }

    /// <summary>
    /// <c>STDistance</c> between two points evaluates along the great elliptic
    /// arc; the value itself is pinned in <see cref="SpatialValueTests"/>.
    /// </summary>
    [TestMethod]
    public void GeographyMethodCall_STDistance_MeasuresBetweenPoints()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, geography::Parse('POINT(0 0)'))");
        var distance = (double)sim.ExecuteScalar("select g.STDistance(geography::Parse('POINT(0 1)')) from dbo.loc")!;
        Assert.IsGreaterThan(110574.0, distance);
        Assert.IsLessThan(110575.0, distance);
    }

    /// <summary>
    /// <c>STDistance</c> reaches shapes that aren't points: the closest approach
    /// from a stored point to a line lands mid-arc, and the value is real's own.
    /// </summary>
    [TestMethod]
    public void GeographyMethodCall_STDistance_MeasuresToALine()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, geography::Parse('POINT(0.005 0.001)'))");
        var distance = (double)sim.ExecuteScalar("select g.STDistance(geography::Parse('LINESTRING(0 0, 0.01 0)')) from dbo.loc")!;
        AreEqual(110.57427581595613, distance, 1e-6);
    }

    [TestMethod]
    public void GeometryMethodCall_STAsText_RendersCanonicalWkt()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.shape (id int, g geometry)");
        _ = sim.ExecuteNonQuery("insert dbo.shape values (1, geometry::STGeomFromText('POINT(0 0)', 0))");
        AreEqual("POINT (0 0)", sim.ExecuteScalar("select g.STAsText() from dbo.shape"));
    }

    [TestMethod]
    public void GeographyMethodCall_STIntersects_ThrowsAtExecute()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, geography::Parse('POINT(0 0)'))");
        var ex = Throws<NotSupportedException>(() => _ = sim.ExecuteScalar("select g.STIntersects(geography::Parse('POINT(1 1)')) from dbo.loc"));
        Assert.Contains("STIntersects", ex.Message);
    }

    [TestMethod]
    public void CreateView_WithSpatialMethod_ProjectsThroughView()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.loc (id int, g geography)",
            "insert dbo.loc values (1, geography::Parse('POINT(0 0)'))",
            "create view dbo.v_loc as select id, g.STAsText() as wkt from dbo.loc");
        AreEqual("v_loc", sim.ExecuteScalar("select name from sys.views where object_id = object_id('dbo.v_loc')"));
        AreEqual("POINT (0 0)", sim.ExecuteScalar("select wkt from dbo.v_loc"));
    }

    [TestMethod]
    public void CreateView_WithUnmodeledSpatialMethod_Succeeds_FailsAtExecute()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table dbo.loc (id int, g geography)",
            "insert dbo.loc values (1, geography::Parse('POINT(0 0)'))",
            "create view dbo.v_hit as select id, g.STIntersects(geography::Parse('POINT(1 1)')) as hit from dbo.loc");
        // View created successfully — the spatial method call parsed cleanly.
        AreEqual("v_hit", sim.ExecuteScalar("select name from sys.views where object_id = object_id('dbo.v_hit')"));
        // ...but execute fails, since round-earth topology has no evaluation.
        _ = Throws<NotSupportedException>(() => _ = sim.ExecuteScalar("select hit from dbo.v_hit"));
    }

    [TestMethod]
    public void CreateSpatialIndex_Geometry_PopulatesSysSpatialIndexes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"
            create table dbo.shape (id int primary key, g geometry);
            create spatial index sp_g on dbo.shape(g) with (bounding_box = (0, 0, 10, 10))");
        AreEqual("sp_g", sim.ExecuteScalar("select name from sys.spatial_indexes where object_id = object_id('dbo.shape')"));
        AreEqual("SPATIAL", sim.ExecuteScalar("select type_desc from sys.spatial_indexes where object_id = object_id('dbo.shape')"));
        AreEqual(3, sim.ExecuteScalar("select spatial_index_type from sys.spatial_indexes where object_id = object_id('dbo.shape')"));
        AreEqual("GEOMETRY", sim.ExecuteScalar("select spatial_index_type_desc from sys.spatial_indexes where object_id = object_id('dbo.shape')"));
    }

    [TestMethod]
    public void CreateSpatialIndex_Geography_DefaultTessellationIsGeographyAutoGrid()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"
            create table dbo.loc (id int primary key, g geography);
            create spatial index sp_loc on dbo.loc(g)");
        AreEqual("GEOGRAPHY_AUTO_GRID", sim.ExecuteScalar("select tessellation_scheme from sys.spatial_indexes where object_id = object_id('dbo.loc')"));
        AreEqual(4, sim.ExecuteScalar("select spatial_index_type from sys.spatial_indexes where object_id = object_id('dbo.loc')"));
        AreEqual("GEOGRAPHY", sim.ExecuteScalar("select spatial_index_type_desc from sys.spatial_indexes where object_id = object_id('dbo.loc')"));
    }

    [TestMethod]
    public void CreateSpatialIndex_BoundingBox_RoundTripsIntoTessellations()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"
            create table dbo.shape (id int primary key, g geometry);
            create spatial index sp_g on dbo.shape(g) with (bounding_box = (-5, -10, 15, 25))");
        AreEqual(-5d, sim.ExecuteScalar("select bounding_box_xmin from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
        AreEqual(-10d, sim.ExecuteScalar("select bounding_box_ymin from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
        AreEqual(15d, sim.ExecuteScalar("select bounding_box_xmax from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
        AreEqual(25d, sim.ExecuteScalar("select bounding_box_ymax from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
    }

    [TestMethod]
    public void CreateSpatialIndex_GridsWithLevelNames_ParsesToCodes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"
            create table dbo.shape (id int primary key, g geometry);
            create spatial index sp_g on dbo.shape(g) with (bounding_box = (0, 0, 10, 10), grids = (LOW, HIGH, LOW, HIGH), cells_per_object = 16)");
        AreEqual((short)1, sim.ExecuteScalar("select level_1_grid from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
        AreEqual("LOW", sim.ExecuteScalar("select level_1_grid_desc from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
        AreEqual((short)3, sim.ExecuteScalar("select level_2_grid from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
        AreEqual("HIGH", sim.ExecuteScalar("select level_2_grid_desc from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
        AreEqual(16, sim.ExecuteScalar("select cells_per_object from sys.spatial_index_tessellations where object_id = object_id('dbo.shape')"));
    }

    [TestMethod]
    public void CreateSpatialIndex_DuplicateName_RaisesMsg2714()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(@"
            create table dbo.shape (id int primary key, g geometry);
            create spatial index sp_g on dbo.shape(g) with (bounding_box = (0, 0, 10, 10))");
        _ = sim.AssertSqlError(
            "create spatial index sp_g on dbo.shape(g) with (bounding_box = (0, 0, 5, 5))",
            2714);
    }

    [TestMethod]
    public void SysSpatialReferenceSystems_EmptyByDefault()
        => AreEqual(0, new Simulation().ExecuteScalar("select count(*) from sys.spatial_reference_systems"));

    [TestMethod]
    public void SysSpatialReferenceSystems_ColumnsAreReachable()
    {
        var sim = new Simulation();
        // Column shape probe: SELECT should succeed even with no rows.
        _ = sim.ExecuteScalar("select count(spatial_reference_id) + count(authority_name) + count(well_known_text) from sys.spatial_reference_systems");
    }

    [TestMethod]
    public void GeographyCast_ToNVarchar_RoundTrips()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.loc (id int, g geography)");
        _ = sim.ExecuteNonQuery("insert dbo.loc values (1, geography::Parse('POINT(0 0)'))");
        AreEqual("POINT (0 0)", sim.ExecuteScalar("select cast(g as nvarchar(max)) from dbo.loc"));
    }

    [TestMethod]
    public void GeographyParse_NullWkt_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select cast(geography::Parse(cast(null as nvarchar(max))) as nvarchar(max))"));

    [TestMethod]
    public void GeometryStGeomFromText_NullWkt_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select cast(geometry::STGeomFromText(cast(null as nvarchar(max)), 0) as nvarchar(max))"));

    [TestMethod]
    public void GeographyPoint_NullCoord_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar(
            "select cast(geography::Point(cast(null as float), 1, 4326) as nvarchar(max))"));

    [TestMethod]
    public void GeographyParse_NoArgs_RaisesArityError()
        => new Simulation().AssertSqlError("select geography::Parse()", 174, "The Parse function requires 1 argument(s).");

    [TestMethod]
    public void GeometryStaticCall_MissingOpenParen_Throws()
        => _ = Throws<DbException>(() => new Simulation().ExecuteScalar(
            "select geometry::Parse 'POINT (0 0)'"));

    [TestMethod]
    public void GeographyStaticCall_TrailingGarbage_Throws()
        => _ = Throws<DbException>(() => new Simulation().ExecuteScalar(
            "select geography::Parse('POINT (0 0)' garbage)"));

    [TestMethod]
    public void GeographyParse_InSelectInto_ProjectsSpatialColumnType()
    {
        var sim = new Simulation();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("select geography::Parse('POINT(0 0)') as g into #t").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select count(*) from #t").ExecuteScalar());
    }

    [TestMethod]
    public void GeographyParse_AsComputedColumn_AlterPeerColumn_WalksExpression()
    {
        // ALTER COLUMN walks every computed column on the table to detect
        // whether it transitively depends on the column being altered, by
        // calling VisitColumnReferences on each computed expression. A
        // spatial static call has no column refs but the walk still
        // recurses into its arguments.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table c1 (id int, dummy varchar(10), g as geography::Parse('POINT(0 0)') persisted);
            alter table c1 alter column dummy varchar(20)
            """);
        AreEqual(0, sim.ExecuteScalar("select count(*) from c1"));
    }

    [TestMethod]
    public void CreateSpatialIndex_NonSpatialColumn_RaisesMsg12002()
        => new Simulation().AssertSqlError(
            "create table dbo.np (id int); create spatial index six on dbo.np(id)",
            12002,
            "The requested spatial index on column 'id' of table 'np' could not be created because the column type is not geometry or geography . Specify a column name that refers to a column with a geometry or geography data type.");

    /// <summary>
    /// DATALENGTH over a spatial value measures the CLR-UDT serialization
    /// (what a real server stores), not the simulator's WKT text: a 2D
    /// point is 22 bytes as <c>int</c> — probe-confirmed against WWI's
    /// Application.Cities CityID 1 on the live reference (22,
    /// base type int). DacFx bacpac export writes DATALENGTH([geoCol]) as
    /// the BCP length prefix for the wire value bytes, so the two must
    /// measure the same serialized form.
    /// </summary>
    [TestMethod]
    public void DataLength_SpatialValues_MeasureClrSerialization()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dbo.dl (id int, g geography, m geometry);
            insert dbo.dl values (1, geography::Parse('POINT(-77.4533235 40.8997903)'), geometry::Parse('POINT(1 2)'))
            """);
        AreEqual(22, sim.ExecuteScalar("select datalength(g) from dbo.dl"));
        AreEqual(22, sim.ExecuteScalar("select datalength(m) from dbo.dl"));
    }
}
