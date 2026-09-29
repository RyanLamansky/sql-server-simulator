using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The spatial constructive operations: the four set operations, the
/// envelope, hull and boundary, the buffers, <c>Reduce</c>, <c>MakeValid</c>
/// and the four spatial aggregates.
/// </summary>
/// <remarks>
/// Every expected string is SQL Server 2025's own text for the expression,
/// probed 2026-09-28 — the last-digit noise in a computed vertex included,
/// since the precision grid it comes from is modeled.
/// </remarks>
[TestClass]
public sealed class SpatialConstructiveTests
{
    private static object? Eval(string expression) => new Simulation().ExecuteScalar($"select {expression}");

    private static string? Text(string expression) => Eval($"({expression}).ToString()") as string;

    private static string? Op(string a, string operation, string b) =>
        Text($"geometry::Parse('{a}').{operation}(geometry::Parse('{b}'))");

    [TestMethod]
    public void Intersection_OverlappingSquares_CarriesTheGridNoiseOnComputedVertices() =>
        AreEqual("POLYGON ((5 5, 10.000000000000018 5.0000000000000355, 10 10, 5.0000000000000355 10.000000000000018, 5 5))",
            Op("POLYGON((0 0,10 0,10 10,0 10,0 0))", "STIntersection", "POLYGON((5 5,15 5,15 15,5 15,5 5))"));

    [TestMethod]
    public void Intersection_OfAnInstanceWithItself_NormalizesTheRing() =>
        AreEqual("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))",
            Op("POLYGON((10 10,0 10,0 0,10 0,10 10))", "STIntersection", "POLYGON((0 0,0 10,10 10,10 0,0 0))"));

