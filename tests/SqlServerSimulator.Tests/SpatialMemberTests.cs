using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The representative-point and simplicity members: <c>geometry</c>'s
/// <c>STCentroid</c> / <c>STPointOnSurface</c> / <c>STIsSimple</c>,
/// <c>geography</c>'s <c>EnvelopeAngle</c> / <c>EnvelopeCenter</c>, and the
/// spatial <i>column</i>'s property form (<c>Location.Lat</c>).
/// </summary>
/// <remarks>
/// Every pinned value is SQL Server 2025's own, probed 2026-08-02. Real's own
/// answers for the areal members carry float noise a few ulps wide
/// (<c>POINT (2.0000000000000071 1.0000000000000036)</c> for a centre the exact
/// arithmetic puts at <c>(2, 1)</c>), so those cases assert against the exact
/// value with a tolerance; the cases real answers exactly are pinned exactly.
/// </remarks>
[TestClass]
public sealed class SpatialMemberTests
{
    private static object? Eval(string expression) => new Simulation().ExecuteScalar($"select {expression}");

    private static string? Wkt(string expression) => (string?)Eval($"({expression}).STAsText()");

    /// <summary>Asserts a computed point against real's answer, allowing for the drift real's own arithmetic shows.</summary>
    private static void AssertPoint(double x, double y, string? wkt)
    {
        IsNotNull(wkt);
        var body = wkt[(wkt.IndexOf('(') + 1)..wkt.IndexOf(')')].Split(' ');
        AreEqual(x, double.Parse(body[0], System.Globalization.CultureInfo.InvariantCulture), 1e-9);
        AreEqual(y, double.Parse(body[1], System.Globalization.CultureInfo.InvariantCulture), 1e-9);
    }

    /// <summary>Asserts the member reads NULL, which the client surfaces as <see cref="DBNull"/>.</summary>
    private static void AssertNull(string expression) => AreEqual(DBNull.Value, Eval(expression));

    private static SimulatedSqlException Fails(string expression, int number) =>
        new Simulation().AssertSqlError($"select {expression}", number);

    /// <summary>A spatial failure reaches the client as Msg 6522 carrying the 24nnn code in its text.</summary>
    private static void AssertSpatialFailure(int code, string expression) =>
        Contains($"{code}: ", Fails(expression, 6522).Message);

    /// <summary>Asserts a bounding-cap angle, which real reports to within a bit or two of the exact value.</summary>
    private static void AssertAngle(double expected, string expression) =>
        AreEqual(expected, (double)Eval(expression)!, 1e-12);

    private static string Geometry(string wkt) => $"geometry::Parse('{wkt}')";

    private static string Geography(string wkt) => $"geography::Parse('{wkt}')";

