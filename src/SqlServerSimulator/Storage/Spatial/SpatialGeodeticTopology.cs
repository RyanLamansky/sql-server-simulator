namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// The round-earth counterparts of <see cref="SpatialTopology"/>'s straight-edge
/// primitives: where two great elliptic arcs meet, whether they run alongside
/// each other, and which side of one a point sits on.
/// </summary>
/// <remarks>
/// <para>Every question a planar engine answers with a cross product becomes a
/// question about two central planes here. Two arcs lie on planes through the
/// ellipsoid's centre, so unless the planes coincide they meet in a line whose
/// two surface points are the only places the arcs can touch — that single fact
/// carries the crossing test, the intersection collection and the properly-cross
/// test alike. Arcs that <i>do</i> share a plane are the one-dimensional overlap
/// case and are handled by comparing spans instead, because their cross product
/// is pure roundoff.</para>
/// <para>Sidedness is answered by stepping off the arc rather than by a
/// determinant: the point a short way to the left of an edge is computed on the
/// surface and then classified by the same winding test everything else uses,
/// which keeps one definition of "inside" for the whole engine.</para>
/// </remarks>
internal static class SpatialGeodeticTopology
{
    /// <summary>
    /// Metres within which two computed surface points name the same place.
    /// Arc intersections land within a nanometre or so of each other in double
    /// precision, and the shapes a predicate compares are separated by far
    /// more, so a tenth of a millimetre separates computation noise from any
    /// distinction a caller drew.
    /// </summary>
    public const double NodeTolerance = 1e-4;

    /// <summary>
    /// Relative threshold below which two plane normals count as parallel — the
    /// same one <see cref="SpatialGreatElliptic.Cross"/> uses, since a smaller
    /// one would name an arbitrary surface point from roundoff.
    /// </summary>
    private const double ParallelTolerance = 1e-12;

    /// <summary>How far off an edge a sidedness probe steps, as a fraction of the edge's own chord.</summary>
    private const double OffsetFraction = 1e-6;

    /// <summary>Floor and ceiling on that step, in metres: above the on-arc tolerance, below any feature separation.</summary>
    private const double MinimumOffset = 1e-3;
    private const double MaximumOffset = 1.0;

    public static bool Same(SpatialVector a, SpatialVector b) => (a - b).SquaredLength <= NodeTolerance * NodeTolerance;

    /// <summary>
    /// True when two edges' bounding spheres touch, so they could meet at all.
    /// A few flops here rule out the pair before either plane cross product,
    /// which is what keeps the pairwise scans over a many-vertex border cheap.
    /// </summary>
    public static bool Near(in RoundEarthEdge first, in RoundEarthEdge second)
    {
        var reach = Reach(first) + Reach(second) + NodeTolerance;
        return (first.Centre - second.Centre).SquaredLength <= reach * reach;
    }

    /// <summary>
    /// Radius of a sphere about an edge's chord midpoint that holds the whole
    /// arc. The arc rises at most <c>R - √(R² - (c/2)²)</c> above its chord, and
    /// taking the ellipsoid's smallest radius for <c>R</c> keeps that on the
    /// safe side.
    /// </summary>
    public static double Reach(in RoundEarthEdge edge)
    {
        var minor = SpatialEllipsoid.SemiMinor;
        return edge.Radius + minor - Math.Sqrt(Math.Max(0, (minor * minor) - (edge.Radius * edge.Radius)));
    }

    /// <summary>
    /// Index pairs whose bounding spheres touch — every pair that can possibly
    /// meet. A sweep in increasing minimum x with an active list keeps the usual
    /// case near-linear where the naive double loop is quadratic, which matters
    /// because a stored border polygon carries thousands of edges and validity
    /// is checked before most instance methods run.
    /// </summary>
    public static IEnumerable<(int First, int Second)> CandidatePairs(List<RoundEarthEdge> edges)
    {
        var low = new double[edges.Count];
        var high = new double[edges.Count];
        var order = new int[edges.Count];
        for (var i = 0; i < edges.Count; i++)
        {
            var reach = Reach(edges[i]) + (NodeTolerance / 2);
            low[i] = edges[i].Centre.X - reach;
            high[i] = edges[i].Centre.X + reach;
            order[i] = i;
        }
        var byLow = low;
        Array.Sort(order, (x, y) => byLow[x].CompareTo(byLow[y]));

        var active = new List<int>();
        foreach (var index in order)
        {
            _ = active.RemoveAll(other => high[other] < low[index]);
            foreach (var other in active)
            {
                if (Near(edges[index], edges[other]))
                    yield return (Math.Min(index, other), Math.Max(index, other));
            }
            active.Add(index);
        }
    }

