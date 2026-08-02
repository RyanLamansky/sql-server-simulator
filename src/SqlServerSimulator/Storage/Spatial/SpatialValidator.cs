namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>geometry</c>'s <c>STIsValid()</c> — the OGC validity rules real applies,
/// and the gate behind Msg 24144.
/// </summary>
/// <remarks>
/// <para>Real stores a malformed-but-parseable instance happily (the exterior
/// ring closure and point-count checks fire at parse; nothing else does) and
/// then refuses to <i>operate</i> on it. The rules below are probe-derived
/// against SQL Server 2025:</para>
/// <list type="bullet">
/// <item>A <b>Point</b> or <b>MultiPoint</b> is always valid, repeated
/// coordinates included.</item>
/// <item>A <b>LineString</b> is invalid when its last two vertices coincide,
/// or when any two of its segments share a one-dimensional stretch. Crossing
/// itself at a point is fine — that costs simplicity, not validity — and a
/// repeated vertex anywhere but the end is fine too.</item>
/// <item>A <b>MultiLineString</b> adds: no two members may share a
/// one-dimensional stretch. Meeting at a point is fine.</item>
/// <item>A <b>Polygon</b>'s rings must each enclose area and be simple, must
/// not cross or share a one-dimensional stretch with each other, must hold
/// every interior ring inside the exterior one without nesting interior rings,
/// and must leave the interior <b>connected</b> — a ring touching another at
/// two points pinches the interior in two and is invalid, while a single touch
/// is fine.</item>
/// <item>A <b>MultiPolygon</b>'s members may touch at points but may not
/// overlap, share a one-dimensional stretch, or contain one another.</item>
/// <item>A <b>GeometryCollection</b> is valid exactly when every member is;
/// members may overlap each other freely.</item>
/// </list>
/// </remarks>
internal static class SpatialValidator
{
    public static bool IsValid(SpatialShape shape)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.LineString:
                if (!LineIsValid(shape.Figures))
                    return false;
                break;
            case SpatialShapeType.MultiLineString:
                if (!MembersStayApart(shape, membersEnclose: false))
                    return false;
                break;
            case SpatialShapeType.Polygon:
                if (!PolygonIsValid(shape.Figures))
                    return false;
                break;
            case SpatialShapeType.MultiPolygon:
                if (!MembersStayApart(shape, membersEnclose: true))
                    return false;
                break;
            default:
                break;
        }
        foreach (var child in shape.Children)
        {
            if (!IsValid(child))
                return false;
        }
        return true;
    }

    /// <summary>Every non-degenerate edge of a member's own figures — line vertices or ring vertices alike.</summary>
    private static List<PlanarSegment> SegmentsOf(SpatialShape shape)
    {
        var segments = new List<PlanarSegment>();
        foreach (var figure in shape.Figures)
            AddRun(figure, segments);
        return segments;
    }

    private static void AddRun(SpatialCoordinate[] figure, List<PlanarSegment> into)
    {
        for (var i = 1; i < figure.Length; i++)
        {
            var segment = new PlanarSegment(PlanarPoint.From(figure[i - 1]), PlanarPoint.From(figure[i]));
            if (!segment.IsDegenerate)
                into.Add(segment);
        }
    }

    private static bool LineIsValid(SpatialCoordinate[][] figures)
    {
        foreach (var figure in figures)
        {
            if (figure.Length == 0)
                continue;
            // A figure that stops on the vertex it already sits on is invalid,
            // where the same repetition anywhere earlier in the run is not.
            if (PlanarPoint.From(figure[^1]) == PlanarPoint.From(figure[^2]))
                return false;
        }
        var segments = new List<PlanarSegment>();
        foreach (var figure in figures)
            AddRun(figure, segments);
        foreach (var (first, second) in SpatialTopology.CandidatePairs(segments))
        {
            if (SpatialTopology.OverlapsIn1D(segments[first], segments[second]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Cross-member rules of a Multi* instance: no member may share a
    /// one-dimensional stretch with another, and — where
    /// <paramref name="membersEnclose"/> says the members bound area — no
    /// member may cross or swallow another either.
    /// </summary>
    private static bool MembersStayApart(SpatialShape shape, bool membersEnclose)
    {
        var members = new List<PlanarSegment>[shape.Children.Length];
        for (var i = 0; i < members.Length; i++)
            members[i] = SegmentsOf(shape.Children[i]);

        var combined = new List<PlanarSegment>();
        var owner = new List<int>();
        for (var i = 0; i < members.Length; i++)
        {
            foreach (var segment in members[i])
            {
                combined.Add(segment);
                owner.Add(i);
            }
        }
        foreach (var (first, second) in SpatialTopology.CandidatePairs(combined))
        {
            if (owner[first] == owner[second])
                continue;
            if (SpatialTopology.OverlapsIn1D(combined[first], combined[second]))
                return false;
            if (membersEnclose && SpatialTopology.ProperlyCross(combined[first], combined[second]))
                return false;
        }
        if (!membersEnclose)
            return true;
        for (var i = 0; i < members.Length; i++)
        {
            for (var j = i + 1; j < members.Length; j++)
            {
                if (AnyVertexStrictlyInside(members[i], members[j]) || AnyVertexStrictlyInside(members[j], members[i]))
                    return false;
            }
        }
        return true;
    }

    /// <summary>True when a vertex of <paramref name="probe"/> falls in the open area bounded by <paramref name="rings"/>.</summary>
    private static bool AnyVertexStrictlyInside(List<PlanarSegment> probe, List<PlanarSegment> rings)
    {
        foreach (var segment in probe)
        {
            if (!SpatialTopology.OnAnySegment(segment.A, rings) && SpatialTopology.IsInsideRings(segment.A, rings))
                return true;
        }
        return false;
    }

    private static bool PolygonIsValid(SpatialCoordinate[][] figures)
    {
        var rings = new List<PlanarPoint[]>();
        var ringSegments = new List<List<PlanarSegment>>();
        foreach (var figure in figures)
        {
            if (figure.Length == 0)
                continue;
            var ring = Collapse(figure);
            // Fewer than four surviving vertices, or a shoelace sum of zero,
            // means the ring bounds nothing.
            if (ring.Length < 4 || SpatialTopology.SignedRingArea(ring) == 0)
                return false;
            var segments = new List<PlanarSegment>();
            for (var i = 1; i < ring.Length; i++)
                segments.Add(new(ring[i - 1], ring[i]));
            if (!RingIsSimple(segments))
                return false;
            rings.Add(ring);
            ringSegments.Add(segments);
        }
        return rings.Count == 0
            || (RingsStayApart(ringSegments) && HolesSitInsideShell(ringSegments) && InteriorStaysConnected(ringSegments));
    }

    /// <summary>Drops consecutive repeats, which real tolerates in a ring while treating the collapsed run as the real geometry.</summary>
    private static PlanarPoint[] Collapse(SpatialCoordinate[] figure)
    {
        var kept = new List<PlanarPoint>(figure.Length);
        foreach (var coordinate in figure)
        {
            var point = PlanarPoint.From(coordinate);
            if (kept.Count == 0 || kept[^1] != point)
                kept.Add(point);
        }
        return [.. kept];
    }

    /// <summary>
    /// A ring is simple when consecutive segments meet only at their shared
    /// vertex and no other pair meets at all — a ring that crosses or merely
    /// touches itself bounds no well-defined interior.
    /// </summary>
    private static bool RingIsSimple(List<PlanarSegment> segments)
    {
        var last = segments.Count - 1;
        foreach (var (first, second) in SpatialTopology.CandidatePairs(segments))
        {
            var adjacent = second == first + 1 || (first == 0 && second == last);
            if (adjacent
                ? SpatialTopology.OverlapsIn1D(segments[first], segments[second])
                : SpatialTopology.Intersects(segments[first], segments[second]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Rings of one polygon may meet at points but never cross or run alongside each other.</summary>
    private static bool RingsStayApart(List<List<PlanarSegment>> rings)
    {
        for (var i = 0; i < rings.Count; i++)
        {
            for (var j = i + 1; j < rings.Count; j++)
            {
                foreach (var one in rings[i])
                {
                    foreach (var other in rings[j])
                    {
                        if (SpatialTopology.OverlapsIn1D(one, other) || SpatialTopology.ProperlyCross(one, other))
                            return false;
                    }
                }
            }
        }
        return true;
    }

    /// <summary>Every interior ring must lie within the exterior one and outside every sibling.</summary>
    private static bool HolesSitInsideShell(List<List<PlanarSegment>> rings)
    {
        var shell = rings[0];
        for (var i = 1; i < rings.Count; i++)
        {
            foreach (var segment in rings[i])
            {
                if (!SpatialTopology.OnAnySegment(segment.A, shell) && !SpatialTopology.IsInsideRings(segment.A, shell))
                    return false;
            }
            for (var j = i + 1; j < rings.Count; j++)
            {
                if (AnyVertexStrictlyInside(rings[i], rings[j]) || AnyVertexStrictlyInside(rings[j], rings[i]))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The polygon's interior must stay in one piece. Treating the rings as
    /// nodes and each distinct point where two of them meet as an edge, a cycle
    /// in that graph is exactly a chain of touches that cuts the interior — a
    /// hole meeting the shell twice, or a run of holes closing back on the
    /// shell.
    /// </summary>
    private static bool InteriorStaysConnected(List<List<PlanarSegment>> rings)
    {
        var component = new int[rings.Count];
        for (var i = 0; i < component.Length; i++)
            component[i] = i;

        int Find(int node)
        {
            while (component[node] != node)
                node = component[node] = component[component[node]];
            return node;
        }

        var meetings = new HashSet<PlanarPoint>();
        for (var i = 0; i < rings.Count; i++)
        {
            for (var j = i + 1; j < rings.Count; j++)
            {
                meetings.Clear();
                foreach (var one in rings[i])
                {
                    foreach (var other in rings[j])
                        SpatialTopology.CollectIntersections(one, other, meetings);
                }
                for (var touch = 0; touch < meetings.Count; touch++)
                {
                    var left = Find(i);
                    var right = Find(j);
                    if (left == right)
                        return false;
                    component[left] = right;
                }
            }
        }
        return true;
    }
}
