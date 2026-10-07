using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// GML in both directions, <c>sys.spatial_reference_systems</c> and the
/// per-SRID measures it drives, the planar lobe split and the Multi* members
/// WKT writes <c>EMPTY</c>, against answers probed from SQL Server 2025
/// (2026-10-06).
/// </summary>
[TestClass]
public sealed class SpatialGmlAndReferenceSystemTests
{
    private const string Gml = "xmlns=\"http://www.opengis.net/gml\"";

    [TestMethod]
    [DataRow("geometry", "POINT(1 2)", "<Point xmlns=\"http://www.opengis.net/gml\"><pos>1 2</pos></Point>")]
    [DataRow("geography", "POINT(1 2)", "<Point xmlns=\"http://www.opengis.net/gml\"><pos>2 1</pos></Point>")]
    [DataRow("geometry", "POLYGON((0 0,10 0,10 10,0 0),(1 1,2 1,2 2,1 1))", "<Polygon xmlns=\"http://www.opengis.net/gml\"><exterior><LinearRing><posList>0 0 10 0 10 10 0 0</posList></LinearRing></exterior><interior><LinearRing><posList>1 1 2 1 2 2 1 1</posList></LinearRing></interior></Polygon>")]
    [DataRow("geometry", "MULTILINESTRING((0 0,1 1),EMPTY)", "<MultiCurve xmlns=\"http://www.opengis.net/gml\"><curveMembers><LineString><posList>0 0 1 1</posList></LineString><LineString><posList/></LineString></curveMembers></MultiCurve>")]
    [DataRow("geometry", "CURVEPOLYGON(COMPOUNDCURVE((0 0,2 0),CIRCULARSTRING(2 0,1 1,0 0)))", "<PolygonPatch xmlns=\"http://www.opengis.net/gml\"><exterior><Ring><curveMember><CompositeCurve><curveMember><LineString><posList>0 0 2 0</posList></LineString></curveMember><curveMember><ArcString><posList>2 0 1 1 0 0</posList></ArcString></curveMember></CompositeCurve></curveMember></Ring></exterior></PolygonPatch>")]
    [DataRow("geometry", "POINT(-0.000001 123456789012345678)", "<Point xmlns=\"http://www.opengis.net/gml\"><pos>-1E-06 1.2345678901234568E+17</pos></Point>")]
    [DataRow("geography", "FULLGLOBE", "<FullGlobe xmlns=\"http://schemas.microsoft.com/sqlserver/2011/geography\"/>")]
    public void AsGml_Writes_Reals_Markup_And_Reads_Back(string type, string wkt, string expected)
    {
        var srid = type == "geography" ? 4326 : 0;
        var sim = new Simulation();
        AreEqual(expected, sim.ExecuteScalar($"select cast({type}::Parse('{wkt}').AsGml() as nvarchar(max))"));
        AreEqual(sim.ExecuteScalar($"select {type}::Parse('{wkt}').ToString()"), sim.ExecuteScalar($"select {type}::GeomFromGml({type}::Parse('{wkt}').AsGml(), {srid}).ToString()"));
    }

