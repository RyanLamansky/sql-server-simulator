namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Round-earth distance along the <b>great elliptic arc</b> — the curve cut
/// from the reference ellipsoid by the plane through the two points and the
/// ellipsoid's centre. This is the curve SQL Server measures
/// <c>geography</c> along, and it is <i>not</i> the geodesic.
/// </summary>
/// <remarks>
/// <para>The distinction is measurable and was how the curve was identified
/// (2026-07-31): a Vincenty geodesic, accurate to well under a millimetre at
/// these distances, lands 3.3 m short of real over Seattle→Paris while
/// matching a meridian degree to 0.065 mm. Meridians and the equator→pole
/// path are exactly where the two curves coincide; oblique paths are where
/// they part, and the great elliptic arc is the longer of the two.</para>
/// <para><b>Accuracy against real</b>: this computes the arc exactly, so the
/// residual is real's own approximation — at most ~6e-9 relative across the
/// probed set (59 mm over a quarter meridian, 3.2 mm over Seattle→Paris,
/// exact on the equator, where the section is a circle). Callers should not
/// expect bit-identical agreement; see <c>docs/claude/spatial.md</c>.</para>
/// </remarks>
internal static class SpatialGreatElliptic
{
    /// <summary>WGS 84 semi-major axis, metres. Every modeled geography SRID resolves to this ellipsoid.</summary>
    private const double SemiMajor = 6378137.0;

    /// <summary>WGS 84 flattening.</summary>
    private const double Flattening = 1.0 / 298.257223563;

    private const double SemiMinor = SemiMajor * (1 - Flattening);

    private const double FirstEccentricitySquared = 1 - (SemiMinor * SemiMinor / (SemiMajor * SemiMajor));

    /// <summary>
    /// Distance in metres between two points given in WKT order (longitude,
    /// latitude), measured along the great elliptic arc.
    /// </summary>
    public static double Distance(SpatialCoordinate from, SpatialCoordinate to)
    {
        var p1 = ToCartesian(from);
        var p2 = ToCartesian(to);
        var normal = Cross(p1, p2);
        var normalLength = Math.Sqrt(Dot(normal, normal));
        // Coincident (or exactly antipodal) points define no unique plane. A
        // zero distance is right for the first; the second can't arise from a
        // valid geography instance, which rejects an antipodal edge at parse.
        if (normalLength == 0)
            return 0;

        // Orthonormal basis of the cutting plane, with p1 on the first axis.
        var e1 = Scale(p1, 1 / Math.Sqrt(Dot(p1, p1)));
        var e2 = Scale(Cross(Scale(normal, 1 / normalLength), e1), 1);
        e2 = Scale(e2, 1 / Math.Sqrt(Dot(e2, e2)));

        // The ellipsoid's quadratic form restricted to that plane is a 2x2
        // symmetric matrix; rotating it to its principal axes gives the
        // section ellipse's semi-axes directly.
        var q11 = Form(e1, e1);
        var q12 = Form(e1, e2);
        var q22 = Form(e2, e2);
        var rotation = 0.5 * Math.Atan2(2 * q12, q11 - q22);
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        var f1 = Add(Scale(e1, cos), Scale(e2, sin));
        var f2 = Add(Scale(e1, -sin), Scale(e2, cos));
        var axis1 = 1.0 / Math.Sqrt(Form(f1, f1));
        var axis2 = 1.0 / Math.Sqrt(Form(f2, f2));

        var start = Math.Atan2(Dot(p1, f2) / axis2, Dot(p1, f1) / axis1);
        var finish = Math.Atan2(Dot(p2, f2) / axis2, Dot(p2, f1) / axis1);
        var sweep = finish - start;
        while (sweep > Math.PI)
            sweep -= 2 * Math.PI;
        while (sweep < -Math.PI)
            sweep += 2 * Math.PI;

        return Math.Abs(Integrate(start, start + sweep, axis1, axis2));
    }

