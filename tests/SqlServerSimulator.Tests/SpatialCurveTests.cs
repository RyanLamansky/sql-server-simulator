using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The curved spatial kinds — <c>CIRCULARSTRING</c>, <c>COMPOUNDCURVE</c> and
/// <c>CURVEPOLYGON</c> — and the whole-globe <c>FULLGLOBE</c>: their text,
/// binary and well-known binary forms, their members, measures and
/// linearization, and the curve-producing members.
/// </summary>
/// <remarks>
/// Expected values are SQL Server 2025's own, probed 2026-09-29; a double
/// asserted within a tolerance is one whose last digits real computes by an
/// arithmetic the probes didn't pin down.
/// </remarks>
[TestClass]
public sealed class SpatialCurveTests
{
    private static object? Eval(string expression) => new Simulation().ExecuteScalar($"select {expression}");

    private static string? Text(string expression) => Eval($"({expression}).ToString()") as string;

    private static string? Hex(string expression) => Eval($"convert(varchar(max), {expression}, 1)") as string;

    private const string Compound = "COMPOUNDCURVE((0 0, 1 1), CIRCULARSTRING(1 1, 2 2, 3 1), (3 1, 4 0))";

    private const string Circle = "CURVEPOLYGON(CIRCULARSTRING(0 0, 2 2, 4 0, 2 -2, 0 0))";

    [TestMethod]
    [DataRow("CIRCULARSTRING(0 0, 1 1, 2 0, 3 -1, 4 0)", "CIRCULARSTRING (0 0, 1 1, 2 0, 3 -1, 4 0)")]
    [DataRow("compoundcurve((0 0, 1 1, 2 1), circularstring(2 1, 3 2, 4 1, 5 0, 6 1))", "COMPOUNDCURVE ((0 0, 1 1, 2 1), CIRCULARSTRING (2 1, 3 2, 4 1, 5 0, 6 1))")]
    [DataRow("CURVEPOLYGON((0 0, 10 0, 10 10, 0 10, 0 0), CIRCULARSTRING(2 5, 5 8, 8 5, 5 2, 2 5))", "CURVEPOLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), CIRCULARSTRING (2 5, 5 8, 8 5, 5 2, 2 5))")]
    [DataRow("CURVEPOLYGON(COMPOUNDCURVE((0 0, 4 0), CIRCULARSTRING(4 0, 2 2, 0 0)))", "CURVEPOLYGON (COMPOUNDCURVE ((0 0, 4 0), CIRCULARSTRING (4 0, 2 2, 0 0)))")]
    [DataRow("GEOMETRYCOLLECTION(COMPOUNDCURVE((0 0, 1 1)), CURVEPOLYGON EMPTY)", "GEOMETRYCOLLECTION (COMPOUNDCURVE ((0 0, 1 1)), CURVEPOLYGON EMPTY)")]
    [DataRow("CIRCULARSTRING(0 0 NULL 1, 1 1 NULL 2, 2 0 NULL 3)", "CIRCULARSTRING (0 0 NULL 1, 1 1 NULL 2, 2 0 NULL 3)")]
    public void Text_RoundTripsInRealsSpelling(string wkt, string expected) =>
        AreEqual(expected, Text($"geometry::Parse('{wkt}')"));

