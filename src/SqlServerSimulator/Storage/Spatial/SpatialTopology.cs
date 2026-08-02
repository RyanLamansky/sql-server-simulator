namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// A planar (x, y) location, the working coordinate of the topology engine.
/// Z and M are dropped on the way in — real ignores them in every predicate.
/// </summary>
internal readonly struct PlanarPoint(double x, double y) : IEquatable<PlanarPoint>
{
    public readonly double X = x;

    public readonly double Y = y;

    public bool Equals(PlanarPoint other) => this.X.Equals(other.X) && this.Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is PlanarPoint other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(this.X, this.Y);

    public static bool operator ==(PlanarPoint left, PlanarPoint right) => left.Equals(right);

    public static bool operator !=(PlanarPoint left, PlanarPoint right) => !left.Equals(right);

    public static PlanarPoint From(SpatialCoordinate coordinate) => new(coordinate.X, coordinate.Y);
}

/// <summary>A straight edge between two <see cref="PlanarPoint"/>s.</summary>
internal readonly struct PlanarSegment(PlanarPoint a, PlanarPoint b)
{
    public readonly PlanarPoint A = a;

    public readonly PlanarPoint B = b;

    public bool IsDegenerate => this.A == this.B;

    public double MinX => Math.Min(this.A.X, this.B.X);

    public double MaxX => Math.Max(this.A.X, this.B.X);

    public double MinY => Math.Min(this.A.Y, this.B.Y);

    public double MaxY => Math.Max(this.A.Y, this.B.Y);

    public PlanarPoint Midpoint => new((this.A.X / 2) + (this.B.X / 2), (this.A.Y / 2) + (this.B.Y / 2));
}

/// <summary>
/// Straight-edge planar geometry primitives: orientation, incidence,
/// segment intersection and even-odd point-in-area.
/// </summary>
/// <remarks>
/// <para><b>Arithmetic model.</b> Every test runs in <c>double</c>, and the
/// orientation determinant carries a relative error filter: a determinant no
/// larger than the roundoff bound of its own two products reads as
/// <b>collinear</b>. That is what real does — probing SQL Server 2025 with
/// points a few ulps off an oblique segment (<c>POINT(1.1666666666666665
/// 0.5)</c> against <c>LINESTRING(0 0, 7 3)</c>, whose naive cross product is
/// 4.4e-16) reports them <i>on</i> the segment, while a point 1e-18 off an
/// axis-aligned segment — where the determinant is computed with no roundoff
/// at all — reports <i>off</i> it. Exact arithmetic would answer "off" in both
/// cases; a fixed epsilon would answer "on" in both.</para>
/// <para>Coordinates otherwise compare exactly. Real additionally snaps a
/// point lying within roughly 1e-15 of the coordinate extent onto a segment
/// endpoint, which the simulator doesn't reproduce — see
/// <c>docs/claude/spatial.md</c>.</para>
/// </remarks>
internal static class SpatialTopology
{
    /// <summary>
    /// Shewchuk's static filter constant for a 2D orientation determinant,
    /// <c>(3 + 16ε)ε</c> at <c>ε = 2^-53</c>. A determinant within this
    /// multiple of the sum of its terms' magnitudes cannot be signed reliably.
    /// </summary>
    private const double OrientationErrorBound = 3.3306690738754706e-16;

    /// <summary>
    /// Sign of the cross product of <c>b - a</c> and <c>c - a</c>: 1 when
    /// <paramref name="c"/> lies left of the directed line, -1 right, and 0
    /// when the three are collinear (or too close to call).
    /// </summary>
    public static int Orientation(PlanarPoint a, PlanarPoint b, PlanarPoint c)
    {
        var left = (b.X - a.X) * (c.Y - a.Y);
        var right = (b.Y - a.Y) * (c.X - a.X);
        var determinant = left - right;
        var bound = OrientationErrorBound * (Math.Abs(left) + Math.Abs(right));
        return determinant > bound ? 1 : determinant < -bound ? -1 : 0;
    }

