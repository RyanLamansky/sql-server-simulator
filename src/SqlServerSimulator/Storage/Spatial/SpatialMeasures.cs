namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// The measurements a spatial instance answers — <c>STArea()</c>,
/// <c>STLength()</c> and <c>STDistance()</c> — for both spatial types.
/// </summary>
/// <remarks>
/// <para>The planar half is flat-earth Cartesian and exact for the shapes the
/// model carries. The round-earth half measures along the <b>great elliptic
/// arc</b> real joins <c>geography</c> vertices with (see
/// <see cref="SpatialGreatElliptic"/>), and its area integral is
/// <see cref="SpatialEllipsoidArea"/>; neither is a coordinate swap over the
/// planar code.</para>
/// <para>Area and length recurse through Multi* and GeometryCollection members,
/// and both report 0 rather than NULL for a shape of the wrong dimension — a
/// Point has no length and no area, a LineString no area.</para>
/// </remarks>
internal static class SpatialMeasures
{
    /// <summary>
    /// Total planar area: each polygon contributes its exterior ring's area
    /// less its interior rings'. Ring orientation doesn't matter — the
    /// shoelace sum is taken absolute per ring, which is what lets a polygon
    /// written clockwise measure the same as one written counter-clockwise.
    /// </summary>
    public static double Area(SpatialShape shape)
    {
        var total = 0.0;
        if (shape.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon)
        {
            for (var i = 0; i < shape.Figures.Length; i++)
            {
                var ring = Math.Abs(SignedRingArea(shape.Figures[i]));
                total += i == 0 ? ring : -ring;
            }
        }
        foreach (var child in shape.Children)
            total += Area(child);
        return total;
    }

    /// <summary>
    /// Total round-earth area in metres squared. Unlike the planar measure this
    /// one reads ring orientation, because a <c>geography</c> ring wound against
    /// the left-hand rule names the complementary region — see
    /// <see cref="SpatialEllipsoidArea.Polygon"/>.
    /// </summary>
    public static double GeographyArea(SpatialShape shape)
    {
        var total = 0.0;
        if (shape.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon && shape.Figures.Length > 0)
            total += SpatialEllipsoidArea.Polygon(shape.Figures);
        foreach (var child in shape.Children)
            total += GeographyArea(child);
        return total;
    }

    /// <summary>
    /// Total planar length: the perimeter of every ring of every polygon plus
    /// the length of every line figure. A polygon's <c>STLength()</c> is its
    /// boundary, which is why the ring walk is shared with the line walk.
    /// </summary>
    public static double Length(SpatialShape shape)
    {
        var total = 0.0;
        if (shape.Type is not (SpatialShapeType.Point or SpatialShapeType.MultiPoint))
        {
            foreach (var figure in shape.Figures)
                total += FigureLength(figure);
        }
        foreach (var child in shape.Children)
            total += Length(child);
        return total;
    }

    /// <summary>
    /// Round-earth length in metres: every figure's consecutive vertices
    /// joined by great elliptic arcs, summed the same way the planar walk
    /// sums straight segments. A polygon's length is its boundary here too.
    /// </summary>
    public static double GeographyLength(SpatialShape shape)
    {
        var total = 0.0;
        if (shape.Type is not (SpatialShapeType.Point or SpatialShapeType.MultiPoint))
        {
            foreach (var figure in shape.Figures)
            {
                for (var i = 1; i < figure.Length; i++)
                    total += SpatialGreatElliptic.Distance(figure[i - 1], figure[i]);
            }
        }
        foreach (var child in shape.Children)
            total += GeographyLength(child);
        return total;
    }

