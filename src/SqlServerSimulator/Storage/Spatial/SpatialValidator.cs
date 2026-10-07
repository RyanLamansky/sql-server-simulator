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
/// <item>A <b>LineString</b> is invalid when it ends on a repeat it reaches
/// from above in sweep order, or when any two of its segments share a
/// one-dimensional stretch on the precision grid. Crossing itself at a point
/// is fine — that costs simplicity, not validity — and a repeated vertex
/// anywhere but the end is fine too.</item>
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
    /// <summary>
    /// Judges the instance as real does: on the precision grid the
    /// constructive operations use, spanning the instance's own extent. A
    /// line that retraces itself exactly in its written coordinates can miss
    /// itself by a grid step once snapped — the truncating rounding lifts a
    /// vertex below the centre of an axis by one step — and real then finds
    /// no overlap: <c>LINESTRING(12 2, 6 16, 9 9)</c> is valid while
    /// <c>LINESTRING(0 0, 4 4, 2 2)</c> is not (probed 2026-09-28 against SQL
    /// Server 2025).
    /// </summary>
    public static bool IsValid(SpatialShape shape) => IsValidSnapped(Snapped(shape));

    /// <summary>The shape on the precision grid <see cref="IsValid"/> judges it on.</summary>
    public static SpatialShape Snapped(SpatialShape shape) => Snap(shape, SpatialPrecisionGrid.Over(shape));

    private static SpatialShape Snap(SpatialShape shape, SpatialPrecisionGrid grid)
    {
        var figures = new SpatialCoordinate[shape.Figures.Length][];
        for (var i = 0; i < figures.Length; i++)
        {
            var figure = shape.Figures[i];
            var snapped = new SpatialCoordinate[figure.Length];
            for (var j = 0; j < figure.Length; j++)
            {
                var point = grid.Snap(figure[j]);
                snapped[j] = new SpatialCoordinate(point.X, point.Y);
            }
            figures[i] = snapped;
        }
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < children.Length; i++)
            children[i] = Snap(shape.Children[i], grid);
        return new SpatialShape(shape.Type, figures, children);
    }

    /// <summary>
    /// Whether a figure ends on a repeat real refuses. A repeated vertex is
    /// harmless anywhere but the end, and there only when the figure arrives
    /// at it from above in sweep order: <c>LINESTRING(15 7.5, 2 5, 2 5)</c> is
    /// valid while <c>LINESTRING(2 5, 15 7, 15 7)</c> is not, and a ring
    /// written from its top-right corner with its closing vertex doubled is
    /// invalid where one written from its lowest corner is not (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </summary>
    public static bool EndsOnRefusedRepeat(SpatialCoordinate[] figure)
    {
        if (figure.Length < 2 || PlanarPoint.From(figure[^1]) != PlanarPoint.From(figure[^2]))
            return false;
        var last = figure[^1];
        for (var i = figure.Length - 3; i >= 0; i--)
        {
            var point = figure[i];
            if (point.X != last.X || point.Y != last.Y)
                return last.Y > point.Y || (last.Y == point.Y && last.X > point.X);
        }
        return true;
    }

    private static bool IsValidSnapped(SpatialShape shape)
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
            if (!IsValidSnapped(child))
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
            if (EndsOnRefusedRepeat(figure))
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
            if (EndsOnRefusedRepeat(figure))
                return false;
            var collapsed = Collapse(figure);
            if (collapsed.Length < 4)
                return false;
            // The exterior ring may revisit its own vertices, as long as what
            // it closes off there are holes (see Lobes).
            var lobes = rings.Count == 0 ? Lobes(collapsed) : null;
            foreach (var ring in lobes ?? [collapsed])
            {
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
        }
        return rings.Count == 0
            || (RingsStayApart(ringSegments) && HolesSitInsideShell(ringSegments) && InteriorStaysConnected(ringSegments));
    }

    /// <summary>
    /// The exterior ring split at the vertices it revisits, its main lobe first
    /// and the lobes it closes off after it — null for a ring that revisits
    /// none, and the ring whole when a lobe turns the main one's way, which the
    /// simple-ring rule then refuses.
    /// </summary>
    /// <remarks>
    /// Real reads a lobe wound against the ring's main one as a hole that
    /// happens to meet its shell, which the ordinary ring rules then judge, so
    /// <c>POLYGON((5 0, 10 0, 10 10, 0 10, 0 0, 5 0, 3 3, 7 3, 5 0))</c> is
    /// valid while the same ring with its inner lobe wound the same way is not
    /// (probed 2026-10-06 against SQL Server 2025) — the split the round-earth
    /// validator makes, where a ring's winding names its interior anyway.
    /// </remarks>
    private static List<PlanarPoint[]>? Lobes(PlanarPoint[] ring)
    {
        var lobes = new List<PlanarPoint[]>();
        var path = new List<PlanarPoint>(ring.Length);
        var at = new Dictionary<PlanarPoint, int>();
        // The closing repeat is the walk's return to its start, not a revisit.
        for (var i = 0; i < ring.Length - 1; i++)
        {
            var point = ring[i];
            if (at.TryGetValue(point, out var start))
            {
                var lobe = new PlanarPoint[path.Count - start + 1];
                path.CopyTo(start, lobe, 0, path.Count - start);
                lobe[^1] = point;
                lobes.Add(lobe);
                for (var drop = start + 1; drop < path.Count; drop++)
                    _ = at.Remove(path[drop]);
                path.RemoveRange(start + 1, path.Count - start - 1);
                continue;
            }
            at[point] = path.Count;
            path.Add(point);
        }
        if (lobes.Count == 0)
            return null;
        var last = new PlanarPoint[path.Count + 1];
        path.CopyTo(last);
        last[^1] = path[0];
        lobes.Add(last);

        // The main lobe encloses the most area; every other one has to turn
        // the other way to be a hole rather than a second lobe of the shell.
        var main = lobes[0];
        foreach (var lobe in lobes)
        {
            if (Math.Abs(SpatialTopology.SignedRingArea(lobe)) > Math.Abs(SpatialTopology.SignedRingArea(main)))
                main = lobe;
        }
        var winding = Math.Sign(SpatialTopology.SignedRingArea(main));
        var ordered = new List<PlanarPoint[]>(lobes.Count) { main };
        foreach (var lobe in lobes)
        {
            if (lobe == main)
                continue;
            if (Math.Sign(SpatialTopology.SignedRingArea(lobe)) == winding)
                return [ring];
            ordered.Add(lobe);
        }
        return ordered;
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
