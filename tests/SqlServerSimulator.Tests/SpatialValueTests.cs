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
    /// Round-earth length and distance measure along the <b>great elliptic
    /// arc</b> — the curve real uses, which is not the geodesic. The expected
    /// values are real's own (2026-07-31); the tolerance is relative because
    /// the simulator computes the arc exactly while real carries ~6e-9 of its
    /// own approximation error, worst over a quarter meridian.
    /// </summary>
    [TestMethod]
    [DataRow("geography::Parse('LINESTRING(0 0, 0 1)').STLength()", 110574.38849340599)]
    [DataRow("geography::Parse('LINESTRING(0 0, 1 0)').STLength()", 111319.49073588519)]
    [DataRow("geography::Parse('LINESTRING(0 0, 1 1)').STLength()", 156899.5679650511)]
    [DataRow("geography::Parse('LINESTRING(0 0,1 0,1 1)').STLength()", 221893.87922929117)]
    [DataRow("geography::Parse('POLYGON((0 0,1 0,1 1,0 1,0 0))').STLength()", 443770.9170048034)]
    [DataRow("geography::Parse('MULTILINESTRING((0 0,1 1),(2 2,3 3))').STLength()", 313728.8967557313)]
    [DataRow("geography::Parse('POINT(0 0)').STDistance(geography::Parse('POINT(0 1)'))", 110574.38849340599)]
    [DataRow("geography::Parse('POINT(-122.35 47.62)').STDistance(geography::Parse('POINT(2.35 48.86)'))", 8064123.530150785)]
    [DataRow("geography::Parse('POINT(0 0)').STDistance(geography::Parse('POINT(0 90)'))", 10001965.67018311)]
    [DataRow("geography::Parse('POINT(139.7 35.7)').STDistance(geography::Parse('POINT(-74.0 40.7)'))", 10872930.202557612)]
    public void GreatEllipticMeasures_MatchRealWithinItsOwnError(string expression, double realValue)
    {
        var actual = (double)Eval(expression)!;
        var relative = Math.Abs(actual - realValue) / realValue;
        Assert.IsLessThan(1e-8, relative, $"relative error {relative:E3} exceeds 1e-8 (got {actual:R}, real {realValue:R})");
    }

    /// <summary>
    /// The great elliptic arc is <i>longer</i> than the geodesic on an oblique
    /// path and identical along a meridian. Seattle→Paris is where the two
    /// separate by metres — the measurement that identified the curve.
    /// </summary>
    [TestMethod]
    public void GreatEllipticArc_ExceedsTheGeodesicOnObliquePaths()
    {
        // Vincenty geodesic for the same pair, computed independently.
        const double Geodesic = 8064120.203344;
        var actual = (double)Eval("geography::Parse('POINT(-122.35 47.62)').STDistance(geography::Parse('POINT(2.35 48.86)'))")!;
        Assert.IsGreaterThan(3.0, actual - Geodesic, $"expected the great elliptic arc to exceed the geodesic by ~3.3 m, got {actual - Geodesic:F3}");
    }

    /// <summary>
    /// Exactly antipodal points define no unique plane, and real measures the
    /// smallest central section between them — half the meridian ellipse's
    /// perimeter, not half the equator's, which is 33 km longer.
    /// </summary>
    [TestMethod]
    public void GreatEllipticArc_AntipodalPoints_MeasureHalfTheMeridian()
    {
        var actual = (double)Eval("geography::Parse('POINT(0 0)').STDistance(geography::Parse('POINT(180 0)'))")!;
        AreEqual(20003931.458240643, actual, 1e-3);
    }

    /// <summary>
    /// Planar <c>STDistance</c> is the straight-line closest approach over every
    /// shape pair: the perpendicular foot where it lands on an edge, the nearer
    /// endpoint where it doesn't, and zero where the instances meet or one
    /// contains the other. A point inside a polygon's <i>hole</i> is outside the
    /// polygon and measures to the hole's ring. Every value is real's own, and
    /// the measure is symmetric, so each row is asserted both ways round.
    /// </summary>
    [TestMethod]
    [DataRow("POINT(1 1)", "LINESTRING(0 0, 4 0)", 1.0)]
    [DataRow("POINT(-3 4)", "LINESTRING(0 0, 4 0)", 5.0)]
    [DataRow("POINT(2 0)", "LINESTRING(0 0, 4 0)", 0.0)]
    [DataRow("POINT(2 5)", "MULTILINESTRING((0 0, 4 0),(0 9, 4 9))", 4.0)]
    [DataRow("POINT(-1 5)", "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))", 1.4142135623730951)]
    [DataRow("POINT(2 2)", "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))", 0.0)]
    [DataRow("POINT(0 2)", "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))", 0.0)]
    [DataRow("POINT(2 2)", "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0),(1 1, 1 3, 3 3, 3 1, 1 1))", 1.0)]
    [DataRow("LINESTRING(0 0, 4 0)", "LINESTRING(0 3, 4 3)", 3.0)]
    [DataRow("LINESTRING(0 0, 4 0)", "LINESTRING(5 1, 8 6)", 1.4142135623730951)]
    [DataRow("LINESTRING(0 0, 4 4)", "LINESTRING(0 4, 4 0)", 0.0)]
    [DataRow("LINESTRING(0 0, 2 2)", "LINESTRING(2 2, 4 0)", 0.0)]
    [DataRow("LINESTRING(-1 2, 5 2)", "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))", 0.0)]
    [DataRow("LINESTRING(-3 2, -1 2)", "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))", 1.0)]
    [DataRow("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))", "POLYGON((3 0, 4 0, 4 1, 3 1, 3 0))", 2.0)]
    [DataRow("POLYGON((0 0, 2 0, 2 2, 0 2, 0 0))", "POLYGON((1 1, 3 1, 3 3, 1 3, 1 1))", 0.0)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))", "POLYGON((1 1, 2 1, 2 2, 1 2, 1 1))", 0.0)]
    [DataRow("POLYGON((0 0, 6 0, 6 6, 0 6, 0 0),(1 1, 1 5, 5 5, 5 1, 1 1))", "POLYGON((2 2, 3 2, 3 3, 2 3, 2 2))", 1.0)]
    [DataRow("MULTIPOINT((0 0),(10 10))", "MULTIPOINT((3 4),(20 20))", 5.0)]
    [DataRow("GEOMETRYCOLLECTION(POINT(0 0), LINESTRING(10 10, 12 12))", "POINT(11 10)", 0.7071067811865476)]
    public void PlanarDistance_MatchesReal(string left, string right, double expected)
    {
        AreEqual(expected, Eval($"geometry::Parse('{left}').STDistance(geometry::Parse('{right}'))"));
        AreEqual(expected, Eval($"geometry::Parse('{right}').STDistance(geometry::Parse('{left}'))"));
    }

    /// <summary>
    /// Round-earth <c>STDistance</c> is the closest approach along great
    /// elliptic arcs, and the discriminating case is a perpendicular foot that
    /// lands mid-arc rather than on a vertex. Values are real's own
    /// (2026-08-02); the tolerance is per row because what varies is real's own
    /// arc-length accuracy, which is 1e-11 or better on most of these and
    /// reaches 1e-8 where the measure runs a long way along the equator.
    /// </summary>
    [TestMethod]
    [DataRow("POINT(45 60)", "LINESTRING(-122.35 47.62, 2.35 48.86)", 2953566.2952130623, 1e-9)]
    [DataRow("POINT(0 10)", "LINESTRING(-30 0, 30 0)", 1105854.8440379163, 1e-8)]
    [DataRow("POINT(-140 40)", "LINESTRING(-122.35 47.62, 2.35 48.86)", 1647653.4612717708, 1e-9)]
    [DataRow("POINT(0.005 0.001)", "LINESTRING(0 0, 0.01 0)", 110.57427581595613, 1e-9)]
    [DataRow("POINT(-1 0.5)", "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))", 111315.2799742638, 1e-9)]
    [DataRow("POINT(0.5 0.5)", "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0),(0.2 0.2, 0.2 0.8, 0.8 0.8, 0.8 0.2, 0.2 0.2))", 33171.99278780328, 1e-9)]
    [DataRow("POINT(-170 -80)", "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))", 11101669.988725359, 1e-9)]
    [DataRow("LINESTRING(0 0, 10 0)", "LINESTRING(0 5, 10 5)", 552885.4511005873, 1e-9)]
    [DataRow("LINESTRING(0 0, 10 0)", "LINESTRING(20 1, 30 8)", 1118617.1615951585, 1e-9)]
    [DataRow("LINESTRING(0 0, 10 0)", "LINESTRING(20 0, 30 0)", 1113194.9192238282, 2e-8)]
    [DataRow("LINESTRING(0 0, 0 10)", "LINESTRING(0 20, 0 30)", 1106511.4317928148, 2e-8)]
    [DataRow("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))", "POLYGON((3 0, 4 0, 4 1, 3 1, 3 0))", 222605.29426164986, 1e-8)]
    [DataRow("MULTIPOINT((0 0),(10 10))", "LINESTRING(20 20, 21 21)", 1541856.4391023766, 1e-9)]
    public void GeographyDistance_MatchesRealWithinItsOwnError(string left, string right, double realValue, double tolerance)
    {
        AssertRelative(realValue, $"geography::Parse('{left}').STDistance(geography::Parse('{right}'))", tolerance);
        AssertRelative(realValue, $"geography::Parse('{right}').STDistance(geography::Parse('{left}'))", tolerance);
    }

    /// <summary>
    /// Instances that meet measure zero on the round earth as well: crossing
    /// arcs, a point inside a polygon, and a polygon inside another.
    /// </summary>
    [TestMethod]
    [DataRow("LINESTRING(-1 -1, 1 1)", "LINESTRING(-1 1, 1 -1)")]
    [DataRow("LINESTRING(0 0, 10 0)", "LINESTRING(5 0, 15 0)")]
    [DataRow("POINT(0.5 0.5)", "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))")]
    [DataRow("POLYGON((0 0, 2 0, 2 2, 0 2, 0 0))", "POLYGON((1 1, 3 1, 3 3, 1 3, 1 1))")]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))", "POLYGON((1 1, 2 1, 2 2, 1 2, 1 1))")]
    public void GeographyDistance_TouchingInstances_MeasureZero(string left, string right)
    {
        AreEqual(0.0, Eval($"geography::Parse('{left}').STDistance(geography::Parse('{right}'))"));
        AreEqual(0.0, Eval($"geography::Parse('{right}').STDistance(geography::Parse('{left}'))"));
    }

    /// <summary>
    /// Operands in different spatial reference systems aren't comparable and
    /// an empty operand has no position — real answers NULL to both rather
    /// than raising. Probe-confirmed.
    /// </summary>
    [TestMethod]
    [DataRow("geometry::Parse('POINT(0 0)').STDistance(geometry::STGeomFromText('POINT(3 4)',4326))")]
    [DataRow("geometry::Parse('POINT EMPTY').STDistance(geometry::Parse('POINT(3 4)'))")]
    [DataRow("geometry::Parse('POINT(0 0)').STDistance(geometry::Parse('POINT EMPTY'))")]
    [DataRow("geometry::Parse('GEOMETRYCOLLECTION(POINT EMPTY)').STDistance(geometry::Parse('POINT(1 1)'))")]
    [DataRow("geometry::Parse('POINT(0 0)').STDistance(null)")]
    [DataRow("geography::Parse('POINT EMPTY').STDistance(geography::Parse('POINT(1 1)'))")]
    public void Distance_UncomparableOperands_ReadNull(string expression)
        => AreEqual(DBNull.Value, Eval(expression));

    /// <summary>A distance argument may be written as well-known text, which real reads as an instance of the receiver's type.</summary>
    [TestMethod]
    public void Distance_StringArgument_ReadsAsWellKnownText()
        => AreEqual(5.0, Eval("geometry::Parse('POINT(0 0)').STDistance('POINT(3 4)')"));

    /// <summary>
    /// Round-earth <c>STArea()</c> integrates the ellipsoid's own surface
    /// element over the region its <b>great elliptic</b> edges bound — a
    /// "horizontal" edge bulges poleward between its endpoints and the area
    /// follows the bulge, which is what separates real's answer from the
    /// parallel-bounded quadrangle by 3e-3 m² on a 0.01° square.
    /// </summary>
    /// <remarks>
    /// Values are real's own (2026-08-02) and the tolerance is per row, because
    /// what varies across the matrix is <i>real's</i> accuracy: it holds 1e-10
    /// or better on ordinary polygons and degrades on edges spanning a large
    /// longitude range, the worse the nearer the pole they run — see
    /// <c>docs/claude/spatial.md</c>.
    /// </remarks>
    [TestMethod]
    [DataRow("POLYGON((0 0, 0.01 0, 0.01 0.01, 0 0.01, 0 0))", 1230907.2048772429, 1e-9)]
    [DataRow("POLYGON((137 0, 137.01 0, 137.01 0.01, 137 0.01, 137 0))", 1230907.2048797607, 1e-9)]
    [DataRow("POLYGON((0 -0.01, 0.01 -0.01, 0.01 0, 0 0, 0 -0.01))", 1230907.2048772429, 1e-9)]
    [DataRow("POLYGON((0 60, 0.01 60, 0.01 60.01, 0 60.01, 0 60))", 621587.2415050108, 1e-9)]
    [DataRow("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))", 12308776255.868843, 1e-9)]
    [DataRow("POLYGON((-10 40, 10 40, 10 55, -10 55, -10 40))", 2489995392880.062, 1e-9)]
    [DataRow("POLYGON((-5 -5, 5 -5, 5 5, -5 5, -5 -5))", 1232493798489.4854, 1e-9)]
    [DataRow("POLYGON((0 0, 10 0, 10 0.0001, 0 0.0001, 0 0))", 12340413.869161015, 1e-9)]
    [DataRow("POLYGON((0 89, 1 89, 1 89.5, 0 89.5, 0 89))", 81645307.09980054, 1e-9)]
    [DataRow("POLYGON((0 40, 20 40, 20 50, 0 50, 0 40))", 1741374661124.119, 1e-9)]
    [DataRow("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0),(0.2 0.2, 0.2 0.8, 0.8 0.8, 0.8 0.2, 0.2 0.2))", 7877653739.931041, 1e-9)]
    [DataRow("MULTIPOLYGON(((0 0, 1 0, 1 1, 0 1, 0 0)),((5 5, 6 5, 6 6, 5 6, 5 5)))", 24562837957.141075, 1e-9)]
    [DataRow("GEOMETRYCOLLECTION(POLYGON((0 0, 1 0, 1 1, 0 1, 0 0)), LINESTRING(0 0, 3 4), POINT(9 9))", 12308776246.986383, 5e-9)]
    [DataRow("POLYGON((0 -89, 1 -89, 1 89, 0 89, 0 -89))", 1416631205336.26, 1e-7)]
    [DataRow("POLYGON((0 0, 90 0, 90 1, 0 1, 0 0))", 1410304200101.5747, 1e-7)]
    [DataRow("POLYGON((0 0, 90 0, 90 90, 0 90, 0 0))", 63758201600773.19, 1e-7)]
    [DataRow("POLYGON((0 0, 90 0, 0 90, 0 0))", 63758201600773.19, 1e-7)]
    [DataRow("POLYGON((0 0, 90 0, 180 0, 270 0, 0 0))", 255032806403092.7, 1e-7)]
    [DataRow("POLYGON((0 60, 90 60, 180 60, 270 60, 0 60))", 23453971598496.586, 1e-4)]
    [DataRow("POLYGON((0 89, 90 89, 180 89, 270 89, 0 89))", 24955024856.362816, 3e-4)]
    [DataRow("POLYGON((0 89, 90 89, 90 89.5, 0 89.5, 0 89))", 4679122332.091554, 3e-4)]
    public void EllipsoidalArea_MatchesRealWithinItsOwnError(string wkt, double realValue, double tolerance)
        => AssertRelative(realValue, $"geography::Parse('{wkt}').STArea()", tolerance);

    /// <summary>
    /// Real's own accuracy is what the coarse-polygon tolerances above absorb:
    /// a four-vertex ring around the pole differs from the model by 1e-4 while
    /// the same cap written with 360 vertices — the same region, shorter edges —
    /// comes back within 1e-8.
    /// </summary>
    [TestMethod]
    public void EllipsoidalArea_PolarCap_ClosesOnRealAsItsEdgesShorten()
    {
        var vertices = string.Join(", ", Enumerable.Range(0, 360).Select(i => $"{i} 89"));
        AssertRelative(39190016078.92191, $"geography::Parse('POLYGON(({vertices}, 0 89))').STArea()", 1e-8);
    }

    /// <summary>
    /// A <c>geography</c> ring carries orientation — its interior lies to the
    /// left of the direction it is written — so the clockwise spelling of a
    /// square names everything except that square and measures the rest of the
    /// globe. The planar type ignores orientation entirely.
    /// </summary>
    [TestMethod]
    public void EllipsoidalArea_ReversedRing_MeasuresTheComplement()
    {
        AssertRelative(510065620480089.25, "geography::Parse('POLYGON((0 0, 0 0.01, 0.01 0.01, 0.01 0, 0 0))').STArea()", 1e-9);
        var square = (double)Eval("geography::Parse('POLYGON((0 0, 0.01 0, 0.01 0.01, 0 0.01, 0 0))').STArea()")!;
        var complement = (double)Eval("geography::Parse('POLYGON((0 0, 0 0.01, 0.01 0.01, 0.01 0, 0 0))').STArea()")!;
        AreEqual(510065621724088.56, square + complement, 1.0);
    }

    /// <summary>A shape of the wrong dimension has no round-earth area either.</summary>
    [TestMethod]
    [DataRow("POINT(0 0)")]
    [DataRow("LINESTRING(0 0, 1 1)")]
    [DataRow("POLYGON EMPTY")]
    [DataRow("POINT EMPTY")]
    public void EllipsoidalArea_WrongDimension_MeasuresZero(string wkt)
        => AreEqual(0.0, Eval($"geography::Parse('{wkt}').STArea()"));

    /// <summary>
    /// Asserts a measurement against real's own value on a relative tolerance:
    /// the simulator computes its model exactly, so the gap that remains is
    /// real's approximation rather than the simulator's.
    /// </summary>
    private static void AssertRelative(double realValue, string expression, double tolerance)
    {
        var actual = (double)Eval(expression)!;
        var relative = Math.Abs(actual - realValue) / realValue;
        Assert.IsLessThan(tolerance, relative, $"relative error {relative:E3} exceeds {tolerance:E0} (got {actual:R}, real {realValue:R})");
    }
}