    /// <summary>
    /// Closest approach between two planar instances: zero where they meet or
    /// one contains the other, and otherwise the least distance between their
    /// component points and edges.
    /// </summary>
    /// <remarks>
    /// Boundaries that don't meet leave only containment to check, and a single
    /// vertex answers it: if no edge of one instance crosses an edge of the
    /// other, either one sits whole inside the other or the two are apart. The
    /// even-odd rule reads a point in a hole as outside, which is what real
    /// answers — a point in a polygon's hole measures to the hole's ring.
    /// </remarks>
    public static double PlanarDistance(SpatialShape a, SpatialShape b)
    {
        var left = new SpatialRelateOperand(a);
        var right = new SpatialRelateOperand(b);
        var leftEdges = EdgesOf(left);
        var rightEdges = EdgesOf(right);
        var best = double.PositiveInfinity;
        foreach (var point in left.Points)
        {
            foreach (var other in right.Points)
                best = Math.Min(best, SpatialTopology.Distance(point, other));
            foreach (var edge in rightEdges)
                best = Math.Min(best, SpatialTopology.Distance(point, edge));
        }
        foreach (var point in right.Points)
        {
            foreach (var edge in leftEdges)
                best = Math.Min(best, SpatialTopology.Distance(point, edge));
        }
        foreach (var edge in leftEdges)
        {
            foreach (var other in rightEdges)
            {
                if (SpatialTopology.ExtentDistance(edge, other) < best)
                    best = Math.Min(best, SpatialTopology.Distance(edge, other));
            }
        }
        return best == 0 || PlanarCovers(left, right) || PlanarCovers(right, left) ? 0 : best;
    }

    /// <summary>
    /// Closest approach between two round-earth instances, in metres, measured
    /// along great elliptic arcs.
    /// </summary>
    /// <remarks>
    /// <para>The shape of the answer matches the planar one — crossing edges and
    /// containment are zero, everything else is a minimum over component pairs —
    /// but a pair costs a search along the arcs where the planar one costs a
    /// projection, so the pairs worth searching are picked out first.</para>
    /// <para>The picking runs on <b>chords</b>: the straight line between two
    /// surface points passes through the ellipsoid, so a chord never exceeds the
    /// surface distance between the same points, and it exceeds it by at most a
    /// bounded factor. One cheap pass takes the shortest chord over every pair;
    /// inflating it by that factor gives a threshold no winning pair can be
    /// above, and the exact pass measures only the pairs that clear it. Without
    /// the pre-pass a scan starting from an infinite bound measures the whole
    /// first row exactly, which for a pair of many-vertex borders is thousands
    /// of searches that a chord rules out in a few flops each.</para>
    /// </remarks>
    public static double GeographyDistance(SpatialShape a, SpatialShape b)
    {
        var left = new RoundEarthOperand(a);
        var right = new RoundEarthOperand(b);
        var threshold = ChordThreshold(ShortestChord(left, right));
        var best = double.PositiveInfinity;
        foreach (var point in left.Points)
        {
            foreach (var other in right.Points)
            {
                if ((point - other).Length <= threshold)
                    best = Math.Min(best, SpatialGreatElliptic.Distance(point, other));
            }
            best = Math.Min(best, NearestEdge(point, right, threshold));
        }
        foreach (var point in right.Points)
            best = Math.Min(best, NearestEdge(point, left, threshold));
        for (var i = 0; i < left.Edges.Length; i++)
        {
            ref readonly var edge = ref left.Edges[i];
            for (var j = 0; j < right.Edges.Length; j++)
            {
                ref readonly var other = ref right.Edges[j];
                var reach = threshold + edge.Radius + other.Radius;
                if ((edge.Centre - other.Centre).SquaredLength <= reach * reach && ChordDistance(edge, other) <= threshold)
                    best = Math.Min(best, SpatialGreatElliptic.ClosestApproach(edge.Arc, other.Arc));
            }
        }
        return best == 0 || right.EnclosesAny(left) || left.EnclosesAny(right) ? 0 : best;
    }

    /// <summary>Least distance from a point to any of an operand's edges, skipping the ones the chord threshold rules out.</summary>
    private static double NearestEdge(SpatialVector point, RoundEarthOperand operand, double threshold)
    {
        var best = double.PositiveInfinity;
        for (var i = 0; i < operand.Edges.Length; i++)
        {
            ref readonly var edge = ref operand.Edges[i];
            if (ChordDistance(point, edge) <= threshold)
                best = Math.Min(best, SpatialGreatElliptic.ClosestApproach(point, edge.Arc).Distance);
        }
        return best;
    }