    /// <summary>True when the two arcs are cut from the same central plane, so they trace the same great ellipse.</summary>
    public static bool Coplanar(in GreatEllipticArc first, in GreatEllipticArc second)
    {
        var firstNormal = first.PlaneNormal;
        var secondNormal = second.PlaneNormal;
        var cross = firstNormal.Cross(secondNormal).Length;
        return !double.IsNaN(cross) && cross <= ParallelTolerance * firstNormal.Length * secondNormal.Length;
    }

    /// <summary>
    /// Every surface point the two edges share, added to <paramref name="into"/>.
    /// Coplanar edges contribute whichever of their four endpoints lies on the
    /// other, which is the whole shared boundary when they merely touch and the
    /// two ends of the shared stretch when they overlap.
    /// </summary>
    public static void CollectIntersections(in RoundEarthEdge first, in RoundEarthEdge second, GeodeticNodes into)
    {
        if (Coplanar(first.Arc, second.Arc))
        {
            foreach (var candidate in SharedEndpoints(first, second))
                _ = into.Add(candidate);
            return;
        }
        var meeting = SpatialEllipsoid.OntoSurface(first.Arc.PlaneNormal.Cross(second.Arc.PlaneNormal));
        if (Meets(first, second, meeting))
            _ = into.Add(meeting);
        var opposite = meeting * -1;
        if (Meets(first, second, opposite))
            _ = into.Add(opposite);
    }

    /// <summary>True when the two edges meet anywhere.</summary>
    public static bool Intersects(in RoundEarthEdge first, in RoundEarthEdge second)
    {
        if (Coplanar(first.Arc, second.Arc))
            return SharedEndpoints(first, second).Count > 0;
        var meeting = SpatialEllipsoid.OntoSurface(first.Arc.PlaneNormal.Cross(second.Arc.PlaneNormal));
        return Meets(first, second, meeting) || Meets(first, second, meeting * -1);
    }

    /// <summary>
    /// True when the two edges share more than a single point — they must lie on
    /// one great ellipse and their spans must genuinely overlap, which shows up
    /// as two distinct shared endpoints.
    /// </summary>
    public static bool OverlapsIn1D(in RoundEarthEdge first, in RoundEarthEdge second)
    {
        if (!Coplanar(first.Arc, second.Arc))
            return false;
        var shared = SharedEndpoints(first, second);
        for (var i = 0; i < shared.Count; i++)
        {
            for (var j = i + 1; j < shared.Count; j++)
            {
                if (!Same(shared[i], shared[j]))
                    return true;
            }
        }
        return false;
    }

    /// <summary>True when the two edges cross at a point interior to both, rather than meeting at an end.</summary>
    public static bool ProperlyCross(in RoundEarthEdge first, in RoundEarthEdge second)
    {
        if (Coplanar(first.Arc, second.Arc))
            return false;
        var meeting = SpatialEllipsoid.OntoSurface(first.Arc.PlaneNormal.Cross(second.Arc.PlaneNormal));
        return CrossesAt(first, second, meeting) || CrossesAt(first, second, meeting * -1);
    }

    /// <summary>True when <paramref name="point"/> lies on any of the edges.</summary>
    public static bool OnAnyEdge(SpatialVector point, List<RoundEarthEdge> edges)
    {
        for (var i = 0; i < edges.Count; i++)
        {
            if (Near(point, edges[i]) && edges[i].Arc.Contains(point))
                return true;
        }
        return false;
    }

    /// <summary>True when a point is inside an edge's bounding sphere, so it could lie on the edge at all.</summary>
    public static bool Near(SpatialVector point, in RoundEarthEdge edge)
    {
        var reach = Reach(edge) + NodeTolerance;
        return (point - edge.Centre).SquaredLength <= reach * reach;
    }

    /// <summary>
    /// The surface point a short way to the named side of an edge, taken at
    /// <paramref name="fraction"/> along it. "Left" is the side a geography ring
    /// puts its interior on, so this is what turns ring orientation into a
    /// region test.
    /// </summary>
    public static SpatialVector Offset(in RoundEarthEdge edge, double fraction, bool leftSide)
    {
        var arc = edge.Arc;
        var point = arc.At(fraction);
        var tangent = arc.TangentAt(arc.Start + (fraction * arc.Sweep)).Normalized;
        // The radial direction stands in for the surface normal: the two differ
        // by the flattening, which tilts the step without moving it to the other
        // side of the edge.
        var up = point.Normalized;
        var sideways = up.Cross(tangent).Normalized;
        var step = Math.Clamp((edge.To - edge.From).Length * OffsetFraction, MinimumOffset, MaximumOffset);
        return SpatialEllipsoid.OntoSurface(point + (sideways * (leftSide ? step : -step)));
    }

