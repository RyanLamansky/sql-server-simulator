namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>ShortestLineTo(other)</c> on the round earth: the great elliptic arc
/// from the receiver's point nearest the other instance to the other's point
/// nearest it.
/// </summary>
/// <remarks>
/// <para>The two ends are where the least great elliptic distance between
/// the operands' points and edges is reached, found as the distance measure
/// finds it and then refined on the surface distance itself, since the
/// chord's minimizer is only within the flattening of the arc's and a foot
/// point carries that error at full size. Instances that meet answer
/// <c>LINESTRING EMPTY</c>; an empty operand answers NULL.</para>
/// <para>Real writes both ends through a unit vector and back, reading
/// latitude as a spherical angle, so even an input vertex comes back with
/// its last digit disturbed — <c>POINT (1 1)</c> as
/// <c>0.99999999999999978 1</c> (probed 2026-09-29 against SQL Server
/// 2025).</para>
/// </remarks>
internal static class SpatialGeodeticShortestLine
{
    public static SpatialShape? ShortestLine(SpatialShape a, SpatialShape b)
    {
        if (a.IsEmpty || b.IsEmpty)
            return null;
        if (SpatialGeodeticRelate.Evaluate(SpatialPredicateKind.Intersects, a, b))
            return SpatialShape.Empty(SpatialShapeType.LineString);
        var left = new List<Piece>();
        var right = new List<Piece>();
        var vertices = new Dictionary<(double, double, double), SpatialCoordinate>();
        Collect(a, left, vertices);
        Collect(b, right, vertices);

        // An arc bows off its chord by at most its sagitta, so a pair whose
        // chords stay further apart than any vertex pair's surface distance,
        // less both sagittas, can't hold the answer.
        var nearest = double.PositiveInfinity;
        foreach (var l in left)
        {
            foreach (var r in right)
                nearest = Math.Min(nearest, (l.From - r.From).Length);
        }
        var threshold = SpatialMeasures.ChordThreshold(nearest);

        var best = double.PositiveInfinity;
        (SpatialVector From, SpatialVector To) line = default;
        void Consider(SpatialVector from, SpatialVector to)
        {
            var distance = SpatialGreatElliptic.Distance(from, to);
            if (distance < best)
            {
                best = distance;
                line = (from, to);
            }
        }
        foreach (var l in left)
        {
            foreach (var r in right)
            {
                if (ChordDistance(l, r) - l.Sagitta - r.Sagitta > threshold)
                    continue;
                if (l.Arc is null)
                {
                    Consider(l.From, r.Arc is null ? r.From : Foot(l.From, r));
                    continue;
                }
                if (r.Arc is null)
                {
                    Consider(Foot(r.From, l), r.From);
                    continue;
                }
                Consider(l.From, Foot(l.From, r));
                Consider(l.To, Foot(l.To, r));
                Consider(Foot(r.From, l), r.From);
                Consider(Foot(r.To, l), r.To);

                // A mutual perpendicular inside both arcs, reached by
                // alternating one-sided searches from the best end found.
                var onLeft = line.From;
                for (var round = 0; round < 16; round++)
                {
                    var onRight = Foot(onLeft, r);
                    var next = Foot(onRight, l);
                    Consider(next, onRight);
                    if ((next - onLeft).Length < 1e-9)
                        break;
                    onLeft = next;
                }
            }
        }
        return SpatialShape.Leaf(SpatialShapeType.LineString, [[Written(line.From, vertices), Written(line.To, vertices)]]);
    }

    /// <summary>The straight-line distance between two pieces' chords.</summary>
    private static double ChordDistance(in Piece first, in Piece second) =>
        first.Arc is null ? SpatialMeasures.PointChordDistance(first.From, second.From, second.To)
        : second.Arc is null ? SpatialMeasures.PointChordDistance(second.From, first.From, first.To)
        : SpatialMeasures.ChordDistance(first.From, first.To, second.From, second.To);

    /// <summary>A point (no arc) or an edge of an operand, as surface vectors.</summary>
    private readonly struct Piece(SpatialVector from, SpatialVector to, GreatEllipticArc? arc)
    {
        public readonly SpatialVector From = from;
        public readonly SpatialVector To = to;
        public readonly GreatEllipticArc? Arc = arc;

        /// <summary>The most the arc bows off its chord, taking the ellipsoid's tightest curvature.</summary>
        public readonly double Sagitta = (to - from).SquaredLength / (8 * TightestRadius);
    }