    [TestMethod]
    public void CentroidIsTheAreaWeightedCentreOfAPolygon()
    {
        AssertPoint(2, 1, Wkt($"{Geometry("POLYGON((0 0, 4 0, 4 2, 0 2, 0 0))")}.STCentroid()"));
        AssertPoint(0.5, 0.5, Wkt($"{Geometry("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")}.STCentroid()"));
        AssertPoint(4.0 / 3, 4.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 4 0, 0 4, 0 0))")}.STCentroid()"));
        // A hole subtracts its own moment: (5·100 − 3·4) / 96.
        AssertPoint(
            488.0 / 96,
            488.0 / 96,
            Wkt($"{Geometry("POLYGON((0 0,10 0,10 10,0 10,0 0),(2 2,4 2,4 4,2 4,2 2))")}.STCentroid()"));
    }

    [TestMethod]
    public void CentroidSumsAMultiPolygonsMembers() =>
        AssertPoint(
            5.5,
            0.5,
            Wkt($"{Geometry("MULTIPOLYGON(((0 0,1 0,1 1,0 1,0 0)),((10 0,11 0,11 1,10 1,10 0)))")}.STCentroid()"));

    /// <summary>
    /// Real answers NULL for every kind but Polygon and MultiPolygon — a
    /// <c>GEOMETRYCOLLECTION</c> whose every member is a polygon included.
    /// </summary>
    [TestMethod]
    public void CentroidIsNullForEveryNonPolygonKind()
    {
        AssertNull($"{Geometry("POINT(3 4)")}.STCentroid()");
        AssertNull($"{Geometry("LINESTRING(0 0, 4 0)")}.STCentroid()");
        AssertNull($"{Geometry("MULTIPOINT((0 0),(4 0))")}.STCentroid()");
        AssertNull($"{Geometry("MULTILINESTRING((0 0,2 0),(0 5,2 5))")}.STCentroid()");
        AssertNull($"{Geometry("POLYGON EMPTY")}.STCentroid()");
        AssertNull($"{Geometry("GEOMETRYCOLLECTION(POINT(0 0), POLYGON((0 0,1 0,1 1,0 1,0 0)))")}.STCentroid()");
        AssertNull(
            $"{Geometry("GEOMETRYCOLLECTION(POLYGON((0 0,1 0,1 1,0 1,0 0)), POLYGON((10 0,11 0,11 1,10 1,10 0)))")}.STCentroid()");
    }

    /// <summary>
    /// Real's pick for a polygon with no interior ring is the centroid of the
    /// ear at the ring's topmost — then rightmost — vertex, which is what makes
    /// the L-shape answer from its upper arm rather than its wider lower one.
    /// </summary>
    [TestMethod]
    public void PointOnSurfaceTakesTheEarAtTheTopmostVertex()
    {
        AssertPoint(2.0 / 3, 2.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")}.STPointOnSurface()"));
        AssertPoint(8.0 / 3, 4.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 4 0, 4 2, 0 2, 0 0))")}.STPointOnSurface()"));
        AssertPoint(4.0 / 3, 4.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 4 0, 0 4, 0 0))")}.STPointOnSurface()"));
        AssertPoint(2.0 / 3, 3, Wkt($"{Geometry("POLYGON((0 0, 4 0, 4 1, 1 1, 1 4, 0 4, 0 0))")}.STPointOnSurface()"));
        AssertPoint(3, 5.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 4 0, 5 3, 0 2, 0 0))")}.STPointOnSurface()"));
        AssertPoint(2, 14.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 4 0, 4 4, 2 6, 0 4, 0 0))")}.STPointOnSurface()"));
        AssertPoint(1, 7.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 2 0, 2 2, 1 3, 0 2, 0 0))")}.STPointOnSurface()"));
        AssertPoint(10.0 / 3, 11.0 / 3, Wkt($"{Geometry("POLYGON((0 0, 4 0, 4 10, 2 1, 0 10, 0 0))")}.STPointOnSurface()"));
    }

    /// <summary>The pick is geometric, so rotating the ring or writing it the other way round doesn't move it.</summary>
    [TestMethod]
    public void PointOnSurfaceIgnoresRingRotationAndDirection()
    {
        foreach (var ring in new[]
        {
            "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))",
            "POLYGON((1 0, 1 1, 0 1, 0 0, 1 0))",
            "POLYGON((1 1, 0 1, 0 0, 1 0, 1 1))",
            "POLYGON((0 1, 0 0, 1 0, 1 1, 0 1))",
            "POLYGON((0 0, 0 1, 1 1, 1 0, 0 0))",
        })
        {
            AssertPoint(2.0 / 3, 2.0 / 3, Wkt($"{Geometry(ring)}.STPointOnSurface()"));
        }
    }

    /// <summary>
    /// A <c>MultiPolygon</c> answers from the member reaching furthest right,
    /// then furthest up — order-independently, so writing the members the other
    /// way round doesn't move the answer.
    /// </summary>
    [TestMethod]
    public void PointOnSurfacePicksTheFurthestRightMember()
    {
        AssertPoint(
            32.0 / 3,
            2.0 / 3,
            Wkt($"{Geometry("MULTIPOLYGON(((0 0,5 0,5 5,0 5,0 0)),((10 0,11 0,11 1,10 1,10 0)))")}.STPointOnSurface()"));
        AssertPoint(
            40.0 / 3,
            10.0 / 3,
            Wkt($"{Geometry("MULTIPOLYGON(((0 0,1 0,1 1,0 1,0 0)),((10 0,15 0,15 5,10 5,10 0)))")}.STPointOnSurface()"));
        foreach (var written in new[]
        {
            "MULTIPOLYGON(((0 0,1 0,1 1,0 1,0 0)),((0 10,1 10,1 11,0 11,0 10)))",
            "MULTIPOLYGON(((0 10,1 10,1 11,0 11,0 10)),((0 0,1 0,1 1,0 1,0 0)))",
        })
        {
            AssertPoint(2.0 / 3, 32.0 / 3, Wkt($"{Geometry(written)}.STPointOnSurface()"));
        }
    }

    /// <summary>
    /// A line reports the midpoint of its <i>first segment</i> rather than its
    /// halfway point, a MultiPoint / MultiLineString its first member's answer,
    /// and a collection holding no polygon its <i>last</i> member's.
    /// </summary>
    [TestMethod]
    public void PointOnSurfaceOfANonArealInstance()
    {
        AreEqual("POINT (3 4)", Wkt($"{Geometry("POINT(3 4)")}.STPointOnSurface()"));
        AreEqual("POINT (2 0)", Wkt($"{Geometry("LINESTRING(0 0, 4 0)")}.STPointOnSurface()"));
        AreEqual("POINT (1 0)", Wkt($"{Geometry("LINESTRING(0 0, 2 0, 2 4)")}.STPointOnSurface()"));
        AreEqual("POINT (5 0)", Wkt($"{Geometry("LINESTRING(0 0, 10 0, 11 0)")}.STPointOnSurface()"));
        AreEqual("POINT (4 0)", Wkt($"{Geometry("MULTIPOINT((4 0),(0 0),(2 9))")}.STPointOnSurface()"));
        AreEqual("POINT (1 5)", Wkt($"{Geometry("MULTILINESTRING((0 5,2 5),(0 0,2 0))")}.STPointOnSurface()"));
        AreEqual("POINT (1 0)", Wkt($"{Geometry("MULTILINESTRING((0 0,2 0,2 4),(0 5,2 5))")}.STPointOnSurface()"));
        AreEqual("POINT (5 5)", Wkt($"{Geometry("GEOMETRYCOLLECTION(LINESTRING(0 0,1 1), POINT(5 5))")}.STPointOnSurface()"));
        AreEqual("POINT (0.5 0.5)", Wkt($"{Geometry("GEOMETRYCOLLECTION(POINT(5 5), LINESTRING(0 0,1 1))")}.STPointOnSurface()"));
        AssertNull($"{Geometry("POLYGON EMPTY")}.STPointOnSurface()");
    }

    /// <summary>A collection holding any polygon answers from the polygons, wherever in the member list they sit.</summary>
    [TestMethod]
    public void PointOnSurfaceOfACollectionPrefersItsPolygons()
    {
        AssertPoint(
            2.0 / 3,
            2.0 / 3,
            Wkt($"{Geometry("GEOMETRYCOLLECTION(POINT(0 0), POLYGON((0 0,1 0,1 1,0 1,0 0)))")}.STPointOnSurface()"));
        AssertPoint(
            2.0 / 3,
            10.0 / 3,
            Wkt($"{Geometry("GEOMETRYCOLLECTION(POLYGON((0 0,1 0,1 5,0 5,0 0)), POINT(99 99))")}.STPointOnSurface()"));
        AssertPoint(
            32.0 / 3,
            2.0 / 3,
            Wkt($"{Geometry("GEOMETRYCOLLECTION(POLYGON((0 0,1 0,1 1,0 1,0 0)), POLYGON((10 0,11 0,11 1,10 1,10 0)))")}.STPointOnSurface()"));
    }

    /// <summary>
    /// Where the ear rule doesn't apply — a polygon with a hole, or one whose
    /// topmost ear runs outside the shape — the answer is the simulator's own
    /// scanline point, which keeps the guarantee real's own answer carries.
    /// </summary>
    [TestMethod]
    public void PointOnSurfaceAlwaysLandsOnTheInstance()
    {
        foreach (var polygon in new[]
        {
            "POLYGON((0 0,10 0,10 10,0 10,0 0),(2 2,4 2,4 4,2 4,2 2))",
            "POLYGON((0 0, 10 0, 10 10, 0 10, 0 9, 9 9, 9 1, 0 1, 0 0))",
        })
        {
            IsTrue((bool)Eval($"{Geometry(polygon)}.STContains({Geometry(polygon)}.STPointOnSurface())")!);
        }
    }

    /// <summary>Both members are geometry-only, and both refuse an invalid instance.</summary>
    [TestMethod]
    public void CentroidAndPointOnSurfaceAreGeometryOnlyAndGated()
    {
        _ = Fails($"{Geography("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")}.STCentroid()", 6506);
        _ = Fails($"{Geography("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")}.STPointOnSurface()", 6506);
        AssertSpatialFailure(24144, $"{Geometry("POLYGON((0 0, 4 4, 4 0, 0 4, 0 0))")}.STCentroid()");
        AssertSpatialFailure(24144, $"{Geometry("POLYGON((0 0, 4 4, 4 0, 0 4, 0 0))")}.STPointOnSurface()");
        AssertNull("cast(null as geometry).STCentroid()");
        AssertNull("cast(null as geometry).STPointOnSurface()");
    }

    [TestMethod]
    public void CentroidAndPointOnSurfaceKeepTheirSrid()
    {
        AreEqual(3857, Eval($"geometry::STGeomFromText('POLYGON((0 0,4 0,4 2,0 2,0 0))', 3857).STCentroid().STSrid"));
        AreEqual(3857, Eval($"geometry::STGeomFromText('POLYGON((0 0,4 0,4 2,0 2,0 0))', 3857).STPointOnSurface().STSrid"));
    }

    /// <summary>
    /// The bounding cap is the normalized <b>sum of the instance's points as
    /// unit vectors</b>, with the greatest angle to any of them as the radius.
    /// The 1° square's centre sitting north of latitude 0.5 is what identifies
    /// the model — the coordinate midpoint would put it at 0.5 exactly.
    /// </summary>
    [TestMethod]
    public void EnvelopeCentreIsTheVectorMeanOfThePoints()
    {
        AreEqual("POINT (-122 47)", Wkt($"{Geography("POINT(-122 47)")}.EnvelopeCenter()"));
        AreEqual(0.0, Eval($"{Geography("POINT(-122 47)")}.EnvelopeAngle()"));

        AssertPoint(0.5, 0.50001903822621641, Wkt($"{Geography("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")}.EnvelopeCenter()"));
        AssertAngle(0.70711575561904183, $"{Geography("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")}.EnvelopeAngle()");

        AssertPoint(5, 5.0190018174896434, Wkt($"{Geography("POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))")}.EnvelopeCenter()"));
        AssertAngle(7.0799977988259633, $"{Geography("POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))")}.EnvelopeAngle()");

        AssertPoint(5, 0, Wkt($"{Geography("LINESTRING(0 0, 10 0)")}.EnvelopeCenter()"));
        AssertAngle(5, $"{Geography("LINESTRING(0 0, 10 0)")}.EnvelopeAngle()");
        AssertPoint(5, 0, Wkt($"{Geography("MULTIPOINT((0 0),(10 0))")}.EnvelopeCenter()"));

        AssertPoint(3.6635154357698974, 0, Wkt($"{Geography("LINESTRING(0 0, 1 0, 10 0)")}.EnvelopeCenter()"));
        AssertAngle(6.3364845642301013, $"{Geography("LINESTRING(0 0, 1 0, 10 0)")}.EnvelopeAngle()");
    }

    /// <summary>
    /// A closed figure's repeated last point takes no part in the sum, while an
    /// ordinary repeat does — which is why the retraced triangle centres on
    /// three points and the doubled line vertex on three.
    /// </summary>
    [TestMethod]
    public void EnvelopeCentreDropsOnlyTheClosingRepeat()
    {
        AssertPoint(6.6534418593972156, 3.340864229009163, Wkt($"{Geography("LINESTRING(0 0, 10 0, 10 10, 0 0)")}.EnvelopeCenter()"));
        AssertAngle(7.4417360806296768, $"{Geography("LINESTRING(0 0, 10 0, 10 10, 0 0)")}.EnvelopeAngle()");
        AssertPoint(3.3295630553023212, 0, Wkt($"{Geography("LINESTRING(0 0, 0 0, 10 0)")}.EnvelopeCenter()"));
        AssertAngle(6.6704369446976797, $"{Geography("LINESTRING(0 0, 0 0, 10 0)")}.EnvelopeAngle()");
    }

    /// <summary>
    /// An instance no cap below a hemisphere holds reports the angle as 180,
    /// and one whose summed direction cancels centres on the north pole.
    /// </summary>
    [TestMethod]
    public void EnvelopeAngleReportsAHemisphereAs180()
    {
        AssertAngle(89.5, $"{Geography("MULTIPOINT((0 0),(179 0))")}.EnvelopeAngle()");
        AssertPoint(89.5, 0, Wkt($"{Geography("MULTIPOINT((0 0),(179 0))")}.EnvelopeCenter()"));
        AssertAngle(89.5, $"{Geography("MULTIPOINT((0 0),(181 0))")}.EnvelopeAngle()");
        AssertPoint(-89.5, 0, Wkt($"{Geography("MULTIPOINT((0 0),(181 0))")}.EnvelopeCenter()"));

        AreEqual(180.0, Eval($"{Geography("MULTIPOINT((0 0),(90 0),(180 0))")}.EnvelopeAngle()"));
        AreEqual("POINT (90 0)", Wkt($"{Geography("MULTIPOINT((0 0),(90 0),(180 0))")}.EnvelopeCenter()"));
        AreEqual(180.0, Eval($"{Geography("MULTIPOINT((0 0),(180 0))")}.EnvelopeAngle()"));
        AreEqual("POINT (0 90)", Wkt($"{Geography("MULTIPOINT((0 0),(180 0))")}.EnvelopeCenter()"));
        AreEqual("POINT (0 90)", Wkt($"{Geography("MULTIPOINT((0 0),(120 0),(240 0))")}.EnvelopeCenter()"));
        AreEqual("POINT (0 90)", Wkt($"{Geography("MULTIPOINT((0 0),(179.9999999 0))")}.EnvelopeCenter()"));
        AssertPoint(89.999999635535289, 0, Wkt($"{Geography("MULTIPOINT((0 0),(179.999999 0))")}.EnvelopeCenter()"));
    }

    [TestMethod]
    public void EnvelopeMembersAreGeographyOnly()
    {
        _ = Fails($"{Geometry("POINT(1 2)")}.EnvelopeAngle()", 6506);
        _ = Fails($"{Geometry("POINT(1 2)")}.EnvelopeCenter()", 6506);
        AssertNull($"{Geography("POINT EMPTY")}.EnvelopeAngle()");
        AssertNull($"{Geography("POINT EMPTY")}.EnvelopeCenter()");
        AreEqual(4326, Eval($"{Geography("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")}.EnvelopeCenter().STSrid"));
        AssertSpatialFailure(24144, $"{Geography("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0), (0 0, 1 0, 1 1, 0 1, 0 0))")}.EnvelopeAngle()");
    }

    /// <summary>
    /// A curve is simple when it meets itself nowhere but at consecutive
    /// segments' shared vertex — a closed figure's ends counting as
    /// consecutive, so a ring written as a LINESTRING is simple.
    /// </summary>
    [TestMethod]
    public void SimplicityOfACurve()
    {
        IsFalse((bool)Eval($"{Geometry("LINESTRING(0 0, 2 2, 2 0, 0 2)")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("LINESTRING(0 0, 2 0, 2 2, 0 2, 0 0)")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("LINESTRING(0 0, 2 0, 2 2, 1 2, 1 0)")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("LINESTRING(0 0, 2 0, 2 2, 0 2, 0 0, 1 -1)")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("LINESTRING(0 0, 4 4)")}.STIsSimple()")!);
    }

    /// <summary>
    /// Two figures of one instance may meet only at a point on both curves'
    /// boundaries. A ring is closed and so has none, which is what makes a
    /// point-touching MultiPolygon and a hole meeting its shell both valid and
    /// not simple.
    /// </summary>
    [TestMethod]
    public void SimplicityAcrossFigures()
    {
        IsFalse((bool)Eval($"{Geometry("MULTILINESTRING((0 0,2 2),(0 2,2 0))")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("MULTILINESTRING((0 0,2 0),(2 0,4 0))")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("MULTILINESTRING((0 0,4 0),(2 0,2 2))")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("POLYGON((0 0,4 0,4 4,0 4,0 0))")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("POLYGON((0 0,10 0,10 10,0 10,0 0),(2 2,4 2,4 4,2 4,2 2))")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("POLYGON((0 0,10 0,10 10,0 10,0 0),(0 0,4 2,2 4,0 0))")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("MULTIPOLYGON(((0 0,4 0,4 4,0 4,0 0)),((10 0,14 0,14 4,10 4,10 0)))")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("MULTIPOLYGON(((0 0,4 0,4 4,0 4,0 0)),((4 4,8 4,8 8,4 8,4 4)))")}.STIsSimple()")!);
    }

    /// <summary>
    /// Points must be distinct, an empty instance is simple, and a
    /// <c>GEOMETRYCOLLECTION</c>'s members are judged one at a time — real
    /// never compares one member against another, so two crossing lines are
    /// simple as a collection and not as a MULTILINESTRING.
    /// </summary>
    [TestMethod]
    public void SimplicityOfPointsAndCollections()
    {
        IsTrue((bool)Eval($"{Geometry("MULTIPOINT((0 0),(1 1))")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("MULTIPOINT((0 0),(0 0))")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("POINT EMPTY")}.STIsSimple()")!);
        IsTrue((bool)Eval($"{Geometry("GEOMETRYCOLLECTION(LINESTRING(0 0,2 2),LINESTRING(0 2,2 0))")}.STIsSimple()")!);
        IsTrue((bool)Eval(
            $"{Geometry("GEOMETRYCOLLECTION(POLYGON((0 0,4 0,4 4,0 4,0 0)),POLYGON((4 4,8 4,8 8,4 8,4 4)))")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("GEOMETRYCOLLECTION(LINESTRING(0 0,2 2,2 0,0 2))")}.STIsSimple()")!);
        IsFalse((bool)Eval($"{Geometry("GEOMETRYCOLLECTION(MULTIPOINT((0 0),(0 0)))")}.STIsSimple()")!);
    }

    [TestMethod]
    public void SimplicityIsGeometryOnlyAndGated()
    {
        _ = Fails($"{Geography("LINESTRING(0 0, 2 2)")}.STIsSimple()", 6506);
        AssertSpatialFailure(24144, $"{Geometry("POLYGON((0 0, 4 4, 4 0, 0 4, 0 0))")}.STIsSimple()");
        AssertNull("cast(null as geometry).STIsSimple()");
    }

    private static Simulation WithSpatialColumn(string columns = "id int, Location geography")
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            $"create table t ({columns})",
            "insert into t values (1, geography::Point(47.6, -122.3, 4326))");
        return simulation;
    }

    /// <summary>
    /// A spatial <i>column</i>'s property form. Nothing in the syntax separates
    /// <c>Location.Lat</c> from an <c>alias.column</c> reference, so the scope
    /// settles it — the two-part form off the column, the three-part form
    /// through the source's own qualifier.
    /// </summary>
    [TestMethod]
    public void SpatialColumnCarriesThePropertyForm()
    {
        var simulation = WithSpatialColumn();
        AreEqual(47.6, simulation.ExecuteScalar("select Location.Lat from t"));
        AreEqual(-122.3, simulation.ExecuteScalar("select Location.Long from t"));
        AreEqual(4326, simulation.ExecuteScalar("select Location.STSrid from t"));
        AreEqual(48.6, simulation.ExecuteScalar("select Location.Lat + 1 from t"));
        AreEqual(47.6, simulation.ExecuteScalar("select q.Location.Lat from t as q"));
        AreEqual(47.6, simulation.ExecuteScalar("select t.Location.Lat from t"));
        AreEqual(1, simulation.ExecuteScalar("select id from t where Location.Lat > 40"));
        AreEqual(47.6, simulation.ExecuteScalar("select Location.Lat from t order by Location.Long"));
    }

    /// <summary>
    /// Real reports Msg 326 where both readings bind — a source aliased like
    /// the spatial column, carrying a column named like the member. With no
    /// such column the property reading wins outright.
    /// </summary>
    [TestMethod]
    public void SpatialPropertyFormYieldsToAnAmbiguousColumn()
    {
        var ambiguous = new Simulation();
        ambiguous.ExecuteBatches(
            "create table t (Lat int, Location geography)",
            "insert into t values (7, geography::Point(47.6, -122.3, 4326))");
        ambiguous.AssertSqlError(
            "select Location.Lat from t as Location",
            326,
            "Multi-part identifier 'Location.Lat' is ambiguous. Both columns 'Location' and 'Location.Lat' exist.");

        AreEqual(47.6, WithSpatialColumn().ExecuteScalar("select Location.Lat from t as Location"));
    }

    /// <summary>
    /// Once the qualifier resolves to a spatial column, an unknown leaf is real's
    /// missing-member error rather than a column failure, whichever form it was
    /// written in; a non-spatial qualifier and the four-part spelling real
    /// refuses both stay column references.
    /// </summary>
    /// <remarks>
    /// The last two report Msg 207 where real reports Msg 4104 for any
    /// unbindable multi-part name — a general column-resolution difference that
    /// has nothing to do with the spatial reading.
    /// </remarks>
    [TestMethod]
    public void SpatialPropertyFormReportsRealsErrors()
    {
        var simulation = WithSpatialColumn();
        _ = simulation.AssertSqlError("select Location.Bogus from t", 6592);
        _ = simulation.AssertSqlError("select Location.Lat() from t", 6506);
        _ = simulation.AssertSqlError("select id.Lat from t", 207);
        _ = simulation.AssertSqlError("select dbo.t.Location.Lat from t", 207);
    }
}