    /// <summary>
    /// Shortest chord between any pair of the two operands' components — a
    /// lower bound on the answer, computed with no arc search at all. Each pair
    /// is pre-screened against the bounding spheres of the two edges, which
    /// costs a subtraction where the chord itself costs a clamped projection.
    /// </summary>
    private static double ShortestChord(RoundEarthOperand left, RoundEarthOperand right)
    {
        var best = double.PositiveInfinity;
        foreach (var point in left.Points)
        {
            foreach (var other in right.Points)
                best = Math.Min(best, (point - other).Length);
            best = Math.Min(best, NearestChord(point, right, best));
        }
        foreach (var point in right.Points)
            best = Math.Min(best, NearestChord(point, left, best));
        for (var i = 0; i < left.Edges.Length; i++)
        {
            ref readonly var edge = ref left.Edges[i];
            for (var j = 0; j < right.Edges.Length; j++)
            {
                ref readonly var other = ref right.Edges[j];
                var reach = best + edge.Radius + other.Radius;
                if ((edge.Centre - other.Centre).SquaredLength < reach * reach)
                    best = Math.Min(best, ChordDistance(edge, other));
            }
        }
        return best;
    }

    private static double NearestChord(SpatialVector point, RoundEarthOperand operand, double best)
    {
        for (var i = 0; i < operand.Edges.Length; i++)
        {
            ref readonly var edge = ref operand.Edges[i];
            var reach = best + edge.Radius;
            if ((point - edge.Centre).SquaredLength < reach * reach)
                best = Math.Min(best, ChordDistance(point, edge));
        }
        return best;
    }

    /// <summary>
    /// The largest surface distance a chord of the given length can stand for.
    /// A chord <c>c</c> subtending a circle of radius <c>R</c> spans an arc of
    /// <c>c·(θ/2)/sin(θ/2)</c>, which stays under <c>c·(1 + c²/6R²)</c> for
    /// every chord up to the whole diameter; taking <c>R</c> as the ellipsoid's
    /// smallest radius keeps the bound on the safe side.
    /// </summary>
    private static double ChordThreshold(double chord) =>
        chord * (1 + (chord * chord / (6 * SpatialEllipsoid.SemiMinor * SpatialEllipsoid.SemiMinor)));

    /// <summary>True when any vertex of <paramref name="other"/> lies inside <paramref name="area"/>'s rings.</summary>
    private static bool PlanarCovers(SpatialRelateOperand area, SpatialRelateOperand other)
    {
        if (area.RingSegments.Count == 0)
            return false;
        foreach (var point in other.Points)
        {
            if (SpatialTopology.IsInsideRings(point, area.RingSegments))
                return true;
        }
        foreach (var segment in EdgesOf(other))
        {
            if (SpatialTopology.IsInsideRings(segment.A, area.RingSegments))
                return true;
        }
        return false;
    }

    /// <summary>Every line and ring segment of an operand, which for distance are the same thing.</summary>
    private static List<PlanarSegment> EdgesOf(SpatialRelateOperand operand)
    {
        var edges = new List<PlanarSegment>(operand.LineSegments.Count + operand.RingSegments.Count);
        edges.AddRange(operand.LineSegments);
        edges.AddRange(operand.RingSegments);
        return edges;
    }

    private static double ChordDistance(SpatialVector point, in RoundEarthEdge edge) =>
        PointChordDistance(point, edge.From, edge.To);

    private static double ChordDistance(in RoundEarthEdge first, in RoundEarthEdge second) =>
        ChordDistance(first.From, first.To, second.From, second.To);

    /// <summary>Distance from a point to a straight chord in three dimensions.</summary>
    private static double PointChordDistance(SpatialVector point, SpatialVector from, SpatialVector to)
    {
        var along = to - from;
        var squaredLength = along.Dot(along);
        var fraction = squaredLength == 0 ? 0 : Math.Clamp((point - from).Dot(along) / squaredLength, 0, 1);
        return (point - (from + (along * fraction))).Length;
    }

