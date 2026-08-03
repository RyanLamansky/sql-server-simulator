using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>geography</c>'s topological surface: the six predicates real exposes
/// there, <c>STIsValid</c>, the Msg 24144 gate, and the Msg 24206 refusal of an
/// antipodal edge.
/// </summary>
/// <remarks>
/// Every answer pinned here is SQL Server 2025's own, harvested 2026-08-02 by
/// driving two shape squares — 2,500 and 1,296 ordered pairs — plus 67 validity
/// cases through the reference and diffing them against the simulator; the
/// residual disagreements are recorded under Divergences in
/// <c>docs/claude/spatial.md</c>.
/// </remarks>
[TestClass]
public sealed class SpatialGeographyPredicateTests
{
    private static object? Eval(string expression) => new Simulation().ExecuteScalar($"select {expression}");

    /// <summary>Evaluates a <c>bit</c>-yielding expression that is known not to be NULL.</summary>
    private static bool Test(string expression) => (bool)Eval(expression)!;

    private static bool Predicate(string a, string predicate, string b) =>
        Test($"geography::Parse('{a}').{predicate}(geography::Parse('{b}'))");

    private static bool IsValid(string wkt) => Test($"geography::Parse('{wkt}').STIsValid()");

    /// <summary>
    /// A round-earth edge is a great elliptic arc, so the arc from (0,0) to
    /// (2,2) does not pass through (1,1) — the two lines are equal as
    /// <c>geometry</c> and not as <c>geography</c>, which is the whole reason
    /// the planar engine can't answer here.
    /// </summary>
    [TestMethod]
    public void ArcDoesNotFollowTheChord()
    {
        IsFalse(Predicate("LINESTRING(0 0, 2 2)", "STEquals", "LINESTRING(0 0, 1 1, 2 2)"));
        IsTrue(Predicate("LINESTRING(0 0, 2 2)", "STIntersects", "LINESTRING(0 0, 1 1, 2 2)"));
        IsFalse(Predicate("LINESTRING(0 0, 2 2)", "STOverlaps", "LINESTRING(0 0, 1 1, 2 2)"));
        IsTrue(Test("geometry::Parse('LINESTRING(0 0, 2 2)').STEquals(geometry::Parse('LINESTRING(0 0, 1 1, 2 2)'))"));
    }

    /// <summary>
    /// A "horizontal" edge bulges poleward between its endpoints, so a point
    /// north of the latitude band is still inside the polygon. The top edge of
    /// a 4°-wide square at latitude 4 peaks near 4.0024°, which is where the
    /// answer turns over.
    /// </summary>
    [TestMethod]
    public void PolewardBulgeOfAnEdgeIsInsideThePolygon()
    {
        const string square = "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))";
        IsTrue(Predicate("POINT(2 4)", "STWithin", square));
        IsTrue(Predicate("POINT(2 4.002)", "STWithin", square));
        IsFalse(Predicate("POINT(2 4.003)", "STWithin", square));
        IsFalse(Predicate("POINT(2 4.01)", "STWithin", square));
    }

    /// <summary>
    /// Ring orientation is load-bearing: a geography ring puts its interior on
    /// the <b>left</b>, so the clockwise spelling of a square names everything
    /// else and contains the far side of the globe.
    /// </summary>
    [TestMethod]
    public void ClockwiseRingNamesTheComplement()
    {
        const string clockwise = "POLYGON((0 0, 0 1, 1 1, 1 0, 0 0))";
        IsFalse(Predicate(clockwise, "STContains", "POINT(0.5 0.5)"));
        IsTrue(Predicate(clockwise, "STContains", "POINT(50 50)"));
        IsTrue(Predicate(clockwise, "STIntersects", "POINT(50 50)"));
        IsFalse(Predicate(clockwise, "STIntersects", "POINT(0.5 0.5)"));
        // The counter-clockwise spelling of the same ring is the small square.
        IsTrue(Predicate("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))", "STContains", "POINT(0.5 0.5)"));
        IsFalse(Predicate("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))", "STContains", "POINT(50 50)"));
    }