    [TestMethod]
    [DataRow($"<MultiPoint {Gml}><pointMember><Point><pos>1 2</pos></Point></pointMember><pointMembers><Point><pos>3 4</pos></Point></pointMembers></MultiPoint>", "MULTIPOINT ((1 2), (3 4))")]
    [DataRow($"<LineString {Gml}><pos>1 2</pos><pos>3 4</pos></LineString>", "LINESTRING (1 2, 3 4)")]
    [DataRow("<gml:Point xmlns:gml=\"http://www.opengis.net/gml\"><gml:pos>1e3 -2.5</gml:pos></gml:Point>", "POINT (1000 -2.5)")]
    [DataRow($"<Arc {Gml}><posList>0 0 1 1 2 0</posList></Arc>", "CIRCULARSTRING (0 0, 1 1, 2 0)")]
    [DataRow($"<MultiPoint {Gml}><pointMembers><Point/></pointMembers></MultiPoint>", "MULTIPOINT (EMPTY)")]
    public void GeomFromGml_Reads_The_Other_Spellings(string gml, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select geometry::GeomFromGml('{gml}', 0).ToString()"));

    [TestMethod]
    [DataRow("<Point><pos>1 2</pos></Point>", "24129: The given XML instance is not valid because the top-level tag is Point.")]
    [DataRow($"<Point {Gml}><pos srsDimension=\"3\">1 2 3</pos></Point>", "24130: The given XML instance contains attributes.")]
    [DataRow($"<Point {Gml}><pos>1 2 3</pos></Point>", "24131: The given pos element provides 3 coordinates.")]
    [DataRow($"<LineString {Gml}><posList>1 2 3</posList></LineString>", "24132: The posList element provided has 3 coordinates.")]
    [DataRow($"<Polygon {Gml}><exterior><LinearRing><posList/></LinearRing></exterior></Polygon>", "24143: The posList element provided is empty.")]
    [DataRow($"<Arc {Gml}><posList>0 0 1 1</posList></Arc>", "24216: The arc must contain exactly 3 points.")]
    [DataRow($"<Point {Gml}><pos>1 2</pos></Point><Point {Gml}><pos>1 2</pos></Point>", "24128: The Geography Markup Language (GML) input must have a single top-level tag.")]
    [DataRow($"<Point {Gml}><pos>1 2</pos><pos>3 4</pos></Point>", "System.Xml.XmlException: 'Element' is an invalid XmlNodeType.")]
    [DataRow($"<Polygon {Gml}><exterior><Ring/></exterior></Polygon>", "System.Xml.XmlException: Element 'LinearRing' with namespace name 'http://www.opengis.net/gml' was not found.")]
    [DataRow($"<Point {Gml}><pos>1,2</pos></Point>", "Content cannot be converted to the type System.Double[]. ---> System.FormatException: Input string was not in a correct format.")]
    [DataRow($"<Point {Gml}><pos>INF 2</pos></Point>", "24126: Point coordinates cannot be infinite or not a number (NaN).")]
    public void GeomFromGml_Refuses_What_Real_Refuses(string gml, string fragment)
        => Assert.Contains(fragment, new Simulation().AssertSqlError($"select geometry::GeomFromGml('{gml}', 0)", 6522).Message);

    [TestMethod]
    public void STAsGML_Is_No_Member()
        => _ = new Simulation().AssertSqlError("select geometry::Parse('POINT(1 2)').STAsGML()", 6506);

    [TestMethod]
    public void The_Reference_Systems_View_Lists_Reals_Rows()
    {
        var sim = new Simulation();
        AreEqual(393, sim.ExecuteScalar("select count(*) from sys.spatial_reference_systems"));
        AreEqual("radian|1", sim.ExecuteScalar("select concat(unit_of_measure, '|', unit_conversion_factor) from sys.spatial_reference_systems where spatial_reference_id = 104001"));
        Assert.StartsWith("GEOGCS[\"WGS 84\"", (string)sim.ExecuteScalar("select well_known_text from sys.spatial_reference_systems where spatial_reference_id = 4326")!);
    }

    [TestMethod]
    // Each SRID measures on its own ellipsoid and in its own unit: SRID 104001
    // is the unit sphere in radians, 4157 Clarke 1858 in Clarke's feet.
    [DataRow(104001, 0.24619691677893202, 3.046096848555005E-4)]
    [DataRow(4267, 1565068.8724930042, 12308123852.422932)]
    [DataRow(4157, 5134815.001576386, 132487345353.09479)]
    [DataRow(4269, 1565109.0998773035, 12308776255.868843)]
    public void Geography_Measures_Follow_The_Srid(int srid, double length, double area)
    {
        var sim = new Simulation();
        var measuredLength = (double)sim.ExecuteScalar($"select geography::STGeomFromText('LINESTRING(0 0, 10 10)', {srid}).STLength()")!;
        var measuredArea = (double)sim.ExecuteScalar($"select geography::STGeomFromText('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))', {srid}).STArea()")!;
        AreEqual(length, measuredLength, length * 1e-8);
        AreEqual(area, measuredArea, area * 1e-8);
        AreEqual(4 * Math.PI, (double)sim.ExecuteScalar("select geography::STGeomFromText('FULLGLOBE', 104001).STArea()")!, 1e-12);
    }

    [TestMethod]
    // A lobe the exterior ring closes off is a hole when it winds against the
    // main lobe, and the ring is invalid when it winds the same way.
    [DataRow("POLYGON((5 0, 10 0, 10 10, 0 10, 0 0, 5 0, 3 3, 7 3, 5 0))", true)]
    [DataRow("POLYGON((5 0, 10 0, 10 10, 0 10, 0 0, 5 0, 7 3, 3 3, 5 0))", false)]
    [DataRow("POLYGON((0 0, 10 0, 10 10, 0 10, 0 0, 3 3, 3 7, 7 7, 7 3, 0 0))", true)]
    [DataRow("POLYGON((5 0, 10 0, 10 10, 0 10, 0 0, 5 0, 3 -3, 7 -3, 5 0))", false)]
    public void A_Planar_Ring_Splits_Into_Lobes(string wkt, bool valid)
        => AreEqual(valid, new Simulation().ExecuteScalar($"select geometry::STGeomFromText('{wkt}', 0).STIsValid()"));

    [TestMethod]
    [DataRow("MULTIPOINT (1 2, EMPTY)", "MULTIPOINT ((1 2), EMPTY)")]
    [DataRow("MULTIPOINT (EMPTY, 1 2)", "MULTIPOINT (EMPTY, (1 2))")]
    [DataRow("MULTILINESTRING ((0 0, 1 1), EMPTY)", "MULTILINESTRING ((0 0, 1 1), EMPTY)")]
    [DataRow("MULTIPOLYGON (EMPTY, ((0 0,1 0,1 1,0 0)))", "MULTIPOLYGON (EMPTY, ((0 0, 1 0, 1 1, 0 0)))")]
    public void Multi_Members_May_Be_Empty(string wkt, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select geometry::Parse('{wkt}').ToString()"));

    [TestMethod]
    [DataRow("geometry", "0")]
    [DataRow("geography", "4326")]
    public void GmlWithNoElement_IsTheReadersFormatError(string type, string srid)
    {
        var ex = new Simulation().AssertSqlError($"select {type}::GeomFromGml(cast('' as xml), {srid})", 6522);
        StartsWith($"A .NET Framework error occurred during execution of user-defined routine or aggregate \"{type}\": \r\nSystem.FormatException: One of the identified items was in an invalid format.", ex.Errors[0].Message);
    }
}