    /// <summary>
    /// Distance between two straight chords in three dimensions — the standard
    /// clamped parametric solution, used only as the pruning bound.
    /// </summary>
    private static double ChordDistance(SpatialVector p0, SpatialVector p1, SpatialVector q0, SpatialVector q1)
    {
        var u = p1 - p0;
        var v = q1 - q0;
        var w = p0 - q0;
        var uu = u.Dot(u);
        var uv = u.Dot(v);
        var vv = v.Dot(v);
        var uw = u.Dot(w);
        var vw = v.Dot(w);
        var determinant = (uu * vv) - (uv * uv);
        double sNumerator;
        var sDenominator = determinant;
        double tNumerator;
        var tDenominator = determinant;
        if (determinant <= 0)
        {
            sNumerator = 0;
            sDenominator = 1;
            tNumerator = vw;
            tDenominator = vv;
        }
        else
        {
            sNumerator = (uv * vw) - (vv * uw);
            tNumerator = (uu * vw) - (uv * uw);
            if (sNumerator < 0)
            {
                sNumerator = 0;
                tNumerator = vw;
                tDenominator = vv;
            }
            else if (sNumerator > sDenominator)
            {
                sNumerator = sDenominator;
                tNumerator = vw + uv;
                tDenominator = vv;
            }
        }
        if (tNumerator < 0)
        {
            tNumerator = 0;
            (sNumerator, sDenominator) = -uw < 0 ? (0, 1) : -uw > uu ? (sDenominator, sDenominator) : (-uw, uu);
        }
        else if (tNumerator > tDenominator)
        {
            tNumerator = tDenominator;
            (sNumerator, sDenominator) = uv - uw < 0 ? (0, 1) : uv - uw > uu ? (sDenominator, sDenominator) : (uv - uw, uu);
        }
        var s = sDenominator == 0 ? 0 : sNumerator / sDenominator;
        var t = tDenominator == 0 ? 0 : tNumerator / tDenominator;
        return (w + (u * s) - (v * t)).Length;
    }

    /// <summary>Shoelace sum over a closed ring; the sign carries the ring's orientation.</summary>
    private static double SignedRingArea(SpatialCoordinate[] ring)
    {
        if (ring.Length < 4)
            return 0;
        var sum = 0.0;
        for (var i = 0; i < ring.Length - 1; i++)
            sum += (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
        return sum / 2.0;
    }

    private static double FigureLength(SpatialCoordinate[] points)
    {
        var total = 0.0;
        for (var i = 1; i < points.Length; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            total += Math.Sqrt((dx * dx) + (dy * dy));
        }
        return total;
    }
}

/// <summary>
/// One edge of a round-earth instance: the arc, the endpoints its pruning
/// chord runs between, and that chord's bounding sphere.
/// </summary>
internal readonly struct RoundEarthEdge(SpatialVector from, SpatialVector to)
{
    public readonly SpatialVector From = from;

    public readonly SpatialVector To = to;

    public readonly GreatEllipticArc Arc = GreatEllipticArc.Between(from, to);

    /// <summary>Midpoint of the chord.</summary>
    public readonly SpatialVector Centre = (from + to) * 0.5;

    /// <summary>Half the chord's length, so <c>|p - Centre| - Radius</c> bounds the distance to the edge from below.</summary>
    public readonly double Radius = (to - from).Length * 0.5;
}

/// <summary>
/// One side of a round-earth distance comparison, flattened into the components
/// a measurement reads: isolated points, edge arcs, and the ring runs a
/// containment test walks.
/// </summary>
internal sealed class RoundEarthOperand
{
    public readonly List<SpatialVector> Points = [];

    /// <summary>
    /// Every edge, as an array so the pruning scan can walk it by reference —
    /// a pair of many-vertex borders visits this a few million times, and the
    /// edge is far too wide to copy per visit.
    /// </summary>
    public readonly RoundEarthEdge[] Edges;

    /// <summary>Ranges of <see cref="Edges"/> that close a polygon ring, in the order the rings were collected.</summary>
    public readonly List<(int First, int Count)> Rings = [];

    private readonly List<RoundEarthEdge> collected = [];

