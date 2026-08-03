namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>geography</c>'s <c>STIsValid()</c> — the round-earth validity rules real
/// applies, and the gate behind Msg 24144 on a <c>geography</c> receiver.
/// </summary>
/// <remarks>
/// <para>The rule list is <see cref="SpatialValidator"/>'s with three changes,
/// all probe-derived against SQL Server 2025:</para>
/// <list type="bullet">
/// <item><b>Ring orientation is load-bearing.</b> A geography ring puts its
/// interior on the left of the direction it is written, so a polygon is valid
/// only when every one of its rings agrees on which region that names: a hole
/// wound the same way as its shell is invalid, where planar validity doesn't
/// look at orientation at all. A lone ring wound "backwards" is valid and names
/// the complementary region.</item>
/// <item><b>The edges are arcs, so what counts as retracing changes.</b>
/// <c>LINESTRING(0 0, 2 2, 1 1)</c> is invalid as <c>geometry</c> — the second
/// segment runs back along the first — and valid as <c>geography</c>, because
/// the great elliptic arc from (0,0) to (2,2) doesn't pass through (1,1). A
/// figure that stops on the vertex it already sits on is valid here too, since
/// the repeat collapses before the edge checks rather than after.</item>
/// <item><b>A ring that revisits one of its own vertices splits into lobes</b>,
/// which the ordinary ring rules then judge — see <see cref="Lobes"/>.</item>
/// </list>
/// <para>An edge whose endpoints are <b>antipodal</b> never reaches this: real
/// refuses the instance at construction with Msg 24206, so the reader raises
/// rather than storing something no arc can join.</para>
/// </remarks>
internal static class SpatialGeodeticValidator
{
    /// <summary>
    /// Raises Msg 24206 when any edge joins two exactly antipodal points. Real
    /// refuses such an instance at <b>construction</b> rather than answering
    /// <c>STIsValid() = 0</c> for it, so every path that builds a
    /// <c>geography</c> value from input runs this first.
    /// </summary>
    /// <remarks>
    /// Antipodal endpoints leave the cutting plane undetermined — every plane
    /// through the centre contains both — so there is no arc to join them with,
    /// which is why real treats it as malformed input rather than as invalid
    /// geometry.
    /// </remarks>
    public static void RejectAntipodalEdges(SpatialShape shape)
    {
        foreach (var figure in shape.Figures)
        {
            for (var i = 1; i < figure.Length; i++)
            {
                if (Antipodal(SpatialEllipsoid.ToCartesian(figure[i - 1]), SpatialEllipsoid.ToCartesian(figure[i])))
                    throw SimulatedSqlException.SpatialAntipodalEdge();
            }
        }
        foreach (var child in shape.Children)
            RejectAntipodalEdges(child);
    }

    /// <summary>
    /// True when two surface points sit within
    /// <see cref="AntipodalTolerance"/> of opposite each other. The normalized
    /// cross product is the sine of the angle between them, which near π is the
    /// angular gap from exactly antipodal.
    /// </summary>
    private static bool Antipodal(SpatialVector from, SpatialVector to) =>
        from.Dot(to) < 0 && from.Cross(to).Length <= AntipodalTolerance * from.Length * to.Length;

    /// <summary>
    /// How near antipodal real refuses, in radians — probed to 1e-8 exactly: an
    /// edge from <c>POINT(0 0)</c> reaching latitude 5.7e-7° past the
    /// antimeridian raises where 5.8e-7° is accepted.
    /// </summary>
    private const double AntipodalTolerance = 1e-8;

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

    /// <summary>Every non-degenerate edge of a figure run, consecutive repeats dropped.</summary>
    private static void AddRun(SpatialCoordinate[] figure, List<RoundEarthEdge> into)
    {
        for (var i = 1; i < figure.Length; i++)
        {
            var from = SpatialEllipsoid.ToCartesian(figure[i - 1]);
            var to = SpatialEllipsoid.ToCartesian(figure[i]);
            if (!SpatialGeodeticTopology.Same(from, to))
                into.Add(new(from, to));
        }
    }

