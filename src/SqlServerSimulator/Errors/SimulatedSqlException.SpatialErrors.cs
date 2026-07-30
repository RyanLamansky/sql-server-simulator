using System.Globalization;

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
        var parameter = parameterName is null ? string.Empty : $"Parameter name: {parameterName}\r\n";
        // A failure raised by the hosting layer rather than by the spatial
        // library carries no 24xxx code — a null SRID assignment is the one
        // that reaches here that way.
        var prefix = code == 0 ? string.Empty : $"{code.ToString(CultureInfo.InvariantCulture)}: ";
        return new(
            $"A .NET Framework error occurred during execution of user-defined routine or aggregate \"{(isGeography ? "geography" : "geometry")}\": \r\n"
            + $"{clrExceptionType}: {prefix}{message}\r\n"
            + parameter
            + $"{clrExceptionType}: \r\n.",
            6522,
            16,
            1);
    }

    private const string SpatialFormat = "System.FormatException";
    private const string SpatialOutOfRange = "System.ArgumentOutOfRangeException";
    private const string SpatialArgument = "System.ArgumentException";

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

    /// <summary>24118 / 24120 — a polygon ring with fewer than four points. Real names the exterior ring differently from a numbered interior one.</summary>
    internal static SimulatedSqlException SpatialRingTooFewPoints(bool isGeography, int interiorRingNumber) => interiorRingNumber == 0
        ? SpatialFailure(isGeography, SpatialFormat, 24118,
            "The Polygon input is not valid because the exterior ring does not have enough points. Each ring of a polygon must contain at least four points.")
        : SpatialFailure(isGeography, SpatialFormat, 24120,
            $"The Polygon input is not valid because the interior ring number {interiorRingNumber.ToString(CultureInfo.InvariantCulture)} does not have enough points. Each ring of a polygon must contain at least four points.");

    /// <summary>24119 / 24121 — a polygon ring whose first and last points differ.</summary>
    internal static SimulatedSqlException SpatialRingNotClosed(bool isGeography, int interiorRingNumber) => interiorRingNumber == 0
        ? SpatialFailure(isGeography, SpatialFormat, 24119,
            "The Polygon input is not valid because the start and end points of the exterior ring are not the same. Each ring of a polygon must have the same start and end points.")
        : SpatialFailure(isGeography, SpatialFormat, 24121,
            $"The Polygon input is not valid because the start and end points of the interior ring number {interiorRingNumber.ToString(CultureInfo.InvariantCulture)} are not the same. Each ring of a polygon must have the same start and end points.");

    /// <summary>24201 — a <c>geography</c> coordinate outside the latitude domain. Longitude has no equivalent check; real accepts any value there.</summary>
    internal static SimulatedSqlException SpatialLatitudeOutOfRange() => SpatialFailure(
        isGeography: true, SpatialFormat, 24201, "Latitude values must be between -90 and 90 degrees.");

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

    /// <summary>24144 — an operation that needs a valid instance ran against one that isn't.</summary>
    internal static SimulatedSqlException SpatialInstanceNotValid(bool isGeography) => SpatialFailure(
        isGeography, SpatialArgument, 24144,
        "This operation cannot be completed because the instance is not valid. Use MakeValid to convert the instance to a valid instance. "
        + "Note that MakeValid may cause the points of a geometry instance to shift slightly.");

    /// <summary>
    /// Mimics SQL Server error 6592: a CLR-type member was read without an
    /// argument list where the type exposes no such property — which is what a
    /// spatial <i>method</i> name written without parentheses produces, and
    /// what a property belonging to the other spatial type produces
    /// (<c>Lat</c> on <c>geometry</c>, <c>STX</c> on <c>geography</c>).
    /// </summary>
    internal static SimulatedSqlException ClrPropertyNotFound(string member, string clrTypeName) =>
        new($"Could not find property or field '{member}' for type '{clrTypeName}' in assembly 'Microsoft.SqlServer.Types'.", 6592, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 6506: a CLR-type method was called that the type
    /// doesn't expose — <c>NumRings()</c> on <c>geometry</c>, say, which is a
    /// geography-only extension.
    /// </summary>
    /// <remarks>Real emits this one without a trailing period, unlike <see cref="ClrPropertyNotFound"/>.</remarks>
    internal static SimulatedSqlException ClrMethodNotFound(string member, string clrTypeName) =>
        new($"Could not find method '{member}' for type '{clrTypeName}' in assembly 'Microsoft.SqlServer.Types'", 6506, 16, 10);
}