    /// <summary>The ellipsoid's smallest radius of curvature, the meridian's at the equator, less a margin.</summary>
    private static double TightestRadius => SpatialEllipsoid.SemiMinor * SpatialEllipsoid.SemiMinor / SpatialEllipsoid.SemiMajor * 0.9;

    private static void Collect(SpatialShape shape, List<Piece> pieces, Dictionary<(double, double, double), SpatialCoordinate> vertices)
    {
        SpatialVector Vertex(SpatialCoordinate point)
        {
            var vector = SpatialEllipsoid.ToCartesian(point);
            _ = vertices.TryAdd((vector.X, vector.Y, vector.Z), new SpatialCoordinate(point.X, point.Y));
            return vector;
        }

        foreach (var figure in shape.Figures)
        {
            if (figure.Length == 1)
            {
                var point = Vertex(figure[0]);
                pieces.Add(new(point, point, null));
                continue;
            }
            for (var i = 1; i < figure.Length; i++)
            {
                var from = Vertex(figure[i - 1]);
                var to = Vertex(figure[i]);
                pieces.Add(from.X == to.X && from.Y == to.Y && from.Z == to.Z
                    ? new(from, from, null)
                    : new(from, to, GreatEllipticArc.Between(from, to)));
            }
        }
        foreach (var child in shape.Children)
            Collect(child, pieces, vertices);
    }

    /// <summary>
    /// The point of <paramref name="piece"/>'s arc nearest <paramref name="point"/>:
    /// the chord search's pick, refined to where the surface distance stops
    /// falling. A minimum is flat, so searching on the distance itself stalls
    /// at the square root of its rounding — a few metres of foot — and the
    /// refinement finds the zero of its slope instead.
    /// </summary>
    private static SpatialVector Foot(SpatialVector point, in Piece piece)
    {
        var arc = piece.Arc!.Value;
        var (fraction, _) = SpatialGreatElliptic.ClosestApproach(point, arc);
        if (fraction is 0 or 1)
            return fraction == 0 ? piece.From : piece.To;
        const double step = 1e-5;
        double Slope(double t) =>
            SpatialGreatElliptic.Distance(point, arc.At(t + step)) - SpatialGreatElliptic.Distance(point, arc.At(t - step));
        // The chord's minimizer sits within the flattening of the arc's, so a
        // bracket of that order around it, widened when it misses, holds the
        // zero.
        double low, high;
        for (var reach = 1e-4; ; reach *= 4)
        {
            low = Math.Max(step, fraction - reach);
            high = Math.Min(1 - step, fraction + reach);
            if (Slope(low) < 0 && Slope(high) > 0)
                break;
            if (low == step && high == 1 - step)
                return arc.At(fraction);
        }
        for (var round = 0; round < 60 && high - low > 1e-16; round++)
        {
            var mid = (low + high) / 2;
            if (Slope(mid) < 0)
                low = mid;
            else
                high = mid;
        }
        return arc.At((low + high) / 2);
    }

    /// <summary>A surface point as real writes it: through the unit vector of its geodetic coordinates.</summary>
    private static SpatialCoordinate Written(SpatialVector point, Dictionary<(double, double, double), SpatialCoordinate> vertices)
    {
        double lon, lat;
        if (vertices.TryGetValue((point.X, point.Y, point.Z), out var vertex))
        {
            lon = vertex.X * SpatialEllipsoid.RadiansPerDegree;
            lat = vertex.Y * SpatialEllipsoid.RadiansPerDegree;
        }
        else
        {
            lon = Math.Atan2(point.Y, point.X);
            lat = SpatialEllipsoid.GeodeticLatitude(point);
        }
        var unit = new SpatialVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        return new SpatialCoordinate(
            Math.Atan2(unit.Y, unit.X) * SpatialEllipsoid.DegreesPerRadian,
            Math.Atan2(unit.Z, unit.AxialRadius) * SpatialEllipsoid.DegreesPerRadian);
    }
}