    public RoundEarthOperand(SpatialShape shape)
    {
        Collect(shape);
        this.Edges = [.. this.collected];
    }

    /// <summary>
    /// True when any vertex of <paramref name="other"/> lies inside this
    /// operand's rings.
    /// </summary>
    /// <remarks>
    /// Containment is the winding of the rings seen from the point: in the frame
    /// that puts the point at a pole, a ring that encircles it turns through a
    /// full revolution and one that doesn't returns to where it started. Summing
    /// over every ring handles holes without a separate rule — a point in a hole
    /// collects a turn from the shell and its negative from the hole.
    /// </remarks>
    public bool EnclosesAny(RoundEarthOperand other)
    {
        if (this.Rings.Count == 0)
            return false;
        foreach (var point in other.Points)
        {
            if (Encloses(point))
                return true;
        }
        for (var i = 0; i < other.Edges.Length; i++)
        {
            if (Encloses(other.Edges[i].From))
                return true;
        }
        return false;
    }

    private bool Encloses(SpatialVector point)
    {
        var axis = point.Normalized;
        var reference = Math.Abs(axis.Z) < 0.9 ? new SpatialVector(0, 0, 1) : new SpatialVector(1, 0, 0);
        var east = axis.Cross(reference).Normalized;
        var north = east.Cross(axis);
        var winding = 0.0;
        foreach (var (firstEdge, count) in this.Rings)
        {
            for (var i = 0; i < count; i++)
            {
                var arc = this.Edges[firstEdge + i].Arc;
                winding += Sweep(arc, north, east, 0, 1, Azimuth(arc.At(0), north, east), Azimuth(arc.At(1), north, east), 0);
            }
        }
        return Math.Abs(winding) > Math.PI;
    }

    /// <summary>
    /// Azimuth change along an arc as seen from the frame's pole. An edge that
    /// swings more than a right angle is halved first, so taking the short way
    /// round between two samples never misses a turn.
    /// </summary>
    private static double Sweep(
        in GreatEllipticArc arc, SpatialVector north, SpatialVector east,
        double low, double high, double lowAzimuth, double highAzimuth, int depth)
    {
        var delta = SpatialEllipsoid.ShortestLongitudeDelta(lowAzimuth, highAzimuth);
        if (Math.Abs(delta) <= Math.PI / 2 || depth >= 12)
            return delta;
        var middle = (low + high) / 2;
        var middleAzimuth = Azimuth(arc.At(middle), north, east);
        return Sweep(arc, north, east, low, middle, lowAzimuth, middleAzimuth, depth + 1)
            + Sweep(arc, north, east, middle, high, middleAzimuth, highAzimuth, depth + 1);
    }

    private static double Azimuth(SpatialVector point, SpatialVector north, SpatialVector east) =>
        Math.Atan2(point.Dot(east), point.Dot(north));

    private void Collect(SpatialShape shape)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
            case SpatialShapeType.MultiPoint:
                foreach (var figure in shape.Figures)
                {
                    foreach (var coordinate in figure)
                        this.Points.Add(SpatialEllipsoid.ToCartesian(coordinate));
                }
                break;
            case SpatialShapeType.LineString:
            case SpatialShapeType.MultiLineString:
                foreach (var figure in shape.Figures)
                    AddRun(figure);
                break;
            case SpatialShapeType.Polygon:
            case SpatialShapeType.MultiPolygon:
                foreach (var figure in shape.Figures)
                {
                    var first = this.collected.Count;
                    AddRun(figure);
                    this.Rings.Add((first, this.collected.Count - first));
                }
                break;
            default:
                break;
        }
        foreach (var child in shape.Children)
            Collect(child);
    }

    private void AddRun(SpatialCoordinate[] figure)
    {
        var previous = figure.Length == 0 ? default : SpatialEllipsoid.ToCartesian(figure[0]);
        for (var i = 1; i < figure.Length; i++)
        {
            var next = SpatialEllipsoid.ToCartesian(figure[i]);
            if (figure[i].X != figure[i - 1].X || figure[i].Y != figure[i - 1].Y)
                this.collected.Add(new(previous, next));
            previous = next;
        }
    }
}
