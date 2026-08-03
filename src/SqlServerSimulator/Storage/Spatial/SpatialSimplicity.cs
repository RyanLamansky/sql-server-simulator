namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>geometry</c>'s <c>STIsSimple()</c> — whether the instance's own point set
/// meets itself anywhere the OGC rules don't allow.
/// </summary>
/// <remarks>
/// <para>Simplicity is what validity stops short of: a self-crossing
/// <c>LINESTRING</c> is a valid instance and not a simple one. The rules, all
/// probed against SQL Server 2025:</para>
/// <list type="bullet">
/// <item>A <b>MultiPoint</b> is simple when no two of its points coincide.</item>
/// <item>A <b>curve</b> — a line figure or a polygon ring — is simple when
/// consecutive segments meet only at their shared vertex and no other pair
/// meets at all, with a closed figure's first and last segments counting as
/// consecutive. So a closed ring written as a <c>LINESTRING</c> is simple while
/// one that runs back over its own start and carries on is not.</item>
/// <item>Two <b>different figures</b> of one instance may meet only at a point
/// that is a <b>boundary point of both</b> — an endpoint of an open figure.
/// A ring is closed and so has no boundary, which is why two polygons of a
/// <c>MultiPolygon</c> touching at a single point are valid and not simple, and
/// why a hole meeting its shell at one point is too.</item>
/// <item>A <b>GeometryCollection</b> is simple exactly when every member is —
/// real does not compare one member against another, so a collection of two
/// crossing lines is simple where the same pair as a <c>MULTILINESTRING</c> is
/// not.</item>
/// <item>An <b>empty</b> instance is simple.</item>
/// </list>
/// <para>Every caller has already passed the Msg 24144 validity gate, so the
/// one-dimensional overlaps validity rejects can't reach here.</para>
/// </remarks>
internal static class SpatialSimplicity
{
    public static bool IsSimple(SpatialShape shape)
    {
        if (shape.Type == SpatialShapeType.GeometryCollection)
        {
            foreach (var child in shape.Children)
            {
                if (!IsSimple(child))
                    return false;
            }
            return true;
        }

        if (shape.Type is SpatialShapeType.Point or SpatialShapeType.MultiPoint)
            return PointsAreDistinct(shape);

        var figures = new List<PlanarPoint[]>();
        Collect(shape, figures);
        return FiguresAreSimple(figures) && FiguresStayApart(figures);
    }

    private static bool PointsAreDistinct(SpatialShape shape)
    {
        var seen = new HashSet<PlanarPoint>();
        foreach (var coordinate in shape.Coordinates())
        {
            if (!seen.Add(PlanarPoint.From(coordinate)))
                return false;
        }
        return true;
    }

    private static void Collect(SpatialShape shape, List<PlanarPoint[]> into)
    {
        foreach (var figure in shape.Figures)
        {
            var collapsed = Collapse(figure);
            if (collapsed.Length >= 2)
                into.Add(collapsed);
        }
        foreach (var child in shape.Children)
            Collect(child, into);
    }

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

    private static bool IsClosed(PlanarPoint[] figure) => figure.Length > 2 && figure[0] == figure[^1];

    /// <summary>Each figure taken on its own: no pair of its segments may meet other than at a shared vertex of consecutive ones.</summary>
    private static bool FiguresAreSimple(List<PlanarPoint[]> figures)
    {
        foreach (var figure in figures)
        {
            var segments = new List<PlanarSegment>();
            for (var i = 1; i < figure.Length; i++)
            {
                var segment = new PlanarSegment(figure[i - 1], figure[i]);
                if (!segment.IsDegenerate)
                    segments.Add(segment);
            }
            var closed = IsClosed(figure);
            var last = segments.Count - 1;
            foreach (var (first, second) in SpatialTopology.CandidatePairs(segments))
            {
                var consecutive = second == first + 1 || (closed && first == 0 && second == last);
                if (consecutive
                    ? SpatialTopology.OverlapsIn1D(segments[first], segments[second])
                    : SpatialTopology.Intersects(segments[first], segments[second]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Across figures: every meeting has to sit on a boundary point of both
    /// curves, which for an open figure is one of its two endpoints and for a
    /// closed one is nowhere.
    /// </summary>
    private static bool FiguresStayApart(List<PlanarPoint[]> figures)
    {
        var boundaries = new HashSet<PlanarPoint>[figures.Count];
        for (var i = 0; i < figures.Count; i++)
        {
            boundaries[i] = IsClosed(figures[i]) ? [] : [figures[i][0], figures[i][^1]];
        }

        var meetings = new HashSet<PlanarPoint>();
        for (var i = 0; i < figures.Count; i++)
        {
            for (var j = i + 1; j < figures.Count; j++)
            {
                meetings.Clear();
                for (var a = 1; a < figures[i].Length; a++)
                {
                    var one = new PlanarSegment(figures[i][a - 1], figures[i][a]);
                    for (var b = 1; b < figures[j].Length; b++)
                    {
                        var other = new PlanarSegment(figures[j][b - 1], figures[j][b]);
                        if (SpatialTopology.OverlapsIn1D(one, other))
                            return false;
                        SpatialTopology.CollectIntersections(one, other, meetings);
                    }
                }
                foreach (var meeting in meetings)
                {
                    if (!boundaries[i].Contains(meeting) || !boundaries[j].Contains(meeting))
                        return false;
                }
            }
        }
        return true;
    }
}