    [TestMethod]
    public void Intersection_TouchingSquares_YieldTheSharedEdgeOrCorner()
    {
        AreEqual("LINESTRING (4 4, 4 0)", Op("POLYGON((0 0,4 0,4 4,0 4,0 0))", "STIntersection", "POLYGON((4 0,8 0,8 4,4 4,4 0))"));
        AreEqual("POINT (4 4)", Op("POLYGON((2 2,4 2,4 4,2 4,2 2))", "STIntersection", "POLYGON((4 4,6 4,6 6,4 6,4 4))"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Op("POLYGON((0 0,4 0,4 4,0 4,0 0))", "STIntersection", "POLYGON((8 0,9 0,9 4,8 4,8 0))"));
    }

    [TestMethod]
    public void Intersection_MixedDimensions_ListsComponentsInDescendingSweepOrder() =>
        AreEqual("GEOMETRYCOLLECTION (LINESTRING (2 4, 1 4), POLYGON ((3 3, 4.0000000000000071 3, 4 4, 3 4.0000000000000071, 3 3)), LINESTRING (4 2, 4 1))",
            Op("POLYGON((0 0,4 0,4 4,0 4,0 0))", "STIntersection", "MULTIPOLYGON(((4 1,6 1,6 2,4 2,4 1)),((1 4,2 4,2 6,1 6,1 4)),((3 3,5 3,5 5,3 5,3 3)))"));

    [TestMethod]
    public void Intersection_LineThroughPolygon_RunsFromTheHigherEnd()
    {
        AreEqual("LINESTRING (4 4, 2 2)", Op("LINESTRING(0 0,10 10)", "STIntersection", "POLYGON((2 2,4 2,4 4,2 4,2 2))"));
        AreEqual("LINESTRING (4 4, 2 2)", Op("LINESTRING(10 10,0 0)", "STIntersection", "POLYGON((2 2,4 2,4 4,2 4,2 2))"));
        AreEqual("LINESTRING (4.0000000000000213 3, 2.0000000000000284 3)", Op("LINESTRING(0 3,10 3)", "STIntersection", "POLYGON((2 2,4 2,4 4,2 4,2 2))"));
    }

    [TestMethod]
    public void Intersection_CrossingLines_YieldTheCrossingPoint() =>
        AreEqual("POINT (5 0)", Op("LINESTRING(0 0,10 0)", "STIntersection", "LINESTRING(5 -5,5 5)"));

    [TestMethod]
    public void Union_DisjointPolygons_ListTheHigherFirst()
    {
        AreEqual("MULTIPOLYGON (((20 20, 30 20, 30 30, 20 30, 20 20)), ((0 0, 10 0, 10 10, 0 10, 0 0)))",
            Op("POLYGON((10 10,0 10,0 0,10 0,10 10))", "STUnion", "POLYGON((20 20,30 20,30 30,20 30,20 20))"));
        AreEqual("MULTIPOLYGON (((0 0, 10 0, 10 10, 0 10, 0 0)), ((20 -5, 30 -5, 30 30, 20 30, 20 -5)))",
            Op("POLYGON((0 0,10 0,10 10,0 10,0 0))", "STUnion", "POLYGON((20 -5,30 -5,30 30,20 30,20 -5))"));
    }

    [TestMethod]
    public void Union_AdjacentPolygons_KeepEveryNodeAsAVertex() =>
        AreEqual("POLYGON ((0 0, 10 0, 10 5, 20 5, 20 15, 10 15, 10 10, 0 10, 0 0))",
            Op("POLYGON((0 0,10 0,10 10,0 10,0 0))", "STUnion", "POLYGON((10 5,20 5,20 15,10 15,10 5))"));

    [TestMethod]
    public void Union_CrossingLines_PairTheEdgesAtTheNode()
    {
        AreEqual("MULTILINESTRING ((5 5, 5 0, 10 0), (5 -5, 5 0, 0 0))", Op("LINESTRING(0 0,10 0)", "STUnion", "LINESTRING(5 -5,5 5)"));
        AreEqual("MULTILINESTRING ((0 10, 5.0000000000000178 5.0000000000000178, 10 10), (10 0, 5.0000000000000178 5.0000000000000178, 0 0))",
            Op("LINESTRING(0 0,10 10)", "STUnion", "LINESTRING(0 10,10 0)"));
    }

    [TestMethod]
    public void Union_LineDirection_FollowsTheSweepNotTheInput()
    {
        AreEqual("LINESTRING (20 0, 10 0, 5 0, 0 0)", Op("LINESTRING(0 0,10 0)", "STUnion", "LINESTRING(5 0,20 0)"));
        AreEqual("MULTILINESTRING ((0 20, 0 10), (10 10, 10 0, 0 0))", Op("LINESTRING(0 0,10 0,10 10)", "STUnion", "LINESTRING(0 10,0 20)"));
        AreEqual("GEOMETRYCOLLECTION (POINT (99 99), LINESTRING (0 0, 8 0, 4 4, 0 0))", Op("LINESTRING(4 4,8 0,0 0,4 4)", "STUnion", "POINT(99 99)"));
    }

    [TestMethod]
    public void Union_ThreeLinesMeetingAtAPoint_PairTheLowerTwo() =>
        AreEqual("GEOMETRYCOLLECTION (POINT (99 99), LINESTRING (4 9, 4 4), LINESTRING (8 0, 4 4, 0 0))",
            Op("MULTILINESTRING((4 4,8 0),(0 0,4 4),(4 4,4 9))", "STUnion", "POINT(99 99)"));

    [TestMethod]
    public void Union_Points_ListTheHigherFirstAndMergeDuplicates()
    {
        AreEqual("MULTIPOINT ((1 2), (2 1))", Op("POINT(2 1)", "STUnion", "POINT(1 2)"));
        AreEqual("MULTIPOINT ((0 5), (3 3), (2 2), (1 1))", Op("MULTIPOINT((3 3),(1 1),(2 2),(1 1))", "STUnion", "POINT(0 5)"));
        AreEqual("POINT (1 1)", Op("POINT(1 1)", "STUnion", "POINT(1 1)"));
    }

    [TestMethod]
    public void Union_PointOnALine_BecomesAVertex() =>
        AreEqual("LINESTRING (5 0, 1 0, 0 0)", Op("POINT(1 0)", "STUnion", "LINESTRING(0 0,5 0)"));

    [TestMethod]
    public void Union_MixedKinds_YieldACollection() =>
        AreEqual("GEOMETRYCOLLECTION (POINT (9 9), POLYGON ((2 2, 4 2, 4 4, 2 4, 2 2)), LINESTRING (1 1, 0 0))",
            Op("POLYGON((2 2,4 2,4 4,2 4,2 2))", "STUnion", "GEOMETRYCOLLECTION(LINESTRING(0 0,1 1),POINT(9 9))"));

    [TestMethod]
    public void Union_DropsZAndM() =>
        AreEqual("GEOMETRYCOLLECTION (POINT (20 20), POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0)))",
            Op("POLYGON((0 0 1,10 0 1,10 10 1,0 10 1,0 0 1))", "STUnion", "POINT(20 20 5)"));

    [TestMethod]
    public void Difference_Hole_RunsClockwiseFromItsLowestVertex()
    {
        AreEqual("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 2 8, 8 8, 8 2, 2 2))",
            Op("POLYGON((0 0,10 0,10 10,0 10,0 0))", "STDifference", "POLYGON((2 2,8 2,8 8,2 8,2 2))"));
        AreEqual("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (6 2, 6 4, 8 4, 8 2, 6 2), (2 6, 2 8, 4 8, 4 6, 2 6))",
            Op("POLYGON((0 0,10 0,10 10,0 10,0 0))", "STDifference", "MULTIPOLYGON(((6 2,8 2,8 4,6 4,6 2)),((2 6,4 6,4 8,2 8,2 6)))"));
    }

    [TestMethod]
    public void Difference_SplitPolygon_CarriesTheBiasBelowTheGridCentre() =>
        AreEqual("MULTIPOLYGON (((4.2632564145606011E-14 6.0000000000000142, 9.9999999999999858 6.0000000000000142, 10 10, 0 10, 4.2632564145606011E-14 6.0000000000000142)), ((0 0, 10 0, 9.9999999999999858 4.0000000000000213, 4.2632564145606011E-14 4.0000000000000213, 0 0)))",
            Op("POLYGON((0 0,10 0,10 10,0 10,0 0))", "STDifference", "POLYGON((-1 4,11 4,11 6,-1 6,-1 4))"));

    [TestMethod]
    public void Difference_LineMinusPolygon_KeepsTheOutsideStretches() =>
        AreEqual("MULTILINESTRING ((10 10, 4 4), (2 2, 0 0))", Op("LINESTRING(0 0,10 10)", "STDifference", "POLYGON((2 2,4 2,4 4,2 4,2 2))"));

    [TestMethod]
    public void Difference_OfEqualInstances_IsEmpty()
    {
        AreEqual("GEOMETRYCOLLECTION EMPTY", Op("POLYGON((0 0,10 0,10 10,0 10,0 0))", "STDifference", "POLYGON((0 0,10 0,10 10,0 10,0 0))"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Op("POINT(1 1)", "STDifference", "POINT(1 1)"));
    }

    [TestMethod]
    public void SymDifference_PolygonAndLine_AbsorbsTheLineInsideTheArea() =>
        AreEqual("GEOMETRYCOLLECTION (POLYGON ((2 2, 4 2, 4 4, 2 4, 2 2)), LINESTRING (2 2, 0 0))",
            Op("POLYGON((2 2,4 2,4 4,2 4,2 2))", "STSymDifference", "LINESTRING(0 0,3 3)"));

    [TestMethod]
    public void SetOperations_NullOrOtherSrid_ReadNull()
    {
        IsNull(Text("geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))').STUnion(NULL)"));
        IsNull(Text("geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))').STUnion(geometry::STGeomFromText('POINT(1 1)', 4326))"));
    }

    [TestMethod]
    public void SetOperations_StringArgument_ReadsAsWellKnownText() =>
        AreEqual("GEOMETRYCOLLECTION (POINT (20 20), POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0)))",
            Text("geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))').STUnion('POINT(20 20)')"));

    [TestMethod]
    public void SetOperations_InvalidInstance_Raises24144()
    {
        var ex = new Simulation().AssertSqlError("select geometry::Parse('POLYGON((0 0,10 0,0 10,10 10,0 0))').STUnion('POINT(20 20)')", 6522);
        Assert.Contains("24144", ex.Message);
    }

    [TestMethod]
    public void SetOperations_ResultCarriesTheReceiverSrid() =>
        AreEqual(7, Eval("geometry::STGeomFromText('POINT(1 1)', 7).STUnion(geometry::STGeomFromText('POINT(2 2)', 7)).STSrid"));

    [TestMethod]
    public void Envelope_DegenerateAxis_WidensByTwoPartsInAHundredMillion()
    {
        AreEqual("POLYGON ((0.99999998 1.99999996, 1.00000002 1.99999996, 1.00000002 2.00000004, 0.99999998 2.00000004, 0.99999998 1.99999996))",
            Text("geometry::Parse('POINT(1 2)').STEnvelope()"));
        AreEqual("POLYGON ((0 -1E-08, 3 -1E-08, 3 1E-08, 0 1E-08, 0 -1E-08))", Text("geometry::Parse('LINESTRING(0 0,3 0)').STEnvelope()"));
    }

    [TestMethod]
    public void Envelope_StartsAtTheLowerLeftCorner() =>
        AreEqual("POLYGON ((-1 0, 4 0, 4 5, -1 5, -1 0))", Text("geometry::Parse('POLYGON((0 0,4 1,3 5,-1 3,0 0))').STEnvelope()"));

    [TestMethod]
    public void Envelope_Empty_IsAnEmptyCollection() =>
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('POINT EMPTY').STEnvelope()"));

    [TestMethod]
    public void ConvexHull_StartsAtTheRightmostLowestVertex()
    {
        AreEqual("POLYGON ((4 1, 3 5, -1 3, 0 0, 4 1))", Text("geometry::Parse('POLYGON((0 0,4 1,3 5,-1 3,0 0))').STConvexHull()"));
        AreEqual("POLYGON ((10 1, 10 10, -1 10, 0 0, 10 1))", Text("geometry::Parse('MULTIPOINT((0 0),(10 1),(10 10),(-1 10))').STConvexHull()"));
    }

    [TestMethod]
    public void ConvexHull_CollinearVertices_KeptOnlyOffTheScanPivot()
    {
        AreEqual("POLYGON ((10 0, 10 10, 0 10, 0 5, 0 0, 10 0))", Text("geometry::Parse('MULTIPOINT((0 0),(10 0),(10 10),(0 10),(0 5))').STConvexHull()"));
        AreEqual("POLYGON ((10 0, 10 10, 0 10, 0 0, 10 0))", Text("geometry::Parse('MULTIPOINT((0 0),(10 0),(10 10),(0 10),(5 5),(10 5))').STConvexHull()"));
        AreEqual("POLYGON ((6 5, 6 6, 5 5, 1 1, 0 0, 2 0, 6 5))",
            Text("geometry::Parse('GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(1 1,2 0),POLYGON((5 5,6 5,6 6,5 5)))').STConvexHull()"));
    }

    [TestMethod]
    public void ConvexHull_Degenerate_YieldsALineOrAPoint()
    {
        AreEqual("LINESTRING (3 4, 1 2)", Text("geometry::Parse('MULTIPOINT((1 2),(3 4),(2 3))').STConvexHull()"));
        AreEqual("POINT (1 2)", Text("geometry::Parse('MULTIPOINT((1 2),(1 2))').STConvexHull()"));
    }

    [TestMethod]
    public void Boundary_Polygon_ListsTheRingsAsLines() =>
        AreEqual("MULTILINESTRING ((2 2, 2 4, 4 4, 4 2, 2 2), (0 0, 10 0, 10 10, 0 10, 0 0))",
            Text("geometry::Parse('POLYGON((10 10,0 10,0 0,10 0,10 10),(2 2,4 2,4 4,2 4,2 2))').STBoundary()"));

    [TestMethod]
    public void Boundary_Lines_FollowTheModTwoRule()
    {
        AreEqual("MULTIPOINT ((2 0), (0 0))", Text("geometry::Parse('LINESTRING(0 0,1 1,2 0)').STBoundary()"));
        AreEqual("MULTIPOINT ((6 6), (1 1), (2 0), (0 0))", Text("geometry::Parse('MULTILINESTRING((0 0,1 1),(1 1,2 0),(1 1,6 6))').STBoundary()"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('LINESTRING(0 0,1 1,2 0,0 0)').STBoundary()"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('MULTIPOINT((1 1),(2 2))').STBoundary()"));
    }

    [TestMethod]
    public void Buffer_Point_IsA128SidedPolygonAtAnyDistance()
    {
        AreEqual(129, Eval("geometry::Parse('POINT(0 0)').STBuffer(1).STNumPoints()"));
        AreEqual(129, Eval("geometry::Parse('POINT(0 0)').STBuffer(1000).STNumPoints()"));
    }

    [TestMethod]
    public void BufferWithTolerance_StepCount_DoublesUntilTheSagittaFits()
    {
        AreEqual(9, Eval("geometry::Parse('POINT(0 0)').BufferWithTolerance(1, 0.1, 0).STNumPoints()"));
        AreEqual(33, Eval("geometry::Parse('POINT(0 0)').BufferWithTolerance(1, 0.01, 1).STNumPoints()"));
        AreEqual(257, Eval("geometry::Parse('POINT(0 0)').BufferWithTolerance(1, 0.0001, 0).STNumPoints()"));
    }

    [TestMethod]
    public void Buffer_ZeroDistance_ReturnsTheInstanceAsItStands() =>
        AreEqual("POLYGON ((0 10, 10 10, 10 0, 0 0, 0 10))", Text("geometry::Parse('POLYGON((0 10,10 10,10 0,0 0,0 10))').STBuffer(0)"));

    [TestMethod]
    public void Buffer_NegativeDistance_ErodesAreasAndEmptiesPointsAndLines()
    {
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))').STBuffer(-6)"));
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geometry::Parse('POINT(0 0)').STBuffer(-1)"));
        AreEqual(5, Eval("geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))').STBuffer(-1).STNumPoints()"));
    }

