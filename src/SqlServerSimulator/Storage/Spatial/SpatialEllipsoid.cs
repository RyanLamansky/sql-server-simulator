namespace SqlServerSimulator.Storage.Spatial;

/// <summary>A geocentric Cartesian vector, metres, in the ellipsoid's own frame (x through 0°E, z through the north pole).</summary>
internal readonly struct SpatialVector(double x, double y, double z)
{
    public readonly double X = x;

    public readonly double Y = y;

    public readonly double Z = z;

    public static SpatialVector operator +(SpatialVector left, SpatialVector right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static SpatialVector operator -(SpatialVector left, SpatialVector right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static SpatialVector operator *(SpatialVector vector, double factor) =>
        new(vector.X * factor, vector.Y * factor, vector.Z * factor);

    public double Dot(SpatialVector other) => (this.X * other.X) + (this.Y * other.Y) + (this.Z * other.Z);

    public SpatialVector Cross(SpatialVector other) => new(
        (this.Y * other.Z) - (this.Z * other.Y),
        (this.Z * other.X) - (this.X * other.Z),
        (this.X * other.Y) - (this.Y * other.X));

    public double Length => Math.Sqrt(Dot(this));

    /// <summary>Squared length, for the comparisons that don't need the root.</summary>
    public double SquaredLength => Dot(this);

    /// <summary>Distance from the z axis — zero exactly at a pole, which is where longitude stops being defined.</summary>
    public double AxialRadius => Math.Sqrt((this.X * this.X) + (this.Y * this.Y));

    public SpatialVector Normalized => this * (1.0 / Length);
}

/// <summary>
/// The WGS 84 reference ellipsoid every modeled <c>geography</c> SRID resolves
/// to, and the two scalar fields the round-earth measures are built from: the
/// quadratic form whose level set <i>is</i> the surface, and the area of the
/// zone between the equator and a parallel.
/// </summary>
/// <remarks>
/// <para>Real carries a per-SRID ellipsoid (a unit-sphere SRID measures the
/// same polygon in radians squared); the simulator measures every geography
/// value on WGS 84.</para>
/// <para><see cref="AreaBelow"/> is the antiderivative of the surface element
/// <c>a²(1-e²)cosφ / (1-e²sin²φ)²</c> in latitude, so the area of an ellipsoidal
/// region is a line integral of it around the boundary — see
/// <see cref="SpatialEllipsoidArea"/>.</para>
/// </remarks>
internal static class SpatialEllipsoid
{
    /// <summary>Semi-major axis, metres.</summary>
    public const double SemiMajor = 6378137.0;

    public const double Flattening = 1.0 / 298.257223563;

    public const double SemiMinor = SemiMajor * (1 - Flattening);

    public const double EccentricitySquared = 1 - (SemiMinor * SemiMinor / (SemiMajor * SemiMajor));

    public static readonly double Eccentricity = Math.Sqrt(EccentricitySquared);

    /// <summary>
    /// Area of the zone from the equator to the north pole, per radian of
    /// longitude — the closing constant a ring encircling a pole needs, and a
    /// quarter of <see cref="SurfaceArea"/> over π.
    /// </summary>
    public static readonly double PolarZone = AreaBelow(Math.PI / 2);

    /// <summary>Total surface area, metres squared.</summary>
    public static readonly double SurfaceArea = 4 * Math.PI * PolarZone;

    /// <summary>Geodetic (longitude, latitude) in degrees to the surface point in geocentric Cartesian metres.</summary>
    public static SpatialVector ToCartesian(SpatialCoordinate point)
    {
        var latitude = point.Y * Math.PI / 180.0;
        var longitude = point.X * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitude);
        var primeVertical = SemiMajor / Math.Sqrt(1 - (EccentricitySquared * sinLatitude * sinLatitude));
        return new(
            primeVertical * Math.Cos(latitude) * Math.Cos(longitude),
            primeVertical * Math.Cos(latitude) * Math.Sin(longitude),
            primeVertical * (1 - EccentricitySquared) * sinLatitude);
    }

    /// <summary>The ellipsoid's quadratic form <c>uᵀ diag(1/a², 1/a², 1/b²) v</c>; it is 1 exactly on the surface.</summary>
    public static double QuadraticForm(SpatialVector u, SpatialVector v) =>
        (u.X * v.X / (SemiMajor * SemiMajor))
        + (u.Y * v.Y / (SemiMajor * SemiMajor))
        + (u.Z * v.Z / (SemiMinor * SemiMinor));

    /// <summary>Scales a direction until it lands on the surface.</summary>
    public static SpatialVector OntoSurface(SpatialVector direction) =>
        direction * (1.0 / Math.Sqrt(QuadraticForm(direction, direction)));

    /// <summary>
    /// Geodetic latitude, radians, of a point on (or a direction to) the
    /// surface. The radial scaling cancels, so a direction answers the same as
    /// the surface point it names.
    /// </summary>
    public static double GeodeticLatitude(SpatialVector point) =>
        Math.Atan2(point.Z, (1 - EccentricitySquared) * point.AxialRadius);

    /// <summary>
    /// Area between the equator and latitude <paramref name="latitude"/>
    /// (radians) per radian of longitude, signed with the latitude.
    /// </summary>
    public static double AreaBelow(double latitude)
    {
        var sin = Math.Sin(latitude);
        return SemiMajor * SemiMajor * (1 - EccentricitySquared)
            * ((sin / (2 * (1 - (EccentricitySquared * sin * sin)))) + (Math.Atanh(Eccentricity * sin) / (2 * Eccentricity)));
    }

    /// <summary>Longitude difference folded into (-π, π] — the sweep an edge takes, never the long way round.</summary>
    public static double ShortestLongitudeDelta(double from, double to)
    {
        var delta = to - from;
        while (delta > Math.PI)
            delta -= 2 * Math.PI;
        while (delta <= -Math.PI)
            delta += 2 * Math.PI;
        return delta;
    }
}

/// <summary>
/// The 20-node Gauss-Legendre rule both round-earth integrals run on, applied
/// as a composite rule over as many panels as the span asks for.
/// </summary>
/// <remarks>
/// Callers write their own panel loop rather than passing a delegate: a
/// measurement walks every edge of every ring, so the rule has to add nothing
/// per edge.
/// </remarks>
internal static class GaussLegendre
{
    /// <summary>Positive abscissae on [-1, 1]; each pairs with its own negation.</summary>
    public static readonly double[] Nodes =
    [
        0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150,
        0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949,
    ];

    /// <summary>Weights paired with <see cref="Nodes"/>.</summary>
    public static readonly double[] Weights =
    [
        0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184,
        0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521,
    ];

    /// <summary>Panels a composite rule needs to cover <paramref name="span"/> at the named granularity.</summary>
    public static int PanelsFor(double span, double granularity, int cap) =>
        Math.Clamp((int)Math.Ceiling(Math.Abs(span) / granularity), 1, cap);
}
