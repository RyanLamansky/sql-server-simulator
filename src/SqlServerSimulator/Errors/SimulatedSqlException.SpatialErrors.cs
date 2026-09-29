using System.Globalization;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Builds SQL Server error 6522: the spatial library raised, and the server
    /// reports it as a failure of the user-defined routine that hosts the type.
    /// Every spatial 24xxx failure reaches a client this way, so each factory
    /// below funnels through here.
    /// </summary>
    /// <param name="isGeography">Selects the routine name real quotes — <c>geography</c> or <c>geometry</c>.</param>
    /// <param name="clrExceptionType">The .NET exception name real names in the wrapped text.</param>
    /// <param name="code">The 24xxx code real prefixes to the message.</param>
    /// <param name="message">The message body.</param>
    /// <param name="parameterName">Argument name real appends on a <c>Parameter name:</c> line, when it emits one.</param>
    /// <remarks>
    /// Real appends the .NET stack frames of its own spatial assembly after the
    /// repeated exception-type line; the simulator stops at that line, since
    /// the frames name internal Microsoft methods that have no counterpart
    /// here. Everything through the <c>24nnn: </c> message — and the
    /// <c>Parameter name:</c> line an argument failure carries — is reproduced
    /// verbatim.
    /// </remarks>
    private static SimulatedSqlException SpatialFailure(
        bool isGeography,
        string clrExceptionType,
        int code,
        string message,
        string? parameterName = null)
    {
        // A failure raised by the hosting layer rather than by the spatial
        // library carries no 24xxx code — a null SRID assignment is the one
        // that reaches here that way.
        var prefix = code == 0 ? string.Empty : $"{code.ToString(CultureInfo.InvariantCulture)}: ";
        return ClrTypeFailure(isGeography ? "geography" : "geometry", clrExceptionType, prefix + message, parameterName, state: 1);
    }

    /// <summary>
    /// Msg 6522 as a system CLR type's library reports it: the routine line,
    /// the exception type and message, the <c>Parameter name:</c> line an
    /// argument failure carries, and the repeated exception type — stopping
    /// before the stack frames real appends, which name internal Microsoft
    /// methods (see <see cref="SpatialFailure"/>).
    /// </summary>
    private static SimulatedSqlException ClrTypeFailure(string routine, string clrExceptionType, string message, string? parameterName, byte state)
    {
        var parameter = parameterName is null ? string.Empty : $"Parameter name: {parameterName}\r\n";
        return new(
            $"A .NET Framework error occurred during execution of user-defined routine or aggregate \"{routine}\": \r\n"
            + $"{clrExceptionType}: {message}\r\n"
            + parameter
            + $"{clrExceptionType}: \r\n.",
            6522,
            16,
            state);
    }

    private const string SpatialFormat = "System.FormatException";
    private const string SpatialOutOfRange = "System.ArgumentOutOfRangeException";
    private const string SpatialArgument = "System.ArgumentException";

    /// <summary>The spatial library's own exception type, which the round-earth checks report under rather than a <c>System.</c> one.</summary>
    private const string SpatialGeodetic = "Microsoft.SqlServer.Types.GLArgumentException";

    /// <summary>
    /// The label list real names in <see cref="SpatialInvalidLabel"/> — the
    /// curved and whole-globe kinds appear here because real accepts them,
    /// even though no operation evaluates one.
    /// </summary>
    private const string SpatialValidLabels =
        "POINT, LINESTRING, POLYGON, MULTIPOINT, MULTILINESTRING, MULTIPOLYGON, GEOMETRYCOLLECTION, "
        + "CIRCULARSTRING, COMPOUNDCURVE, CURVEPOLYGON and FULLGLOBE (geography Data Type only)";

    /// <summary>24114 — the leading word isn't a recognized WKT label. Real echoes the whole remaining input, not just the word.</summary>
    internal static SimulatedSqlException SpatialInvalidLabel(bool isGeography, string input) => SpatialFailure(
        isGeography, SpatialFormat, 24114,
        $"The label {input} in the input well-known text (WKT) is not valid. Valid labels are {SpatialValidLabels}.");

    /// <summary>24141 — a coordinate slot holds something that isn't a number.</summary>
    internal static SimulatedSqlException SpatialNumberExpected(bool isGeography, int position, string token) => SpatialFailure(
        isGeography, SpatialFormat, 24141,
        $"A number is expected at position {position.ToString(CultureInfo.InvariantCulture)} of the input. The input has {token}.");

    /// <summary>24142 — a required literal (a type label, or a punctuation character) isn't there.</summary>
    internal static SimulatedSqlException SpatialTokenExpected(bool isGeography, string expected, int position, string actual) => SpatialFailure(
        isGeography, SpatialFormat, 24142,
        $"Expected \"{expected}\" at position {position.ToString(CultureInfo.InvariantCulture)}. The input has \"{actual}\".");

    /// <summary>24111 — the input parsed but has trailing content.</summary>
    internal static SimulatedSqlException SpatialWktNotValid(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24111, "The well-known text (WKT) input is not valid.");

    /// <summary>24112 — the input is empty or all whitespace.</summary>
    internal static SimulatedSqlException SpatialWktEmpty(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24112,
        "The well-known text (WKT) input is empty. To input an empty instance, specify an empty instance of one of "
        + "the following types: Point, LineString, Polygon, MultiPoint, MultiLineString, MultiPolygon, CircularString, "
        + "CompoundCurve, CurvePolygon or GeometryCollection.");

    /// <summary>24209 — the input stopped mid-shape.</summary>
    internal static SimulatedSqlException SpatialUnexpectedEndOfInput(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24209,
        "Unexpected end of input. Check that the input data is complete and has not been truncated.");

    /// <summary>24117 — a LineString with fewer than two points.</summary>
    internal static SimulatedSqlException SpatialLineStringTooFewPoints(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24117,
        "The LineString input is not valid because it does not have enough points. A LineString must have at least two points.");

    /// <summary>
    /// 24118 / 24120 — a polygon ring with fewer than four points. <c>geometry</c>
    /// names the exterior ring differently from a numbered interior one;
    /// <c>geography</c> numbers every ring from 1 under 24305.
    /// </summary>
    internal static SimulatedSqlException SpatialRingTooFewPoints(bool isGeography, int interiorRingNumber, bool planarNumbering = false) => isGeography && !planarNumbering
        ? SpatialFailure(isGeography, SpatialFormat, 24305,
            $"The Polygon input is not valid because the ring number {(interiorRingNumber + 1).ToString(CultureInfo.InvariantCulture)} does not have enough points. Each ring of a polygon must contain at least four points.")
        : interiorRingNumber == 0
            ? SpatialFailure(isGeography, SpatialFormat, 24118,
                "The Polygon input is not valid because the exterior ring does not have enough points. Each ring of a polygon must contain at least four points.")
            : SpatialFailure(isGeography, SpatialFormat, 24120,
                $"The Polygon input is not valid because the interior ring number {interiorRingNumber.ToString(CultureInfo.InvariantCulture)} does not have enough points. Each ring of a polygon must contain at least four points.");

    /// <summary>24119 / 24121 — a polygon ring whose first and last points differ; <c>geography</c>'s is 24306, numbering every ring from 1.</summary>
    internal static SimulatedSqlException SpatialRingNotClosed(bool isGeography, int interiorRingNumber) => isGeography
        ? SpatialFailure(isGeography, SpatialFormat, 24306,
            $"The Polygon input is not valid because the start and end points of the ring number {(interiorRingNumber + 1).ToString(CultureInfo.InvariantCulture)} are not the same. Each ring of a polygon must have the same start and end points.")
        : interiorRingNumber == 0
            ? SpatialFailure(isGeography, SpatialFormat, 24119,
                "The Polygon input is not valid because the start and end points of the exterior ring are not the same. Each ring of a polygon must have the same start and end points.")
            : SpatialFailure(isGeography, SpatialFormat, 24121,
                $"The Polygon input is not valid because the start and end points of the interior ring number {interiorRingNumber.ToString(CultureInfo.InvariantCulture)} are not the same. Each ring of a polygon must have the same start and end points.");

    /// <summary>24150 — a <c>FULLGLOBE</c> member of a <c>GEOMETRYCOLLECTION</c>.</summary>
    internal static SimulatedSqlException SpatialFullGlobeInCollection() => SpatialFailure(
        isGeography: true, SpatialFormat, 24150,
        "FullGlobe instances cannot be objects in the GeometryCollection. GeometryCollections can contain the following instances: Points, MultiPoints, "
        + "LineStrings, MultiLineStrings, Polygons, MultiPolygons, CircularStrings, CompoundCurves, CurvePolygons and GeometryCollections.");

    /// <summary>24115 — a well-known binary record whose type code names no shape, <c>FULLGLOBE</c>'s shape-table code 11 included.</summary>
    internal static SimulatedSqlException SpatialWkbNotValid(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24115, "The well-known binary (WKB) input is not valid.");

    /// <summary>24212 — a <c>CIRCULARSTRING</c> with a single point.</summary>
    internal static SimulatedSqlException SpatialCircularStringTooFewPoints(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24212,
        "The CircularString input is not valid because it does not have enough points. A CircularString must have at least three points.");

    /// <summary>24214 — an arc whose three points disagree on Z, a missing Z included.</summary>
    internal static SimulatedSqlException SpatialArcZNotEqual(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24214, "Circular arc segments with Z values must have equal Z value for all 3 points.");

    /// <summary>24134 — a <c>COMPOUNDCURVE</c> element that doesn't start where the previous one ended.</summary>
    internal static SimulatedSqlException SpatialCompoundCurveNotContinuous(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24134,
        "Sequential parts of a compound curve must have one common endpoint. Add a common endpoint. All coordinates, including optional Z and M, must be equal.");

    /// <summary>24300 — a <c>CURVEPOLYGON</c> ring written <c>CIRCULARSTRING EMPTY</c>, which real's builder reports as a missing figure.</summary>
    internal static SimulatedSqlException SpatialCircularStringRingEmpty(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24300, $"Expected a call to BeginFigure, but {(isGeography ? "EndGeography" : "EndGeometry")} was called.");

    /// <summary>24301 — a <c>CURVEPOLYGON</c> ring written <c>COMPOUNDCURVE EMPTY</c>, which real's builder reports as a missing segment.</summary>
    internal static SimulatedSqlException SpatialCompoundCurveRingEmpty(bool isGeography) => SpatialFailure(
        isGeography, SpatialFormat, 24301, $"Expected a call to AddSegmentLine or AddSegmentArc, but {(isGeography ? "EndGeography" : "EndGeometry")} was called.");

    /// <summary>24151 — <c>STCurveN</c> index below 1.</summary>
    internal static SimulatedSqlException SpatialCurveIndexTooSmall(bool isGeography, int n) => SpatialFailure(
        isGeography, SpatialOutOfRange, 24151,
        $"The curve index n ({n.ToString(CultureInfo.InvariantCulture)}) passed to STCurveN is less than 1. This number must be greater than or equal to 1 and less than or equal to the number of curves returned by STNumCurves.",
        "n");

    /// <summary>24152 — a <c>CurveToLineWithTolerance</c> tolerance that isn't positive.</summary>
    internal static SimulatedSqlException SpatialCurveToleranceNotValid(bool isGeography, double tolerance) => SpatialFailure(
        isGeography, SpatialOutOfRange, 24152,
        $"The tolerance ({Storage.Spatial.SpatialWktWriter.Format(tolerance)}) passed to CurveToLineWithTolerance is not valid. Tolerances must be positive numbers.",
        "tolerance");

    /// <summary>24201 — a <c>geography</c> coordinate outside the latitude domain. Longitude has no equivalent check; real accepts any value there.</summary>
    internal static SimulatedSqlException SpatialLatitudeOutOfRange() => SpatialFailure(
        isGeography: true, SpatialFormat, 24201, "Latitude values must be between -90 and 90 degrees.");

    /// <summary>
    /// 24206 — a <c>geography</c> edge whose endpoints are exactly antipodal.
    /// Real raises it while <i>constructing</i> the instance, and reports it as
    /// its own spatial-library exception type rather than a <c>System.</c> one.
    /// </summary>
    internal static SimulatedSqlException SpatialAntipodalEdge() => SpatialFailure(
        isGeography: true, SpatialGeodetic, 24206,
        "The specified input cannot be accepted because it contains an edge with antipodal points. For information about "
        + "using spatial methods with FullGlobe objects, see Types of Spatial Data in SQL Server Books Online.");

    /// <summary>24207 — a <c>geography</c> buffer distance past about half the globe's circumference.</summary>
    internal static SimulatedSqlException SpatialBufferExceedsGlobe() => SpatialFailure(
        isGeography: true, SpatialGeodetic, 24207, "The specified buffer distance exceeds the full globe. Decrease the buffer distance.");

    /// <summary>24102 — <c>STPointN</c> index below 1. Real's wording differs from <see cref="SpatialGeometryIndexTooSmall"/> by one word ("This number" vs "The number"), reproduced verbatim.</summary>
    internal static SimulatedSqlException SpatialPointIndexTooSmall(bool isGeography, int n) => SpatialFailure(
        isGeography, SpatialOutOfRange, 24102,
        $"The point index n ({n.ToString(CultureInfo.InvariantCulture)}) passed to STPointN is less than 1. This number must be greater than or equal to 1 and less than or equal to the number of points returned by STNumPoints.",
        "n");

    /// <summary>24103 — <c>STGeometryN</c> index below 1.</summary>
    internal static SimulatedSqlException SpatialGeometryIndexTooSmall(bool isGeography, int n) => SpatialFailure(
        isGeography, SpatialOutOfRange, 24103,
        $"The geometry index n ({n.ToString(CultureInfo.InvariantCulture)}) passed to STGeometryN is less than 1. The number must be greater than or equal to 1 and should be less than or equal to the number of instances returned by STNumGeometries.",
        "n");

    /// <summary>24104 — a ring index below 1. Geography's <c>RingN</c> reports this under <c>STInteriorRingN</c>'s name, matching real.</summary>
    internal static SimulatedSqlException SpatialRingIndexTooSmall(bool isGeography, int n) => SpatialFailure(
        isGeography, SpatialOutOfRange, 24104,
        $"The ring index n ({n.ToString(CultureInfo.InvariantCulture)}) passed to STInteriorRingN is less than 1. The number must be greater than or equal to 1 and should be less than or equal to the number of rings returned by STNumInteriorRing.",
        "n");

    /// <summary>24210 — a binary payload whose version byte is outside the accepted range.</summary>
    internal static SimulatedSqlException SpatialUnexpectedVersion(bool isGeography, int version) => SpatialFailure(
        isGeography, SpatialFormat, 24210,
        $"{(isGeography ? "Geography" : "Geometry")} type with an unexpected version of {version.ToString(CultureInfo.InvariantCulture)} received; only versions up to 2 are accepted.");

    /// <summary>24303 — a shape kind the target spatial type doesn't admit (<c>FULLGLOBE</c> on <c>geometry</c>).</summary>
    internal static SimulatedSqlException SpatialInvalidOpenGisType(bool isGeography, string typeName) => SpatialFailure(
        isGeography, SpatialFormat, 24303, $"The OpenGisGeometryType provided, {typeName}, is not valid.");

    /// <summary>24100 — an SRID outside the accepted domain.</summary>
    internal static SimulatedSqlException SpatialInvalidSrid(bool isGeography) => SpatialFailure(
        isGeography, SpatialArgument, 24100,
        "The spatial reference identifier (SRID) is not valid. SRIDs must be between 0 and 999999.");

    /// <summary>
    /// 24105 — <c>InstanceOf</c> was handed a name outside the OGC type
    /// hierarchy. <c>FullGlobe</c> counts as outside it on <c>geometry</c>,
    /// which is the only per-type difference.
    /// </summary>
    internal static SimulatedSqlException SpatialInvalidInstanceOfType(bool isGeography, string argument) => SpatialFailure(
        isGeography, SpatialArgument, 24105,
        $"The geometryType argument in InstanceOf ('{argument}') is not valid. This argument must contain one of the "
        + "following types: Geometry, Point, LineString, Curve, Polygon, Surface, MultiPoint, MultiLineString, "
        + "MultiPolygon, MultiCurve, MultiSurface, GeometryCollection, CircularString, CompoundCurve, CurvePolygon "
        + "or FullGlobe (geography Data Type only).");

    /// <summary>A NULL assigned to <c>STSrid</c>, which real reports as the bare .NET argument failure with no 24xxx code.</summary>
    internal static SimulatedSqlException SpatialSridCannotBeNull(bool isGeography) => SpatialFailure(
        isGeography, "System.ArgumentNullException", 0, "Value cannot be null.");

    /// <summary>
    /// Mimics SQL Server error 6595: a CLR-type property was assigned to that
    /// exposes no setter — every spatial property but <c>STSrid</c>.
    /// </summary>
    internal static SimulatedSqlException ClrPropertyReadOnly(string member, string clrTypeName) =>
        new($"Could not assign to property '{member}' for type '{clrTypeName}' in assembly 'Microsoft.SqlServer.Types' because it is read only.", 6595, 16, 1);

    /// <summary>24109 — <c>STRelate</c>'s pattern argument isn't nine characters long. A NULL argument reports zero, matching real.</summary>
    internal static SimulatedSqlException SpatialRelateMaskLength(bool isGeography, int length) => SpatialFailure(
        isGeography, SpatialFormat, 24109,
        "The intersectionPatternMatrix argument to STRelate is not valid. This argument must contain exactly 9 characters, "
        + $"but the string provided has {length.ToString(CultureInfo.InvariantCulture)} characters.");

    /// <summary>24110 — a character outside <c>STRelate</c>'s pattern alphabet. Real reports the zero-based position and is case-sensitive.</summary>
    internal static SimulatedSqlException SpatialRelateMaskCharacter(bool isGeography, int position, char character) => SpatialFailure(
        isGeography, SpatialFormat, 24110,
        $"Character {position.ToString(CultureInfo.InvariantCulture)} ({character}) of the intersectionPatternMatrix argument to "
        + "STRelate is not valid. This argument must only contain the characters 0, 1, 2, T, F, and *.");

    /// <summary>24144 — an operation that needs a valid instance ran against one that isn't.</summary>
    internal static SimulatedSqlException SpatialInstanceNotValid(bool isGeography) => SpatialFailure(
        isGeography, SpatialArgument, 24144,
        "This operation cannot be completed because the instance is not valid. Use MakeValid to convert the instance to a valid instance. "
        + "Note that MakeValid may cause the points of a geometry instance to shift slightly.");

    /// <summary>24108 — <c>BufferWithTolerance</c> was handed a tolerance that isn't positive.</summary>
    internal static SimulatedSqlException SpatialBufferToleranceNotValid(bool isGeography, double tolerance) => SpatialFailure(
        isGeography, SpatialOutOfRange, 24108,
        $"The tolerance ({Storage.Spatial.SpatialWktWriter.Format(tolerance)}) passed to BufferWithTolerance is not valid. Tolerances must be positive numbers.",
        parameterName: "tolerance");

    /// <summary>24125 — <c>Reduce</c> was handed a negative tolerance.</summary>
    internal static SimulatedSqlException SpatialReduceToleranceNotValid(bool isGeography, double tolerance) => SpatialFailure(
        isGeography, SpatialOutOfRange, 24125,
        $"The tolerance ({Storage.Spatial.SpatialWktWriter.Format(tolerance)}) passed to Reduce is not valid. Tolerances must be positive numbers.",
        parameterName: "tolerance");

    /// <summary>
    /// Msg 6569 — a spatial method parameter that refuses NULL was handed one.
    /// Real numbers the parameter from 1 and names the method as
    /// <c>type::Method</c>.
    /// </summary>
    internal static SimulatedSqlException SpatialParameterNotNull(bool isGeography, string method, int parameter) => new(
        $"'{(isGeography ? "geography" : "geometry")}::{method}' failed because parameter {parameter.ToString(CultureInfo.InvariantCulture)} is not allowed to be null.",
        6569,
        16,
        1);

    /// <summary>
    /// 24144 raised from inside one of the spatial aggregates, which real
    /// reports under the aggregate's own class name —
    /// <c>GeometryUnionAggregate</c>, <c>GeographyCollectionAggregate</c> and
    /// so on — rather than the type's.
    /// </summary>
    internal static SimulatedSqlException SpatialAggregateInstanceNotValid(string aggregateName) => ClrTypeFailure(
        aggregateName, SpatialArgument,
        "24144: This operation cannot be completed because the instance is not valid. Use MakeValid to convert the instance to a valid instance. "
        + "Note that MakeValid may cause the points of a geometry instance to shift slightly.",
        parameterName: null,
        state: 1);

    /// <summary>
    /// Mimics SQL Server error 6592: a CLR-type member was read without an
    /// argument list where the type exposes no such property — which is what a
    /// spatial <i>method</i> name written without parentheses produces, and
    /// what a property belonging to the other spatial type produces
    /// (<c>Lat</c> on <c>geometry</c>, <c>STX</c> on <c>geography</c>).
    /// </summary>
    internal static SimulatedSqlException ClrPropertyNotFound(string member, string clrTypeName, string assemblyName = "Microsoft.SqlServer.Types") =>
        new($"Could not find property or field '{member}' for type '{clrTypeName}' in assembly '{assemblyName}'.", 6592, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 6506: a CLR-type method was called that the type
    /// doesn't expose — <c>NumRings()</c> on <c>geometry</c>, say, which is a
    /// geography-only extension.
    /// </summary>
    /// <remarks>Real emits this one without a trailing period, unlike <see cref="ClrPropertyNotFound"/>.</remarks>
    internal static SimulatedSqlException ClrMethodNotFound(string member, string clrTypeName) =>
        new($"Could not find method '{member}' for type '{clrTypeName}' in assembly 'Microsoft.SqlServer.Types'", 6506, 16, 10);

    /// <summary>
    /// Mimics SQL Server error 6210: a spatial operand reached <c>MAX</c> /
    /// <c>MIN</c>, which order their input. Real leads its response with this
    /// one and follows with the ordinary Msg 8117, so the pair travels together
    /// out of <c>Aggregator.Create</c>. The other non-comparable families raise
    /// 8117 alone.
    /// </summary>
    internal static SimulatedSqlException ClrTypeNotFullyComparable(SqlType type) =>
        new($"CLR type '{type.SqlServerName}' is not fully comparable.", 6210, 16, 1);
}