    [TestMethod]
    public void Buffer_Line_EndsInRoundCaps() =>
        AreEqual(135, Eval("geometry::Parse('LINESTRING(0 0,1000 0)').STBuffer(1).STNumPoints()"));

    [TestMethod]
    public void Buffer_NullArgument_IsMsg6569() =>
        new Simulation().AssertSqlError(
            "select geometry::Parse('POINT(0 0)').STBuffer(NULL)", 6569,
            "'geometry::STBuffer' failed because parameter 1 is not allowed to be null.");

    [TestMethod]
    public void BufferWithTolerance_NonPositiveTolerance_Raises24108()
    {
        var ex = new Simulation().AssertSqlError("select geometry::Parse('POINT(0 0)').BufferWithTolerance(1, 0, 1)", 6522);
        Assert.Contains("24108: The tolerance (0) passed to BufferWithTolerance is not valid.", ex.Message);
    }

    [TestMethod]
    public void Reduce_DouglasPeucker_KeepsVerticesBeyondTheTolerance()
    {
        AreEqual("LINESTRING (0 0, 2 0, 3 5, 4 0)", Text("geometry::Parse('LINESTRING(0 0,1 0.1,2 0,3 5,4 0)').Reduce(0.5)"));
        AreEqual("LINESTRING (14 7, 2 5, 9 7)", Text("geometry::Parse('LINESTRING(14 7, 2 5, 9 7)').Reduce(2)"));
    }