    /// <summary>A hole subtracts, and a point in it is outside the polygon.</summary>
    [TestMethod]
    public void HoleIsOutsideThePolygon()
    {
        const string donut = "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 3, 3 3, 3 1, 1 1))";
        IsTrue(Predicate(donut, "STContains", "POINT(0.5 0.5)"));
        IsFalse(Predicate(donut, "STContains", "POINT(2 2)"));
        IsTrue(Predicate(donut, "STDisjoint", "POINT(2 2)"));
    }

    /// <summary>
    /// Two empty instances are equal and disjoint and intersect nothing —
    /// the same shape the planar engine reports.
    /// </summary>
    [TestMethod]
    public void EmptyInstances()
    {
        IsTrue(Predicate("POINT EMPTY", "STEquals", "POLYGON EMPTY"));
        IsTrue(Predicate("POINT EMPTY", "STDisjoint", "POLYGON EMPTY"));
        IsFalse(Predicate("POINT EMPTY", "STIntersects", "POLYGON EMPTY"));
        IsTrue(Predicate("POINT EMPTY", "STDisjoint", "POINT(0 0)"));
        IsFalse(Predicate("POINT EMPTY", "STContains", "POLYGON EMPTY"));
        IsFalse(Predicate("POINT EMPTY", "STWithin", "POLYGON EMPTY"));
        IsFalse(Predicate("POINT EMPTY", "STOverlaps", "POLYGON EMPTY"));
    }

    /// <summary>A polygon spanning the antimeridian behaves like any other.</summary>
    [TestMethod]
    public void AntimeridianSpanningPolygon()
    {
        const string band = "POLYGON((179 0, -179 0, -179 2, 179 2, 179 0))";
        IsTrue(Predicate(band, "STContains", "POINT(180 1)"));
        IsTrue(Predicate(band, "STContains", "POINT(-179.5 1)"));
        IsFalse(Predicate(band, "STContains", "POINT(178 1)"));
    }

    /// <summary>A ring encircling a pole contains it.</summary>
    [TestMethod]
    public void PolarCapContainsThePole()
    {
        const string cap = "POLYGON((0 80, 120 80, -120 80, 0 80))";
        IsTrue(Predicate(cap, "STContains", "POINT(0 90)"));
        IsTrue(Predicate(cap, "STContains", "POINT(45 85)"));
        IsFalse(Predicate(cap, "STContains", "POINT(0 70)"));
    }

    /// <summary>Two boxes sharing only a boundary stretch touch but do not overlap.</summary>
    [TestMethod]
    public void OverlapsNeedsSharedInterior()
    {
        IsTrue(Predicate("POLYGON((0 0, 2 0, 2 2, 0 2, 0 0))", "STOverlaps", "POLYGON((1 1, 3 1, 3 3, 1 3, 1 1))"));
        IsFalse(Predicate("POLYGON((0 0, 2 0, 2 2, 0 2, 0 0))", "STOverlaps", "POLYGON((5 5, 7 5, 7 7, 5 7, 5 5))"));
        // Different dimensions never overlap.
        IsFalse(Predicate("POINT(1 1)", "STOverlaps", "POLYGON((0 0, 2 0, 2 2, 0 2, 0 0))"));
    }

    /// <summary>Contains and within are each other's mirror, and a shape contains itself.</summary>
    [TestMethod]
    public void ContainsAndWithinMirror()
    {
        const string outer = "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))";
        const string inner = "POLYGON((1 1, 2 1, 2 2, 1 2, 1 1))";
        IsTrue(Predicate(outer, "STContains", inner));
        IsTrue(Predicate(inner, "STWithin", outer));
        IsFalse(Predicate(inner, "STContains", outer));
        IsTrue(Predicate(outer, "STEquals", outer));
        IsTrue(Predicate(outer, "STContains", outer));
        IsTrue(Predicate(outer, "STWithin", outer));
    }

    /// <summary>Operands in different spatial reference systems read NULL, and a NULL operand propagates.</summary>
    [TestMethod]
    public void MismatchedSridAndNullReadNull()
    {
        AreEqual(DBNull.Value, Eval("geography::STGeomFromText('POINT(0 0)', 4326).STIntersects(geography::STGeomFromText('POINT(0 0)', 4269))"));
        AreEqual(DBNull.Value, Eval("geography::Parse('POINT(0 0)').STIntersects(cast(null as geography))"));
    }

    /// <summary>A non-spatial argument is read as well-known text, which is what real does.</summary>
    [TestMethod]
    public void StringArgumentIsReadAsWellKnownText() =>
        IsTrue(Test("geography::Parse('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))').STContains('POINT(2 2)')"));