    /// <summary>Geodetic (longitude, latitude) in degrees to geocentric Cartesian on the ellipsoid surface.</summary>
    private static (double X, double Y, double Z) ToCartesian(SpatialCoordinate point)
    {
        var latitude = point.Y * Math.PI / 180.0;
        var longitude = point.X * Math.PI / 180.0;
        var sinLatitude = Math.Sin(latitude);
        var primeVertical = SemiMajor / Math.Sqrt(1 - (FirstEccentricitySquared * sinLatitude * sinLatitude));
        return (
            primeVertical * Math.Cos(latitude) * Math.Cos(longitude),
            primeVertical * Math.Cos(latitude) * Math.Sin(longitude),
            primeVertical * (1 - FirstEccentricitySquared) * sinLatitude);
    }

    /// <summary>The ellipsoid's quadratic form <c>uᵀ diag(1/a², 1/a², 1/b²) v</c>.</summary>
    private static double Form((double X, double Y, double Z) u, (double X, double Y, double Z) v) =>
        (u.X * v.X / (SemiMajor * SemiMajor))
        + (u.Y * v.Y / (SemiMajor * SemiMajor))
        + (u.Z * v.Z / (SemiMinor * SemiMinor));

    /// <summary>
    /// Arc length of the section ellipse between two parameter angles — an
    /// incomplete elliptic integral of the second kind, taken by composite
    /// 20-node Gauss-Legendre. The integrand is smooth and the sweep is at
    /// most π, so eight panels put the quadrature error far below the
    /// difference from real.
    /// </summary>
    private static double Integrate(double from, double to, double axis1, double axis2)
    {
        const int panels = 8;
        var total = 0.0;
        var width = (to - from) / panels;
        for (var panel = 0; panel < panels; panel++)
        {
            var mid = from + (panel * width) + (width / 2);
            var half = width / 2;
            var sum = 0.0;
            for (var i = 0; i < GaussNodes.Length; i++)
            {
                sum += GaussWeights[i]
                    * (Integrand(mid + (half * GaussNodes[i]), axis1, axis2) + Integrand(mid - (half * GaussNodes[i]), axis1, axis2));
            }
            total += sum * half;
        }
        return total;
    }

    private static double Integrand(double t, double axis1, double axis2)
    {
        var sin = Math.Sin(t);
        var cos = Math.Cos(t);
        return Math.Sqrt((axis1 * axis1 * sin * sin) + (axis2 * axis2 * cos * cos));
    }

    /// <summary>Positive abscissae of the 20-node Gauss-Legendre rule on [-1, 1].</summary>
    private static readonly double[] GaussNodes =
    [
        0.0765265211334973, 0.2277858511416451, 0.3737060887154195, 0.5108670019508271, 0.6360536807265150,
        0.7463319064601508, 0.8391169718222188, 0.9122344282513259, 0.9639719272779138, 0.9931285991850949,
    ];

    /// <summary>Weights paired with <see cref="GaussNodes"/>.</summary>
    private static readonly double[] GaussWeights =
    [
        0.1527533871307258, 0.1491729864726037, 0.1420961093183820, 0.1316886384491766, 0.1181945319615184,
        0.1019301198172404, 0.0832767415767048, 0.0626720483341091, 0.0406014298003869, 0.0176140071391521,
    ];

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) p, (double X, double Y, double Z) q) =>
        ((p.Y * q.Z) - (p.Z * q.Y), (p.Z * q.X) - (p.X * q.Z), (p.X * q.Y) - (p.Y * q.X));

    private static double Dot((double X, double Y, double Z) p, (double X, double Y, double Z) q) =>
        (p.X * q.X) + (p.Y * q.Y) + (p.Z * q.Z);

    private static (double X, double Y, double Z) Scale((double X, double Y, double Z) p, double factor) =>
        (p.X * factor, p.Y * factor, p.Z * factor);

    private static (double X, double Y, double Z) Add((double X, double Y, double Z) p, (double X, double Y, double Z) q) =>
        (p.X + q.X, p.Y + q.Y, p.Z + q.Z);
}