    [TestMethod]
    public void Reduce_HomogeneousCollection_BecomesTheMultiForm() =>
        AreEqual("MULTIPOINT ((1 1), (2 2))", Text("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 1),GEOMETRYCOLLECTION(POINT(2 2)))').Reduce(0)"));

    [TestMethod]
    public void Reduce_NegativeTolerance_Raises24125()
    {
        var ex = new Simulation().AssertSqlError("select geometry::Parse('LINESTRING(0 0,1 1,2 0)').Reduce(-1)", 6522);
        Assert.Contains("24125: The tolerance (-1) passed to Reduce is not valid.", ex.Message);
    }

    [TestMethod]
    public void MakeValid_ValidInstance_ComesBackUntouched() =>
        AreEqual("POLYGON ((0 10, 10 10, 10 0, 0 0, 0 10))", Text("geometry::Parse('POLYGON((0 10,10 10,10 0,0 0,0 10))').MakeValid()"));

    [TestMethod]
    public void MakeValid_BowTie_SplitsIntoTwoTrianglesThroughTheGrid() =>
        AreEqual("MULTIPOLYGON (((5.0000000000000178 5.0000000000000178, 10 10, 3.5527136788005009E-14 10, 5.0000000000000178 5.0000000000000178)), ((3.5527136788005009E-14 3.5527136788005009E-14, 10 3.5527136788005009E-14, 5.0000000000000178 5.0000000000000178, 3.5527136788005009E-14 3.5527136788005009E-14)))",
            Text("geometry::Parse('POLYGON((0 0,10 0,0 10,10 10,0 0))').MakeValid()"));