    [TestMethod]
    public void Serialization_IsVersionTwoWithFigureTypesAndSegments()
    {
        AreEqual("0x0000000002040300000000000000000000000000000000000000000000000000F03F000000000000F03F0000000000000040000000000000000001000000020000000001000000FFFFFFFF0000000008",
            Hex("cast(geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)') as varbinary(max))"));
        AreEqual("0x0000000002040500000000000000000000000000000000000000000000000000F03F000000000000F03F000000000000004000000000000000400000000000000840000000000000F03F0000000000001040000000000000000001000000030000000001000000FFFFFFFF000000000903000000020302",
            Hex($"cast(geometry::Parse('{Compound}') as varbinary(max))"));
        AreEqual("0x0000000002040A00000000000000000000000000000000000000000000000000244000000000000000000000000000002440000000000000244000000000000000000000000000002440000000000000000000000000000000000000000000000040000000000000144000000000000014400000000000002040000000000000204000000000000014400000000000001440000000000000004000000000000000400000000000001440020000000100000000020500000001000000FFFFFFFF000000000A",
            Hex("cast(geometry::Parse('CURVEPOLYGON((0 0, 10 0, 10 10, 0 10, 0 0), CIRCULARSTRING(2 5, 5 8, 8 5, 5 2, 2 5))') as varbinary(max))"));
        // In version 2 a plain polygon's rings are line figures too.
        AreEqual("0x0000000002040700000000000000000000000000000000000000000000000000F03F0000000000000000000000000000F03F000000000000F03F0000000000000000000000000000000000000000000000000000000000000000000000000000F03F000000000000F03F00000000000000400000000000000000020000000100000000020400000003000000FFFFFFFF0000000007000000000000000003000000000100000008",
            Hex("cast(geometry::Parse('GEOMETRYCOLLECTION(POLYGON((0 0,1 0,1 1,0 0)), CIRCULARSTRING(0 0, 1 1, 2 0))') as varbinary(max))"));
        AreEqual("0x000000000204000000000000000001000000FFFFFFFFFFFFFFFF08", Hex("cast(geometry::Parse('CIRCULARSTRING EMPTY') as varbinary(max))"));
    }

    [TestMethod]
    public void Serialization_RoundTripsThroughVarbinary()
    {
        AreEqual("COMPOUNDCURVE ((0 0, 1 1), CIRCULARSTRING (1 1, 2 2, 3 1), (3 1, 4 0))", Text($"cast(cast(geometry::Parse('{Compound}') as varbinary(max)) as geometry)"));
        AreEqual("CURVEPOLYGON (CIRCULARSTRING (0 0, 2 2, 4 0, 2 -2, 0 0))", Text($"cast(cast(geography::Parse('{Circle}') as varbinary(max)) as geography)"));
    }

    [TestMethod]
    public void Serialization_GeographyLargerThanAHemisphere_IsVersionTwoWithItsOwnBit()
    {
        // The clockwise square names everything but itself.
        AreEqual("0xE610000002240500000000000000000000000000000000000000000000000000F03F0000000000000000000000000000F03F000000000000F03F0000000000000000000000000000F03F0000000000000000000000000000000001000000010000000001000000FFFFFFFF0000000003",
            Hex("cast(geography::Parse('POLYGON((0 0,0 1,1 1,1 0,0 0))') as varbinary(max))"));
        AreEqual(110, Eval("geography::Parse('POLYGON((0 0,0 1,1 1,1 0,0 0))').MinDbCompatibilityLevel()"));
        AreEqual(100, Eval("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').MinDbCompatibilityLevel()"));
        AreEqual(110, Eval("geometry::Parse('CURVEPOLYGON((0 0,1 0,1 1,0 0))').MinDbCompatibilityLevel()"));
        AreEqual(180.0, Eval("geography::Parse('POLYGON((0 0,0 1,1 1,1 0,0 0))').EnvelopeAngle()"));
        AreEqual("POINT (0 90)", Text("geography::Parse('POLYGON((0 0,0 1,1 1,1 0,0 0))').EnvelopeCenter()"));
    }

    [TestMethod]
    public void WellKnownBinary_WritesEachElementAsItsOwnRecord()
    {
        AreEqual("0x01090000000300000001020000000200000000000000000000000000000000000000000000000000F03F000000000000F03F010800000003000000000000000000F03F000000000000F03F000000000000004000000000000000400000000000000840000000000000F03F0102000000020000000000000000000840000000000000F03F00000000000010400000000000000000",
            Hex($"geometry::Parse('{Compound}').STAsBinary()"));
        AreEqual("CURVEPOLYGON (COMPOUNDCURVE ((0 0, 4 0), CIRCULARSTRING (4 0, 2 2, 0 0)))",
            Text("geometry::STGeomFromWKB(0x010A000000010000000109000000020000000102000000020000000000000000000000000000000000000000000000000010400000000000000000010800000003000000000000000000104000000000000000000000000000000040000000000000004000000000000000000000000000000000, 0)"));
    }

