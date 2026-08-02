namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// One <b>great elliptic arc</b> — the curve cut from the reference ellipsoid
/// by the plane through two surface points and the ellipsoid's centre, which
/// is the curve SQL Server joins consecutive <c>geography</c> vertices with.
/// </summary>
/// <remarks>
/// <para>The section is an ellipse, and the arc is held in that ellipse's own
/// principal frame: <see cref="MajorAxis"/> and <see cref="MinorAxis"/> are the
/// semi-axis <i>vectors</i>, so <c>MajorAxis·cos t + MinorAxis·sin t</c> traces
/// the section and lands on the ellipsoid for every <c>t</c>. Length is then
/// the incomplete elliptic integral of the second kind between the endpoints'
/// parameters, and every other question — a point at a fraction along the arc,
/// the longitude it sweeps, where it crosses another arc — is answered in the
/// same frame.</para>
/// <para>Two endpoints that coincide or are exactly antipodal define no unique
/// plane. The first is a zero-length arc; the second real answers with half the
/// meridian ellipse's perimeter (probe-confirmed: <c>POINT(0 0)</c> to
/// <c>POINT(180 0)</c> measures 20003931.458 m, the smallest central section's
/// half-perimeter rather than the equator's), so the degenerate construction
/// puts the arc on the meridian through the first point.</para>
/// </remarks>
internal readonly struct GreatEllipticArc
{
    /// <summary>Semi-axis vector of the section ellipse's major axis.</summary>
    public readonly SpatialVector MajorAxis;

    /// <summary>Semi-axis vector of the section ellipse's minor axis, perpendicular to <see cref="MajorAxis"/>.</summary>
    public readonly SpatialVector MinorAxis;

    public readonly double MajorLength;

    public readonly double MinorLength;

    /// <summary>Ellipse parameter of the arc's first endpoint.</summary>
    public readonly double Start;

    /// <summary>Signed parameter span to the second endpoint, never more than π in magnitude.</summary>
    public readonly double Sweep;

    private GreatEllipticArc(SpatialVector major, SpatialVector minor, double start, double sweep)
    {
        this.MajorAxis = major;
        this.MinorAxis = minor;
        this.MajorLength = major.Length;
        this.MinorLength = minor.Length;
        this.Start = start;
        this.Sweep = sweep;
    }

    /// <summary>The surface point at ellipse parameter <paramref name="t"/>.</summary>
    public SpatialVector PointAt(double t) => (this.MajorAxis * Math.Cos(t)) + (this.MinorAxis * Math.Sin(t));

    /// <summary>Derivative of <see cref="PointAt"/> — the section's tangent, whose length is the arc-length element.</summary>
    public SpatialVector TangentAt(double t) => (this.MajorAxis * -Math.Sin(t)) + (this.MinorAxis * Math.Cos(t));

    /// <summary>The surface point a <paramref name="fraction"/> of the way from the first endpoint to the second.</summary>
    public SpatialVector At(double fraction) => PointAt(this.Start + (fraction * this.Sweep));

    /// <summary>Ellipse parameter of a point lying in the section's plane.</summary>
    public double ParameterOf(SpatialVector point) => Math.Atan2(
        point.Dot(this.MinorAxis) / (this.MinorLength * this.MinorLength),
        point.Dot(this.MajorAxis) / (this.MajorLength * this.MajorLength));

    /// <summary>True when <paramref name="t"/> names a point between the arc's endpoints.</summary>
    public bool Covers(double t)
    {
        var offset = t - this.Start;
        while (offset > Math.PI)
            offset -= 2 * Math.PI;
        while (offset < -Math.PI)
            offset += 2 * Math.PI;
        return this.Sweep >= 0 ? offset >= 0 && offset <= this.Sweep : offset <= 0 && offset >= this.Sweep;
    }

    /// <summary>Normal of the section's plane.</summary>
    public SpatialVector PlaneNormal => this.MajorAxis.Cross(this.MinorAxis);

    /// <summary>Where along the arc a point in its plane falls, 0 at the first endpoint and 1 at the second.</summary>
    public double FractionOf(SpatialVector point) => this.Sweep == 0 ? 0 : (ParameterOf(point) - this.Start) / this.Sweep;

    /// <summary>
    /// True when a surface point lies on the arc — in its plane, and between its
    /// endpoints. The plane test is relative, so a point up to a few microns off
    /// still counts; that is what makes a distance to something the arc runs
    /// through come back as exactly zero rather than as the residual a search
    /// for a minimum with a kink in it leaves behind.
    /// </summary>
    public bool Contains(SpatialVector point)
    {
        var normal = PlaneNormal;
        return Math.Abs(normal.Dot(point)) <= 1e-12 * normal.Length * point.Length && Covers(ParameterOf(point));
    }

    /// <summary>Arc length in metres.</summary>
    public double Length => SpatialGreatElliptic.Integrate(this, this.Start, this.Start + this.Sweep);

    /// <summary>Builds the arc joining two surface points.</summary>
    public static GreatEllipticArc Between(SpatialVector from, SpatialVector to)
    {
        var normal = from.Cross(to);
        var normalLength = normal.Length;
        // Coincident and antipodal pairs both leave the plane undetermined, and
        // an exactly antipodal pair still crosses to a normal at the noise floor
        // rather than to zero — sin(π) is not 0 in floating point.
        if (normalLength <= 1e-15 * from.Length * to.Length)
            return Degenerate(from, coincident: from.Dot(to) > 0);

        // Orthonormal basis of the cutting plane, with the first point on the
        // first axis; rotating the ellipsoid's restricted quadratic form to its
        // principal axes gives the section ellipse's semi-axes directly.
        var e1 = from.Normalized;
        var e2 = (normal * (1 / normalLength)).Cross(e1).Normalized;
        var q11 = SpatialEllipsoid.QuadraticForm(e1, e1);
        var q12 = SpatialEllipsoid.QuadraticForm(e1, e2);
        var q22 = SpatialEllipsoid.QuadraticForm(e2, e2);
        var rotation = 0.5 * Math.Atan2(2 * q12, q11 - q22);
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        var f1 = (e1 * cos) + (e2 * sin);
        var f2 = (e1 * -sin) + (e2 * cos);
        var major = f1 * (1.0 / Math.Sqrt(SpatialEllipsoid.QuadraticForm(f1, f1)));
        var minor = f2 * (1.0 / Math.Sqrt(SpatialEllipsoid.QuadraticForm(f2, f2)));

        var arc = new GreatEllipticArc(major, minor, 0, 0);
        var start = arc.ParameterOf(from);
        var sweep = arc.ParameterOf(to) - start;
        while (sweep > Math.PI)
            sweep -= 2 * Math.PI;
        while (sweep < -Math.PI)
            sweep += 2 * Math.PI;
        return new(major, minor, start, sweep);
    }

    /// <inheritdoc cref="Between(SpatialVector, SpatialVector)"/>
    public static GreatEllipticArc Between(SpatialCoordinate from, SpatialCoordinate to) =>
        Between(SpatialEllipsoid.ToCartesian(from), SpatialEllipsoid.ToCartesian(to));

    /// <summary>
    /// The arc for a pair defining no plane: the meridian section through
    /// <paramref name="from"/>, swept not at all for a coincident pair and half
    /// way round for an antipodal one.
    /// </summary>
    private static GreatEllipticArc Degenerate(SpatialVector from, bool coincident)
    {
        var axial = from.AxialRadius;
        var equatorial = axial == 0 ? new SpatialVector(1, 0, 0) : new SpatialVector(from.X / axial, from.Y / axial, 0);
        var arc = new GreatEllipticArc(
            equatorial * SpatialEllipsoid.SemiMajor,
            new SpatialVector(0, 0, SpatialEllipsoid.SemiMinor),
            0,
            0);
        return new(arc.MajorAxis, arc.MinorAxis, arc.ParameterOf(from), coincident ? 0 : Math.PI);
    }
}