    [TestMethod]
    public void MakeValid_FlatRing_BecomesALine() =>
        AreEqual("LINESTRING (20 0, 10 0, 7.1054273576010019E-14 0)", Text("geometry::Parse('POLYGON((0 0,10 0,20 0,0 0))').MakeValid()"));

    [TestMethod]
    public void UnionAggregate_FoldsTheRows() =>
        AreEqual("POLYGON ((0 0, 10 0, 10.000000000000018 5.0000000000000355, 15 5, 15 15, 5 15, 5.0000000000000355 10.000000000000018, 0 10, 0 0))",
            Eval("""
                geometry::UnionAggregate(g).ToString()
                from (values (geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))')), (geometry::Parse('POLYGON((5 5,15 5,15 15,5 15,5 5))'))) v(g)
                """));

    [TestMethod]
    public void UnionAggregate_StringRows_ReadAsWellKnownText() =>
        AreEqual("MULTIPOINT ((2 2), (1 1))", Eval("geometry::UnionAggregate(g).ToString() from (values ('POINT(1 1)'), ('POINT(2 2)')) v(g)"));

    [TestMethod]
    public void EnvelopeAggregate_SkipsNullsAndWidensDegenerateAxes()
    {
        AreEqual("POLYGON ((-5 0, 30 0, 30 20, -5 20, -5 0))", Eval("""
            geometry::EnvelopeAggregate(g).ToString()
            from (values (geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))')), (geometry::Parse('POINT(20 20)')), (NULL), (geometry::Parse('LINESTRING(-5 5, 30 5)'))) v(g)
            """));
        AreEqual("POLYGON ((0.99999998 1, 1.00000002 1, 1.00000002 3, 0.99999998 3, 0.99999998 1))",
            Eval("geometry::EnvelopeAggregate(g).ToString() from (values (geometry::Parse('POINT(1 1)')), (geometry::Parse('POINT(1 3)'))) v(g)"));
    }

    [TestMethod]
    public void CollectionAggregate_FlattensCollectionRowsOneLevel() =>
        AreEqual("GEOMETRYCOLLECTION (MULTIPOINT ((1 1), (2 2)), POINT (3 3), POINT EMPTY)", Eval("""
            geometry::CollectionAggregate(g).ToString()
            from (values (geometry::Parse('MULTIPOINT((1 1),(2 2))')), (geometry::Parse('GEOMETRYCOLLECTION(POINT(3 3))')), (geometry::Parse('POINT EMPTY'))) v(g)
            """));

    [TestMethod]
    public void ConvexHullAggregate_HullsEveryRowsVertices() =>
        AreEqual("POLYGON ((30 5, 20 20, 5 15, -5 5, 0 0, 10 0, 30 5))", Eval("""
            geometry::ConvexHullAggregate(g).ToString()
            from (values (geometry::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))')), (geometry::Parse('POLYGON((5 5,15 5,15 15,5 15,5 5))')),
                         (geometry::Parse('POINT(20 20)')), (NULL), (geometry::Parse('LINESTRING(-5 5, 30 5)'))) v(g)
            """));

    [TestMethod]
    public void SpatialAggregates_NoRowsOrMixedSrids_ReadNull()
    {
        AreEqual(DBNull.Value, Eval("geometry::UnionAggregate(g).ToString() from (values (geometry::Parse('POINT(1 1)'))) v(g) where 1 = 0"));
        AreEqual(DBNull.Value, Eval("geometry::CollectionAggregate(g).ToString() from (values (geometry::STGeomFromText('POINT(1 1)', 7)), (geometry::STGeomFromText('POINT(2 2)', 8))) v(g)"));
    }

    [TestMethod]
    public void SpatialAggregates_GroupPerGroup() =>
        AreEqual(2, new Simulation().ExecuteScalar("""
            select count(*) from (
                select k, geometry::UnionAggregate(g) as u
                from (values (1, geometry::Parse('POINT(1 1)')), (1, geometry::Parse('POINT(2 2)')), (2, geometry::Parse('POINT(5 5)'))) v(k, g)
                group by k) q
            """));

    [TestMethod]
    public void UnionAggregate_InvalidRow_NamesTheAggregateClass()
    {
        var ex = new Simulation().AssertSqlError(
            "select geometry::UnionAggregate(g) from (values (geometry::Parse('POLYGON((0 0,10 0,0 10,10 10,0 0))'))) v(g)", 6522);
        Assert.Contains("user-defined routine or aggregate \"GeometryUnionAggregate\"", ex.Message);
        Assert.Contains("24144", ex.Message);
    }