    [TestMethod]
    [DataRow("geometry::Parse('CIRCULARSTRING(0 0, 1 1)')", 24142, "Expected \",\" at position 23. The input has \")\".")]
    [DataRow("geometry::Parse('CIRCULARSTRING(0 0)')", 24212, "The CircularString input is not valid because it does not have enough points.")]
    [DataRow("geometry::Parse('CIRCULARSTRING(0 0 1, 1 1 2, 2 0 3)')", 24214, "Circular arc segments with Z values must have equal Z value for all 3 points.")]
    [DataRow("geography::Parse('COMPOUNDCURVE((0 0, 1 1), (2 2, 3 3))')", 24134, "Sequential parts of a compound curve must have one common endpoint.")]
    [DataRow("geometry::Parse('COMPOUNDCURVE(LINESTRING(0 0, 1 1))')", 24142, "Expected \"(\" at position 14. The input has \"L\".")]
    [DataRow("geometry::Parse('COMPOUNDCURVE(COMPOUNDCURVE((0 0,1 1)))')", 24142, "Expected \"CIRCULARSTRING\" at position 15. The input has \"COMPOUNDCURVE(\".")]
    [DataRow("geometry::Parse('CURVEPOLYGON(CIRCULARSTRING EMPTY)')", 24300, "Expected a call to BeginFigure, but EndGeometry was called.")]
    [DataRow("geography::Parse('CURVEPOLYGON(COMPOUNDCURVE EMPTY)')", 24301, "Expected a call to AddSegmentLine or AddSegmentArc, but EndGeography was called.")]
    [DataRow("geometry::Parse('CURVEPOLYGON(CIRCULARSTRING(0 0, 1 1, 0 0))')", 24118, "the exterior ring does not have enough points")]
    [DataRow("geography::Parse('POLYGON((0 0, 1 0, 0 0))')", 24305, "the ring number 1 does not have enough points")]
    [DataRow("geography::Parse('POLYGON((0 0, 1 0, 1 1, 0 0),(0 0, 1 0, 1 1, 0 1))')", 24306, "the start and end points of the ring number 2 are not the same")]
    [DataRow("geography::Parse('CURVEPOLYGON(CIRCULARSTRING(0 0, 1 1, 2 0, 1 -1, 0 0), EMPTY)')", 24120, "the interior ring number 1 does not have enough points")]
    [DataRow("geography::Parse('GEOMETRYCOLLECTION(FULLGLOBE, POINT(1 1))')", 24150, "FullGlobe instances cannot be objects in the GeometryCollection.")]
    [DataRow("geography::STGeomFromWKB(0x010B00000000, 4326)", 24115, "The well-known binary (WKB) input is not valid.")]
    public void Parse_RefusesWhatRealRefuses(string expression, int code, string message)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}.ToString()", 6522);
        Assert.Contains($"{code}: ", ex.Message);
        Assert.Contains(message, ex.Message);
    }

    [TestMethod]
    public void Members_ReadTheCurveAsWritten()
    {
        AreEqual("CompoundCurve", Eval($"geometry::Parse('{Compound}').STGeometryType()"));
        AreEqual(3, Eval($"geometry::Parse('{Compound}').STNumCurves()"));
        AreEqual("CIRCULARSTRING (1 1, 2 2, 3 1)", Text($"geometry::Parse('{Compound}').STCurveN(2)"));
        AreEqual("LINESTRING (3 1, 4 0)", Text($"geometry::Parse('{Compound}').STCurveN(3)"));
        IsNull(Text($"geometry::Parse('{Compound}').STCurveN(4)"));
        AreEqual(5, Eval($"geometry::Parse('{Compound}').STNumPoints()"));
        AreEqual("POINT (2 2)", Text($"geometry::Parse('{Compound}').STPointN(3)"));
        AreEqual("POINT (4 0)", Text($"geometry::Parse('{Compound}').STEndPoint()"));
        IsTrue((bool)Eval($"geometry::Parse('{Compound}').InstanceOf('Curve')")!);
        IsFalse((bool)Eval($"geometry::Parse('{Compound}').InstanceOf('CircularString')")!);
        AreEqual(2, Eval("geometry::Parse('LINESTRING(0 0, 1 1, 2 0)').STNumCurves()"));
        AreEqual(DBNull.Value, Eval("geometry::Parse('POINT(1 1)').STNumCurves()"));
        AreEqual(1, Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STDimension()"));
        AreEqual(110, Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').MinDbCompatibilityLevel()"));
    }

    [TestMethod]
    public void Members_CurvePolygonRingsComeBackAsTheCurvesTheyWere()
    {
        AreEqual("COMPOUNDCURVE ((0 0, 4 0), CIRCULARSTRING (4 0, 2 2, 0 0))", Text("geometry::Parse('CURVEPOLYGON(COMPOUNDCURVE((0 0, 4 0), CIRCULARSTRING(4 0, 2 2, 0 0)))').STExteriorRing()"));
        AreEqual("CIRCULARSTRING (2 5, 5 8, 8 5, 5 2, 2 5)", Text("geometry::Parse('CURVEPOLYGON((0 0, 10 0, 10 10, 0 10, 0 0), CIRCULARSTRING(2 5, 5 8, 8 5, 5 2, 2 5))').STInteriorRingN(1)"));
        AreEqual(1, Eval("geography::Parse('CURVEPOLYGON(COMPOUNDCURVE((0 0, 4 0), CIRCULARSTRING(4 0, 2 2, 0 0)))').NumRings()"));
        IsTrue((bool)Eval($"geometry::Parse('{Circle}').InstanceOf('Surface')")!);
        IsTrue((bool)Eval("geometry::Parse('CIRCULARSTRING(0 0, 2 2, 4 0, 2 -2, 0 0)').STIsRing()")!);
    }

    [TestMethod]
    public void Members_ARinglessKindAnswersNullBeforeItsValidityIsAsked()
    {
        // An arc returning to its start degenerates to a point, which geometry calls invalid.
        IsFalse((bool)Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 0 0)').STIsValid()")!);
        AreEqual(DBNull.Value, Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 0 0)').STNumInteriorRing()"));
        AreEqual(0.0, Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 0 0)').STLength()"));
        Assert.Contains("24151: ", new Simulation().AssertSqlError("select geometry::Parse('CIRCULARSTRING(0 0, 1 1, 0 0)').STCurveN(0).ToString()", 6522).Message);
        Assert.Contains("24144: ", new Simulation().AssertSqlError("select geometry::Parse('CIRCULARSTRING(0 0, 1 1, 0 0)').STIsRing()", 6522).Message);
        IsTrue((bool)Eval("geography::Parse('CIRCULARSTRING(1 1, 0 0, 1 1)').STIsValid()")!);
    }

    [TestMethod]
    public void Measures_FollowTheArcs()
    {
        AreEqual(Math.PI, (double)Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STLength()")!, 1e-14);
        AreEqual(4 * Math.PI, (double)Eval($"geometry::Parse('{Circle}').STArea()")!, 1e-13);
        AreEqual(4 + (2 * Math.PI), (double)Eval("geometry::Parse('CURVEPOLYGON(COMPOUNDCURVE((0 0, 4 0), CIRCULARSTRING(4 0, 2 2, 0 0)))').STLength()")!, 1e-13);
        AreEqual(100 - (9 * Math.PI), (double)Eval("geometry::Parse('CURVEPOLYGON((0 0, 10 0, 10 10, 0 10, 0 0), CIRCULARSTRING(2 5, 5 8, 8 5, 5 2, 2 5))').STArea()")!, 1e-12);
        // Real's closest approach runs to the circle itself, √26 − 2, not to a linearization of it.
        AreEqual(Math.Sqrt(26) - 2, (double)Eval($"geometry::Parse('{Circle}').STDistance(geometry::Parse('POINT(1 5)'))")!, 1e-14);
        AreEqual(4.0990195135927845, (double)Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STDistance(geometry::Parse('CIRCULARSTRING(0 5, 1 6, 2 5)'))")!, 1e-14);
        AreEqual(3.0, (double)Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STDistance(geometry::Parse('CIRCULARSTRING(5 0, 6 -1, 7 0)'))")!, 1e-14);
        AreEqual(11.806248474865697, (double)Eval("geometry::Parse('COMPOUNDCURVE((0 0, 1 0), CIRCULARSTRING(1 0, 2 1, 3 0))').STDistance(geometry::Parse('LINESTRING(10 10, 11 12)'))")!, 1e-13);
        // A straight segment's band beside an arc's ring: real's area to about 1e-4.
        AreEqual(4.886234037, (double)Eval("geometry::Parse('COMPOUNDCURVE((0 0, 1 0), CIRCULARSTRING(1 0, 2 1, 3 0))').STBuffer(0.5).STArea()")!, 5e-4);
        AreEqual(348215.5120912028, (double)Eval("geography::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STLength()")!, 348215.5 * 1e-8);
        AreEqual(77191629910.77402, (double)Eval("geography::Parse('CURVEPOLYGON(COMPOUNDCURVE((0 0, 4 0), CIRCULARSTRING(4 0, 2 2, 0 0)))').STArea()")!, 77191629910.0 * 1e-8);
    }

    [TestMethod]
    public void CurveToLine_StepsEachArcByPowersOfTwo()
    {
        AreEqual(33, Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STCurveToLine().STNumPoints()"));
        // 0.001 of the instance's larger side: a far-off point coarsens the arc.
        AreEqual(18, Eval("geometry::Parse('GEOMETRYCOLLECTION(CIRCULARSTRING(0 0, 1 1, 2 0), POINT(5 5))').STCurveToLine().STNumPoints()"));
        AreEqual("LINESTRING (0 0, 2 0, 4 0)", Text("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0, 3 -1, 4 0)').CurveToLineWithTolerance(0.5, 1)"));
        AreEqual(5, Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').CurveToLineWithTolerance(0.1, 0).STNumPoints()"));
        AreEqual("POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))", Text("geometry::Parse('CURVEPOLYGON((0 0, 4 0, 4 4, 0 4, 0 0))').STCurveToLine()"));
        AreEqual("LINESTRING (1 2, 2 3, 3 2)", Text("geometry::Parse('CIRCULARSTRING(1 2 3 4, 2 3 3 5, 3 2 3 6)').CurveToLineWithTolerance(0.5, 1)"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('CIRCULARSTRING EMPTY').STCurveToLine()"));
        AreEqual(2049, Eval("geography::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').CurveToLineWithTolerance(1e-9, 0).STNumPoints()"));
    }

    [TestMethod]
    public void CurveToLine_RefusesATolerance()
    {
        Assert.Contains("24152: The tolerance (0) passed to CurveToLineWithTolerance is not valid.",
            new Simulation().AssertSqlError("select geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').CurveToLineWithTolerance(0, 0).ToString()", 6522).Message);
        _ = new Simulation().AssertSqlError("select geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').CurveToLineWithTolerance(1, null).ToString()", 6569);
    }

    [TestMethod]
    public void Operations_ReadTheLinearization()
    {
        // The middle control point is a step of the linearization; a point on the circle between steps isn't.
        IsTrue((bool)Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STIntersects(geometry::Parse('POINT(1 1)'))")!);
        IsFalse((bool)Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STIntersects(geometry::Parse('POINT(1.6 0.8)'))")!);
        IsTrue((bool)Eval("geometry::Parse('CURVEPOLYGON(CIRCULARSTRING(0 0, 1 1, 2 0, 1 -1, 0 0))').STContains(geometry::Parse('POINT(1.6 0.79999)'))")!);
        IsFalse((bool)Eval("geometry::Parse('CURVEPOLYGON(CIRCULARSTRING(0 0, 1 1, 2 0, 1 -1, 0 0))').STContains(geometry::Parse('POINT(1.6 0.7999999)'))")!);
        AreEqual("POLYGON ((-4E-06 -4E-06, 4.000004 -4E-06, 4.000004 4.000004, -4E-06 4.000004, -4E-06 -4E-06))",
            Text("geometry::Parse('CURVEPOLYGON((0 0, 4 0, 4 4, 0 4, 0 0))').STEnvelope()"));
    }

    [TestMethod]
    public void Reduce_KeepsGenuineArcs()
    {
        AreEqual("LINESTRING (0 0, 2 2)", Text("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 2)').Reduce(0.1)"));
        AreEqual("CIRCULARSTRING (0 0, 1 1, 2 0)", Text("geometry::Parse('COMPOUNDCURVE(CIRCULARSTRING(0 0, 1 1, 2 0))').Reduce(0.1)"));
        AreEqual("POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0))", Text("geometry::Parse('CURVEPOLYGON((0 0, 4 0, 4 4, 0 4, 0 0))').Reduce(0.1)"));
        AreEqual("GEOMETRYCOLLECTION (CIRCULARSTRING (0 0, 1 1, 2 0), POINT (5 5))", Text("geometry::Parse('GEOMETRYCOLLECTION(CIRCULARSTRING(0 0, 1 1, 2 0), POINT(5 5))').Reduce(0.1)"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('CIRCULARSTRING EMPTY').Reduce(0.1)"));
    }

    [TestMethod]
    public void Buffer_OfACurve_SweepsTheArcs()
    {
        AreEqual(3.9254240649146555, (double)Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').STBuffer(0.5).STArea()")!, 1e-5);
        IsTrue((bool)Eval("geometry::Parse('CIRCULARSTRING(0 0, 2 2, 4 0, 2 -2, 0 0)').STBuffer(0.5).STIsValid()")!);
        AreEqual(12.567237715181506, (double)Eval("geometry::Parse('CIRCULARSTRING(0 0, 2 2, 4 0, 2 -2, 0 0)').STBuffer(0.5).STArea()")!, 1e-6);
    }

    [TestMethod]
    public void BufferWithCurves_OfAPoint_IsTwoHalfCircles()
    {
        AreEqual("CURVEPOLYGON (CIRCULARSTRING (5 4.5, 5.5 5, 5 5.5, 4.5 5, 5 4.5))", Text("geometry::Parse('POINT(5 5)').BufferWithCurves(0.5)"));
        AreEqual("POINT (1 2)", Text("geometry::Parse('POINT(1 2)').BufferWithCurves(0)"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('POINT(1 2)').BufferWithCurves(-1)"));
        _ = new Simulation().AssertSqlError("select geometry::Parse('POINT(5 5)').BufferWithCurves(null).ToString()", 6569);

        // geography: the control points at 45°, 135°, 225° and 315°, a kilometre away.
        var ring = Text("geography::Parse('POINT(5 5)').BufferWithCurves(1000)")!;
        Assert.StartsWith("CURVEPOLYGON (CIRCULARSTRING (5.006397427", ring);
        Assert.Contains(", 4.993602572", ring);
    }

    [TestMethod]
    public void EnvelopeAggregate_Geography_IsACircleAboutTheMergedCap()
    {
        var sim = new Simulation();
        var text = (string)sim.ExecuteScalar("""
            select geography::EnvelopeAggregate(g).ToString()
            from (values (geography::Parse('POINT(0 0)')), (geography::Parse('POINT(100 0)'))) v(g)
            """)!;
        Assert.StartsWith("CURVEPOLYGON (CIRCULARSTRING (90.40228", text);
        Assert.Contains("9.59771", text);
        AreEqual("FULLGLOBE", sim.ExecuteScalar("""
            select geography::EnvelopeAggregate(g).ToString()
            from (values (geography::Parse('POLYGON((0 0,0 1,1 1,1 0,0 0))'))) v(g)
            """));
        AreEqual("GEOMETRYCOLLECTION EMPTY", sim.ExecuteScalar("""
            select geography::EnvelopeAggregate(g).ToString()
            from (values (geography::Parse('POINT EMPTY'))) v(g)
            """));
    }

    [TestMethod]
    public void FullGlobe_IsAValueOfItsOwn()
    {
        AreEqual("FULLGLOBE", Text("geography::Parse('FULLGLOBE')"));
        AreEqual("0xE61000000224000000000000000001000000FFFFFFFFFFFFFFFF0B", Hex("cast(geography::Parse('FULLGLOBE') as varbinary(max))"));
        AreEqual("0x017E000000", Hex("geography::Parse('FULLGLOBE').STAsBinary()"));
        AreEqual("FULLGLOBE", Text("geography::STGeomFromWKB(0x017E000000, 4326)"));
        AreEqual(510065621710996.44, Eval("geography::Parse('FULLGLOBE').STArea()"));
        AreEqual("FullGlobe", Eval("geography::Parse('FULLGLOBE').STGeometryType()"));
        IsFalse((bool)Eval("geography::Parse('FULLGLOBE').STIsEmpty()")!);
        IsFalse((bool)Eval("geography::Parse('FULLGLOBE').InstanceOf('Surface')")!);
        AreEqual(180.0, Eval("geography::Parse('FULLGLOBE').EnvelopeAngle()"));
        AreEqual(110, Eval("geography::Parse('FULLGLOBE').MinDbCompatibilityLevel()"));
    }

    [TestMethod]
    public void FullGlobe_HoldsEverything()
    {
        IsTrue((bool)Eval("geography::Parse('FULLGLOBE').STContains(geography::Parse('POINT(1 1)'))")!);
        IsTrue((bool)Eval("geography::Parse('POINT(1 1)').STWithin(geography::Parse('FULLGLOBE'))")!);
        IsFalse((bool)Eval("geography::Parse('FULLGLOBE').STOverlaps(geography::Parse('POINT(1 1)'))")!);
        AreEqual("FULLGLOBE", Text("geography::Parse('FULLGLOBE').STUnion(geography::Parse('POINT(1 1)'))"));
        AreEqual("POINT (1 1)", Text("geography::Parse('FULLGLOBE').STIntersection(geography::Parse('POINT(1 1)'))"));
        AreEqual("POLYGON ((0 0, 0 1, 1 1, 1 0, 0 0))", Text("geography::Parse('FULLGLOBE').STDifference(geography::Parse('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))'))"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geography::Parse('POINT(1 1)').STDifference(geography::Parse('FULLGLOBE'))"));
        AreEqual("FULLGLOBE", Text("geography::Parse('FULLGLOBE').STBuffer(10)"));
    }

    [TestMethod]
    public void Collection_EndpointsAreItsFirstAndLastPoints()
    {
        AreEqual("POINT (3 3)", Text("geometry::Parse('MULTILINESTRING((0 0,1 1),(2 2,3 3))').STEndPoint()"));
        AreEqual("POINT (0 0)", Text("geometry::Parse('GEOMETRYCOLLECTION(CIRCULARSTRING(0 0, 1 1, 2 0), POINT(5 5))').STStartPoint()"));
        AreEqual(DBNull.Value, Eval("geometry::Parse('LINESTRING(0 0,1 1)').STNumInteriorRing()"));
    }

    [TestMethod]
    public void ReorientObject_ReversesRingsAndLeavesLines() =>
        AreEqual("LINESTRING (0 0, 1 1)", Text("geography::Parse('LINESTRING(0 0, 1 1)').ReorientObject()"));

    [TestMethod]
    public void IsValidDetailed_ReportsAValidInstance()
    {
        AreEqual("24400: Valid", Eval($"geometry::Parse('{Compound}').IsValidDetailed()"));
        AreEqual("24400: Valid", Eval("geography::Parse('POINT(1 2)').IsValidDetailed()"));
    }
}
