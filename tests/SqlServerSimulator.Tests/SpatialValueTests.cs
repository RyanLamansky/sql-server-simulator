using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>geography</c> / <c>geometry</c> value model: WKT parsing and its
/// canonical re-rendering, per-value SRID, Z and M ordinates, EMPTY
/// instances, the UDT and OGC binary forms, and the structural member
/// surface. Every expectation here is probe-confirmed against SQL Server
/// 2025 (2026-07-30).
/// </summary>
/// <remarks>
/// The measures, predicates and constructive operations still raise — those
/// live in <see cref="SpatialTypeTests"/> alongside the catalog surface.
/// </remarks>
[TestClass]
public sealed class SpatialValueTests
{
    private static object? Eval(string expression) => new Simulation().ExecuteScalar($"select {expression}");

    [TestMethod]
    [DataRow("geometry::Parse('POINT(1 2)')", "POINT (1 2)")]
    [DataRow("geometry::Parse('POINT (1.50 2)')", "POINT (1.5 2)")]
    [DataRow("geometry::Parse('  point ( 1 2 )  ')", "POINT (1 2)")]
    [DataRow("geometry::Parse('LINESTRING(0 0, 1 1, 2 2)')", "LINESTRING (0 0, 1 1, 2 2)")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1,1 0,0 0))')", "POLYGON ((0 0, 0 1, 1 1, 1 0, 0 0))")]
    [DataRow("geometry::Parse('MULTIPOINT(0 0, 1 1)')", "MULTIPOINT ((0 0), (1 1))")]
    [DataRow("geometry::Parse('MULTIPOINT((0 0),(1 1))')", "MULTIPOINT ((0 0), (1 1))")]
    [DataRow("geometry::Parse('MULTIPOLYGON(((0 0,0 1,1 1,0 0)),((5 5,5 6,6 6,5 5)))')", "MULTIPOLYGON (((0 0, 0 1, 1 1, 0 0)), ((5 5, 5 6, 6 6, 5 5)))")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))')", "GEOMETRYCOLLECTION (POINT (1 2), LINESTRING (0 0, 1 1))")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(GEOMETRYCOLLECTION(POINT(1 2)))')", "GEOMETRYCOLLECTION (GEOMETRYCOLLECTION (POINT (1 2)))")]
    [DataRow("geometry::Parse('POINT EMPTY')", "POINT EMPTY")]
    [DataRow("geometry::Parse('MULTIPOLYGON EMPTY')", "MULTIPOLYGON EMPTY")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT EMPTY, POINT(1 2))')", "GEOMETRYCOLLECTION (POINT EMPTY, POINT (1 2))")]
    [DataRow("geometry::Parse('POINT(1 2 3)')", "POINT (1 2 3)")]
    [DataRow("geometry::Parse('POINT(1 2 3 4)')", "POINT (1 2 3 4)")]
    [DataRow("geometry::Parse('POINT(1 2 NULL 4)')", "POINT (1 2 NULL 4)")]
    public void Wkt_RoundTripsInCanonicalForm(string expression, string expected)
        => AreEqual(expected, Eval($"{expression}.ToString()"));

    /// <summary>Ordinates print in .NET's shortest round-trip form, which is what real emits.</summary>
    [TestMethod]
    [DataRow("1.123456789012345678", "1.1234567890123457")]
    [DataRow("1e10", "10000000000")]
    [DataRow("0.000001", "1E-06")]
    [DataRow("1e30", "1E+30")]
    public void Wkt_FormatsOrdinatesRoundTrippably(string literal, string expected)
        => AreEqual($"POINT ({expected} 2)", Eval($"geometry::Parse('POINT({literal} 2)').ToString()"));

    [TestMethod]
    public void Srid_DefaultsPerType()
    {
        AreEqual(0, Eval("geometry::Parse('POINT(1 2)').STSrid"));
        AreEqual(4326, Eval("geography::Parse('POINT(1 2)').STSrid"));
        AreEqual(4326, Eval("geometry::STGeomFromText('POINT(1 2)', 4326).STSrid"));
    }

    /// <summary>A spatial value's binary form is SQL Server's UDT serialization, not its text.</summary>
    [TestMethod]
    [DataRow("geometry::STGeomFromText('POINT(1 2)', 4326)", "E6100000010C000000000000F03F0000000000000040")]
    [DataRow("geometry::Parse('POINT EMPTY')", "000000000104000000000000000001000000FFFFFFFFFFFFFFFF01")]
    [DataRow("geometry::Parse('POINT(1 2 3)')", "00000000010D000000000000F03F00000000000000400000000000000840")]
    [DataRow("geography::Parse('POINT(1 2)')", "E6100000010C0000000000000040000000000000F03F")]
    [DataRow("geography::Parse('LINESTRING(0 0,1 1,2 2)')", "E610000001040300000000000000000000000000000000000000000000000000F03F000000000000F03F0000000000000040000000000000004001000000010000000001000000FFFFFFFF0000000002")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(GEOMETRYCOLLECTION(POINT(1 2)))')", "00000000010401000000000000000000F03F000000000000004001000000010000000003000000FFFFFFFF0000000007000000000000000007010000000000000001")]
    public void UdtBinary_MatchesRealBytes(string expression, string expectedHex)
        => AreEqual(expectedHex, Convert.ToHexString((byte[])Eval($"cast({expression} as varbinary(max))")!));

    [TestMethod]
    public void UdtBinary_RoundTripsBackToTheInstance()
        => AreEqual("POINT (1 2)", Eval("cast(cast(geometry::Parse('POINT(1 2)') as varbinary(max)) as geometry).ToString()"));

    /// <summary><c>STAsBinary()</c> is OGC well-known binary and drops Z/M; <c>AsBinaryZM()</c> keeps them on the ISO type codes.</summary>
    [TestMethod]
    [DataRow("geometry::Parse('POINT(1 2)').STAsBinary()", "0101000000000000000000F03F0000000000000040")]
    [DataRow("geometry::Parse('LINESTRING(0 0,1 1)').STAsBinary()", "01020000000200000000000000000000000000000000000000000000000000F03F000000000000F03F")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1,0 0))').STAsBinary()", "01030000000100000004000000000000000000000000000000000000000000000000000000000000000000F03F000000000000F03F000000000000F03F00000000000000000000000000000000")]
    [DataRow("geography::Parse('POINT(-122 47)').STAsBinary()", "01010000000000000000805EC00000000000804740")]
    [DataRow("geometry::Parse('POINT(1 2 3 4)').STAsBinary()", "0101000000000000000000F03F0000000000000040")]
    [DataRow("geometry::Parse('POINT(1 2 3 4)').AsBinaryZM()", "01B90B0000000000000000F03F000000000000004000000000000008400000000000001040")]
    public void OgcBinary_MatchesRealBytes(string expression, string expectedHex)
        => AreEqual(expectedHex, Convert.ToHexString((byte[])Eval(expression)!));

    [TestMethod]
    public void StGeomFromWkb_ReadsOgcBinary()
        => AreEqual("POINT (1 2)", Eval("geometry::STGeomFromWKB(0x0101000000000000000000F03F0000000000000040, 0).ToString()"));

    /// <summary><c>STAsText()</c> drops Z and M where <c>ToString()</c> / <c>AsTextZM()</c> keep them.</summary>
    [TestMethod]
    public void TextRenderings_DifferOnZM()
    {
        AreEqual("POINT (1 2)", Eval("geometry::Parse('POINT(1 2 3)').STAsText()"));
        AreEqual("POINT (1 2 3)", Eval("geometry::Parse('POINT(1 2 3)').AsTextZM()"));
        AreEqual("POINT (1 2 3)", Eval("geometry::Parse('POINT(1 2 3)').ToString()"));
    }

    [TestMethod]
    [DataRow("geometry::Parse('POINT(1 2)').STGeometryType()", "Point")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1,0 0))').STGeometryType()", "Polygon")]
    [DataRow("geometry::Parse('MULTIPOLYGON(((0 0,0 1,1 1,0 0)))').STGeometryType()", "MultiPolygon")]
    [DataRow("geometry::Parse('LINESTRING(0 0, 1 1, 2 0)').STPointN(2).ToString()", "POINT (1 1)")]
    [DataRow("geometry::Parse('LINESTRING(0 0, 1 1, 2 0)').STStartPoint().ToString()", "POINT (0 0)")]
    [DataRow("geometry::Parse('LINESTRING(0 0, 1 1, 2 0)').STEndPoint().ToString()", "POINT (2 0)")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))').STExteriorRing().ToString()", "LINESTRING (0 0, 0 3, 3 3, 3 0, 0 0)")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))').STInteriorRingN(1).ToString()", "LINESTRING (1 1, 1 2, 2 2, 2 1, 1 1)")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))').STPointN(3).ToString()", "POINT (3 3)")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1),POLYGON((0 0,0 1,1 1,0 0)))').STGeometryN(2).ToString()", "LINESTRING (0 0, 1 1)")]
    [DataRow("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').RingN(1).ToString()", "LINESTRING (0 0, 1 0, 1 1, 0 1, 0 0)")]
    public void Accessors_ExtractComponents(string expression, string expected)
        => AreEqual(expected, Eval(expression));

    [TestMethod]
    [DataRow("geometry::Parse('LINESTRING(0 0, 1 1, 2 0)').STNumPoints()", 3)]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))').STNumPoints()", 10)]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))').STNumInteriorRing()", 1)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1),POLYGON((0 0,0 1,1 1,0 0)))').STNumGeometries()", 3)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1),POLYGON((0 0,0 1,1 1,0 0)))').STNumPoints()", 7)]
    [DataRow("geometry::Parse('POINT(1 2)').STNumGeometries()", 1)]
    [DataRow("geometry::Parse('POINT EMPTY').STNumGeometries()", 0)]
    [DataRow("geometry::Parse('POINT EMPTY').STNumPoints()", 0)]
    [DataRow("geometry::Parse('POINT(1 2)').STDimension()", 0)]
    [DataRow("geometry::Parse('LINESTRING(0 0,1 1)').STDimension()", 1)]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1,0 0))').STDimension()", 2)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2),POLYGON((0 0,0 1,1 1,0 0)))').STDimension()", 2)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION EMPTY').STDimension()", -1)]
    [DataRow("geometry::Parse('POINT EMPTY').STDimension()", -1)]
    [DataRow("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').NumRings()", 1)]
    [DataRow("geometry::Parse('POINT(1 2)').MinDbCompatibilityLevel()", 100)]
    public void Counts_MatchReal(string expression, int expected)
        => AreEqual(expected, Eval(expression));

    /// <summary>An index above the count reads as NULL, unlike an index below 1, which raises.</summary>
    [TestMethod]
    [DataRow("geometry::Parse('LINESTRING(0 0,1 1)').STPointN(9)")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2))').STGeometryN(5)")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0))').STInteriorRingN(1)")]
    [DataRow("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').RingN(9)")]
    [DataRow("geometry::Parse('POLYGON EMPTY').STExteriorRing()")]
    [DataRow("geometry::Parse('POINT EMPTY').STStartPoint()")]
    [DataRow("geometry::Parse('POINT EMPTY').STGeometryN(1)")]
    public void IndexPastTheEnd_ReadsNull(string expression)
        => AreEqual(DBNull.Value, Eval($"{expression}.ToString()"));

    [TestMethod]
    [DataRow("geometry::Parse('LINESTRING(0 0,1 1)').STPointN(0)", 24102)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2))').STGeometryN(0)", 24103)]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0))').STInteriorRingN(0)", 24104)]
    [DataRow("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').RingN(0)", 24104)]
    public void IndexBelowOne_RaisesSpatialFailure(string expression, int spatialCode)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}.ToString()", 6522);
        Assert.Contains($": {spatialCode}: ", ex.Message);
        Assert.Contains("Parameter name: n", ex.Message);
    }

    [TestMethod]
    public void Properties_ReadSinglePointOrdinates()
    {
        AreEqual(1d, Eval("geometry::Parse('POINT(1 2)').STX"));
        AreEqual(2d, Eval("geometry::Parse('POINT(1 2)').STY"));
        AreEqual(47.62d, Eval("geography::Parse('POINT(-122.35 47.62)').Lat"));
        AreEqual(-122.35d, Eval("geography::Parse('POINT(-122.35 47.62)').Long"));
        AreEqual(3d, Eval("geometry::Parse('POINT(1 2 3 4)').Z"));
        AreEqual(4d, Eval("geometry::Parse('POINT(1 2 3 4)').M"));
    }

    /// <summary>Ordinate properties are defined only on a non-empty Point.</summary>
    [TestMethod]
    [DataRow("geometry::Parse('LINESTRING(0 0,1 1)').STX")]
    [DataRow("geometry::Parse('POINT EMPTY').STX")]
    [DataRow("geometry::Parse('POINT(1 2)').Z")]
    [DataRow("geography::Parse('LINESTRING(0 0,1 1)').NumRings()")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1,0 0))').STIsRing()")]
    [DataRow("geometry::Parse('MULTIPOINT((0 0),(1 1))').STIsRing()")]
    public void UndefinedMember_ReadsNull(string expression)
        => AreEqual(DBNull.Value, Eval(expression));

    [TestMethod]
    [DataRow("geometry::Parse('POINT(1 2 3)').HasZ", true)]
    [DataRow("geometry::Parse('POINT(1 2 3)').HasM", false)]
    [DataRow("geometry::Parse('POINT(1 2 3 4)').HasM", true)]
    [DataRow("geometry::Parse('POINT EMPTY').STIsEmpty()", true)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT EMPTY)').STIsEmpty()", true)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT EMPTY, POINT(1 2))').STIsEmpty()", false)]
    [DataRow("geometry::Parse('LINESTRING(0 0, 1 1, 2 0)').STIsClosed()", false)]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))').STIsClosed()", true)]
    [DataRow("geometry::Parse('MULTILINESTRING((0 0,1 1,0 1,0 0))').STIsClosed()", true)]
    [DataRow("geometry::Parse('MULTILINESTRING((0 0,1 1),(2 2,3 3))').STIsClosed()", false)]
    [DataRow("geometry::Parse('POINT(1 2)').STIsClosed()", false)]
    [DataRow("geometry::Parse('LINESTRING EMPTY').STIsClosed()", false)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1),POLYGON((0 0,0 1,1 1,0 0)))').STIsClosed()", false)]
    [DataRow("geometry::Parse('LINESTRING(0 0,0 1,1 1,0 0)').STIsRing()", true)]
    [DataRow("geometry::Parse('POINT(1 2)').InstanceOf('Point')", true)]
    [DataRow("geometry::Parse('POINT(1 2)').InstanceOf('Geometry')", true)]
    [DataRow("geometry::Parse('POINT(1 2)').InstanceOf('Curve')", false)]
    public void Predicates_MatchReal(string expression, bool expected)
        => AreEqual(expected, Eval(expression));

    [TestMethod]
    public void Point_TakesTypeSpecificCoordinateOrder()
    {
        AreEqual("POINT (1 2)", Eval("geometry::Point(1, 2, 4326).ToString()"));
        AreEqual(4326, Eval("geometry::Point(1, 2, 4326).STSrid"));
        AreEqual("POINT (-122.3 47.6)", Eval("geography::Point(47.6, -122.3, 4326).ToString()"));
    }

    [TestMethod]
    public void PerKindConstructor_BindsItsOwnLabel()
    {
        AreEqual("POINT (1 2)", Eval("geometry::STPointFromText('POINT(1 2)', 0).ToString()"));
        AreEqual("LINESTRING (0 0, 1 1)", Eval("geometry::STLineFromText('LINESTRING(0 0,1 1)',0).ToString()"));
        AreEqual("POLYGON ((0 0, 0 1, 1 1, 0 0))", Eval("geometry::STPolyFromText('POLYGON((0 0,0 1,1 1,0 0))',0).ToString()"));
    }

    /// <summary>
    /// A per-kind constructor handed another kind reports Msg 24142, whose
    /// position and echo follow real's own idiosyncratic width rule.
    /// </summary>
    [TestMethod]
    [DataRow("geometry::STPointFromText('LINESTRING(0 0,1 1)', 0)", "Expected \"POINT\" at position 1. The input has \"LINES\".")]
    [DataRow("geometry::STPointFromText('PO', 0)", "Expected \"POINT\" at position 0. The input has \"P\".")]
    [DataRow("geometry::STLineFromText('POINT(1 2)', 0)", "Expected \"LINESTRING\" at position 0. The input has \"POINT(1 2)\".")]
    [DataRow("geometry::STPolyFromText('POINT(1 2)', 0)", "Expected \"POLYGON\" at position 1. The input has \"POINT(1\".")]
    [DataRow("geometry::STGeomCollFromText('POINT(1 2)', 0)", "Expected \"GEOMETRYCOLLECTION\" at position 0. The input has \"P\".")]
    [DataRow("geometry::Parse('POINTX(1 2)')", "Expected \"(\" at position 5. The input has \"X\".")]
    [DataRow("geometry::Parse('MULTIPOINT((0 0), 1 1)')", "Expected \"(\" at position 18. The input has \"1\".")]
    public void WrongLabel_RaisesTokenExpected(string expression, string expectedText)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}.ToString()", 6522);
        Assert.Contains($"System.FormatException: 24142: {expectedText}", ex.Message);
    }

    [TestMethod]
    [DataRow("geometry::Parse('NONSENSE(1 2) EXTRA')", 24114, "The label NONSENSE(1 2) EXTRA in the input")]
    [DataRow("geometry::Parse('  BAD (1 2)')", 24114, "The label BAD (1 2) in the input")]
    [DataRow("geometry::Parse('POINT(1)')", 24141, "A number is expected at position 7 of the input. The input has ).")]
    [DataRow("geometry::Parse('POINT(1 X)')", 24141, "A number is expected at position 9 of the input. The input has X.")]
    [DataRow("geometry::Parse('LINESTRING(0 0,1 X)')", 24141, "A number is expected at position 18 of the input. The input has X.")]
    [DataRow("geometry::Parse('LINESTRING(0 0,)')", 24141, "A number is expected at position 15 of the input. The input has ).")]
    [DataRow("geometry::Parse('POINT()')", 24141, "A number is expected at position 6 of the input. The input has ).")]
    [DataRow("geometry::Parse('POINT(1 2')", 24209, "Unexpected end of input.")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT(1 2)')", 24209, "Unexpected end of input.")]
    [DataRow("geometry::Parse('POINT(1 2)X')", 24111, "The well-known text (WKT) input is not valid.")]
    [DataRow("geometry::Parse('')", 24112, "The well-known text (WKT) input is empty.")]
    [DataRow("geometry::Parse('LINESTRING(0 0)')", 24117, "The LineString input is not valid because it does not have enough points.")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1))')", 24118, "the exterior ring does not have enough points")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1,0 0),(1 1,1 2))')", 24120, "the interior ring number 1 does not have enough points")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 1,1 1,1 0))')", 24119, "the start and end points of the exterior ring are not the same")]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 9))')", 24121, "the start and end points of the interior ring number 1 are not the same")]
    [DataRow("geography::Parse('POINT(2 200)')", 24201, "Latitude values must be between -90 and 90 degrees.")]
    [DataRow("geometry::Parse('FULLGLOBE')", 24303, "The OpenGisGeometryType provided, FullGlobe, is not valid.")]
    public void MalformedInput_RaisesRealsFailure(string expression, int spatialCode, string expectedFragment)
    {
        var ex = new Simulation().AssertSqlError($"select {expression}.ToString()", 6522);
        Assert.Contains($": {spatialCode}: ", ex.Message);
        Assert.Contains(expectedFragment, ex.Message);
    }

    /// <summary>The wrapper real puts around every spatial failure, reproduced through the exception-type line.</summary>
    [TestMethod]
    public void SpatialFailure_ReproducesTheDotNetRoutineWrapper()
    {
        var ex = new Simulation().AssertSqlError("select geometry::Parse('NONSENSE').ToString()", 6522);
        StartsWith(
            "A .NET Framework error occurred during execution of user-defined routine or aggregate \"geometry\": \r\nSystem.FormatException: 24114: ",
            ex.Message);
        Assert.Contains("\r\nSystem.FormatException: \r\n.", ex.Message);
    }

    /// <summary>
    /// A method name written without parentheses, and a property written with
    /// them, both report the CLR member-not-found error real reports.
    /// </summary>
    [TestMethod]
    [DataRow("geometry::Parse('POINT(1 2)').STNumPoints", "STNumPoints", "SqlGeometry")]
    [DataRow("geometry::Parse('POINT(1 2)').Lat", "Lat", "SqlGeometry")]
    [DataRow("geography::Parse('POINT(1 2)').STX", "STX", "SqlGeography")]
    public void WrongMemberForm_RaisesPropertyNotFound(string expression, string member, string clrType)
        => new Simulation().AssertSqlError(
            $"select {expression}",
            6592,
            $"Could not find property or field '{member}' for type 'Microsoft.SqlServer.Types.{clrType}' in assembly 'Microsoft.SqlServer.Types'.");

    /// <summary>A method belonging to the other spatial type reports Msg 6506 — which real emits without a trailing period.</summary>
    [TestMethod]
    public void ForeignMethod_RaisesMethodNotFound()
        => new Simulation().AssertSqlError(
            "select geometry::Parse('POLYGON((0 0,0 1,1 1,0 0))').NumRings()",
            6506,
            "Could not find method 'NumRings' for type 'Microsoft.SqlServer.Types.SqlGeometry' in assembly 'Microsoft.SqlServer.Types'");

    [TestMethod]
    public void Constructor_ChecksArgumentCountAtParse()
    {
        new Simulation().AssertSqlError("select geometry::STGeomFromText('POINT(1 2)')", 174, "The STGeomFromText function requires 2 argument(s).");
        new Simulation().AssertSqlError("select geometry::Point(1, 2)", 174, "The Point function requires 3 argument(s).");
    }

    /// <summary>A NULL receiver yields NULL from every member rather than raising.</summary>
    [TestMethod]
    public void NullInstance_ReadsNullThroughout()
    {
        var sim = new Simulation();
        AreEqual(DBNull.Value, sim.ExecuteScalar("declare @g geometry = null; select @g.STGeometryType()"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("declare @g geometry = null; select @g.STIsEmpty()"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("declare @g geometry = null; select @g.STSrid"));
    }

    /// <summary>A spatial column's byte length is its serialization, not its text.</summary>
    [TestMethod]
    public void DataLength_MeasuresTheSerialization()
        => AreEqual(22, Eval("datalength(geography::Parse('POINT(-122.35 47.62)'))"));

    /// <summary>Real folds negative zero onto positive zero on the way in, so it never reaches the text or the bytes.</summary>
    [TestMethod]
    public void NegativeZero_NormalizesOnParse()
    {
        AreEqual("POINT (0 0)", Eval("geometry::Parse('POINT(-0 -0)').ToString()"));
        AreEqual("LINESTRING (0 0, 1 1)", Eval("geometry::Parse('LINESTRING(-0 -0, 1 1)').ToString()"));
        AreEqual(0d, Eval("geometry::Parse('POINT(-0 -0)').STX"));
        AreEqual("00000000010C00000000000000000000000000000000", Convert.ToHexString((byte[])Eval("cast(geometry::Parse('POINT(-0 -0)') as varbinary(max))")!));
    }

    /// <summary>
    /// <c>InstanceOf</c>'s root type is <c>Geometry</c> for both spatial types,
    /// and a name outside the OGC hierarchy is an argument failure rather than
    /// a false answer. <c>FullGlobe</c> is the one name whose validity is
    /// type-specific.
    /// </summary>
    [TestMethod]
    public void InstanceOf_ValidatesItsArgument()
    {
        IsTrue((bool)Eval("geography::Parse('POINT(1 2)').InstanceOf('Geometry')")!);
        IsTrue((bool)Eval("geometry::Parse('POINT(1 2)').InstanceOf('point')")!);
        IsFalse((bool)Eval("geography::Parse('POINT(1 2)').InstanceOf('FullGlobe')")!);
        foreach (var name in new[] { "Geography", "Bogus" })
        {
            var ex = new Simulation().AssertSqlError($"select geography::Parse('POINT(1 2)').InstanceOf('{name}')", 6522);
            Assert.Contains($"24105: The geometryType argument in InstanceOf ('{name}') is not valid.", ex.Message);
        }
        // FullGlobe is a geography-only kind, so naming it against geometry is invalid rather than false.
        Assert.Contains("24105", new Simulation().AssertSqlError("select geometry::Parse('POINT(1 2)').InstanceOf('FullGlobe')", 6522).Message);
    }

    /// <summary><c>STSrid</c> is the one assignable spatial member.</summary>
    [TestMethod]
    public void StSrid_IsSettable()
    {
        var sim = new Simulation();
        AreEqual(4269, sim.ExecuteScalar("declare @g geography = geography::Parse('POINT(1 2)'); set @g.STSrid = 4269; select @g.STSrid"));
        AreEqual("POINT (1 2)", sim.ExecuteScalar("declare @g geography = geography::Parse('POINT(1 2)'); set @g.STSrid = 4269; select @g.ToString()"));
    }

    [TestMethod]
    public void StSrid_RejectsOutOfRangeAndNull()
    {
        Assert.Contains("24100", new Simulation().AssertSqlError("select geometry::STGeomFromText('POINT(1 2)', -5).ToString()", 6522).Message);
        Assert.Contains("24100", new Simulation().AssertSqlError("declare @g geometry = geometry::Parse('POINT(1 2)'); set @g.STSrid = -1; select @g.STSrid", 6522).Message);
        Assert.Contains(
            "System.ArgumentNullException: Value cannot be null.",
            new Simulation().AssertSqlError("declare @g geometry = geometry::Parse('POINT(1 2)'); set @g.STSrid = null; select @g.STSrid", 6522).Message);
    }

    /// <summary>Assigning any other spatial property reports the read-only error rather than silently working.</summary>
    [TestMethod]
    public void ReadOnlyProperty_RejectsAssignment()
        => new Simulation().AssertSqlError(
            "declare @g geometry = geometry::Parse('POINT(1 2)'); set @g.STX = 5; select @g.ToString()",
            6595,
            "Could not assign to property 'STX' for type 'Microsoft.SqlServer.Types.SqlGeometry' in assembly 'Microsoft.SqlServer.Types' because it is read only.");

    /// <summary>Geography's <c>ReorientObject()</c> reverses ring orientation, which is what flips a polygon's inside and outside.</summary>
    [TestMethod]
    public void ReorientObject_ReversesRingOrder()
        => AreEqual(
            "POLYGON ((0 0, 0 1, 1 1, 1 0, 0 0))",
            Eval("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').ReorientObject().ToString()"));

    /// <summary>Real accepts the curved kinds; the simulator names them as unbuilt rather than rejecting them as unknown labels.</summary>
    [TestMethod]
    public void CurvedShapes_ReportUnmodeled()
    {
        var ex = Throws<NotSupportedException>(() => Eval("geometry::Parse('CIRCULARSTRING(0 0, 1 1, 2 0)').ToString()"));
        Assert.Contains("CircularString", ex.Message);
    }

    /// <summary>
    /// Planar <c>STArea()</c> and <c>STLength()</c> over every shape kind.
    /// Probe-confirmed against SQL Server 2025 (2026-07-31); a polygon's
    /// length is its boundary, and a shape of the wrong dimension measures 0
    /// rather than NULL.
    /// </summary>
    [TestMethod]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0))').STArea()", 9.0)]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))').STArea()", 8.0)]
    [DataRow("geometry::Parse('POLYGON((0 0,4 0,4 3,0 0))').STArea()", 6.0)]
    [DataRow("geometry::Parse('MULTIPOLYGON(((0 0,0 2,2 2,2 0,0 0)),((5 5,5 6,6 6,6 5,5 5)))').STArea()", 5.0)]
    [DataRow("geometry::Parse('POINT(1 2)').STArea()", 0.0)]
    [DataRow("geometry::Parse('POLYGON EMPTY').STArea()", 0.0)]
    [DataRow("geometry::Parse('LINESTRING(0 0,3 4)').STLength()", 5.0)]
    [DataRow("geometry::Parse('LINESTRING(0 0,3 4,3 0)').STLength()", 9.0)]
    [DataRow("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0))').STLength()", 12.0)]
    [DataRow("geometry::Parse('MULTILINESTRING((0 0,3 4),(0 0,0 5))').STLength()", 10.0)]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POLYGON((0 0,0 2,2 2,2 0,0 0)),LINESTRING(0 0,3 4))').STLength()", 13.0)]
    [DataRow("geometry::Parse('POINT(1 2)').STLength()", 0.0)]
    [DataRow("geometry::Parse('LINESTRING EMPTY').STLength()", 0.0)]
    public void PlanarMeasures_MatchReal(string expression, double expected)
        => AreEqual(expected, Eval(expression));

    /// <summary>
    /// Ring orientation doesn't change a planar area — the shoelace sum is
    /// taken per ring in absolute value, so a clockwise polygon measures the
    /// same as the counter-clockwise spelling of the same shape.
    /// </summary>
    [TestMethod]
    public void PlanarArea_IgnoresRingOrientation()
        => AreEqual(
            Eval("geometry::Parse('POLYGON((0 0,0 3,3 3,3 0,0 0))').STArea()"),
            Eval("geometry::Parse('POLYGON((0 0,3 0,3 3,0 3,0 0))').STArea()"));

    /// <summary>
    /// The round-earth measures stay unmodeled: real measures <c>geography</c>
    /// along the great elliptic arc, which is a different curve from the
    /// geodesic and not a coordinate swap over the planar code.
    /// </summary>
    [TestMethod]
    [DataRow("geography::Parse('LINESTRING(0 0, 0 1)').STLength()")]
    [DataRow("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').STArea()")]
    public void GeographyMeasures_ReportUnmodeled(string expression)
    {
        var ex = Throws<NotSupportedException>(() => Eval(expression));
        Assert.Contains("great elliptic", ex.Message);
    }
}