    [TestMethod]
    public void UnionAggregate_IntegerOperand_IsMsg206() =>
        new Simulation().AssertSqlError("select geometry::UnionAggregate(1)", 206, "Operand type clash: int is incompatible with geometry");

    [TestMethod]
    public void Geography_Union_WritesRingsFromTheirNorthwestVertex() =>
        AreEqual("MULTIPOLYGON (((20 30, 20 20, 30 20, 30 30, 20 30)), ((0 10, 0 0, 10 0, 10 10, 0 10)))",
            Text("geography::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))').STUnion(geography::Parse('POLYGON((20 20,30 20,30 30,20 30,20 20))'))"));

    [TestMethod]
    public void Geography_Intersection_CrossesAlongTheGreatEllipticArcs()
    {
        var text = Text("geography::Parse('POLYGON((0 0,10 0,10 10,0 10,0 0))').STIntersection(geography::Parse('POLYGON((5 5,15 5,15 15,5 15,5 5))'))")!;
        var sim = new Simulation();
        // The top edge of the first square bows north of latitude 10 at
        // longitude 5, and the ring starts there, least along the y axis.
        AreEqual(10.037423045910833, (double)sim.ExecuteScalar($"select geography::Parse('{text}').STPointN(1).Lat")!, 1e-9);
        AreEqual(5.0190018174896718, (double)sim.ExecuteScalar($"select geography::Parse('{text}').STPointN(3).Lat")!, 1e-9);
    }

    [TestMethod]
    public void Geography_ConvexHull_StartsAtTheSouthwestVertex() =>
        AreEqual("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))", Text("geography::Parse('MULTIPOINT((0 0),(10 0),(10 10),(0 10))').STConvexHull()"));

    [TestMethod]
    public void Geography_CollectionAggregate_KeepsRowOrder() =>
        AreEqual("GEOMETRYCOLLECTION (POINT (1 1), POINT (2 2))",
            Eval("geography::CollectionAggregate(g).ToString() from (values (geography::Parse('POINT(1 1)')), (geography::Parse('POINT(2 2)'))) v(g)"));

    [TestMethod]
    public void Intersection_ObliqueCrossing_FollowsTheSegmentReachingHigherFromItsTop() =>
        AreEqual("POINT (8.08558334826804 7.6052689520981449)",
            Op("LINESTRING(12.3 2.5,0.0 17.4)", "STIntersection", "LINESTRING(4.2 4.3,19.6 17.4)"));

    [TestMethod]
    public void Intersection_FarFromTheOrigin_AnchorsTheGridOnTheCentre() =>
        AreEqual("POLYGON ((1000000.0005 1000000.0005, 1000000.001 1000000.0005, 1000000.001 1000000.001, 1000000.0005 1000000.001, 1000000.0005 1000000.0005))",
            Op("POLYGON((1000000 1000000, 1000000.001 1000000, 1000000.001 1000000.001, 1000000 1000000.001, 1000000 1000000))", "STIntersection",
                "POLYGON((1000000.0005 1000000.0005, 1000000.0015 1000000.0005, 1000000.0015 1000000.0015, 1000000.0005 1000000.0015, 1000000.0005 1000000.0005))"));

    [TestMethod]
    public void Validity_TrailingRepeat_RefusedOnlyWhenReachedFromBelow()
    {
        IsTrue((bool)Eval("geometry::Parse('LINESTRING(15 7.5, 2 5, 2 5)').STIsValid()")!);
        IsFalse((bool)Eval("geometry::Parse('LINESTRING(2 5, 15 7, 15 7)').STIsValid()")!);
        IsTrue((bool)Eval("geometry::Parse('POLYGON((0 0, 2 0, 2 2, 0 2, 0 0, 0 0))').STIsValid()")!);
        IsFalse((bool)Eval("geometry::Parse('POLYGON((2 2, 0 2, 0 0, 2 0, 2 2, 2 2))').STIsValid()")!);
    }

    [TestMethod]
    public void Validity_RetraceJudgedOnTheGrid()
    {
        IsTrue((bool)Eval("geometry::Parse('LINESTRING(12 2, 6 16, 9 9)').STIsValid()")!);
        IsFalse((bool)Eval("geometry::Parse('LINESTRING(0 0, 4 4, 2 2)').STIsValid()")!);
    }

    [TestMethod]
    public void Union_LineValidOnTheGrid_NoLongerRaises() =>
        AreEqual("GEOMETRYCOLLECTION (POINT (10 14), LINESTRING (15 7.5, 2 5))", Op("LINESTRING(15 7.5, 2 5, 2 5)", "STUnion", "POINT(10 14)"));