    /// <summary>
    /// <c>STIsValid</c> answers on <c>geography</c>, and the round-earth rules
    /// differ from the planar ones: a figure that stops on the vertex it
    /// already sits on is valid here, and so is a "backtrack" whose second edge
    /// leaves the first arc.
    /// </summary>
    [TestMethod]
    [DataRow("POINT(0 0)", true)]
    [DataRow("MULTIPOINT((0 0), (0 0))", true)]
    [DataRow("LINESTRING(0 0, 2 2, 2 0, 0 2)", true)]
    [DataRow("LINESTRING(0 0, 2 0, 2 0, 4 0)", true)]
    [DataRow("LINESTRING(0 0, 2 0, 2 0)", true)]
    [DataRow("LINESTRING(0 0, 1 1, 1 1)", true)]
    [DataRow("LINESTRING(0 0, 0 0, 1 1)", true)]
    [DataRow("LINESTRING(0 0, 2 2, 1 1)", true)]
    [DataRow("LINESTRING(0 0, 1 1, 0.5 0.5)", true)]
    [DataRow("LINESTRING(0 0, 0 0)", false)]
    [DataRow("LINESTRING(0 0, 1 0, 0 0)", false)]
    [DataRow("LINESTRING(0 0, 4 0, 2 0)", false)]
    [DataRow("LINESTRING(0 0, 1 1, 2 2, 1 1)", false)]
    [DataRow("LINESTRING(0 0, 1 0, 2 0, 1 0)", false)]
    [DataRow("MULTILINESTRING((0 0, 2 0), (1 0, 3 0))", false)]
    [DataRow("MULTILINESTRING((0 0, 2 0), (2 0, 4 0))", true)]
    [DataRow("GEOMETRYCOLLECTION(LINESTRING(0 0, 2 0), LINESTRING(1 0, 3 0))", true)]
    public void LineValidity(string wkt, bool expected) => AreEqual(expected, IsValid(wkt));