/// <summary>
/// Round-earth measurement along the great elliptic arc: length, and the
/// closest approach between a point and an arc or between two arcs.
/// </summary>
/// <remarks>
/// <para>Real does <i>not</i> measure <c>geography</c> along the geodesic,
/// which is the assumption any stock implementation starts from. The
/// distinction is measurable and was how the curve was identified
/// (2026-07-31): a Vincenty geodesic, accurate to well under a millimetre at
/// these distances, lands 3.3 m short of real over Seattle→Paris while matching
/// a meridian degree to 0.065 mm. Meridians and the equator→pole path are
/// exactly where the two curves coincide; oblique paths are where they part,
/// and the great elliptic arc is the longer of the two.</para>
/// <para><b>Accuracy against real</b>: the arc is computed exactly, so the
/// residual is real's own approximation — at most ~6e-9 relative across the
/// probed set (59 mm over a quarter meridian, 3.2 mm over Seattle→Paris, exact
/// on the equator, where the section is a circle). Callers should not expect
/// bit-identical agreement; see <c>docs/claude/spatial.md</c>.</para>
/// </remarks>
internal static class SpatialGreatElliptic
{
    /// <summary>
    /// Distance in metres between two points given in WKT order (longitude,
    /// latitude), measured along the great elliptic arc.
    /// </summary>
    public static double Distance(SpatialCoordinate from, SpatialCoordinate to) => GreatEllipticArc.Between(from, to).Length;