    [TestMethod]
    public void BufferWithTolerance_StepCount_FollowsTheSagittaEstimate()
    {
        AreEqual(9, Eval("geometry::Parse('POINT(0 0)').BufferWithTolerance(1, 0.3084, 1).STNumPoints()"));
        AreEqual(5, Eval("geometry::Parse('POINT(0 0)').BufferWithTolerance(1, 0.3085, 1).STNumPoints()"));
        AreEqual(19, Eval("geometry::Parse('LINESTRING(0 0, 10 0, 0 5)').BufferWithTolerance(1, 0.3, 1).STNumPoints()"));
    }

    [TestMethod]
    public void Buffer_ObliqueLine_SplitsTheCapWhereTheRingStarts()
    {
        AreEqual(121, Eval("geometry::Parse('LINESTRING(0 9, 7 8)').STBuffer(2).STNumPoints()"));
        AreEqual(6.998458355915315, (double)Eval("geometry::Parse('LINESTRING(0 9, 7 8)').STBuffer(2).STPointN(1).STX")!, 1e-12);
    }

    [TestMethod]
    public void Geography_PointBuffer_StartsNorthEastOnTheCircle()
    {
        AreEqual(129, Eval("geography::Parse('POINT(-100.91 3.26)').STBuffer(20000).STNumPoints()"));
        AreEqual(-100.78231301708725, (double)Eval("geography::Parse('POINT(-100.91 3.26)').STBuffer(20000).STPointN(1).Long")!, 1e-9);
        AreEqual(3.3874558095025704, (double)Eval("geography::Parse('POINT(-100.91 3.26)').STBuffer(20000).STPointN(1).Lat")!, 1e-9);
    }

    [TestMethod]
    public void Geography_Reduce_MeasuresInMetres()
    {
        AreEqual("LINESTRING (0 0, 2 0)", Text("geography::Parse('LINESTRING(0 0,1 0.001,2 0)').Reduce(1000)"));
        AreEqual("LINESTRING (0 0, 1 0.001, 2 0)", Text("geography::Parse('LINESTRING(0 0,1 0.001,2 0)').Reduce(100)"));
    }

    [TestMethod]
    public void Geography_MakeValid_ValidInstanceComesBackUntouched() =>
        AreEqual("POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0))", Text("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').MakeValid()"));

    [TestMethod]
    public void ShortestLineTo_RunsFromTheReceiverAndIsEmptyWhereTheyMeet()
    {
        AreEqual("LINESTRING (3 4, 0 0)", Op("POINT(3 4)", "ShortestLineTo", "POINT(0 0)"));
        AreEqual("LINESTRING (4 1, 6 1)", Op("POLYGON((0 0,4 0,4 4,0 4,0 0))", "ShortestLineTo", "POLYGON((6 1,8 1,8 3,6 3,6 1))"));
        AreEqual("LINESTRING EMPTY", Op("LINESTRING(0 0, 10 10)", "ShortestLineTo", "LINESTRING(0 10, 10 0)"));
        IsNull(Op("POINT EMPTY", "ShortestLineTo", "POINT(1 1)"));
    }

    [TestMethod]
    public void Filter_WithoutAnIndex_IsSTIntersects()
    {
        IsTrue((bool)Eval("geometry::Parse('POINT(0 0)').Filter(geometry::Parse('POINT(0 0)'))")!);
        IsFalse((bool)Eval("geometry::Parse('POINT(0 0)').Filter(geometry::Parse('POINT(1 1)'))")!);
    }

    [TestMethod]
    public void Geography_PointBuffer_StepsItsHalvesAboutTheGnomonicCircle()
    {
        // Probed 2026-09-29: the vertices between the control points sit on
        // real's own arc, which the unit-sphere circle misses by ~1e-8 degrees.
        AreEqual(10.008534033166589, (double)Eval("geography::Point(45, 10, 4326).STBuffer(1000).STPointN(2).Long")!, 1e-9);
        AreEqual(45.006656819309505, (double)Eval("geography::Point(45, 10, 4326).STBuffer(1000).STPointN(2).Lat")!, 1e-9);
        AreEqual(-38.405224041813725, (double)Eval("geography::Point(-14.17, -38.53, 4326).STBuffer(20000).STPointN(2).Long")!, 1e-9);
    }

    [TestMethod]
    public void Geography_PointBuffer_StepCountFollowsTheDistance()
    {
        AreEqual(129, Eval("geography::Point(45, 10, 4326).STBuffer(20000).STNumPoints()"));
        AreEqual(257, Eval("geography::Point(45, 10, 4326).STBuffer(30000).STNumPoints()"));
        AreEqual(513, Eval("geography::Point(45, 10, 4326).STBuffer(100000).STNumPoints()"));
        AreEqual(33, Eval("geography::Point(45, 10, 4326).BufferWithTolerance(1000, 100, 0).STNumPoints()"));
        AreEqual("LineString", Eval("geography::Point(45, 10, 4326).BufferWithTolerance(1, 10, 0).STGeometryType()"));
    }

    [TestMethod]
    public void Geography_Buffer_PastHalfTheGlobe_Is24207()
    {
        var ex = new Simulation().AssertSqlError("select geography::Point(45, 10, 4326).STBuffer(20000000)", 6522);
        Assert.Contains("24207: The specified buffer distance exceeds the full globe.", ex.Message);
    }