    /// <summary>True when <paramref name="p"/> lies on the closed segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    public static bool OnSegment(PlanarPoint p, PlanarPoint a, PlanarPoint b) =>
        p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X)
        && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y)
        && Orientation(a, b, p) == 0;

    /// <inheritdoc cref="OnSegment(PlanarPoint, PlanarPoint, PlanarPoint)"/>
    public static bool OnSegment(PlanarPoint p, PlanarSegment segment) => OnSegment(p, segment.A, segment.B);

    /// <summary>True when <paramref name="p"/> lies on any of <paramref name="segments"/>.</summary>
    public static bool OnAnySegment(PlanarPoint p, List<PlanarSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (OnSegment(p, segment))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when the two segments share a one-dimensional stretch, which is
    /// the overlap kind real rejects as invalid in a line, between line
    /// members and between polygon rings.
    /// </summary>
    public static bool OverlapsIn1D(PlanarSegment s, PlanarSegment t)
    {
        if (s.IsDegenerate || t.IsDegenerate)
            return false;
        if (Orientation(s.A, s.B, t.A) != 0 || Orientation(s.A, s.B, t.B) != 0)
            return false;
        // Project onto whichever axis carries more of s's extent, so a nearly
        // vertical segment doesn't collapse to a zero-width interval.
        var useX = Math.Abs(s.B.X - s.A.X) >= Math.Abs(s.B.Y - s.A.Y);
        var (s0, s1) = useX ? (s.A.X, s.B.X) : (s.A.Y, s.B.Y);
        var (t0, t1) = useX ? (t.A.X, t.B.X) : (t.A.Y, t.B.Y);
        var low = Math.Max(Math.Min(s0, s1), Math.Min(t0, t1));
        var high = Math.Min(Math.Max(s0, s1), Math.Max(t0, t1));
        return high > low;
    }

    /// <summary>
    /// True when the segments cross transversally — each straddles the other's
    /// line, so they meet at a point interior to both.
    /// </summary>
    public static bool ProperlyCross(PlanarSegment s, PlanarSegment t)
    {
        if (s.IsDegenerate || t.IsDegenerate)
            return false;
        var o1 = Orientation(s.A, s.B, t.A);
        var o2 = Orientation(s.A, s.B, t.B);
        var o3 = Orientation(t.A, t.B, s.A);
        var o4 = Orientation(t.A, t.B, s.B);
        return o1 != 0 && o2 != 0 && o1 != o2 && o3 != 0 && o4 != 0 && o3 != o4;
    }

    /// <summary>True when the two segments meet anywhere.</summary>
    public static bool Intersects(PlanarSegment s, PlanarSegment t) =>
        OnSegment(t.A, s) || OnSegment(t.B, s) || OnSegment(s.A, t) || OnSegment(s.B, t) || ProperlyCross(s, t);

    /// <summary>Straight-line distance between two points.</summary>
    public static double Distance(PlanarPoint p, PlanarPoint q) => Math.Sqrt(SquaredDistance(p, q));

    private static double SquaredDistance(PlanarPoint p, PlanarPoint q)
    {
        var dx = p.X - q.X;
        var dy = p.Y - q.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// Distance from a point to a closed segment: the perpendicular foot where
    /// it lands between the endpoints, and the nearer endpoint otherwise.
    /// </summary>
    public static double Distance(PlanarPoint p, PlanarSegment segment)
    {
        var dx = segment.B.X - segment.A.X;
        var dy = segment.B.Y - segment.A.Y;
        var squaredLength = (dx * dx) + (dy * dy);
        if (squaredLength == 0)
            return Distance(p, segment.A);
        var along = Math.Clamp((((p.X - segment.A.X) * dx) + ((p.Y - segment.A.Y) * dy)) / squaredLength, 0, 1);
        return Distance(p, new PlanarPoint(segment.A.X + (along * dx), segment.A.Y + (along * dy)));
    }

    /// <summary>
    /// Distance between two closed segments: zero where they meet, and
    /// otherwise the nearest of the four endpoint-to-segment approaches, one of
    /// which always carries the minimum for straight edges.
    /// </summary>
    public static double Distance(PlanarSegment s, PlanarSegment t) =>
        Intersects(s, t)
            ? 0
            : Math.Min(
                Math.Min(Distance(s.A, t), Distance(s.B, t)),
                Math.Min(Distance(t.A, s), Distance(t.B, s)));

    /// <summary>
    /// Lower bound on the distance between two segments, from their bounding
    /// boxes alone — the pruning test that keeps a many-vertex pair from
    /// measuring every combination.
    /// </summary>
    public static double ExtentDistance(PlanarSegment s, PlanarSegment t)
    {
        var dx = Math.Max(0, Math.Max(s.MinX - t.MaxX, t.MinX - s.MaxX));
        var dy = Math.Max(0, Math.Max(s.MinY - t.MaxY, t.MinY - s.MaxY));
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Appends every point at which the two segments meet that a planar
    /// arrangement has to node at: each endpoint lying on the other segment,
    /// and the computed crossing of a transversal pair. An endpoint is
    /// preferred over a computed coordinate wherever both describe the same
    /// meeting, which keeps noded vertices exactly on the input vertices.
    /// </summary>
    public static void CollectIntersections(PlanarSegment s, PlanarSegment t, HashSet<PlanarPoint> into)
    {
        var touched = AddIfOn(t.A, s, into);
        touched |= AddIfOn(t.B, s, into);
        touched |= AddIfOn(s.A, t, into);
        touched |= AddIfOn(s.B, t, into);
        if (!touched && ProperlyCross(s, t) && CrossingPoint(s, t) is { } crossing)
            _ = into.Add(crossing);
    }

    private static bool AddIfOn(PlanarPoint point, PlanarSegment host, HashSet<PlanarPoint> into)
    {
        if (!OnSegment(point, host))
            return false;
        _ = into.Add(point);
        return true;
    }

    /// <summary>The intersection of two transversal segments, or null when the denominator vanishes.</summary>
    private static PlanarPoint? CrossingPoint(PlanarSegment s, PlanarSegment t)
    {
        var sx = s.B.X - s.A.X;
        var sy = s.B.Y - s.A.Y;
        var tx = t.B.X - t.A.X;
        var ty = t.B.Y - t.A.Y;
        var denominator = (sx * ty) - (sy * tx);
        if (denominator == 0 || double.IsNaN(denominator))
            return null;
        var parameter = (((t.A.X - s.A.X) * ty) - ((t.A.Y - s.A.Y) * tx)) / denominator;
        return new(s.A.X + (parameter * sx), s.A.Y + (parameter * sy));
    }

    /// <summary>
    /// Even-odd point-in-area over a flat list of closed-ring segments. Every
    /// caller has already established the point is off the rings, and every
    /// caller's rings come from an instance real considers valid, which is
    /// what makes the even-odd rule agree with shell-minus-holes.
    /// </summary>
    public static bool IsInsideRings(PlanarPoint p, List<PlanarSegment> ringSegments)
    {
        var inside = false;
        foreach (var segment in ringSegments)
        {
            var a = segment.A;
            var b = segment.B;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X))
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Index pairs whose segments' x extents overlap — every pair that can
    /// possibly meet. A sweep in increasing minimum x with an active list keeps
    /// the usual case near-linear where the naive double loop is quadratic,
    /// which matters because a stored border polygon carries thousands of
    /// segments and validity is checked before most instance methods run.
    /// </summary>
    public static IEnumerable<(int First, int Second)> CandidatePairs(List<PlanarSegment> segments)
    {
        var order = new int[segments.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        var byMinX = segments;
        Array.Sort(order, (x, y) => byMinX[x].MinX.CompareTo(byMinX[y].MinX));

        var active = new List<int>();
        foreach (var index in order)
        {
            var minX = segments[index].MinX;
            _ = active.RemoveAll(other => segments[other].MaxX < minX);
            foreach (var other in active)
                yield return (Math.Min(index, other), Math.Max(index, other));
            active.Add(index);
        }
    }

    /// <summary>Shoelace sum over a closed ring; the sign carries the ring's orientation.</summary>
    public static double SignedRingArea(PlanarPoint[] ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Length - 1; i++)
            sum += (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
        return sum / 2.0;
    }
}