    /// <inheritdoc cref="Distance(SpatialCoordinate, SpatialCoordinate)"/>
    public static double Distance(SpatialVector from, SpatialVector to) => GreatEllipticArc.Between(from, to).Length;

    /// <summary>
    /// Arc length of the section ellipse between two parameter angles — an
    /// incomplete elliptic integral of the second kind, taken by composite
    /// 20-node Gauss-Legendre. The integrand is smooth and the panel count
    /// tracks the sweep, so the quadrature error stays far below the difference
    /// from real.
    /// </summary>
    public static double Integrate(in GreatEllipticArc arc, double from, double to)
    {
        var panels = GaussLegendre.PanelsFor(to - from, Math.PI / 8, 8);
        var total = 0.0;
        var width = (to - from) / panels;
        for (var panel = 0; panel < panels; panel++)
        {
            var mid = from + (panel * width) + (width / 2);
            var half = width / 2;
            var sum = 0.0;
            for (var i = 0; i < GaussLegendre.Nodes.Length; i++)
            {
                sum += GaussLegendre.Weights[i]
                    * (arc.TangentAt(mid + (half * GaussLegendre.Nodes[i])).Length
                        + arc.TangentAt(mid - (half * GaussLegendre.Nodes[i])).Length);
            }
            total += sum * half;
        }
        return Math.Abs(total);
    }

    /// <summary>
    /// Closest approach in metres between a surface point and an arc, with the
    /// fraction along the arc where it happens.
    /// </summary>
    /// <remarks>
    /// <para>The search runs on the <b>chord</b> to the arc, not on the arc
    /// length, and only the winner is measured properly. A chord and the surface
    /// distance it stands for are related by a factor that depends on the chord
    /// alone as long as the section's curvature holds still, so the two have the
    /// same minimizer to within the ellipsoid's flattening — worth ~1e-12
    /// relative on the value here, three orders below real's own error, for a
    /// search step that costs two trigonometric evaluations instead of a whole
    /// elliptic integral.</para>
    /// <para>The distance from a point to a sub-π arc is unimodal along it, so a
    /// golden-section search brackets the minimum; 48 rounds shrink the bracket
    /// below 1e-10 of the arc, and the value is quadratic in that. The endpoints
    /// are measured too, since the minimum sits at one of them whenever the
    /// perpendicular foot misses the arc.</para>
    /// </remarks>
    public static (double Fraction, double Distance) ClosestApproach(SpatialVector point, in GreatEllipticArc arc)
    {
        if (arc.Contains(point))
            return (Math.Clamp(arc.FractionOf(point), 0, 1), 0);
        var low = 0.0;
        var high = 1.0;
        var inner = high - (Ratio * (high - low));
        var outer = low + (Ratio * (high - low));
        var innerValue = (point - arc.At(inner)).Length;
        var outerValue = (point - arc.At(outer)).Length;
        for (var round = 0; round < 48 && high - low > 1e-12; round++)
        {
            if (innerValue < outerValue)
            {
                (high, outer, outerValue) = (outer, inner, innerValue);
                inner = high - (Ratio * (high - low));
                innerValue = (point - arc.At(inner)).Length;
            }
            else
            {
                (low, inner, innerValue) = (inner, outer, outerValue);
                outer = low + (Ratio * (high - low));
                outerValue = (point - arc.At(outer)).Length;
            }
        }
        var best = (Fraction: 0.0, Distance: Distance(point, arc.At(0)));
        var end = Distance(point, arc.At(1));
        if (end < best.Distance)
            best = (1.0, end);
        var interior = Distance(point, arc.At(inner));
        return interior < best.Distance ? (inner, interior) : best;
    }