    [TestMethod]
    public void Geography_LineBuffer_BandsCapsAndJoins()
    {
        AreEqual(131, Eval("geography::Parse('LINESTRING(0 45, 1 45)').STBuffer(1000).STNumPoints()"));
        AreEqual(-0.012682561702143821, (double)Eval("geography::Parse('LINESTRING(0 45, 1 45)').STBuffer(1000).STPointN(1).Long")!, 1e-9);
        // Areas probed 2026-09-29.
        AreEqual(225784963.37947562, (double)Eval("geography::Parse('LINESTRING(0 0, 1 0)').STBuffer(1000).STArea()")!, 225784963.0 * 1e-8);
        AreEqual(446720214.91173047, (double)Eval("geography::Parse('LINESTRING(0 0, 1 0, 1 1)').STBuffer(1000).STArea()")!, 446720214.0 * 1e-4);
        AreEqual(12755694405.137461, (double)Eval("geography::Parse('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))').STBuffer(1000).STArea()")!, 12755694405.0 * 1e-5);
        AreEqual(11868998007.083227, (double)Eval("geography::Parse('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))').STBuffer(-1000).STArea()")!, 11868998007.0 * 1e-5);
        AreEqual("GEOMETRYCOLLECTION EMPTY", Text("geography::Parse('LINESTRING(0 0, 1 0)').STBuffer(-1000)"));
    }

    [TestMethod]
    public void Geography_ShortestLineTo_WritesEndsThroughAUnitVector()
    {
        AreEqual("LINESTRING (0 0, 0.99999999999999978 1)", Text("geography::Parse('POINT(0 0)').ShortestLineTo(geography::Parse('POINT(1 1)'))"));
        AreEqual("LINESTRING EMPTY", Text("geography::Parse('POINT(0 42)').ShortestLineTo(geography::Parse('POLYGON((-1 40, 1 40, 1 44, -1 44, -1 40))'))"));
        IsNull(Text("geography::Parse('POINT EMPTY').ShortestLineTo(geography::Parse('POINT(1 1)'))"));
        // The foot on the line bows north with the great elliptic arc.
        AreEqual(44.004360898092408, (double)Eval("geography::Parse('POINT(0 45)').ShortestLineTo(geography::Parse('LINESTRING(-1 44, 1 44)')).STEndPoint().Lat")!, 1e-9);
        AreEqual(2.4879513251438476, (double)Eval("geography::Parse('LINESTRING(0 0, 10 10)').ShortestLineTo(geography::Parse('LINESTRING(0 5, 5 20)')).STStartPoint().Long")!, 1e-8);
    }

    [TestMethod]
    public void Geography_SetOperations_OrderRingsByTheSweepAlongTheYAxis()
    {
        // Northern results start at the vertex least along the Earth-centred
        // y axis, southern ones at the vertex furthest along it.
        AreEqual("POLYGON ((-10.6 -52.3, -10.6 -31.5, -39.6 -31.5, -39.6 -52.3, -10.6 -52.3))",
            Text("geography::Parse('POLYGON((-39.6 -31.5, -39.6 -52.3, -10.6 -52.3, -10.6 -31.5, -39.6 -31.5))').STUnion(geography::Parse('POINT(-39.6 -31.5)'))"));
        AreEqual("GEOMETRYCOLLECTION (LINESTRING (10 8, 1 8), POLYGON ((10.7 6, 11.3 4.8, 12.6 4.9, 13.4 6, 12.7 7.1, 11 7.8, 10.7 6)))",
            Text("geography::Parse('LINESTRING(1 8, 10 8)').STUnion(geography::Parse('POLYGON((12.6 4.9, 13.4 6, 12.7 7.1, 11 7.8, 10.7 6, 11.3 4.8, 12.6 4.9))'))"));
        AreEqual("MULTIPOINT ((0 0), (170 0))", Text("geography::Parse('POINT(0 0)').STUnion(geography::Parse('POINT(170 0)'))"));
    }

    [TestMethod]
    public void Geography_SetOperations_TakeAPolygonLargerThanAHemisphereThroughItsComplement()
    {
        // The clockwise square names the globe less the square.
        AreEqual("Polygon", Eval("geography::Parse('POLYGON((0 0, 0 10, 10 10, 10 0, 0 0))').STUnion(geography::Parse('POINT(50 50)')).STGeometryType()"));
        var text = Text("geography::Parse('POLYGON((0 0, 0 10, 10 10, 10 0, 0 0))').STIntersection(geography::Parse('POLYGON((5 5, 15 5, 15 15, 5 15, 5 5))'))")!;
        Assert.StartsWith("POLYGON ((5 15, ", text);
        AreEqual(7, (int)Eval($"geography::Parse('{text}').STNumPoints()")!);
        AreEqual(2, Eval("geography::Parse('POLYGON((0 0, 0 10, 10 10, 10 0, 0 0))').STIntersection(geography::Parse('POLYGON((20 20, 20 30, 30 30, 30 20, 20 20))')).NumRings()"));
        _ = Throws<NotSupportedException>(() => Eval("geography::Parse('POLYGON((0 0, 0 10, 10 10, 10 0, 0 0))').STUnion(geography::Parse('POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))')).ToString()"));
    }
}