    /// <summary>
    /// Whether <paramref name="point"/> sits on the same side of a ring set as
    /// <paramref name="reference"/>, or <c>null</c> when the path between them
    /// runs into a degeneracy that makes the count unreliable.
    /// </summary>
    /// <remarks>
    /// <para>Side is the <b>parity of boundary crossings</b> along the great
    /// elliptic arc joining the two points. A closed ring set alternates
    /// interior and exterior across every one of its edges, so an even count
    /// means the two points share a face whatever the ring set's shape — and,
    /// unlike an azimuth-sum winding, the answer doesn't change when the pair
    /// happens to straddle an antipode. That is what makes it right for a
    /// geography polygon, whose region may be the unbounded side.</para>
    /// <para>The count is unreliable when the path grazes a vertex or runs
    /// along an edge: a graze adds a crossing that isn't one. Both show up as
    /// an intersection landing on a ring vertex or as a coplanar edge, and the
    /// caller answers by re-asking from a different reference point.</para>
    /// </remarks>
    public static bool? SameSide(
        SpatialVector reference, SpatialVector point, List<RoundEarthEdge> edges, List<(int First, int Count)> rings)
    {
        if (Same(reference, point))
            return true;
        var path = new RoundEarthEdge(reference, point);
        var crossings = new GeodeticNodes();
        foreach (var (first, count) in rings)
        {
            for (var i = 0; i < count; i++)
            {
                var edge = edges[first + i];
                if (Coplanar(path.Arc, edge.Arc))
                    return null;
                CollectIntersections(path, edge, crossings);
            }
        }
        foreach (var crossing in crossings.Points)
        {
            foreach (var (first, count) in rings)
            {
                for (var i = 0; i < count; i++)
                {
                    if (Same(crossing, edges[first + i].From) || Same(crossing, edges[first + i].To))
                        return null;
                }
            }
        }
        return crossings.Points.Count % 2 == 0;
    }

    /// <summary>Endpoints of either edge that lie on the other — the shared boundary of two coplanar arcs.</summary>
    private static List<SpatialVector> SharedEndpoints(in RoundEarthEdge first, in RoundEarthEdge second)
    {
        var shared = new List<SpatialVector>(4);
        if (second.Arc.Contains(first.From))
            shared.Add(first.From);
        if (second.Arc.Contains(first.To))
            shared.Add(first.To);
        if (first.Arc.Contains(second.From))
            shared.Add(second.From);
        if (first.Arc.Contains(second.To))
            shared.Add(second.To);
        return shared;
    }

    private static bool Meets(in RoundEarthEdge first, in RoundEarthEdge second, SpatialVector point) =>
        first.Arc.Contains(point) && second.Arc.Contains(point);

    private static bool CrossesAt(in RoundEarthEdge first, in RoundEarthEdge second, SpatialVector point) =>
        Meets(first, second, point) && Interior(first, point) && Interior(second, point);

    private static bool Interior(in RoundEarthEdge edge, SpatialVector point) =>
        !Same(point, edge.From) && !Same(point, edge.To);
}

/// <summary>
/// The node set an arrangement is built on: distinct surface points, with
/// anything within <see cref="SpatialGeodeticTopology.NodeTolerance"/> of an
/// existing entry folding onto it.
/// </summary>
/// <remarks>
/// Snapping is what a spherical arrangement needs and a planar one doesn't. Two
/// arcs meeting at a vertex both operands wrote produce a point computed from
/// two plane normals, which lands a nanometre or so off the vertex itself — an
/// exact hash set would carry both and split the arrangement. The scan is
/// linear because a predicate's arrangement holds tens of nodes, not thousands.
/// </remarks>
internal sealed class GeodeticNodes
{
    public readonly List<SpatialVector> Points = [];

    /// <summary>
    /// Cell width, metres — comfortably wider than the tolerance, so two points
    /// that should fold together are never more than one cell apart on any axis.
    /// </summary>
    private const double Cell = SpatialGeodeticTopology.NodeTolerance * 8;

    private readonly Dictionary<(long, long, long), List<int>> buckets = [];

    /// <summary>Adds a point unless one already stands for it, and answers the index of whichever now does.</summary>
    public int Add(SpatialVector point)
    {
        var (x, y, z) = KeyOf(point);
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!this.buckets.TryGetValue((x + dx, y + dy, z + dz), out var bucket))
                        continue;
                    foreach (var index in bucket)
                    {
                        if (SpatialGeodeticTopology.Same(this.Points[index], point))
                            return index;
                    }
                }
            }
        }
        this.Points.Add(point);
        var added = this.Points.Count - 1;
        if (!this.buckets.TryGetValue((x, y, z), out var own))
            this.buckets[(x, y, z)] = own = [];
        own.Add(added);
        return added;
    }

    private static (long, long, long) KeyOf(SpatialVector point) =>
        ((long)Math.Floor(point.X / Cell), (long)Math.Floor(point.Y / Cell), (long)Math.Floor(point.Z / Cell));
}