    private static List<RoundEarthEdge> EdgesOf(SpatialShape shape)
    {
        var edges = new List<RoundEarthEdge>();
        foreach (var figure in shape.Figures)
            AddRun(figure, edges);
        return edges;
    }

    /// <summary>
    /// A line is valid when it has an edge left after the repeats collapse and
    /// no two of its edges run alongside each other. Crossing itself at a point
    /// costs simplicity, not validity.
    /// </summary>
    private static bool LineIsValid(SpatialCoordinate[][] figures)
    {
        var edges = new List<RoundEarthEdge>();
        foreach (var figure in figures)
            AddRun(figure, edges);
        if (edges.Count == 0)
            return false;
        foreach (var (first, second) in SpatialGeodeticTopology.CandidatePairs(edges))
        {
            if (SpatialGeodeticTopology.OverlapsIn1D(edges[first], edges[second]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Cross-member rules of a Multi* instance: no member may run alongside
    /// another, and — where <paramref name="membersEnclose"/> says the members
    /// bound area — no member may cross or swallow another either.
    /// </summary>
    private static bool MembersStayApart(SpatialShape shape, bool membersEnclose)
    {
        var members = new List<RoundEarthEdge>[shape.Children.Length];
        var combined = new List<RoundEarthEdge>();
        var owner = new List<int>();
        for (var i = 0; i < members.Length; i++)
        {
            members[i] = EdgesOf(shape.Children[i]);
            foreach (var edge in members[i])
            {
                combined.Add(edge);
                owner.Add(i);
            }
        }
        foreach (var (first, second) in SpatialGeodeticTopology.CandidatePairs(combined))
        {
            if (owner[first] == owner[second])
                continue;
            if (SpatialGeodeticTopology.OverlapsIn1D(combined[first], combined[second])
                || (membersEnclose && SpatialGeodeticTopology.ProperlyCross(combined[first], combined[second])))
            {
                return false;
            }
        }
        if (!membersEnclose)
            return true;
        var operands = new GeodeticRelateOperand[members.Length];
        for (var i = 0; i < operands.Length; i++)
            operands[i] = new(shape.Children[i]);
        for (var i = 0; i < members.Length; i++)
        {
            for (var j = i + 1; j < members.Length; j++)
            {
                if (!operands[i].ExtentMeets(operands[j]))
                    continue;
                if (VertexStrictlyInside(members[i], operands[j]) || VertexStrictlyInside(members[j], operands[i]))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="probe"/> falls in the open region
    /// <paramref name="area"/> bounds.
    /// </summary>
    /// <remarks>
    /// One vertex settles it. No edge of the probe crosses the area's boundary
    /// by the time this runs, so the probe lies wholly inside or wholly outside
    /// — the walk only continues past a vertex that sits <i>on</i> the boundary,
    /// which is the touching case neither answer covers.
    /// </remarks>
    private static bool VertexStrictlyInside(List<RoundEarthEdge> probe, GeodeticRelateOperand area)
    {
        foreach (var edge in probe)
        {
            if (!SpatialGeodeticTopology.OnAnyEdge(edge.From, area.RingEdges))
                return area.RegionContains(edge.From);
        }
        return false;
    }

    private static bool PolygonIsValid(SpatialCoordinate[][] figures)
    {
        var edges = new List<RoundEarthEdge>();
        var rings = new List<(int First, int Count)>();
        foreach (var figure in figures)
        {
            if (figure.Length == 0)
                continue;
            var collapsed = Collapse(figure);
            // A ring with fewer than four surviving vertices bounds nothing.
            if (collapsed.Length < 4)
                return false;
            var lobes = Lobes(collapsed);
            if (lobes.Count == 0)
                return false;
            foreach (var lobe in lobes)
            {
                if (lobe.Length < 4)
                    return false;
                var first = edges.Count;
                AddRun(lobe, edges);
                var count = edges.Count - first;
                if (count == 0)
                    return false;
                rings.Add((first, count));
            }
        }
        return rings.Count == 0
            || (EdgesStayApart(edges, rings)
                && HolesSitInsideShell(edges, rings)
                && OrientationsAgree(edges, rings));
    }

    /// <summary>
    /// Splits a closed ring at the vertices it revisits, giving one closed lobe
    /// per simple cycle the walk contains.
    /// </summary>
    /// <remarks>
    /// A ring that comes back to a vertex it already stood on is not simple, but
    /// real does not reject it outright: it reads the lobes as separate rings
    /// and applies the ordinary ring rules to them, so a small lobe nested
    /// inside the main one and wound the other way is a hole that happens to
    /// meet its shell, which is valid — while two lobes side by side, or a
    /// nested lobe wound the same way, are not. Splitting here is what lets the
    /// ring-set checks answer all four cases without a rule of their own; it is
    /// what real accepts on genuine coastline data, where a border traced back
    /// to its own start vertex is common.
    /// </remarks>
    private static List<SpatialCoordinate[]> Lobes(SpatialCoordinate[] ring)
    {
        var lobes = new List<SpatialCoordinate[]>();
        var path = new List<SpatialCoordinate>(ring.Length);
        var at = new Dictionary<SpatialCoordinate, int>();
        // The closing repeat is the walk's return to its start, not a revisit.
        for (var i = 0; i < ring.Length - 1; i++)
        {
            var point = ring[i];
            if (at.TryGetValue(point, out var start))
            {
                var lobe = new SpatialCoordinate[path.Count - start + 1];
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
        if (path.Count > 0)
        {
            var last = new SpatialCoordinate[path.Count + 1];
            path.CopyTo(last);
            last[^1] = path[0];
            lobes.Add(last);
        }
        return lobes;
    }

    /// <summary>
    /// The three pairwise ring rules, driven off one sweep of the candidate
    /// pairs: a ring meets itself only at the vertex consecutive edges share
    /// (simplicity), two rings never cross or run alongside each other, and the
    /// touches between rings form no cycle — a cycle being exactly a chain that
    /// pinches the polygon's interior in two.
    /// </summary>
    private static bool EdgesStayApart(List<RoundEarthEdge> edges, List<(int First, int Count)> rings)
    {
        var owner = new int[edges.Count];
        for (var ring = 0; ring < rings.Count; ring++)
        {
            for (var i = 0; i < rings[ring].Count; i++)
                owner[rings[ring].First + i] = ring;
        }

        var component = new int[rings.Count];
        for (var i = 0; i < component.Length; i++)
            component[i] = i;

        int Find(int node)
        {
            while (component[node] != node)
                node = component[node] = component[component[node]];
            return node;
        }

        var meetings = new Dictionary<(int, int), GeodeticNodes>();
        foreach (var (first, second) in SpatialGeodeticTopology.CandidatePairs(edges))
        {
            var left = edges[first];
            var right = edges[second];
            if (owner[first] == owner[second])
            {
                var (ringFirst, ringCount) = rings[owner[first]];
                var adjacent = second == first + 1
                    || (first == ringFirst && second == ringFirst + ringCount - 1);
                if (adjacent
                    ? SpatialGeodeticTopology.OverlapsIn1D(left, right)
                    : SpatialGeodeticTopology.Intersects(left, right))
                {
                    return false;
                }
                continue;
            }
            if (SpatialGeodeticTopology.OverlapsIn1D(left, right) || SpatialGeodeticTopology.ProperlyCross(left, right))
                return false;
            var key = (Math.Min(owner[first], owner[second]), Math.Max(owner[first], owner[second]));
            if (!meetings.TryGetValue(key, out var nodes))
                meetings[key] = nodes = new();
            SpatialGeodeticTopology.CollectIntersections(left, right, nodes);
        }

        foreach (var (key, nodes) in meetings)
        {
            for (var touch = 0; touch < nodes.Points.Count; touch++)
            {
                var left = Find(key.Item1);
                var right = Find(key.Item2);
                if (left == right)
                    return false;
                component[left] = right;
            }
        }
        return true;
    }

    /// <summary>The ring with consecutive repeats dropped, which real tolerates while treating the collapsed run as the real geometry.</summary>
    private static SpatialCoordinate[] Collapse(SpatialCoordinate[] figure)
    {
        var kept = new List<SpatialCoordinate>(figure.Length);
        foreach (var coordinate in figure)
        {
            if (kept.Count == 0 || kept[^1] != coordinate)
                kept.Add(coordinate);
        }
        return [.. kept];
    }

    /// <summary>Every interior ring must lie on the shell's interior side and outside every sibling.</summary>
    /// <remarks>
    /// A ring's own left side is the polygon interior, so "inside the shell" is
    /// the shell's left and "inside a sibling hole" is that hole's <i>right</i>
    /// — the same rule read from both roles.
    /// </remarks>
    private static bool HolesSitInsideShell(List<RoundEarthEdge> edges, List<(int First, int Count)> rings)
    {
        var shell = rings[0];
        for (var i = 1; i < rings.Count; i++)
        {
            if (SideOf(edges, shell, rings[i]) == false)
                return false;
            for (var j = i + 1; j < rings.Count; j++)
            {
                if (SideOf(edges, rings[i], rings[j]) == false || SideOf(edges, rings[j], rings[i]) == false)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Which side of <paramref name="ring"/> the whole of <paramref name="probe"/>
    /// lies on — <c>true</c> for the interior side, <c>false</c> for the other,
    /// <c>null</c> when nothing settled it.
    /// </summary>
    /// <remarks>
    /// One vertex settles it: the rings have already been shown not to cross, so
    /// the probe lies wholly on one side. The walk only continues past a vertex
    /// sitting <i>on</i> the ring, which is a touch rather than a side.
    /// </remarks>
    private static bool? SideOf(List<RoundEarthEdge> edges, (int First, int Count) ring, (int First, int Count) probe)
    {
        for (var edge = 0; edge < probe.Count; edge++)
        {
            var vertex = edges[probe.First + edge].From;
            if (!OnRing(edges, ring, vertex))
                return OnInteriorSide(edges, ring, vertex);
        }
        return null;
    }

    /// <summary>
    /// Every ring must agree on which region the polygon names. The region is
    /// read off the first ring's left side, and each ring then has to find that
    /// region on its own left and something else on its right — which is what
    /// rejects a hole wound the same way as its shell.
    /// </summary>
    /// <remarks>
    /// One probe per ring is enough for a ring set that has already passed the
    /// crossing checks: a face is a connected region, and a ring's left side
    /// lies in one face for its whole length once no other ring crosses it.
    /// </remarks>
    private static bool OrientationsAgree(List<RoundEarthEdge> edges, List<(int First, int Count)> rings)
    {
        var interior = SpatialGeodeticTopology.Offset(edges[rings[0].First], 0.5, leftSide: true);
        for (var i = 1; i < rings.Count; i++)
        {
            var left = SpatialGeodeticTopology.Offset(edges[rings[i].First], 0.5, leftSide: true);
            var right = SpatialGeodeticTopology.Offset(edges[rings[i].First], 0.5, leftSide: false);
            if (SpatialGeodeticTopology.SameSide(interior, left, edges, rings) == false
                || SpatialGeodeticTopology.SameSide(interior, right, edges, rings) == true)
            {
                return false;
            }
        }
        return true;
    }

    private static bool OnRing(List<RoundEarthEdge> edges, (int First, int Count) ring, SpatialVector point)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            if (edges[ring.First + i].Arc.Contains(point))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a point lies on the side of one ring that ring calls its
    /// interior — its left — or null when no crossing count from any of the
    /// ring's own probe points comes back reliable.
    /// </summary>
    private static bool? OnInteriorSide(List<RoundEarthEdge> edges, (int First, int Count) ring, SpatialVector point)
    {
        List<(int First, int Count)> single = [ring];
        for (var i = 0; i < 3; i++)
        {
            var reference = SpatialGeodeticTopology.Offset(
                edges[ring.First + (i * ring.Count / 3)], 0.25 + (0.25 * i), leftSide: true);
            if (SpatialGeodeticTopology.SameSide(reference, point, edges, single) is { } side)
                return side;
        }
        return null;
    }
}