    /// <summary>
    /// A polygon's rings must each bound area, stay simple, keep off each
    /// other, and — the round-earth rule the planar validator has no
    /// counterpart for — agree on which region they name.
    /// </summary>
    [TestMethod]
    [DataRow("POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))", false)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, 0 0))", true)]
    [DataRow("POLYGON((0 0, 2 0, 2 0, 0 0, 0 0))", false)]
    [DataRow("POLYGON((0 0, 1 0, 0 0, 0 0))", false)]
    [DataRow("POLYGON((0 0, 2 2, 4 0, 2 0, 0 0))", true)]
    [DataRow("POLYGON((0 0, 90 0, 180 0, 270 0, 0 0))", true)]
    [DataRow("POLYGON((0 -80, 120 -80, -120 -80, 0 -80))", true)]
    // Hole wound against its shell is valid; wound with it, the two disagree
    // about which region the polygon names.
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 3, 3 3, 3 1, 1 1))", true)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 3 1, 3 3, 1 3, 1 1))", false)]
    [DataRow("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0), (0.2 0.2, 0.8 0.2, 0.8 0.8, 0.2 0.8, 0.2 0.2))", false)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 2, 0 2, 1 1))", false)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (0 1, 1 2, 1 1, 0 1))", true)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 2, 2 2, 1 1))", true)]
    // A hole outside the shell, or running alongside it, or nested in a sibling.
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (5 5, 5 6, 6 6, 6 5, 5 5))", false)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (0 1, 0 3, 2 3, 2 1, 0 1))", false)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (0 0, 0 2, 2 2, 2 0, 0 0))", false)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 3, 3 3, 3 1, 1 1), (1 1, 1 3, 3 3, 3 1, 1 1))", false)]
    // Two holes may meet at a point but not along a stretch.
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 2, 2 2, 2 1, 1 1), (2 2, 2 3, 3 3, 3 2, 2 2))", true)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 2, 2 2, 2 1, 1 1), (1 2, 1 3, 2 3, 2 2, 1 2))", false)]
    public void PolygonValidity(string wkt, bool expected) => AreEqual(expected, IsValid(wkt));

    /// <summary>
    /// A ring that comes back to a vertex it already stood on is read as
    /// separate lobes, and the ordinary ring rules decide: a lobe nested inside
    /// the main one and wound the other way is a hole meeting its shell, which
    /// is valid, while a lobe beside it, or a nested one wound the same way, is
    /// not. Real accepts this on genuine coastline data — a WideWorldImporters
    /// border traces back to its own start vertex — and rejects every other
    /// arrangement of the same shape.
    /// </summary>
    [TestMethod]
    // A small lobe inside the square, wound against it.
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, 1 2, 2 1, 0 0))", true)]
    // The same lobe wound with the square.
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, 2 1, 1 2, 0 0))", false)]
    // Lobes side by side, either winding.
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, -1 1, -1 -1, 0 0))", false)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, -1 -1, -1 1, 0 0))", false)]
    [DataRow("POLYGON((0 0, 2 0, 2 2, 0 0, -2 0, -2 -2, 0 0))", false)]
    public void SelfTouchingRingSplitsIntoLobes(string wkt, bool expected) => AreEqual(expected, IsValid(wkt));

    /// <summary>Multi* members may meet at a point; sharing a stretch, overlapping or nesting is invalid.</summary>
    [TestMethod]
    [DataRow("MULTIPOLYGON(((0 0, 2 0, 2 2, 0 2, 0 0)), ((2 2, 4 2, 4 4, 2 4, 2 2)))", true)]
    [DataRow("MULTIPOLYGON(((0 0, 2 0, 2 2, 0 2, 0 0)), ((2 0, 4 0, 4 2, 2 2, 2 0)))", false)]
    [DataRow("MULTIPOLYGON(((0 0, 2 0, 2 2, 0 2, 0 0)), ((1 1, 3 1, 3 3, 1 3, 1 1)))", false)]
    [DataRow("MULTIPOLYGON(((0 0, 2 0, 2 2, 0 2, 0 0)), ((0.5 2, 1.5 2, 1.5 4, 0.5 4, 0.5 2)))", false)]
    [DataRow("GEOMETRYCOLLECTION(POINT(0 0), POLYGON((0 0, 1 1, 1 0, 0 1, 0 0)))", false)]
    [DataRow("GEOMETRYCOLLECTION(POLYGON((0 0, 2 0, 2 2, 0 2, 0 0)), POLYGON((1 1, 3 1, 3 3, 1 3, 1 1)))", true)]
    public void MultiMemberValidity(string wkt, bool expected) => AreEqual(expected, IsValid(wkt));

    /// <summary>
    /// An edge joining two antipodal points defines no arc, and real refuses
    /// the instance while <i>constructing</i> it rather than answering
    /// <c>STIsValid() = 0</c>.
    /// </summary>
    [TestMethod]
    [DataRow("LINESTRING(0 0, 180 0)")]
    [DataRow("LINESTRING(0 90, 0 -90)")]
    [DataRow("LINESTRING(10 45, -170 -45)")]
    public void AntipodalEdgeIsRefusedAtConstruction(string wkt)
    {
        var ex = new Simulation().AssertSqlError($"select geography::Parse('{wkt}').STIsValid()", 6522);
        Assert.Contains(
            "Microsoft.SqlServer.Types.GLArgumentException: 24206: The specified input cannot be accepted because it "
            + "contains an edge with antipodal points. For information about using spatial methods with FullGlobe "
            + "objects, see Types of Spatial Data in SQL Server Books Online.",
            ex.Message);
    }

    /// <summary>
    /// The refusal is an angular tolerance, not exact equality: probed to 1e-8
    /// radians, so a latitude 5.7e-7° past the antimeridian raises where
    /// 5.8e-7° is accepted.
    /// </summary>
    [TestMethod]
    public void AntipodalToleranceIsAngular()
    {
        _ = new Simulation().AssertSqlError("select geography::Parse('LINESTRING(0 0, 180 0.00000057)').STIsValid()", 6522);
        IsTrue(IsValid("LINESTRING(0 0, 180 0.00000058)"));
        IsTrue(IsValid("LINESTRING(0 0, 179.9999 0)"));
        // A pair of antipodal points that no edge joins is fine.
        IsTrue(IsValid("MULTIPOINT((0 0), (180 0))"));
    }

    /// <summary>The same refusal reaches the well-known-binary constructor.</summary>
    [TestMethod]
    public void AntipodalEdgeIsRefusedFromWellKnownBinary()
    {
        var ex = new Simulation().AssertSqlError(
            """
            declare @b varbinary(max) = geometry::Parse('LINESTRING(0 0, 180 0)').STAsBinary();
            select geography::STGeomFromWKB(@b, 4326).STAsText()
            """,
            6522);
        Assert.Contains("24206", ex.Message);
    }

    /// <summary>
    /// Real refuses most of its <c>geography</c> surface on a stored-but-invalid
    /// instance, and the split is the planar one's: the renderings, the ordinate
    /// reads, <c>STSrid</c>, <c>STIsEmpty</c>, <c>STLength</c>,
    /// <c>MinDbCompatibilityLevel</c> and <c>STIsValid</c> itself answer anyway.
    /// </summary>
    [TestMethod]
    [DataRow("STGeometryType()")]
    [DataRow("STDimension()")]
    [DataRow("STNumPoints()")]
    [DataRow("STPointN(1)")]
    [DataRow("STStartPoint()")]
    [DataRow("STEndPoint()")]
    [DataRow("STIsClosed()")]
    [DataRow("STNumGeometries()")]
    [DataRow("STGeometryN(1)")]
    [DataRow("NumRings()")]
    [DataRow("RingN(1)")]
    [DataRow("ReorientObject()")]
    [DataRow("InstanceOf('Polygon')")]
    [DataRow("STArea()")]
    [DataRow("STDistance(geography::Parse('POINT(0.5 0.5)'))")]
    [DataRow("STIntersects(geography::Parse('POINT(0.5 0.5)'))")]
    [DataRow("STContains(geography::Parse('POINT(0.5 0.5)'))")]
    [DataRow("STWithin(geography::Parse('POINT(0.5 0.5)'))")]
    [DataRow("STDisjoint(geography::Parse('POINT(0.5 0.5)'))")]
    [DataRow("STEquals(geography::Parse('POINT(0.5 0.5)'))")]
    [DataRow("STOverlaps(geography::Parse('POINT(0.5 0.5)'))")]
    public void InvalidGeographyInstanceIsRefused(string member)
    {
        var ex = new Simulation().AssertSqlError(
            $"select geography::Parse('POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))').{member}", 6522);
        Assert.Contains("24144: This operation cannot be completed because the instance is not valid.", ex.Message);
    }

    /// <summary>The members real answers from anyway.</summary>
    [TestMethod]
    [DataRow("STAsText()")]
    [DataRow("ToString()")]
    [DataRow("AsTextZM()")]
    [DataRow("STAsBinary()")]
    [DataRow("AsBinaryZM()")]
    [DataRow("STIsEmpty()")]
    [DataRow("STSrid")]
    [DataRow("Lat")]
    [DataRow("Long")]
    [DataRow("HasZ")]
    [DataRow("HasM")]
    [DataRow("STLength()")]
    [DataRow("MinDbCompatibilityLevel()")]
    [DataRow("STIsValid()")]
    public void InvalidGeographyInstanceStillAnswersTheTolerantMembers(string member) =>
        _ = new Simulation().ExecuteScalar($"select geography::Parse('POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))').{member}");

    /// <summary>An invalid <i>argument</i> is refused the same way an invalid receiver is.</summary>
    [TestMethod]
    public void InvalidGeographyArgumentIsRefused()
    {
        var ex = new Simulation().AssertSqlError(
            "select geography::Parse('POINT(0 0)').STIntersects(geography::Parse('POLYGON((0 0, 1 1, 1 0, 0 1, 0 0))'))", 6522);
        Assert.Contains("24144", ex.Message);
    }

    /// <summary>A predicate over a stored <c>geography</c> column reaches the same engine as one over a literal.</summary>
    [TestMethod]
    public void PredicateOverStoredColumn_Evaluates()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table dbo.zones (id int not null primary key, area geography not null);
            insert dbo.zones values
                (1, geography::Parse('POLYGON((0 0,2 0,2 2,0 2,0 0))')),
                (2, geography::Parse('POLYGON((1 1,3 1,3 3,1 3,1 1))')),
                (3, geography::Parse('POLYGON((9 9,10 9,10 10,9 10,9 9))'));
            select count(*) from dbo.zones where area.STIntersects(geography::Parse('POINT(1.5 1.5)')) = 1
            """));
}