    /// <summary>
    /// Closest approach in metres between two arcs. Crossing arcs are zero
    /// apart; otherwise the minimum is either at an endpoint of one of them or
    /// at a mutual perpendicular, and alternating one-dimensional searches
    /// seeded from the best endpoint reach the latter.
    /// </summary>
    public static double ClosestApproach(in GreatEllipticArc first, in GreatEllipticArc second)
    {
        if (Cross(first, second))
            return 0;

        // Four endpoint measurements, each naming a fraction along the first
        // arc: the two from its own endpoints trivially, the two from the
        // second arc's endpoints through the perpendicular foot they land on.
        var onFirst = 0.0;
        var best = ClosestApproach(first.At(0), second).Distance;
        var fromEnd = ClosestApproach(first.At(1), second).Distance;
        if (fromEnd < best)
            (onFirst, best) = (1.0, fromEnd);
        var fromStartOfSecond = ClosestApproach(second.At(0), first);
        if (fromStartOfSecond.Distance < best)
            (onFirst, best) = fromStartOfSecond;
        var fromEndOfSecond = ClosestApproach(second.At(1), first);
        if (fromEndOfSecond.Distance < best)
            (onFirst, best) = fromEndOfSecond;

        for (var round = 0; round < 16; round++)
        {
            var alongSecond = ClosestApproach(first.At(onFirst), second);
            var alongFirst = ClosestApproach(second.At(alongSecond.Fraction), first);
            best = Math.Min(best, Math.Min(alongSecond.Distance, alongFirst.Distance));
            if (Math.Abs(alongFirst.Fraction - onFirst) < 1e-12)
                break;
            onFirst = alongFirst.Fraction;
        }
        return best;
    }

    /// <summary>
    /// True when the two arcs meet. Their planes cut the ellipsoid's centre, so
    /// they intersect in a line whose two surface points are the only places the
    /// arcs can meet.
    /// </summary>
    /// <remarks>
    /// Arcs sharing a plane have no such line, and the test has to reject them
    /// on a <i>relative</i> comparison: two stretches of one great circle cross
    /// to a direction that is pure roundoff, and normalizing that noise would
    /// name an arbitrary point on the ellipsoid which could fall inside both
    /// spans and report a meeting that isn't there. Coplanar arcs reach the
    /// right answer through the endpoint search instead — zero where they
    /// genuinely overlap, since an endpoint of one then lies on the other.
    /// </remarks>
    public static bool Cross(in GreatEllipticArc first, in GreatEllipticArc second)
    {
        var firstNormal = first.PlaneNormal;
        var secondNormal = second.PlaneNormal;
        var direction = firstNormal.Cross(secondNormal);
        var length = direction.Length;
        if (double.IsNaN(length) || length <= 1e-12 * firstNormal.Length * secondNormal.Length)
            return false;
        var meeting = SpatialEllipsoid.OntoSurface(direction);
        return Meets(first, second, meeting) || Meets(first, second, meeting * -1);
    }

    private static bool Meets(in GreatEllipticArc first, in GreatEllipticArc second, SpatialVector point) =>
        first.Covers(first.ParameterOf(point)) && second.Covers(second.ParameterOf(point));

    /// <summary>Golden-section ratio, the fraction of a bracket each round keeps.</summary>
    private static readonly double Ratio = (Math.Sqrt(5) - 1) / 2;
}
