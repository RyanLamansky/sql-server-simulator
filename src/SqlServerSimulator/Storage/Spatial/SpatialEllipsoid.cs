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
/// The reference ellipsoid the round-earth measures run on — WGS 84 unless a
/// measurement enters another SRID's (<see cref="Enter"/>) — and the two scalar
/// fields the round-earth measures are built from: the quadratic form whose
/// level set <i>is</i> the surface, and the area of the zone between the
/// equator and a parallel.
/// </summary>
/// <remarks>
/// <para><see cref="AreaBelow"/> is the antiderivative of the surface element
/// <c>a²(1-e²)cosφ / (1-e²sin²φ)²</c> in latitude, so the area of an ellipsoidal
/// region is a line integral of it around the boundary — see
/// <see cref="SpatialEllipsoidArea"/>.</para>
/// </remarks>
internal static class SpatialEllipsoid
{
    /// <summary>
    /// Degrees to radians as real converts a coordinate: one multiplication by
    /// the precomputed ratio. <c>double.DegreesToRadians</c> multiplies by π
    /// and then divides, which rounds differently in the last place — and the
    /// last place shows wherever real writes a coordinate back through a unit
    /// vector (probed 2026-09-29 against SQL Server 2025).
    /// </summary>
    public const double RadiansPerDegree = Math.PI / 180;

    /// <summary>Radians to degrees as real converts them; see <see cref="RadiansPerDegree"/>.</summary>
    public const double DegreesPerRadian = 180 / Math.PI;

    /// <summary>The ellipsoid a measurement on this thread has entered, or null for WGS 84.</summary>
    [ThreadStatic]
    private static SpatialDatum? entered;

    private static SpatialDatum Datum => entered ?? SpatialDatum.Wgs84;

    /// <summary>
    /// Runs the round-earth arithmetic on <paramref name="datum"/>'s ellipsoid
    /// until the returned scope is disposed; null keeps WGS 84.
    /// </summary>
    public static DatumScope Enter(SpatialDatum? datum)
    {
        var previous = entered;
        entered = datum;
        return new DatumScope(previous);
    }

    /// <summary>Restores the ellipsoid a scope replaced.</summary>
    internal readonly struct DatumScope(SpatialDatum? previous) : IDisposable
    {
        private readonly SpatialDatum? previous = previous;

        public void Dispose() => entered = this.previous;
    }

    /// <summary>Semi-major axis, metres.</summary>
    public static double SemiMajor => Datum.SemiMajor;

    public static double Flattening => Datum.Flattening;

    public static double SemiMinor => Datum.SemiMinor;

    public static double EccentricitySquared => Datum.EccentricitySquared;

    public static double Eccentricity => Datum.Eccentricity;

    /// <summary>
    /// Area of the zone from the equator to the north pole, per radian of
    /// longitude — the closing constant a ring encircling a pole needs, and a
    /// quarter of <see cref="SurfaceArea"/> over π.
    /// </summary>
    public static double PolarZone => Datum.PolarZone;

    /// <summary>Total surface area, metres squared.</summary>
    public static double SurfaceArea => Datum.SurfaceArea;

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
    public static double AreaBelow(double latitude) => Datum.AreaBelow(latitude);

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

/// <summary>
/// One reference ellipsoid — its semi-major axis in metres and its flattening,
/// as a <c>sys.spatial_reference_systems</c> row's WKT names them — with the
/// quantities the round-earth arithmetic derives from them.
/// </summary>
internal sealed class SpatialDatum
{
    /// <summary>WGS 84, which SRID 4326 and every SRID before this model carried measure on.</summary>
    public static readonly SpatialDatum Wgs84 = new(6378137.0, 1.0 / 298.257223563);

    public readonly double SemiMajor;
    public readonly double Flattening;
    public readonly double SemiMinor;
    public readonly double EccentricitySquared;
    public readonly double Eccentricity;
    public readonly double PolarZone;
    public readonly double SurfaceArea;

    /// <summary>An ellipsoid from its semi-major axis and flattening; a flattening of 0 is a sphere.</summary>
    public SpatialDatum(double semiMajor, double flattening)
    {
        this.SemiMajor = semiMajor;
        this.Flattening = flattening;
        this.SemiMinor = semiMajor * (1 - flattening);
        this.EccentricitySquared = 1 - (this.SemiMinor * this.SemiMinor / (semiMajor * semiMajor));
        this.Eccentricity = Math.Sqrt(this.EccentricitySquared);
        this.PolarZone = AreaBelow(Math.PI / 2);
        this.SurfaceArea = 4 * Math.PI * this.PolarZone;
    }

    /// <summary>
    /// Area between the equator and latitude <paramref name="latitude"/>
    /// (radians) per radian of longitude, signed with the latitude; on a sphere
    /// the eccentric term's limit, <c>a² sin φ</c>.
    /// </summary>
    public double AreaBelow(double latitude)
    {
        var sin = Math.Sin(latitude);
        return this.Eccentricity == 0
            ? this.SemiMajor * this.SemiMajor * sin
            : this.SemiMajor * this.SemiMajor * (1 - this.EccentricitySquared)
                * ((sin / (2 * (1 - (this.EccentricitySquared * sin * sin)))) + (Math.Atanh(this.Eccentricity * sin) / (2 * this.Eccentricity)));
    }
}
